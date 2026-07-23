#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"; source "$ROOT/eng/environment.bootstrap.env"
mkdir -p "$ROOT/.tools" "$ROOT/.cache" "$ROOT/$DOTNET_ROOT_REL" "$ROOT/$POWERSHELL_ROOT_REL" "$ROOT/$POWERSHELL_MODULE_ROOT_REL" "$ROOT/$NUGET_CACHE_REL" "$ROOT/$DOTNET_HOME_REL" "$ROOT/$DOWNLOAD_CACHE_REL"
RID=""; case "$(uname -s)-$(uname -m)" in Linux-x86_64) RID=LINUX_X64;; Darwin-x86_64) RID=OSX_X64;; *) echo "Unsupported OS/arch for bash setup: $(uname -s)-$(uname -m)" >&2; exit 1;; esac
lower(){ printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | tr '_' '-'; }
upper(){ printf '%s' "$1" | tr '[:lower:]' '[:upper:]'; }
hash_file(){ alg="$1"; file="$2"; if [ "$alg" = sha256 ]; then if command -v sha256sum >/dev/null; then sha256sum "$file" | awk '{print $1}'; elif command -v shasum >/dev/null; then shasum -a 256 "$file" | awk '{print $1}'; else echo "No SHA-256 tool available" >&2; return 9; fi; else if command -v sha512sum >/dev/null; then sha512sum "$file" | awk '{print $1}'; elif command -v shasum >/dev/null; then shasum -a 512 "$file" | awk '{print $1}'; else echo "No SHA-512 tool available" >&2; return 9; fi; fi; }
sha512_check(){ [[ "$2" =~ ^[A-Fa-f0-9]{128}$ ]] || { echo "Missing or invalid SHA-512 for $1" >&2; return 2; }; local actual; actual=$(upper "$(hash_file sha512 "$1")"); expected=$(upper "$2"); [ "$actual" = "$expected" ] || { echo "SHA512 checksum mismatch for $1. Expected $2, got $actual" >&2; return 3; }; echo "sha512 OK $actual"; }
sha256_check(){ [[ "$2" =~ ^[A-Fa-f0-9]{64}$ ]] || { echo "Missing or invalid SHA-256 for $1" >&2; return 2; }; local actual; actual=$(upper "$(hash_file sha256 "$1")"); expected=$(upper "$2"); [ "$actual" = "$expected" ] || { echo "SHA256 checksum mismatch for $1. Expected $2, got $actual" >&2; return 3; }; echo "sha256 OK $actual"; }
download(){ local url="$1" out="$2"; rm -f "$out"; local code bytes; code=$(curl -L -w '%{http_code}' -o "$out" "$url" || echo "curl-$?"); if [ -f "$out" ]; then bytes=$(wc -c < "$out" 2>/dev/null || echo 0); else bytes=0; fi; echo "URL=$url HTTP=$code bytes=$bytes"; [ "$code" = 200 ] && [ "$bytes" -gt 0 ]; }
write_env_value(){ local name="$1" value="$2"; printf '%s=' "$name"; printf '%q' "$value"; printf '\n'; }
DOTNET_BIN="$ROOT/$DOTNET_ROOT_REL/dotnet"; dotnet_source=local
if [ ! -x "$DOTNET_BIN" ] || [ "$($DOTNET_BIN --version 2>/dev/null || true)" != "$DOTNET_SDK_VERSION" ]; then
  host_dotnet="$(command -v dotnet || true)"
  if [ -n "$host_dotnet" ] && [ "$(dotnet --version 2>/dev/null || true)" = "$DOTNET_SDK_VERSION" ]; then DOTNET_BIN="$host_dotnet"; dotnet_source=host; else
    url_var="DOTNET_${RID}_URL"; sha_var="DOTNET_${RID}_SHA512"; url="${!url_var}"; sha="${!sha_var}"; rid_l=$(lower "$RID"); archive="$ROOT/$DOWNLOAD_CACHE_REL/dotnet-sdk-$DOTNET_SDK_VERSION-$rid_l.tar.gz"
    download "$url" "$archive" || { echo "dotnet download failed" >&2; exit 1; }
    sha512_check "$archive" "$sha"
    tar -tzf "$archive" >/dev/null || { echo "Invalid TAR.GZ archive: $archive" >&2; exit 1; }
    tar -xzf "$archive" -C "$ROOT/$DOTNET_ROOT_REL"
    [ -x "$ROOT/$DOTNET_ROOT_REL/dotnet" ] || { echo "Expected executable missing: $ROOT/$DOTNET_ROOT_REL/dotnet" >&2; exit 1; }
    DOTNET_BIN="$ROOT/$DOTNET_ROOT_REL/dotnet"; dotnet_source=local
  fi
