#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESOLVED="$ROOT/.cache/environment.resolved.env"
[ -s "$RESOLVED" ] || { echo "Resolved environment not found. Run ./eng/setup-environment.sh first: $RESOLVED" >&2; return 1 2>/dev/null || exit 1; }
# shellcheck disable=SC1090
source "$RESOLVED"
export DOTNET_ROOT NUGET_PACKAGES DOTNET_CLI_HOME DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 NUGET_XMLDOC_MODE=skip
prepend(){ case ":${!1:-}:" in *":$2:"*) ;; *) export "$1=$2${!1:+:${!1}}";; esac; }
prepend PATH "$DOTNET_ROOT"; prepend PATH "$POWERSHELL_ROOT"; prepend PSModulePath "$PSMODULE_ROOT"
