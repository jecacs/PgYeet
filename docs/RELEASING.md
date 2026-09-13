# Releasing PgYeet

This is the maintainer checklist for producing an immutable NuGet release. It complements the public
[changelog](../CHANGELOG.md), [security policy](../SECURITY.md), and
[benchmark policy](BENCHMARKS.md).

The canonical release path is a version tag consumed by the repository's trusted-publishing workflow.
No long-lived NuGet API key belongs in the repository or a local script.

The [PgYeet 1.0.0 release notes](../CHANGELOG.md) are still being prepared. Set their actual release
date and change the README status when creating the release commit.

## Version policy

PgYeet follows Semantic Versioning from 1.0.0:

- **patch**: backward-compatible fixes and documentation corrections;
- **minor**: backward-compatible functionality;
- **major**: breaking changes to supported public API or documented behavior.

Public compatibility includes more than method signatures. Parameter names, target frameworks,
dependency major lines, documented exceptions, transaction semantics, key write-back behavior, package
contents, and supported mappings can all affect consumers.

The supported public API is centered on `DbSet<T>.YeetAsync`. Low-level COPY implementation types are
not public contract.

NuGet versions are immutable. Never reuse a published version, move its tag, or attempt to replace its
package contents.

## 1. Choose and synchronize the version

For a release such as `1.0.0`, align:

- the library project `Version`;
- package release notes;
- `README.md` installation examples and status;
- `README.nuget.md` installation examples;
- the dated section in `CHANGELOG.md`;
- the Git tag `v1.0.0`.

Keep an empty `[Unreleased]` section at the top of the changelog for subsequent work. The release date is
the date the tag is created, not the date drafting began.

Search for stale versions and pre-release language:

```bash
rg -n "0[.]1[.]0|0[.]2[.]|v0[.]|Unreleased|preview|prerelease" \
  README.md README.nuget.md CHANGELOG.md docs PgYeet
```

Review every result rather than performing a blind replacement. Historical changelog and benchmark
entries should keep their original versions.

## 2. Confirm compatibility and scope

Verify the package matrix:

| Target framework | EF Core | Npgsql EF Core provider |
| --- | --- | --- |
| `net10.0` | 10.x | 10.x |

The package must contain only the `net10.0` asset and its EF Core/Npgsql 10 dependency group.
Do not publish if an older framework or a different provider major appears in the package.

Before a major release, inspect the compiled public API and decide every exposed symbol intentionally.
Before a patch or minor release, compare it with the previous stable package and resolve every package
validation warning.

Update the support matrix, usage guide, XML docs, and changelog together if compatibility changes.

## 3. Prepare a clean release candidate

Start from the intended main-branch commit:

```bash
git status --short
git branch --show-current
git log -1 --oneline
git fetch origin
```

Requirements:

- the worktree is clean;
- the release commit is on the intended branch;
- local main is not missing reviewed remote commits;
- no generated package, IDE state, database credentials, or benchmark scratch directory is tracked;
- the release notes describe only shipped behavior.

Do not release from an unreviewed local-only commit.

## 4. Restore, format, build, and test

Confirm the SDK selected by `global.json`, the .NET 10 runtime, and Docker:

```bash
dotnet --version
dotnet --list-runtimes
docker version
```

`global.json` pins SDK 10.0.401 with roll-forward disabled, including runtime 10.0.12. The library,
tests, benchmarks, and packed consumer all target `net10.0` with EF Core/Npgsql 10.

Run the exact quality gates used by CI:

```bash
dotnet restore PgYeet.sln --locked-mode -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror
dotnet build PgYeet.sln --no-restore --configuration Release -warnaserror
dotnet format whitespace PgYeet.sln --no-restore --verify-no-changes
dotnet test PgYeet.Tests/PgYeet.Tests.csproj --no-build --configuration Release --verbosity normal
```

The test run must exercise the .NET 10 provider and start PostgreSQL successfully. A
green compile without the Testcontainers integration suite is not a complete release gate.

Investigate warnings; do not suppress a new warning merely to unblock publishing.

## 5. Decide whether benchmark evidence needs refresh

A benchmark refresh is required only when the release makes or advertises a performance-sensitive
change, changes a supported runtime/provider major, or promotes an older number as current evidence.

If required:

```bash
docker compose up -d
dotnet run --configuration Release --project Bench
```

Follow [BENCHMARKS.md](BENCHMARKS.md): record the commit, complete environment, full result table, and
variability. The harness truncates its target table. Never use a shared database.

Do not delay a correctness or security patch merely to produce a marketing benchmark. Remove or label
stale claims instead.

## 6. Pack exactly what will be published

Use a dedicated output directory:

```bash
dotnet pack PgYeet/PgYeet.csproj \
  --configuration Release \
  --no-build \
  --no-restore \
  -p:ContinuousIntegrationBuild=true \
  --output artifacts
```

Expected outputs:

- `PgYeet.1.0.0.nupkg`;
- `PgYeet.1.0.0.snupkg`.

Package validation must pass. Do not ignore an API, dependency, framework, or package-layout diagnostic
without documenting why it is intentional.

## 7. Inspect package contents

List both archives:

```bash
unzip -l artifacts/PgYeet.1.0.0.nupkg
unzip -l artifacts/PgYeet.1.0.0.snupkg
```

The main package must contain:

- `lib/net10.0/PgYeet.dll` and `PgYeet.xml`;
- `README.nuget.md` at the package root;
- `icon.png`;
- correct license expression;
- repository URL and exact source commit;
- dependencies aligned to the matching target-framework major;
- version-specific release notes.