fi
DV="$($DOTNET_BIN --version)"; [ "$DV" = "$DOTNET_SDK_VERSION" ] || { echo "dotnet mismatch. Required $DOTNET_SDK_VERSION, detected $DV" >&2; exit 1; }
PWSH_BIN="$ROOT/$POWERSHELL_ROOT_REL/pwsh"; pwsh_source=local
if [ ! -x "$PWSH_BIN" ] || [ "$($PWSH_BIN -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' 2>/dev/null || true)" != "$POWERSHELL_VERSION" ]; then
  host_pwsh="$(command -v pwsh || true)"
  if [ -n "$host_pwsh" ] && [ "$(pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()')" = "$POWERSHELL_VERSION" ]; then PWSH_BIN="$host_pwsh"; pwsh_source=host; else
    url_var="POWERSHELL_${RID}_URL"; sha_var="POWERSHELL_${RID}_SHA256"; url="${!url_var}"; sha="${!sha_var}"; rid_l=$(lower "$RID"); archive="$ROOT/$DOWNLOAD_CACHE_REL/powershell-$POWERSHELL_VERSION-$rid_l.tar.gz"
    download "$url" "$archive" || { echo "PowerShell download failed" >&2; exit 1; }
    sha256_check "$archive" "$sha"
    tar -tzf "$archive" >/dev/null || { echo "Invalid TAR.GZ archive: $archive" >&2; exit 1; }
    tar -xzf "$archive" -C "$ROOT/$POWERSHELL_ROOT_REL"; chmod +x "$ROOT/$POWERSHELL_ROOT_REL/pwsh"
    [ -x "$ROOT/$POWERSHELL_ROOT_REL/pwsh" ] || { echo "Expected executable missing: $ROOT/$POWERSHELL_ROOT_REL/pwsh" >&2; exit 1; }
    PWSH_BIN="$ROOT/$POWERSHELL_ROOT_REL/pwsh"; pwsh_source=local
  fi
fi
PV="$($PWSH_BIN -NoProfile -Command '$PSVersionTable.PSVersion.ToString()')"; [ "$PV" = "$POWERSHELL_VERSION" ] || { echo "PowerShell mismatch. Required $POWERSHELL_VERSION, detected $PV" >&2; exit 1; }
dotnet_root="$(dirname "$DOTNET_BIN")"; pwsh_root="$(dirname "$PWSH_BIN")"
nuget="$ROOT/$NUGET_CACHE_REL"; dotnet_home="$ROOT/$DOTNET_HOME_REL"; psmodule="$ROOT/$POWERSHELL_MODULE_ROOT_REL"
{
  write_env_value DOTNET_EXE "$DOTNET_BIN"
  write_env_value DOTNET_ROOT "$dotnet_root"
  write_env_value DOTNET_SOURCE "$dotnet_source"
  write_env_value PWSH_EXE "$PWSH_BIN"
  write_env_value POWERSHELL_ROOT "$pwsh_root"
  write_env_value POWERSHELL_SOURCE "$pwsh_source"
  write_env_value NUGET_PACKAGES "$nuget"
  write_env_value DOTNET_CLI_HOME "$dotnet_home"
  write_env_value PSMODULE_ROOT "$psmodule"
} > "$ROOT/.cache/environment.resolved.env"
bash -c '''set -euo pipefail; source "$1"; test -n "$DOTNET_ROOT"; test -n "$POWERSHELL_ROOT"; test -n "$NUGET_PACKAGES"''' _ "$ROOT/.cache/environment.resolved.env"
export BUSINESSOS_DOTNET_EXE="$DOTNET_BIN"
export BUSINESSOS_DOTNET_ROOT="$dotnet_root"
export BUSINESSOS_DOTNET_SOURCE="$dotnet_source"
export BUSINESSOS_PWSH_EXE="$PWSH_BIN"
export BUSINESSOS_PWSH_ROOT="$pwsh_root"
export BUSINESSOS_PWSH_SOURCE="$pwsh_source"
export BUSINESSOS_NUGET="$nuget"
export BUSINESSOS_DOTNET_HOME="$dotnet_home"
export BUSINESSOS_PSMODULE="$psmodule"
export BUSINESSOS_RESOLVED_JSON="$ROOT/.cache/environment.resolved.json"
"$PWSH_BIN" -NoProfile -Command '
$resolved = [ordered]@{
  dotnetExecutable = $env:BUSINESSOS_DOTNET_EXE
  dotnetRoot = $env:BUSINESSOS_DOTNET_ROOT
  dotnetSource = $env:BUSINESSOS_DOTNET_SOURCE
  powershellExecutable = $env:BUSINESSOS_PWSH_EXE
  powershellRoot = $env:BUSINESSOS_PWSH_ROOT
  powershellSource = $env:BUSINESSOS_PWSH_SOURCE
  nugetPackages = $env:BUSINESSOS_NUGET
  dotnetCliHome = $env:BUSINESSOS_DOTNET_HOME
  powershellModuleRoot = $env:BUSINESSOS_PSMODULE
}
$resolved | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $env:BUSINESSOS_RESOLVED_JSON -Encoding utf8
Get-Content -LiteralPath $env:BUSINESSOS_RESOLVED_JSON -Raw | ConvertFrom-Json -ErrorAction Stop | Out-Null
'
if [ $? -ne 0 ]; then echo "Failed to generate resolved JSON" >&2; exit 1; fi
echo "BusinessOS environment setup completed."
