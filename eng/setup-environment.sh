#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"; LOCK="$ROOT/eng/environment.lock.json"
python3 - <<'PY' "$LOCK"
import json,sys; p=sys.argv[1]; data=open(p).read(); assert data.strip(), 'empty manifest'; json.loads(data)
PY
read_json(){ python3 -c 'import json,sys; d=json.load(open(sys.argv[1]));
for k in sys.argv[2].split("."): d=d[k]
print(d)' "$LOCK" "$1"; }
SDK=$(read_json dotnetSdk); PS_MIN=$(read_json powershell.minimumVersion); DOTNET_REL=$(read_json dotnetRoot); PS_REL=$(read_json powershellRoot); NUGET_REL=$(read_json nugetCache)
mkdir -p "$ROOT/.tools" "$ROOT/.cache" "$ROOT/$DOTNET_REL" "$ROOT/$PS_REL" "$ROOT/$NUGET_REL" "$ROOT/.cache/dotnet-home"
need_dotnet=1; if command -v dotnet >/dev/null; then dotnet --list-sdks | awk '{print $1}' | grep -qx "$SDK" && need_dotnet=0 || true; fi
if [ "$need_dotnet" = 1 ] && [ ! -x "$ROOT/$DOTNET_REL/dotnet" ]; then
  URL="https://dot.net/v1/dotnet-install.sh"; TMP="$ROOT/.cache/dotnet-install.sh"; echo "Downloading $URL"; code=$(curl -L -w '%{http_code}' -o "$TMP" "$URL" || echo curl-failed-$?)
  [ "$code" = 200 ] && [ -s "$TMP" ] || { echo "Download failed: $URL HTTP $code" >&2; exit 1; }
  bash "$TMP" --version "$SDK" --install-dir "$ROOT/$DOTNET_REL" --no-path
fi
need_pwsh=1; if command -v pwsh >/dev/null; then v=$(pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'); python3 - <<PY && need_pwsh=0 || true
def t(v): return tuple(int(x) for x in v.split('.')[:3])
assert t('$v') >= t('$PS_MIN')
PY
fi
if [ "$need_pwsh" = 1 ] && [ ! -x "$ROOT/$PS_REL/pwsh" ]; then
  echo "PowerShell >= $PS_MIN not found. Local portable PowerShell must be provisioned in $ROOT/$PS_REL." >&2; exit 1
fi
cat > "$ROOT/.cache/environment.env" <<ENV
DOTNET_ROOT=$ROOT/$DOTNET_REL
NUGET_PACKAGES=$ROOT/$NUGET_REL
DOTNET_CLI_HOME=$ROOT/.cache/dotnet-home
ENV
"$ROOT/eng/activate-environment.sh" >/dev/null || true
echo "BusinessOS environment setup completed."
