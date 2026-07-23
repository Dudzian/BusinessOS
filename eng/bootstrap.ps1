$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Missing dotnet CLI. Install stable .NET SDK compatible with global.json.' }
dotnet --info
if ($LASTEXITCODE -ne 0) { throw "dotnet --info failed with exit code $LASTEXITCODE." }
dotnet workload list
if ($LASTEXITCODE -ne 0) { throw "dotnet workload list failed with exit code $LASTEXITCODE." }
dotnet restore BusinessOS.sln
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
./eng/check-solution-projects.ps1
