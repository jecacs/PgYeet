# PgYeet — Usage Guide

This guide is the normative description of PgYeet 1.0 behavior. For the project overview, see the
[README](../README.md). For measured performance and reproduction rules, see
[BENCHMARKS.md](BENCHMARKS.md).

PgYeet has one supported public operation:

```csharp
Task<int> YeetAsync<T>(
    this DbSet<T> dbSet,
    IEnumerable<T> entities,
    bool returnGeneratedKeys = true,
    CancellationToken ct = default)
    where T : class;
```

## Contents

- [Installation and compatibility](#installation-and-compatibility)
- [Minimal model](#minimal-model)
- [Insert and return generated keys](#insert-and-return-generated-keys)
- [Direct streaming](#direct-streaming)
- [Caller-assigned keys](#caller-assigned-keys)
- [Value converters and strongly typed IDs](#value-converters-and-strongly-typed-ids)
- [Transactions](#transactions)
- [Cancellation](#cancellation)
- [Change tracking and SaveChanges](#change-tracking-and-savechanges)
- [Defaults, computed columns, and shadow properties](#defaults-computed-columns-and-shadow-properties)
- [Supported mappings and limitations](#supported-mappings-and-limitations)
- [Identity sequences, triggers, and correlation](#identity-sequences-triggers-and-correlation)
- [Permissions](#permissions)
- [Logging and diagnostics](#logging-and-diagnostics)
- [Errors](#errors)
- [Retries and concurrency](#retries-and-concurrency)

## Installation and compatibility

```bash
dotnet add package PgYeet --version 1.0.0
```

```xml
<PackageReference Include="PgYeet" Version="1.0.0" />
```

The package aligns its target framework with the EF Core and Npgsql provider major:

| Target framework | EF Core | `Npgsql.EntityFrameworkCore.PostgreSQL` |
| --- | --- | --- |
| `net10.0` | 10.x | 10.x |

Use EF Core 10.0.12+ and Npgsql/provider 10.0.3+ within their 10.x lines. PgYeet targets only
`net10.0`; its package dependency ranges reject cross-major provider resolution.

PgYeet services a target/provider line only while that line's .NET, EF Core, and Npgsql upstreams
remain supported. Plan framework upgrades before an upstream line reaches end of support. Removing a
target framework is a documented compatibility change and, under the current 1.x policy, requires a
new major PgYeet version.

PostgreSQL server-version support follows the matching Npgsql provider. The repository's integration
suite currently starts PostgreSQL 18 through Testcontainers.

## Minimal model

PgYeet uses ordinary EF configuration. No PgYeet attributes or model-building calls are required:

```csharp
using Microsoft.EntityFrameworkCore;

public sealed class User
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.Property(user => user.Name).HasMaxLength(200);
            entity.Property(user => user.Email).HasMaxLength(320);
        });
    }
}
```

The conventional integer primary key is a PostgreSQL identity through the Npgsql provider. PgYeet
reads the final table, schema, column names, store types, converters, and value-generation strategy from
the runtime EF model.

## Insert and return generated keys

```csharp
using PgYeet;

var users = new[]
{
    new User
    {
        Name = "Ada",
        Email = "ada@example.com",
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    },
    new User
    {
        Name = "Alan",
        Email = "alan@example.com",
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    }
};

var inserted = await db.Users.YeetAsync(users, ct: cancellationToken);

if (inserted != users.Length)
    throw new InvalidOperationException("Unexpected inserted row count.");

Console.WriteLine(users[0].Id);
```

When the model has a supported single-column integer identity/serial key and
`returnGeneratedKeys` is `true`, PgYeet:

1. snapshots the input references into a private array while honoring cancellation;
2. validates that every element is non-null and every object reference appears only once;
3. starts a transaction or creates a savepoint in the caller's transaction, then validates the actual
   PostgreSQL backing sequence;
4. creates a temporary table inside that transaction;
5. binary-copies the insertable values and an ordinal to that table;
6. runs `INSERT … SELECT … RETURNING` against the target table;
7. validates the number and CLR range of returned keys;
8. drops the staging table, snapshots the original CLR key values, and assigns the generated values;
9. commits its own transaction or releases its savepoint.

A repeated object reference is rejected before the connection is opened. Otherwise two database rows
would require PgYeet to assign two different keys to one object.

The returned `int` is the number of inserted rows. The objects remain detached unless the application
had already attached them; PgYeet does not change their `EntityState`.

## Direct streaming

Choose the direct path when generated keys are not needed in memory:

```csharp
var inserted = await db.Users.YeetAsync(
    users,
    returnGeneratedKeys: false,
    ct: cancellationToken);
```

For an identity-backed entity, the identity column is omitted and PostgreSQL still generates the stored
value. PgYeet simply leaves the corresponding CLR property unchanged.

The direct path issues one binary `COPY` against the target table and enumerates the input once without
converting it to an array. This allows lazy input:

```csharp
static IEnumerable<User> GenerateUsers(int count)
{
    for (var i = 0; i < count; i++)
    {
        yield return new User
        {
            Name = $"User {i}",
            Email = $"user-{i}@example.com"
        };
    }
}

var inserted = await db.Users.YeetAsync(
    GenerateUsers(10_000),
    returnGeneratedKeys: false,
    ct: cancellationToken);
```

The sequence must be synchronous `IEnumerable<T>`. PgYeet does not accept `IAsyncEnumerable<T>` in
1.0. Each yielded entity must be non-null and valid for the mapped PostgreSQL columns. The public
result is `Int32`. If a lazy direct stream attempts to yield more than `Int32.MaxValue` rows, PgYeet
throws before starting the next row and disposes the binary importer without completing it, so
PostgreSQL aborts the whole COPY rather than committing a prefix.

Entities without a supported store-generated identity use the direct path automatically, even when
`returnGeneratedKeys` is left at its default.

## Caller-assigned keys

Configure application-assigned keys in the EF model and set every value before calling PgYeet:

```csharp
public sealed class ImportItem
{
    public Guid Id { get; set; }
    public required string ExternalCode { get; set; }
}

modelBuilder.Entity<ImportItem>(entity =>
{
    entity.ToTable("import_items");
    entity.Property(item => item.Id).ValueGeneratedNever();
});

var items = source.Select(row => new ImportItem
{
    Id = Guid.NewGuid(),
    ExternalCode = row.Code
});

await db.Set<ImportItem>().YeetAsync(items, ct: cancellationToken);
```

PgYeet copies caller-assigned key values as ordinary columns. It does not call EF value generators.

For a composite key, every component must be supplied by the application. PgYeet has no composite
generated-key write-back or correlation mechanism; do not rely on database generation for any component.

## Value converters and strongly typed IDs

Scalar EF value converters are supported. PgYeet converts each value to the provider type before
writing it:

```csharp
public enum AccountState
{
    Pending,
    Active,
    Disabled
}

modelBuilder.Entity<Account>()
    .Property(account => account.State)
    .HasConversion<string>();
```

Converted properties use a boxed conversion path. They are correct but do not have the same no-boxing
characteristic as an ordinary scalar writer.

An integer-backed strongly typed identity can receive a generated value through its converter:

```csharp
public readonly record struct OrderId(int Value);

public sealed class Order
{
    public OrderId Id { get; set; }
    public required string Reference { get; set; }
}

modelBuilder.Entity<Order>(entity =>
{
    entity.ToTable("orders");
    entity.Property(order => order.Id)
        .HasConversion(
            id => id.Value,
            value => new OrderId(value))
        .UseIdentityByDefaultColumn();
});
```

The provider value must be an integer identity/serial value that PgYeet can represent through `Int64`
and convert back to the CLR key type.

## Transactions

### Existing transaction

PgYeet participates in the current `DbContext` transaction:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

await db.Users.YeetAsync(users, ct: cancellationToken);
await db.AuditEvents.YeetAsync(
    events,
    returnGeneratedKeys: false,
    ct: cancellationToken);

await transaction.CommitAsync(cancellationToken);
```

The caller owns commit and rollback. Disposing the transaction without committing rolls back the
database changes. The generated-key path creates a savepoint for each call. On failure it attempts to
roll back to that savepoint and restore the original key values, preserving earlier caller work.
Staging tables are dropped before a successful call returns, so long transactions do not accumulate
one temporary table per call. The direct COPY path does not create a savepoint; after a PostgreSQL
error on that path, the caller must roll back its transaction or its own savepoint.

Generated keys are assigned to CLR objects before the caller commits. A later rollback does not undo
those in-memory property assignments:

```csharp
await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
{
    await db.Users.YeetAsync(users, ct: cancellationToken);
    // No commit: the rows are rolled back, but users[*].Id remains assigned.
}
```

Reset or reload those objects before reusing them.

### No existing transaction

- The generated-key path starts an Npgsql transaction, commits it on success, and rolls it back on
  failure.
- The direct path is one atomic PostgreSQL `COPY` statement. PgYeet does not create an application-level
  transaction around multiple calls.

If a generated-key operation fails after CLR key assignment, PgYeet attempts to restore every original
key value before rethrowing. A cleanup failure is attached to the original exception's `Data` so the
database or cancellation error remains primary.

The generated-key path requires the current EF transaction to expose an `NpgsqlTransaction`. A wrapped
or non-Npgsql transaction is rejected rather than silently ignored.

## Cancellation

Pass cancellation through the `ct` parameter:

```csharp
await db.Users.YeetAsync(
    users,
    returnGeneratedKeys: false,
    ct: cancellationToken);
```

An already-cancelled token is rejected before model access or input enumeration, including for empty
input. Cancellation is also checked while buffering generated-key input. The token is used while opening the connection, starting and writing COPY, executing the staged insert,
and reading generated keys. Cancellation normally surfaces as `OperationCanceledException` or an
Npgsql-derived exception appropriate to the point of interruption.

If PgYeet owns the generated-key transaction, cleanup and rollback are still attempted after cancellation
with a non-cancelled cleanup token. The generated-key path also attempts to roll back its savepoint
with a non-cancelled token inside a caller-owned transaction. If cleanup fails or the connection is
lost, the caller must roll back/dispose the transaction and determine the database outcome.

Cancellation is not an idempotency guarantee. If the client loses confirmation after PostgreSQL commits,
the application must determine the outcome before retrying.

## Change tracking and SaveChanges

PgYeet executes database work immediately and bypasses the EF Core insert pipeline:

- it does not call `Add`, `AddRange`, or `SaveChanges`;
- it does not attach detached objects;
- it does not set `EntityState.Added` or `EntityState.Unchanged`;
- it does not invoke `SaveChanges` interceptors;
- it does not run EF client-side value generators;
- it does not persist navigation graphs.

Use new or detached objects:

```csharp
var users = BuildUsers();
await db.Users.YeetAsync(users, ct: cancellationToken);
```

Do not do this:

```csharp
db.Users.AddRange(users);
await db.Users.YeetAsync(users, ct: cancellationToken);
await db.SaveChangesAsync(cancellationToken); // may try to insert the same objects again
```

If objects must be tracked after a successful commit, explicitly attach them or query them again with
the state semantics your application requires. Be especially careful when attaching objects whose keys
were assigned inside a transaction that later rolled back.

## Defaults, computed columns, and shadow properties

PgYeet and `SaveChanges` differ intentionally.

### CLR-backed defaults

For every insertable CLR-backed property, PgYeet writes the current CLR value. It does not inspect EF's
sentinel to decide that an “unset” value should be omitted:

```csharp
entity.Property(row => row.CreatedAt)
    .HasDefaultValueSql("CURRENT_TIMESTAMP");
```

If `CreatedAt` is a CLR-backed property, PgYeet writes its current value. PostgreSQL's default does not
replace it. Assign the intended value in the application or use a computed/generated mapping that PgYeet
omits.

### Computed columns

Properties configured with `HasComputedColumnSql` are omitted from COPY and the target insert so
PostgreSQL can compute them. PgYeet 1.0 does not read computed values back.

### Shadow properties

PgYeet cannot read a shadow value from a CLR object. Field-only mappings are distinct from shadow
properties: PgYeet rejects them instead of omitting their data, even when the column is nullable or
has a default. Identity keys must be mapped to a CLR property with a getter and setter (private
accessors are allowed); this guard applies to both insert paths.

For non-key shadow properties:

- nullable or default-backed shadow columns may be omitted;
- a required shadow property with no database value is rejected before COPY.

Map required data to a CLR property or configure the database to supply it.

## Supported mappings and limitations

| Mapping or behavior | 1.0 status | Notes |
| --- | --- | --- |
| Single table per entity | Supported | Schema and identifiers come from the runtime EF model |
| Scalar CLR properties | Supported | Subject to the matching Npgsql provider's type mapping |
| Reference and nullable value types | Supported | Null is written through the binary importer |
| EF value converters | Supported | Converted values use the boxed path |
| Integer `smallint`/`integer`/`bigint` identity or serial | Supported | Generated value must be representable through `Int64` and the CLR key |
| Integer-backed strongly typed identity | Supported | Requires a reversible EF converter |
| Caller-assigned keys | Supported | Set values before the call |
| Caller-assigned composite keys | Conditional | Every component must be an insertable scalar and supplied by the caller |
| Computed columns | Supported by omission | Values are not read back |
| Optional/default-backed shadow columns | Supported by omission | PostgreSQL supplies the value |
| Required shadow column with no database value | Rejected | PgYeet cannot read it from the object |
| Field-only property | Rejected | Map a readable CLR property; field values must not be silently omitted |
| Identity key without CLR getter/setter | Rejected | Key write-back and restoration require both accessors |
| TPH inheritance | Rejected | Discriminator mapping is outside the contract |
| TPT or multiple table mappings | Rejected | One entity maps to one target table |
| Owned types | Rejected | Their columns would otherwise be skipped |
| Complex types | Rejected | Their columns would otherwise be skipped |
| Entity or table splitting | Rejected | Multiple mapped entity shapes are outside the contract |
| Store-generated UUID/default/HiLo primary key | Rejected | PgYeet bypasses EF value generation |
| Composite generated key | Not supported | No key-correlation protocol |
| Descending or cyclic identity | Rejected | Generated-key correlation requires increasing values |
| Missing or incompatible identity/serial backing sequence | Rejected | Generated-key path checks the live PostgreSQL catalogs before COPY |
| Non-primary-key identity/serial column | Rejected | 1.x reads back only one primary-key value |
| Entity with only generated/computed columns | Rejected | At least one insertable CLR-backed column is required |
| Database defaults on CLR-backed columns | Not applied | The CLR value is always written |
| Generated computed-value read-back | Not supported | Only the supported identity key is returned |
| Navigation graph insert | Not supported | Insert one entity type per call |
| Update, delete, merge, upsert | Not supported | PgYeet implements insert only |
| Non-PostgreSQL provider | Rejected | Npgsql connection required |
| `IAsyncEnumerable<T>` input | Not supported | The public API accepts `IEnumerable<T>` |

“Supported scalar properties” does not mean every PostgreSQL extension type has dedicated PgYeet test
coverage. PgYeet delegates binary representation to Npgsql, but a production mapping should be tested
against the exact Npgsql/provider major and PostgreSQL extensions used by the application.

## Identity sequences, triggers, and correlation

Generated-key write-back requires:

- one generated key column;
- a PostgreSQL identity or serial strategy;
- numeric values representable through `Int64`;
- a positive-increment, non-cyclic sequence;
- no trigger or rule that replaces or reorders generated key values.

PgYeet includes an ordinal in the staging table and inserts in that order. It validates that
`RETURNING` produces exactly one key per input row and sorts the returned numeric keys before assignment.
This protects against returned-row ordering differences under the documented monotonic-key assumption.

PgYeet first rejects explicit non-positive or cyclic identity annotations during model inspection.
After it opens the generated-key transaction—but before it creates the staging table or starts
COPY—it resolves the actual backing sequence with `pg_get_serial_sequence` and reads `pg_sequence`.
The operation is rejected if PostgreSQL exposes no backing sequence, if its current increment is not
positive, or if it is cyclic. This database preflight applies to both identity and serial columns and
catches drift after migration, including `ALTER SEQUENCE` changes made outside the EF model.

A trigger that filters a row or multiplies rows changes the returned count; PgYeet throws instead of
assigning an obviously incomplete key set. An identity-rewriting `BEFORE INSERT` trigger can preserve
the returned count while breaking input-to-key correlation, so such triggers are unsupported. PgYeet
cannot reliably detect arbitrary same-count key rewriting.

When PgYeet owns the transaction, a detected mismatch is rolled back. Inside a caller-owned
transaction, PgYeet attempts to roll back its savepoint before throwing. Earlier caller work remains
intact if that rollback succeeds.

## Permissions

The database role needs the privileges PostgreSQL requires for the selected path.

Direct path:

- connection to the database;
- target-table and target-column insert privileges;
- sequence/identity privileges required by the table;
- permissions required by constraints, functions, triggers, and row-level-security policies.

Generated-key path additionally needs:

- permission to create temporary tables in the database;
- permission to read the returned identity value.

Use least privilege. Test the exact production role; owner or superuser credentials can hide missing
grants.

## Logging and diagnostics

PgYeet logs through `ILogger` category `PgYeet` at `Debug` level:

```json
{
  "Logging": {
    "LogLevel": {
      "PgYeet": "Debug"
    }
  }
}
```

PgYeet's own messages include the generated COPY command shape and row counts, not entity values.
EF Core, Npgsql, PostgreSQL, hosting, and application logging are configured independently and may
record different data.

Useful diagnostic context for an incident:

- PgYeet package version;
- target framework;
- EF Core and Npgsql provider versions;
- PostgreSQL server version;
- entity type and relevant model configuration;
- selected direct or generated-key path;
- ambient transaction state;
- complete exception type, message, inner exception, and PostgreSQL SQLSTATE;
- whether a retry was attempted.

Remove credentials and production data before sharing logs or reproductions.

## Errors

PgYeet does not wrap every provider exception. The main categories are:

| Exception | Typical meaning |
| --- | --- |
| `ArgumentNullException` | `dbSet` or `entities` is null |
| `ArgumentException` | The input contains a null element or repeats an object reference on the generated-key path |
| `ArgumentOutOfRangeException` | A direct operation would exceed `Int32.MaxValue` rows; the COPY is aborted |
| `InvalidOperationException` | Entity missing from the EF model, no table mapping, or generated keys cannot be correlated |
| `NotSupportedException` | Non-Npgsql provider, unsupported mapping/key strategy, or incompatible ambient transaction |
| `OperationCanceledException` | Cancellation was observed |
| `NpgsqlException` / `PostgresException` | Connection, protocol, permission, constraint, type, trigger, or server failure |
| `OverflowException` | A returned numeric key cannot fit `Int64` or the configured CLR key type |

PostgreSQL remains authoritative for uniqueness, foreign keys, checks, row-level security, triggers,
and data-type validation. PostgreSQL does not allow `COPY FROM` into an RLS-enabled table for an
ordinary non-bypass role, so PgYeet's direct path cannot be used there. The generated-key path copies
into a temporary table and then uses `INSERT` for the target; the target's normal RLS policy applies to
that statement.

An empty materialized collection returns zero. A lazy sequence that yields no rows also inserts nothing,
but PgYeet may still need to inspect the model and connection before discovering that it is empty.

## Retries and concurrency

PgYeet does not invoke an EF execution strategy or add an automatic retry policy. Bulk insert is
non-idempotent unless the application establishes an idempotency key, conflict policy, or enclosing
transaction with a known outcome.

Do not retry solely because the client timed out. Determine whether PostgreSQL committed the statement
whenever the outcome is unknown.

Normal EF Core concurrency rules still apply:

- do not execute concurrent operations on one `DbContext`;
- do not enumerate one mutable input sequence concurrently;
- a `DbContext` transaction is not a cross-context coordination mechanism;
- mapping metadata is cached safely, but the context and entity objects remain application-owned.

For performance measurement and safe reproduction, continue with [BENCHMARKS.md](BENCHMARKS.md).
