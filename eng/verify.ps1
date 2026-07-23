$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot
if (-not $IsWindows) { throw 'Full Block 1 verification requires Windows because it includes the WinUI desktop smoke test.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Missing dotnet CLI.' }
dotnet --info
if ($LASTEXITCODE -ne 0) { throw "dotnet --info failed with exit code $LASTEXITCODE." }
dotnet sln BusinessOS.sln list
if ($LASTEXITCODE -ne 0) { throw "dotnet sln list failed with exit code $LASTEXITCODE." }
./eng/check-solution-projects.ps1
dotnet restore BusinessOS.sln
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
dotnet format BusinessOS.sln --verify-no-changes
if ($LASTEXITCODE -ne 0) { throw "dotnet format failed with exit code $LASTEXITCODE." }
dotnet build BusinessOS.sln --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
./eng/smoke-test-desktop.ps1 -Configuration Release
./eng/test-verify-test-results.ps1
./eng/test.ps1 -Configuration Release
./eng/check-vulnerable-packages.ps1
