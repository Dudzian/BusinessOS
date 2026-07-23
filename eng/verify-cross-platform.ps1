$ErrorActionPreference='Stop'; Import-Module (Join-Path $PSScriptRoot 'BusinessOS.Engineering.psm1') -Force; $Root=Get-BusinessOSRepoRoot
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'doctor.ps1'),'-Mode','CrossPlatform') $Root
if(Test-Path (Join-Path $Root 'BusinessOS.sln')){ Write-Host 'Solution present: OK' }
if(Get-ChildItem $Root -Recurse -Filter *.csproj | ? FullName -notmatch '[\\/](bin|obj|.tools|.cache)[\\/]'){ Invoke-CheckedCommand dotnet @('restore','BusinessOS.sln') $Root; Invoke-CheckedCommand dotnet @('format','BusinessOS.sln','--verify-no-changes') $Root; Invoke-CheckedCommand dotnet @('test','BusinessOS.sln','--configuration','Release','--no-restore') $Root } else { Write-Host 'No projects found: restore/build/unit/architecture/TRX checks SKIPPED' }
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'check-vulnerable-packages.ps1')) $Root
Write-Host 'WinUI build: SKIPPED — requires Windows verification'; Write-Host 'WinUI smoke test: SKIPPED — requires Windows verification'