The symbol package must contain a portable PDB with SourceLink information for `net10.0`.

Inspect generated metadata and the rendered README:

```bash
unzip -p artifacts/PgYeet.1.0.0.nupkg PgYeet.nuspec
unzip -p artifacts/PgYeet.1.0.0.nupkg README.nuget.md
```

Confirm that the package exposes only the intended public API. In particular, internal COPY and mapping
helpers must not appear as supported public types.

## 8. Smoke-test the packed package

Do not rely only on project-reference tests. Consume the generated `.nupkg` from a minimal application
for each supported target and verify that:

- restore chooses the matching package asset and provider major;
- `using PgYeet;` resolves;
- `DbSet<T>.YeetAsync` compiles with default arguments and cancellation;
- XML documentation appears in the IDE/compiler tooling;
- no project-only dependency is required at runtime.

Run the shared CI/pre-push smoke script; it restores into a fresh cache, checks the exact nupkg
bytes, starts its own PostgreSQL container, and verifies inserts, generated keys, nullable values,
and transaction rollback on .NET 10:

```bash
bash scripts/test-package.sh artifacts/PgYeet.1.0.0.nupkg
```

The script uses `packages.packed.lock.template.json` for the consumer's complete remote dependency
graph. The smoke script uses `jq` to replace only the local PgYeet version and SHA-512 hash; restore
then runs in locked mode. Dependency changes require updating both the ordinary smoke lock and the
packed lock template deliberately. Do not disable locked mode to fix drift.

The smoke database and cache are disposable and cleaned up by the script. No production credentials
are needed.

## 9. Create and push the tag

After the release commit is reviewed, merged, and all gates pass:

```bash
git tag -a v1.0.0 -m "PgYeet 1.0.0"
git show v1.0.0 --stat
git push origin v1.0.0
```

The tag version, project version, changelog, package metadata, and generated filenames must agree
exactly.

Pushing the tag is the publish trigger. Do not push a test tag that matches the release pattern.

## 10. Monitor trusted publishing

Watch the complete release workflow:

1. validate that the tag exactly matches the project version and its commit is contained in `main`;
2. perform locked restore with NuGet audit, warning-free build, formatting check, and PostgreSQL tests;
3. pack and validate one main package and one symbol package, then record both SHA-256 digests;
4. restore the exact packed package and run its smoke application on .NET 10;
5. create and independently verify a GitHub build-provenance attestation covering the package and symbols;
6. stage the artifacts in a durable draft GitHub Release and download/verify them again; on rerun,
   recover the already-staged bytes instead of adopting a newly packed candidate;
7. transfer the canonical artifacts into the privileged publishing job, which has no source checkout
   and verifies both digests and attestations again;
8. authenticate to NuGet with OIDC and push the exact package, allowing duplicate-version responses
   only because the subsequent signature check proves which bytes NuGet accepted;
9. download the NuGet package, verify its signature, and compare the signed original-archive hash
   with the staged package (NuGet repository signing changes the downloaded archive bytes);
10. verify the staged release by exact release ID and compare every remote asset before making that
    draft public. An already-public matching release is accepted on retry.

A job that reached “push” but then failed may have published an immutable package. Complete staged
releases are recoverable by rerunning the workflow for the same tag. Never delete a complete staged
release: it preserves the exact original bytes and attestation needed for recovery.

The workflow stops on partial drafts, duplicate same-tag releases, unexpected ownership/metadata,
failed attestations, or mismatched assets. It does not overwrite or delete them. If the initial upload
was interrupted, inspect the reported release ID and verify that NuGet has no package for this version
before deliberately removing only that incomplete draft and rerunning. If NuGet already has the version
and there is no complete verified canonical release, investigate manually; do not stage replacement bytes.

Never paste an OIDC token, temporary API key, or workflow output containing credentials into an issue,
log excerpt, or chat.

## 11. Verify the public release

After NuGet indexing completes, verify:

- [the package page](https://www.nuget.org/packages/PgYeet) shows the intended version;
- the dedicated NuGet README renders correctly;
- title, description, icon, license, repository, tags, and release notes are correct;
- the .NET 10 dependency group is correct;
- the symbol package was accepted;
- source navigation resolves to the tagged commit;
- `dotnet add package PgYeet --version 1.0.0` restores from nuget.org;
- the GitHub tag points to the reviewed release commit;
- the automatically created GitHub Release contains the package, symbols, both checksum files, and
  Sigstore bundle;
- generated GitHub release notes do not contradict the curated changelog.

Do not create a second release manually after a successful workflow. If automatic GitHub Release
creation failed after NuGet publishing, diagnose that job and repair the release for the existing,
immutable tag; do not push a replacement tag or version.

## 12. If a release is bad

Do not overwrite the package or move the published tag.

- For an ordinary defect, fix forward with the next patch release.
- For a severe package issue, deprecate or unlist the affected NuGet version, explain the replacement,
  and publish a corrected version.
- For a vulnerability, follow [SECURITY.md](../SECURITY.md), coordinate disclosure, and publish a patch.
- If publishing failed before any package reached NuGet, diagnose the workflow and rerun only after
  confirming the version is still unused.

Document the incident and corrective release in `CHANGELOG.md`.

## 13. Open the next development cycle

After verification:

- leave `[Unreleased]` ready for new entries;
- set the next development version only if the repository's versioning policy requires it;
- update the package-validation baseline to the newly published stable version when appropriate;
- capture follow-up work as issues rather than amending the released tag.

The release is complete only when a clean consumer can restore the package, inspect its documentation,
and use the intended public API from nuget.org.
