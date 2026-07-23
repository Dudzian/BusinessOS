$ErrorActionPreference='Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Lock = Get-Content (Join-Path $PSScriptRoot 'environment.lock.json') -Raw | ConvertFrom-Json
$env:DOTNET_ROOT = Join-Path $Root $Lock.dotnetRoot
$env:NUGET_PACKAGES = Join-Path $Root $Lock.nugetCache
$env:DOTNET_CLI_HOME = Join-Path $Root '.cache/dotnet-home'
$env:DOTNET_NOLOGO='1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:NUGET_XMLDOC_MODE='skip'
function Add-PathOnce([string]$Path) { $parts = $env:PATH -split [IO.Path]::PathSeparator; if ($parts -notcontains $Path) { $env:PATH = $Path + [IO.Path]::PathSeparator + $env:PATH } }
Add-PathOnce $env:DOTNET_ROOT; Add-PathOnce (Join-Path $Root $Lock.powershellRoot)
