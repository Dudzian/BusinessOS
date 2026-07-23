param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Missing dotnet CLI.' }
dotnet build BusinessOS.sln --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
