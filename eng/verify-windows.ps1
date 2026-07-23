$ErrorActionPreference='Stop'; Import-Module (Join-Path $PSScriptRoot 'BusinessOS.Engineering.psm1') -Force; $Root=Get-BusinessOSRepoRoot
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'doctor.ps1'),'-Mode','Windows') $Root
Invoke-CheckedCommand dotnet @('restore','BusinessOS.sln') $Root; Invoke-CheckedCommand dotnet @('format','BusinessOS.sln','--verify-no-changes') $Root; Invoke-CheckedCommand dotnet @('build','BusinessOS.sln','-c','Release','--no-restore') $Root; Invoke-CheckedCommand dotnet @('test','BusinessOS.sln','-c','Release','--no-build') $Root
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'check-vulnerable-packages.ps1')) $Root
Write-Host 'WinUI build, EXE launch, MainWindowHandle, title, UI Automation, and safe shutdown checks must pass here when WinUI projects exist.'
