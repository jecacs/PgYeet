# Changelog

All notable changes to PgYeet are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/). Starting with 1.0.0, breaking changes to the supported
public API require a new major version.

## [Unreleased]

## [2.0.0] - 2026-11-22

### Changed

- **Breaking:** target `net11.0` exclusively. Consumers must upgrade from .NET 10 to .NET 11.
- **Breaking:** require EF Core Relational, Npgsql, and the Npgsql EF Core provider in the
  `[11.0.0,12.0.0)` range.
- Pin SDK 11.0.100 with roll-forward disabled and runtime 11.0.0. Align the test suite, benchmark
  harness, package consumer, and automation with the .NET 11 provider line.
- Adapt the benchmark harness to .NET 11 with BenchmarkDotNet `0.16.0-preview.2` and remove
  EFCore.BulkExtensions `10.0.1` comparator cases that fail against EF Core 11.
- Update installation, compatibility, development, and release guidance for the 2.0.0 package.

## [1.0.0] - 2026-09-13

### Added

- Durable draft-release staging and recovery of the exact attested package and symbols before NuGet
  publishing; retries verify NuGet's signed original-package digest before completing the release.
- Locked package-consumer dependency graphs and real PostgreSQL smoke tests on .NET 10.
- Offline regression checks for release staging, recovery, tamper rejection, and publication.

- A .NET 10-only package on SDK 10.0.401 / runtime 10.0.12, EF Core 10.0.12, and Npgsql/provider 10.0.3.
- Dedicated GitHub, NuGet, usage, benchmark, contribution, release, conduct, changelog, and security
  documentation.
- Complete package metadata, release notes, locked dependencies, source symbols, package-content
  validation, checksums, and build provenance attestation.
- Explicit compatibility, transaction, change-tracking, mapping, and support policies.
- Public-surface tests that lock the 1.0 API to `PgYeetExtensions.YeetAsync` and its parameter names.
- Repository security automation for dependency updates, static analysis, dependency review, and
  supply-chain scorecards.

### Changed

- Declared `DbSet<T>.YeetAsync` stable under semantic versioning.
- Narrowed the supported public API to `YeetAsync`; low-level binary COPY machinery is an implementation
  detail.
- Replaced unqualified performance and competitor claims with reproducible methodology and a clearly
  labeled historical baseline.
- Targeted `net10.0` exclusively and aligned EF Core/Npgsql dependencies to 10.x, bounding each
  dependency range before the next incompatible major.
- Made generated-key input validation reject null elements and repeated object references before
  database I/O.
- Added fail-fast guards for store-generated composite keys, non-key identity columns, entities with no
  insertable CLR-backed columns, TPC inheritance, and descending or cyclic identity mappings.
- Added a PostgreSQL catalog preflight that verifies the actual identity/serial backing sequence exists,
  has a positive increment, and is non-cyclic before COPY starts.

### Fixed

- Generated-key failures inside caller-owned transactions now roll back their own savepoint, retaining
  earlier work. Successful calls drop staging tables immediately instead of accumulating them until commit.
- User columns named `__ord` no longer collide with the staging ordinal.
- Field-only values can no longer be silently omitted; unreadable/unwritable identity keys and required
  shadow properties without a database default fail before database I/O.
- Generated-key input references are snapshotted even for mutable lists, with cancellation checks during
  buffering. Already-cancelled operations stop before enumerating even empty inputs.

- Failed PgYeet-owned transactions now attempt to restore the original CLR key values before rethrowing.
- Cleanup failures are preserved on the primary exception instead of hiding the database or
  cancellation failure.
- Direct COPY row counting is bounded by the public `Int32` result contract; an over-limit stream aborts
  the in-progress COPY instead of committing a partial insert.
- Direct COPY reports PostgreSQL's actual inserted-row count when a `BEFORE INSERT` trigger filters rows.

## [0.2.1] - 2026-07-02

### Added

- Package icon and repository logo.
- NuGet title and expanded discovery tags.

## [0.2.0] - 2026-07-02

### Added

- Fail-fast validation for TPH, owned and complex types, split or multi-table mappings, and required
  shadow properties.
- Explicit validation of PostgreSQL identity/serial keys versus unsupported store-generated
  UUID/default/HiLo keys.
- Client-generated `Guid` key support and converted, strongly typed identity key write-back.
- Streaming of lazy enumerables on the direct no-key path.
- Detection of row-filtering or row-multiplying triggers during key write-back.

### Changed

- The mapping cache is keyed by the runtime EF model and entity type.
- `YeetAsync` parameter order changed to `(entities, returnGeneratedKeys, ct)`.
- Rollback no longer uses an already-cancelled operation token.

### Fixed

- Non-Npgsql providers and incompatible ambient transactions now fail with explicit errors.
- Returned key counts and inserted row-count conversions are validated.

## [0.1.0] - 2026-06-15

Initial release.

### Added

- `DbSet<T>.YeetAsync` bulk insert through Npgsql binary COPY.
- Direct COPY and generated identity-key write-back paths.
- EF model mapping, nullable scalar values, value converters, transaction participation, integration
  tests, and the BenchmarkDotNet harness.
- NuGet trusted publishing workflow.

[Unreleased]: https://github.com/jecacs/PgYeet/compare/v2.0.0...HEAD
[2.0.0]: https://github.com/jecacs/PgYeet/compare/v1.0.0...v2.0.0
[1.0.0]: https://github.com/jecacs/PgYeet/compare/v0.2.1...v1.0.0
[0.2.1]: https://github.com/jecacs/PgYeet/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/jecacs/PgYeet/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/jecacs/PgYeet/releases/tag/v0.1.0
