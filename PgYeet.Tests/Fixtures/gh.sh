#!/usr/bin/env bash
set -euo pipefail
args=("$@")
state="${FAKE_RELEASE_ROOT}/state.json"
argument() {
  for ((i=0; i<${#args[@]}-1; i++)); do
    if [[ "${args[i]}" == "$1" ]]; then printf '%s' "${args[i+1]}"; return; fi
  done
  return 1
}
field() {
  for value in "${args[@]}"; do
    if [[ "${value}" == "$1="* ]]; then printf '%s' "${value#*=}"; return; fi
  done
  return 1
}
update() { jq "$@" "${state}" > "${state}.next"; mv "${state}.next" "${state}"; }

if [[ "$1" == attestation && "$2" == verify ]]; then
  [[ "$(argument --repo)" == "${GITHUB_REPOSITORY}" ]]
  [[ "$(argument --source-ref)" == "${GITHUB_REF}" ]]
  [[ "$(argument --source-digest)" == "${GITHUB_SHA}" ]]
  [[ "$(argument --signer-workflow)" == "${GITHUB_REPOSITORY}/.github/workflows/release.yml" ]]
  [[ " ${args[*]} " == *' --deny-self-hosted-runners '* ]]
  digest="$(sha256sum "$3" | cut -d ' ' -f 1)"
  jq --exit-status --arg name "${3##*/}" --arg digest "${digest}" \
    '.[$name] == $digest' "$(argument --bundle)" >/dev/null
  exit
fi

[[ "$1" == api ]]
endpoint=""
for value in "${args[@]}"; do
  if [[ "${value}" == repos/* || "${value}" == https://* ]]; then endpoint="${value}"; break; fi
done
method="$(argument --method || printf GET)"
if [[ " ${args[*]} " == *' --paginate '* ]]; then
  jq '[.releases]' "${state}"
elif [[ "${endpoint}" == */releases/assets/* ]]; then
  cat "${FAKE_RELEASE_ROOT}/${endpoint##*/}"
elif [[ "${method}" == POST && "${endpoint}" == */releases ]]; then
  update --arg tag "$(field tag_name)" --arg sha "$(field target_commitish)" \
    --arg name "$(field name)" --argjson draft "$(field draft)" \
    --argjson prerelease "$(field prerelease)" '
    .releases += [{id: 101, tag_name: $tag, target_commitish: $sha, name: $name,
      draft: $draft, prerelease: $prerelease, author: {login: "github-actions[bot]"}, assets: []}]'
  jq '.releases[-1]' "${state}"
elif [[ "${method}" == POST && "${endpoint}" == */assets\?name=* ]]; then
  [[ "${endpoint}" == https://uploads.github.com/repos/* ]]
  name="${endpoint##*?name=}"
  asset_id="$(jq '.releases[0].assets | length + 201' "${state}")"
  cp "$(argument --input)" "${FAKE_RELEASE_ROOT}/${asset_id}"
  update --arg name "${name}" --argjson id "${asset_id}" \
    '.releases[0].assets += [{id: $id, name: $name, state: "uploaded"}]'
  printf '{}'
elif [[ "${method}" == PATCH ]]; then
  [[ "${endpoint}" == */101 ]]
  update --argjson draft "$(field draft)" '.releases[0].draft = $draft'
  jq '.releases[0]' "${state}"
else
  jq --exit-status --argjson id "${endpoint##*/}" '.releases[] | select(.id == $id)' "${state}"
fi
