# Contributing to PgYeet

Thank you for helping improve PgYeet. The project deliberately keeps a small public surface and a
narrow operational scope, so the best contributions are focused, tested, and explicit about behavior.

By participating, you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before opening a pull request

- Search existing issues and pull requests for the same problem.
- For a bug, include a minimal reproduction and the relevant .NET, EF Core, Npgsql, and PostgreSQL
  versions.
- Open an issue or discussion before implementing a new public API, provider, operation, or mapping
  category. Those changes affect the project's scope and compatibility contract.
- Report suspected vulnerabilities through [SECURITY.md](SECURITY.md), never through a public issue.

Small fixes, tests, and documentation corrections can go directly to a pull request.

## Development prerequisites

- .NET SDK 10.0.401, pinned by `global.json` with roll-forward disabled.
- Docker for the PostgreSQL integration tests.
- Git.
- Bash and `jq` for the release workflow tests; OpenSSL for the packed consumer smoke.

Check the selected SDK and installed runtimes:

```bash
dotnet --version
dotnet --list-runtimes
docker version
```

The test suite starts its own PostgreSQL 18 container with Testcontainers. The separate benchmark
harness uses the repository's `docker-compose.yml`.

To enable the repository's opt-in pre-push quality gate for this clone:

```bash
git config core.hooksPath .githooks
```

The hook runs restore/audit, build, formatting, PostgreSQL tests, package inspection, and the packed
consumer smoke. Set `PGYEET_PREPUSH_SKIP_TESTS=1` for an exceptional local push that must omit Docker
tests and packed execution, or `PGYEET_SKIP_PREPUSH=1` to skip the entire hook. These are local escape
hatches only; they do not bypass required GitHub checks and should not be used for a release tag.

## Build and test

From the repository root:

```bash
dotnet restore PgYeet.sln --locked-mode -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror
dotnet build PgYeet.sln --no-restore --configuration Release -warnaserror
dotnet format whitespace PgYeet.sln --no-restore --verify-no-changes
dotnet test PgYeet.Tests/PgYeet.Tests.csproj --no-build --configuration Release --verbosity normal
```

The library, tests, benchmarks, and package consumer target `net10.0` with EF Core/Npgsql 10.
The SDK is pinned to the current .NET 10 servicing release; update it and dependency locks together.

Docker must be running for the integration suite. Tests use throwaway containers, but they still require
enough local resources to pull and start PostgreSQL.

These are the repository's CI quality gates. Locked restore and NuGet audit failures are release
blockers; do not regenerate a lock file or suppress an advisory without reviewing the dependency
change. The package smoke test in CI additionally consumes the exact packed `.nupkg` from a
`net10.0` application, with its own locked dependency graph and real PostgreSQL inserts/rollback.
Run it locally with `bash scripts/test-package.sh artifacts/PgYeet.1.0.0.nupkg` after packing.

`ReleaseWorkflowTests` exercises staging and retry recovery with local command fixtures as part of
the normal xUnit run. It executes the release workflow's Bash steps without contacting GitHub or NuGet.

## Design and code guidelines

- Preserve the one-operation, one-provider scope unless a broader direction has been agreed first.
- Keep the supported public API centered on `DbSet<T>.YeetAsync`.
- Treat public signatures, parameter names, exceptions, and XML documentation as compatibility surface.
- Enable nullable annotations and avoid suppressing warnings without a documented reason.
- Pass cancellation tokens through asynchronous I/O. Cleanup that must run after cancellation may use a
  non-cancelled token.
- Prefer explicit validation and fail-fast errors over silently incomplete inserts.
- Quote PostgreSQL identifiers derived from EF metadata and never interpolate entity values into SQL.
- Respect the existing `DbContext` transaction; do not hide commit or rollback ownership.
- Do not add automatic retries to a non-idempotent operation without an explicit idempotency design.
- Keep hot-path changes allocation-conscious, but do not claim an optimization without measurement.
- Avoid unrelated cleanup in a focused fix.

PgYeet intentionally bypasses EF Core's change tracker and `SaveChanges` pipeline. Changes must preserve
the documented semantics or update the public contract and changelog accordingly.

## Tests

Every behavior change should have a test at the lowest useful level. Integration behavior must be
verified against real PostgreSQL, not an in-memory provider.

Depending on the change, cover:

- the generated-key and direct paths;
- the `net10.0` / EF Core 10 / Npgsql 10 provider line;
- success and rollback inside an ambient transaction;
- persistence observed from a fresh context or raw SQL;
- empty and lazy inputs;
- nullable and converted scalar values;
- caller-assigned and generated keys;
- unsupported mappings and useful exception messages;
- cancellation and cleanup;
- trigger, constraint, or permission behavior when relevant.

Do not describe a small representative type set as “all PostgreSQL types.” New type-support claims need
targeted tests for the exact Npgsql mappings being promised.

## Documentation

Update documentation in the same pull request when behavior, compatibility, packaging, or operational
requirements change:

- `README.md` for the GitHub overview;
- `README.nuget.md` for package consumers;
- `docs/USAGE.md` for normative behavior and examples;
- `docs/BENCHMARKS.md` for methodology or measured results;
- `CHANGELOG.md` under `Unreleased`;
- public XML comments for API behavior.

Code snippets must compile against the public package surface. Keep GitHub-relative links out of
`README.nuget.md`; use absolute repository URLs there because NuGet renders the file outside GitHub.

## Benchmarks

Benchmarks are evidence, not a test gate or a marketing guarantee. Run them only for performance-sensitive
changes or when refreshing published evidence:

```bash
docker compose up -d
dotnet run -c Release --project Bench
```

The harness truncates its `users` table between iterations. Use only the disposable database configured
by `docker-compose.yml`.

When reporting results, include the commit, date, hardware, OS, .NET runtime, PostgreSQL version,
dependency versions, job settings, full result table, and run-to-run variability. Never replace a
historical result with an unlabeled number from a different environment.

## Pull request checklist

- [ ] The change is focused and its motivation is explained.
- [ ] Release builds succeed for all target frameworks.
- [ ] Tests pass with Docker running.
- [ ] New behavior and failure modes are covered.
- [ ] Public API changes are intentional and reviewed.
- [ ] XML docs and user documentation are current.
- [ ] `CHANGELOG.md` includes a concise `Unreleased` entry.
- [ ] No credentials, connection strings, generated packages, IDE state, or benchmark scratch output
      were committed.
- [ ] Performance claims include reproducible evidence.

## Commit and review expectations

Use clear, imperative commit subjects. A pull request may be asked to split unrelated work, add tests,
clarify a support boundary, or preserve compatibility. Reviews focus on correctness, database safety,
public-contract clarity, and maintainability rather than cleverness.

Maintainers may edit or squash commits when merging. Keep the branch available until the pull request is
fully merged.

## Licensing

By submitting a contribution, you agree that it may be distributed under the repository's
[MIT License](LICENSE).
