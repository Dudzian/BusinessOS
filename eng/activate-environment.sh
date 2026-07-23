#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
json_value(){ python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))'"$2"')' "$ROOT/eng/environment.lock.json"; }
DOTNET_REL=$(json_value x "['dotnetRoot']"); NUGET_REL=$(json_value x "['nugetCache']"); PS_REL=$(json_value x "['powershellRoot']")
export DOTNET_ROOT="$ROOT/$DOTNET_REL"
export NUGET_PACKAGES="$ROOT/$NUGET_REL"
export DOTNET_CLI_HOME="$ROOT/.cache/dotnet-home"
export DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 NUGET_XMLDOC_MODE=skip
prepend_path(){ case ":$PATH:" in *":$1:"*) ;; *) PATH="$1:$PATH";; esac; }
prepend_path "$DOTNET_ROOT"; prepend_path "$ROOT/$PS_REL"; export PATH
