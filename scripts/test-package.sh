#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
package_path="$(realpath "${1:?Usage: test-package.sh path/to/PgYeet.version.nupkg}")"
package_version="$(dotnet msbuild PgYeet/PgYeet.csproj -getProperty:Version -nologo)"
smoke_project=samples/PgYeet.PackageSmoke/PgYeet.PackageSmoke.csproj
smoke_directory="$(mktemp -d "${TMPDIR:-/tmp}/pgyeet-smoke.XXXXXX")"
container_id=""
cleanup() {
  if [[ -n "${container_id}" ]]; then docker rm --force "${container_id}" >/dev/null; fi
  rm -rf -- "${smoke_directory}"
}
trap cleanup EXIT

export PGYEET_LOCAL_FEED
PGYEET_LOCAL_FEED="$(dirname "${package_path}")"
export NUGET_PACKAGES="${smoke_directory}/packages"
lock_path="${smoke_directory}/packages.lock.json"
package_sha512="$(openssl dgst -sha512 -binary "${package_path}" | openssl base64 -A)"
jq --exit-status --arg version "${package_version}" --arg hash "${package_sha512}" '
  .dependencies |= with_entries(
    if .value.PgYeet.requested != "[__PGYEET_VERSION__, __PGYEET_VERSION__]" or
       .value.PgYeet.resolved != "__PGYEET_VERSION__" or
       .value.PgYeet.contentHash != "__PGYEET_SHA512__"
    then error("Unexpected PgYeet lock template; review it before publishing.")
    else .value.PgYeet |= (.requested = "[\($version), \($version)]" |
                          .resolved = $version | .contentHash = $hash)
    end)
' samples/PgYeet.PackageSmoke/packages.packed.lock.template.json > "${lock_path}"
dotnet restore "${smoke_project}" --locked-mode \
  -p:UsePackedPgYeet=true -p:PgYeetPackageVersion="${package_version}" \
  -p:NuGetLockFilePath="${lock_path}" \
  --configfile samples/PgYeet.PackageSmoke/NuGet.packed.config \
  -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror
cmp "${package_path}" "${NUGET_PACKAGES}/pgyeet/${package_version}/pgyeet.${package_version}.nupkg"

postgres_image="${PGYEET_TEST_POSTGRES_IMAGE:-postgres:18.6-alpine3.24@sha256:d3e1620b530c944afa6e887d22eb899824da68e19c52024bf98f5220c88a65b2}"
container_id="$(docker run --detach --publish 127.0.0.1::5432 \
  --env POSTGRES_PASSWORD=pgyeet-smoke --env POSTGRES_DB=pgyeet_smoke "${postgres_image}")"
for attempt in {1..60}; do
  if docker exec "${container_id}" pg_isready --username postgres --dbname pgyeet_smoke >/dev/null; then break; fi
  if (( attempt == 60 )); then docker logs "${container_id}"; exit 1; fi
  sleep 1
done
port="$(docker port "${container_id}" 5432/tcp)"
export PGYEET_SMOKE_CONNECTION_STRING="Host=127.0.0.1;Port=${port##*:};Database=pgyeet_smoke;Username=postgres;Password=pgyeet-smoke"
for framework in net10.0; do
  dotnet build "${smoke_project}" --framework "${framework}" -c Release --no-restore \
    -p:UsePackedPgYeet=true -p:PgYeetPackageVersion="${package_version}" \
    -p:NuGetLockFilePath="${lock_path}" -warnaserror
  dotnet "samples/PgYeet.PackageSmoke/bin/Release/${framework}/PgYeet.PackageSmoke.dll"
done
