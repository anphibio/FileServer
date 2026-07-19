#!/usr/bin/env bash

api_auth_repo_root() {
  if [[ -f "${PWD}/.env" ]]; then
    printf '%s\n' "${PWD}"
    return
  fi
  cd "$(dirname "${BASH_SOURCE[0]}")/.." >/dev/null 2>&1
  pwd
}

read_api_auth_env() {
  local name="$1"
  local value="${2:-}"

  local env_file="$(api_auth_repo_root)/.env"
  if [[ -z "${value}" && -f "${env_file}" ]]; then
    value="$(awk -v key="${name}" 'index($0, key "=") == 1 { print substr($0, length(key) + 2); exit }' "${env_file}")"
  fi

  printf '%s' "${value}"
}

require_human_api_key() {
  local value
  value="$(read_api_auth_env FILESERVER_MONITOR_API_KEY "${FILESERVER_MONITOR_API_KEY:-${API_KEY:-}}")"
  if [[ -z "${value}" ]]; then
    echo "Defina FILESERVER_MONITOR_API_KEY no ambiente ou no arquivo .env." >&2
    return 1
  fi
  printf '%s' "${value}"
}

require_agent_api_key() {
  local value
  value="$(read_api_auth_env FILESERVER_MONITOR_AGENT_API_KEY "${FILESERVER_MONITOR_AGENT_API_KEY:-${AGENT_API_KEY:-}}")"
  if [[ -z "${value}" ]]; then
    echo "Defina FILESERVER_MONITOR_AGENT_API_KEY no ambiente ou no arquivo .env." >&2
    return 1
  fi
  printf '%s' "${value}"
}
