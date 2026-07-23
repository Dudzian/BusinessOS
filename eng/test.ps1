param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Missing dotnet CLI.' }
if (Test-Path artifacts/test-results) { Remove-Item artifacts/test-results -Recurse -Force }
dotnet test BusinessOS.sln --configuration $Configuration --no-build --logger trx --results-directory artifacts/test-results
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
./eng/verify-test-results.ps1
