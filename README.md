# PgYeet

[![NuGet](https://img.shields.io/nuget/v/PgYeet.svg?logo=nuget)](https://www.nuget.org/packages/PgYeet)
[![Downloads](https://img.shields.io/nuget/dt/PgYeet.svg?logo=nuget)](https://www.nuget.org/packages/PgYeet)
[![build](https://github.com/jecacs/PgYeet/actions/workflows/ci.yml/badge.svg)](https://github.com/jecacs/PgYeet/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A free, **MIT-licensed** bulk `INSERT` for **EF Core on PostgreSQL**, built on Npgsql binary `COPY`.
A lightweight alternative to the (commercially licensed) EFCore.BulkExtensions for the most common
operation: **~10–13× faster than `SaveChanges`**, near-zero allocations, no licensing strings attached.

```csharp
await db.Users.YeetAsync(users);                             // COPY + writes generated keys back
await db.Users.YeetAsync(users, returnGeneratedKeys: false); // fastest: one COPY, no key write-back
```

## Why

EF Core's `Add` + `SaveChanges` issues batched `INSERT`s and tracks every entity — slow and
allocation-heavy for large writes. PgYeet streams rows straight into Postgres with binary `COPY`,
reading the column mapping from your existing EF model. No attributes, no extra configuration.

## Benchmarks

`User` entity (int identity PK + 4 scalar columns), MacBook Pro (Apple M1 Pro, 32 GB RAM),
PostgreSQL 18 running in Docker (local), .NET 8, BenchmarkDotNet (10 iterations). Mean time, lower is better; `EfCore_AddRange` = `AddRange` +
`SaveChangesAsync`.

| Method                            |    Rows | Mean        | Allocated |
|-----------------------------------|--------:|------------:|----------:|
| EF Core (`AddRange`+`SaveChanges`)|   1 000 |    68.0 ms  |   8.2 MB  |
| BulkExtensions (insert + keys)    |   1 000 |    25.7 ms  |   6.1 MB  |
| **PgYeet (insert + keys)**        |   1 000 |    12.9 ms  |   155 KB  |
| BulkExtensions (insert)           |   1 000 |     8.0 ms  |   258 KB  |
| **PgYeet (insert, no keys)**      |   1 000 |  **6.5 ms** |  **6 KB** |
| EF Core (`AddRange`+`SaveChanges`)|  10 000 |     402 ms  |    77 MB  |
| BulkExtensions (insert + keys)    |  10 000 |     170 ms  |    59 MB  |
| **PgYeet (insert + keys)**        |  10 000 |      56 ms  |   1.4 MB  |
| BulkExtensions (insert)           |  10 000 |      46 ms  |   2.1 MB  |
| **PgYeet (insert, no keys)**      |  10 000 |   **34 ms** | **12 KB** |
| EF Core (`AddRange`+`SaveChanges`)| 100 000 |   4 101 ms  |   756 MB  |
| BulkExtensions (insert + keys)    | 100 000 |   1 179 ms  |   586 MB  |
| **PgYeet (insert + keys)**        | 100 000 |     421 ms  |   9.3 MB  |
| BulkExtensions (insert)           | 100 000 |     311 ms  |    21 MB  |
| **PgYeet (insert, no keys)**      | 100 000 |  **296 ms** | **53 KB** |

At 100k rows PgYeet's fast path is **~14× faster than EF Core** and allocates **~14 000× less** memory.
Versus EFCore.BulkExtensions: a plain insert is roughly on par on time but allocates **~400× less**; with
key write-back PgYeet is **~2.8× faster** and **~60× lighter** (BulkExtensions' `SetOutputIdentity` loads
output entities). Reproduce — see [Running the benchmark](#running-the-benchmark).

## vs EFCore.BulkExtensions

[EFCore.BulkExtensions](https://github.com/borisdj/EFCore.BulkExtensions) is the go-to bulk library,
but it ships under a **dual license**: free only if you're under $1M annual revenue, a non-profit, or
building open source — otherwise a paid [commercial license](https://codis.tech/efcorebulk) is required.
PgYeet is **MIT**, with no such conditions.

PgYeet deliberately covers only the most common operation — **bulk insert on PostgreSQL** — and does it
with **zero per-row allocations** (BulkExtensions boxes every value). It is *not* a drop-in replacement
for the whole library.

On the insert path PgYeet is faster and far lighter: a plain insert allocates **~400× less** memory, and
with key write-back it's **~2.8× faster** and **~60× lighter** (BulkExtensions' `SetOutputIdentity` loads
output entities). Full numbers in [Benchmarks](#benchmarks) above.

|                      | PgYeet                       | EFCore.BulkExtensions                     |
|----------------------|------------------------------|-------------------------------------------|
| License              | MIT — free, no conditions    | Dual: free under $1M rev / OSS, else paid |
| Operations           | Insert                       | Insert / Update / Delete / Upsert / Read  |
| Providers            | PostgreSQL                   | SQL Server / PostgreSQL / MySQL / SQLite  |
| Per-row allocations  | ~none (zero-boxing writers)  | boxes every value                         |
| Footprint            | one small file set           | full-featured, battle-tested              |

**Use PgYeet** if you just need fast, free bulk inserts into PostgreSQL. **Use BulkExtensions** if you
need updates/deletes/upserts, other databases, or its broad type coverage.

## Install

From [NuGet](https://www.nuget.org/packages/PgYeet):

```bash
dotnet add package PgYeet
```

```xml
<PackageReference Include="PgYeet" Version="0.1.0" />
```

Requires EF Core 8 or 9 + Npgsql, on PostgreSQL. (Targets the EF Core 8 LTS line for the widest reach.)

## Usage

`YeetAsync` is an extension on `DbSet<T>`. It reads the table, columns, store types, value converters
and the identity key straight from your EF model — nothing to annotate.

```csharp
var users = new[]
{
    new User { Name = "Ada",   Email = "ada@example.com" },
    new User { Name = "Alan",  Email = "alan@example.com" },
};

// Bulk insert. If the entity has a store-generated (identity) PK, the generated
// keys are written back onto the entities.
await db.Users.YeetAsync(users);
// users[0].Id is now the DB-assigned value

// Don't need the keys back? Skip the write-back for a single, faster COPY:
await db.Users.YeetAsync(users, returnGeneratedKeys: false);
```

Participates in an ambient `DbContext` transaction if one is open; otherwise it manages its own.

## How it works

- The EF model is read **once per entity type** (cached): table name, column names, store types,
  value converters and the single identity PK.
- Each column gets a **compiled typed writer** — an open-instance delegate over the property getter
  paired with Npgsql's `Write<T>(value, dataTypeName)`. Value types are written **without boxing** on
  the hot path.
- Two execution paths:
  - **keys back** → `COPY` into a temp table, then `INSERT … SELECT … RETURNING`, and the generated
    keys are mapped back onto the entities (ordering is correlated via an ordinal, robust to
    out-of-order `RETURNING`).
  - **no keys** (`returnGeneratedKeys: false`) → a single direct `COPY` into the target table
    (Postgres generates the identity). Fastest path, ~zero per-row allocations.

## Limitations (v0.1)

- One entity type per call; a single-column store-generated (identity) PK for key write-back.
- Scalar properties backed by a CLR property. Not yet handled: shadow properties, owned types /
  table splitting, TPH inheritance (discriminator column).
- Columns with an EF **value converter** use a (correct) boxed write path rather than the zero-alloc one.
- PostgreSQL only — by design.

## Running the benchmark

```bash
docker compose up -d                      # local PostgreSQL
dotnet run -c Release --project Bench     # BenchmarkDotNet, ~1.5 min
```

The benchmark `TRUNCATE`s the `users` table between iterations — point it at a throwaway database.

## Tests

Integration tests run against a real PostgreSQL spun up via [Testcontainers](https://dotnet.testcontainers.org/) — Docker is the only prerequisite:

```bash
dotnet test
```

They cover key write-back, the no-keys fast path, app-assigned keys, all column types (incl. nullable and value-converter), empty input and ambient-transaction rollback. A second suite reads every result back through a fresh connection / raw SQL to prove persistence: key↔row correlation on 500 rows, a 10k-row batch with unique keys, sequential inserts without collisions, special-character round-tripping, committed ambient transactions and null handling.

## Repository layout

| Path        | What                                                          |
|-------------|--------------------------------------------------------------|
| `PgYeet/`   | The library.                                                 |
| `Bench/`    | BenchmarkDotNet comparison vs EF Core.                       |
| `PgYeet.Tests/` | Integration tests (xUnit + Testcontainers / real Postgres). |

## License

[MIT](LICENSE).
