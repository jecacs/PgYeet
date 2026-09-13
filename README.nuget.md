# PgYeet

High-throughput PostgreSQL bulk inserts for Entity Framework Core, powered by Npgsql binary `COPY`.
PgYeet reads table, column, store-type, and value-converter metadata from your existing EF model—no
duplicate mapping and no bulk-specific attributes.

## Install

```bash
dotnet add package PgYeet --version 1.0.0
```

```xml
<PackageReference Include="PgYeet" Version="1.0.0" />
```

PgYeet 1.0 targets .NET 10:

| Target framework | EF Core | Npgsql EF Core provider |
| --- | --- | --- |
| `net10.0` | 10.x | 10.x |

Requires EF Core 10.0.12+ and Npgsql/provider 10.0.3+ within their 10.x lines. The package
targets only `net10.0` and bounds dependencies below the next major version.

## Quick start

```csharp
using PgYeet;

var users = new[]
{
    new User { Name = "Ada", Email = "ada@example.com" },
    new User { Name = "Alan", Email = "alan@example.com" }
};

var inserted = await db.Users.YeetAsync(users, ct: cancellationToken);

// For a supported identity/serial key, generated values are now assigned:
Console.WriteLine(users[0].Id);
```

If generated values are not needed in memory, choose the direct streaming path instead:

```csharp
var inserted = await db.Users.YeetAsync(
    users,
    returnGeneratedKeys: false,
    ct: cancellationToken);
```

These examples are alternatives; calling both inserts the rows twice.

## Execution paths

| Path | Database work | Input | CLR keys |
| --- | --- | --- | --- |
| Generated keys | Temp-table `COPY`, then `INSERT … RETURNING` | Buffered when a supported identity exists | Assigned to the objects |
| Direct | One binary `COPY` into the target table | Enumerated once | Left unchanged |

`YeetAsync` executes immediately and bypasses EF Core change tracking and `SaveChanges`. Use new or
detached objects; do not call `Add` or `AddRange` first. PgYeet can assign generated key properties,
but it does not attach the objects or change their `EntityState`.

Generated-key input must contain distinct, non-null object references. A repeated reference cannot
receive two different keys unambiguously and is rejected before database I/O. Before COPY begins,
PgYeet also verifies the actual PostgreSQL backing sequence is present, positive-increment, and
non-cyclic.

An existing `DbContext` transaction is honored. Without one, the generated-key path owns a transaction
and the direct path is a single atomic PostgreSQL statement. If an ambient transaction is rolled back
after a successful call, the CLR objects still retain those values. A failed generated-key call rolls
back to its own savepoint inside the caller's transaction and attempts to restore the original keys.

## Supported scope

PgYeet supports:

- one entity mapped to one PostgreSQL table;
- scalar CLR-backed properties and nullable values;
- EF value converters;
- caller-assigned keys, including `Guid`;
- single-column integer identity/serial key write-back;
- integer-backed strongly typed identities with an EF converter.

Computed columns are omitted. CLR-backed properties with database defaults are not omitted: PgYeet
writes the current property value, so EF Core's `SaveChanges` sentinel behavior does not apply.

Owned and complex types, inheritance/table splitting, required shadow properties without a database
value, field-only properties, identity keys without a CLR getter/setter, composite generated keys, descending/cyclic identities, non-key identity columns, and
non-identity store-generated keys are outside the 1.0 contract.
Identity-rewriting `BEFORE INSERT` triggers are also unsupported because returned keys cannot be
correlated safely with the input objects.
PostgreSQL rejects direct `COPY FROM` into an RLS-enabled target for an ordinary non-bypass role. The
generated-key path instead uses `INSERT` for the target and is subject to its normal RLS policy.
PgYeet implements insert only; it does not update, delete, upsert, synchronize graphs, or support
providers other than PostgreSQL/Npgsql.

## Learn more

- [Usage guide](https://github.com/jecacs/PgYeet/blob/main/docs/USAGE.md)
- [Benchmarks and methodology](https://github.com/jecacs/PgYeet/blob/main/docs/BENCHMARKS.md)
- [Changelog](https://github.com/jecacs/PgYeet/blob/main/CHANGELOG.md)
- [Security policy](https://github.com/jecacs/PgYeet/blob/main/SECURITY.md)
- [GitHub repository](https://github.com/jecacs/PgYeet)
