<p align="center">
  <img src="https://raw.githubusercontent.com/jecacs/PgYeet/main/assets/logo.png" alt="PgYeet" width="240" />
</p>

# PgYeet

[![NuGet](https://img.shields.io/nuget/v/PgYeet.svg?logo=nuget)](https://www.nuget.org/packages/PgYeet)
[![Downloads](https://img.shields.io/nuget/dt/PgYeet.svg?logo=nuget)](https://www.nuget.org/packages/PgYeet)
[![build](https://github.com/jecacs/PgYeet/actions/workflows/ci.yml/badge.svg)](https://github.com/jecacs/PgYeet/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A focused .NET library for high-throughput bulk `INSERT`s from Entity Framework Core into
PostgreSQL using Npgsql binary `COPY`. PgYeet reuses the mapping already defined in your
`DbContext`—table, columns, store types, and value converters—and provides a direct streaming
path plus optional identity/serial key write-back.

> **Status: preparing 1.0.0.** The supported public API follows
> [Semantic Versioning](https://semver.org/): breaking API changes require a new major version.

📖 **Start with the [usage guide](docs/USAGE.md)** for complete examples, transaction semantics,
the mapping support matrix, and operational caveats.

## Why PgYeet

- **Uses your EF model.** No duplicate mapping, bulk-specific attributes, or parallel metadata layer.
- **Two deliberate paths.** Return supported generated keys, or choose one direct `COPY` when the
  CLR objects do not need them.
- **Streams when it can.** The direct path enumerates the input once without materializing the batch.
- **Fails loudly.** Unsupported mappings are rejected instead of silently dropping required data.
- **Narrow scope.** One operation, one provider, and one small public API: PostgreSQL bulk insert.
- **MIT licensed.** Use it in commercial and open-source software under the terms of [the license](LICENSE).

## Install

The following commands target the planned 1.0.0 release. Before publication, use the locally packed
artifact as described in [RELEASING](docs/RELEASING.md).

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

Use EF Core 10.0.12 or later in the 10.x line and Npgsql/provider 10.0.3 or later in the 10.x
line. The package targets only `net10.0` and bounds dependencies below the next major version.
PostgreSQL server-version support follows the matching Npgsql provider; the repository's integration
suite currently runs against PostgreSQL 18.

## Quick start

```csharp
using PgYeet;

var users = new[]
{
    new User { Name = "Ada", Email = "ada@example.com" },
    new User { Name = "Alan", Email = "alan@example.com" }
};

var inserted = await db.Users.YeetAsync(users, ct: cancellationToken);

Console.WriteLine(inserted);    // 2
Console.WriteLine(users[0].Id); // database-assigned identity value
```

`YeetAsync` executes immediately. Do **not** call `Add` or `AddRange` for the same objects first,
and do not call `SaveChanges` to complete the bulk insert. PgYeet bypasses EF Core change tracking;
it can assign generated key properties, but it does not attach the objects or change their
`EntityState`.

For generated-key write-back, every sequence element must be non-null and each element must be a
distinct object reference. Repeating the same object would make key assignment ambiguous, so PgYeet
rejects that input before opening the database connection.

### Direct streaming

If the application does not need generated keys in memory, choose the direct path instead:

```csharp
var inserted = await db.Users.YeetAsync(
    users,
    returnGeneratedKeys: false,
    ct: cancellationToken);
```

The two snippets are alternatives. Calling both inserts the rows twice.

| Path | Database work | Input handling | CLR key values |
| --- | --- | --- | --- |
| Generated keys | Temp-table `COPY`, then `INSERT … RETURNING` | Buffered when a supported identity exists | Assigned to the objects |
| Direct | One binary `COPY` into the target table | Enumerated once | Left unchanged |

Entities with caller-assigned keys use the direct path automatically. Setting
`returnGeneratedKeys: false` also selects it for identity-backed entities while PostgreSQL still
generates the stored identity values.

## Transactions

When `DbContext.Database.CurrentTransaction` is set, PgYeet uses that transaction and the caller owns
commit or rollback:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

await db.Users.YeetAsync(users, ct: cancellationToken);
await db.AuditEvents.YeetAsync(events, returnGeneratedKeys: false, ct: cancellationToken);

await transaction.CommitAsync(cancellationToken);
```

Without an ambient transaction, the generated-key path creates and owns an Npgsql transaction. The
direct path is a single atomic PostgreSQL `COPY` statement.

Generated keys are written to CLR objects before an ambient transaction is committed. If the caller
later rolls that transaction back, the objects still retain those key values even though the rows no
longer exist. Reset or reload them before reuse.

The generated-key path uses a savepoint inside a caller-owned transaction. On failure it attempts to
roll back that operation while preserving earlier caller work. Staging tables are dropped before the
call returns. If the operation itself fails after key assignment, PgYeet attempts to
restore every original CLR key value before rethrowing. Any cleanup failures are attached to the
original exception's data rather than replacing the database failure.

PgYeet does not add an automatic retry layer. Treat an unknown database outcome carefully: blindly
retrying a non-idempotent insert can create duplicates.

## Supported mappings

The 1.0 contract is intentionally explicit:

| Mapping | 1.0 support |
| --- | --- |
| One entity mapped to one PostgreSQL table | Yes |
| Scalar CLR-backed properties | Yes |
| Nullable scalar values | Yes |
| EF value converters | Yes; the converted path may box values |
| Caller-assigned keys, including `Guid` | Yes; set them before the call |
| Single-column integer identity/serial key write-back | Yes |
| Integer-backed strongly typed identity with an EF converter | Yes |
| Computed columns | Omitted so PostgreSQL computes them |
| Optional/default-backed shadow properties | Omitted |
| Required shadow properties with no database value | Rejected |
| Field-only properties; identity keys without CLR getter/setter | Rejected |
| Owned or complex types | Rejected |
| TPH/TPT inheritance, entity splitting, or table splitting | Rejected |
| Store-generated UUID/default/HiLo keys | Rejected |
| Composite store-generated keys | Not supported |
| Descending or cyclic identity key generation | Rejected |
| Missing or incompatible identity/serial backing sequence | Rejected before generated-key COPY |
| Non-primary-key identity/serial columns | Rejected |
| Entity with no insertable CLR-backed columns | Rejected |
| Graph insert, update, delete, or upsert | Not supported |
| Providers other than PostgreSQL/Npgsql | Not supported |

For a CLR-backed non-key property, PgYeet always writes the current property value. EF Core's
`SaveChanges` sentinel behavior does not apply, so `HasDefaultValue` and `HasDefaultValueSql` do not
replace an unset CLR value. Server-computed columns are different: PgYeet omits them.

Generated-key correlation requires a normal positive-increment, non-cyclic PostgreSQL identity/serial
sequence. PgYeet checks the EF mapping and, before starting COPY, queries the PostgreSQL catalogs to
verify the actual backing sequence. A missing sequence, non-positive increment, or cyclic sequence is
rejected before rows are copied. This runtime check catches schema drift such as an out-of-band
`ALTER SEQUENCE`.

Triggers or rules may enforce constraints, but they must not replace or reorder generated key values.
In particular, identity-rewriting `BEFORE INSERT` triggers are unsupported. PgYeet detects a trigger
that filters or multiplies rows when the returned key count changes; arbitrary same-count key rewriting
cannot be correlated safely and remains outside the contract.

PostgreSQL does not permit `COPY FROM` into a table with row-level security enabled for an ordinary
non-bypass role, so the direct path is unavailable for such a table. The generated-key path copies into
a temporary table and then executes `INSERT` against the target; normal target-table RLS policies still
decide whether that `INSERT` is allowed.

See [Supported mappings and limitations](docs/USAGE.md#supported-mappings-and-limitations) for the
normative details.

## How it works

1. PgYeet builds a column map from the runtime EF `IModel` and caches it per model and entity type.
2. Each non-converted scalar property gets a typed writer paired with Npgsql's generic binary writer;
   PgYeet does not box those values itself.
3. The direct path writes rows to the target table with one binary `COPY`.
4. The generated-key path validates the real backing sequence, copies rows and an ordinal to a temporary
   table, inserts them into the target, reads `RETURNING` values, and assigns the keys to the original
   objects.
5. PostgreSQL constraints, permissions, triggers, and the path-specific RLS behavior above remain in force.

## Performance

PgYeet is designed to avoid EF change-tracker work and per-row SQL commands, but performance depends on
the schema, row width, converters, indexes, triggers, network, PostgreSQL configuration, runtime, and
batch size. The README deliberately makes no universal speed or allocation guarantee.

The repository contains a reproducible BenchmarkDotNet harness and a clearly labeled historical
baseline. See [Benchmarks and methodology](docs/BENCHMARKS.md) before using any number in a decision or
comparison.

```bash
docker compose up -d
dotnet run -c Release --project Bench
```

> The benchmark truncates its `users` table between iterations. Run it only against the throwaway
> database configured by the repository's Docker Compose file.

## Build and test

```bash
dotnet restore PgYeet.sln --locked-mode -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror
dotnet build PgYeet.sln --no-restore --configuration Release -warnaserror
dotnet format whitespace PgYeet.sln --no-restore --verify-no-changes
dotnet test PgYeet.Tests/PgYeet.Tests.csproj --no-build --configuration Release
```

The integration suite uses Testcontainers and a real PostgreSQL instance, so Docker is required. It
covers generated-key write-back, direct streaming, caller-assigned keys, representative scalar and
converted mappings, transaction commit/rollback, persistence from a fresh connection, large batches,
special characters, and fail-fast mapping guards. It does not claim exhaustive coverage of every
PostgreSQL or Npgsql data type.

See [CONTRIBUTING.md](CONTRIBUTING.md) for the development workflow and
[docs/RELEASING.md](docs/RELEASING.md) for the maintainer release checklist.

## Repository layout

| Path | Purpose |
| --- | --- |
| `PgYeet/` | Library and NuGet package |
| `PgYeet.Tests/` | xUnit integration and guard tests |
| `Bench/` | BenchmarkDotNet harness |
| `docs/` | Usage, benchmark, and release documentation |
| `.github/workflows/` | CI and trusted NuGet publishing |

## Support and security

For a reproducible bug, [open an issue](https://github.com/jecacs/PgYeet/issues/new/choose) with a
minimal sample and the relevant PgYeet, .NET, EF Core, Npgsql, and PostgreSQL versions. The project
does not promise a response-time SLA.

PgYeet treats the EF model and database connection as trusted application configuration. Entity values
are sent through Npgsql's binary protocol rather than interpolated into SQL. Target-line support and
private vulnerability reporting are documented in [SECURITY.md](SECURITY.md); do not report a suspected
vulnerability in a public issue.

## Scope

PgYeet intentionally implements one operation for one provider: PostgreSQL bulk insert. It is not a
drop-in replacement for libraries that provide update, delete, upsert, synchronization, graph
persistence, or multiple database providers.

## License

[MIT](LICENSE).
