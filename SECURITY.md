# Security Policy

## Supported versions

| Version | Security fixes |
| --- | --- |
| 1.x | Yes |
| < 1.0 | No |

Security fixes are released for the latest stable 1.x version. Before reporting an issue, update to
the latest patch release when practical and check the [changelog](CHANGELOG.md).

Each target-framework/provider line receives PgYeet security fixes only while its corresponding .NET,
EF Core, and Npgsql upstream line remains supported. Consumers on an upstream end-of-life line must
upgrade to a supported line to receive further fixes. Removing a target framework from PgYeet 1.x is a
documented compatibility change and, under the current policy, requires a new major PgYeet version.

## Reporting a vulnerability

**Do not open a public issue for a suspected vulnerability.**

Use GitHub's private vulnerability reporting:
**[Security → Report a vulnerability](https://github.com/jecacs/PgYeet/security/advisories/new)**.

If private reporting is unavailable, contact the maintainer through the **Contact owners** link on the
[NuGet package page](https://www.nuget.org/packages/PgYeet). Do not send production credentials,
connection strings, or customer data.

A useful report includes:

- the affected PgYeet, .NET, EF Core, Npgsql, and PostgreSQL versions;
- the relevant EF mapping and selected `YeetAsync` path;
- a minimal reproduction using synthetic data;
- the expected and observed behavior;
- an assessment of confidentiality, integrity, or availability impact.

The maintainer will coordinate validation, remediation, release, and disclosure through the private
report. Please allow time for a fix and patched release before public disclosure.

## Security boundaries

PgYeet treats the EF model and Npgsql connection as trusted application configuration. Schema, table,
and column identifiers derived by `YeetAsync` are quoted. Entity values are transmitted through
PostgreSQL binary COPY rather than interpolated into SQL.

PgYeet does not own or persist database credentials. Connection-string storage, authentication,
credential rotation, TLS configuration, and host verification remain the application's responsibility.

PgYeet's own debug messages contain the generated COPY command shape and row counts, not entity values.
Applications must review their EF Core, Npgsql, database, and hosting log configuration separately;
those layers may have different logging behavior.

PostgreSQL permissions, constraints, row-level security, and triggers remain authoritative. PostgreSQL
rejects direct `COPY FROM` into an RLS-enabled table for an ordinary non-bypass role. PgYeet's
generated-key path uses a temporary-table `COPY` followed by target-table `INSERT`, so the target's
normal RLS policy applies to that insert. Do not grant `BYPASSRLS` merely to make the direct path work.

Grant the application role only the privileges required by the chosen path:

- target-table insert privileges;
- identity/sequence privileges when the table requires them;
- database temporary-table privileges for generated-key write-back.

The EF model is executable application configuration. Do not derive model metadata such as store types
from untrusted tenant input. Dynamic schema or table names are quoted, but untrusted parties must not be
allowed to redefine the model itself.

PgYeet does not add an automatic retry layer. After an unknown network or server outcome, a blind retry
may duplicate a non-idempotent insert. Applications that retry must establish their own idempotency
strategy.

## Audit and dependencies

PgYeet has not undergone an independent security audit. Reports involving an EF Core, Npgsql, or other
transitive dependency are still welcome so the affected PgYeet versions can be evaluated and dependency
updates coordinated.

Release publishing uses [NuGet trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
with OIDC. Build/test jobs have no publishing credentials. Separate checkout-free jobs attest both
the package and symbols, preserve them in a draft GitHub Release, and publish those exact artifacts.
The NuGet job verifies the signature on the downloaded package and its signed original-archive hash
before the GitHub Release becomes public. Retry recovery never replaces a complete staged release.

Solution dependencies and both ordinary and packed-consumer smoke graphs are locked. CI audits direct
and transitive dependencies, rejects vulnerable packages at low severity or higher, and exercises the
packed package against PostgreSQL before attestation. CodeQL and dependency review provide additional
checks; these gates are not a substitute for an independent security audit.
