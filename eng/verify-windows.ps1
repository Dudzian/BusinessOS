$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'activate-environment.ps1')
Import-Module (Join-Path $PSScriptRoot 'BusinessOS.Engineering.psm1') -Force
$Root=Get-BusinessOSRepoRoot; if(-not $IsWindows){throw 'verify-windows.ps1 must run on Windows.'}
$art=Join-Path $Root 'artifacts/test-results'; $smoke=Join-Path $Root 'artifacts/smoke-test'; foreach($p in $art,$smoke){if(Test-Path $p){Remove-Item $p -Recurse -Force}; New-Item -ItemType Directory -Force $p|Out-Null}
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'doctor.ps1'),'-Mode','Windows','-SkipEnvironmentTests') $Root
Invoke-CheckedCommand dotnet @('restore','BusinessOS.sln') $Root
Invoke-CheckedCommand dotnet @('format','BusinessOS.sln','--verify-no-changes') $Root
Invoke-CheckedCommand dotnet @('build','BusinessOS.sln','-c','Release','--no-restore') $Root
Invoke-CheckedCommand dotnet @('test','BusinessOS.sln','-c','Release','--no-build','--logger','trx','--results-directory',$art) $Root
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'verify-test-results.ps1'),'-ResultsDirectory',$art) $Root
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $Root 'tests/environment/Environment.Tests.ps1')) $Root
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'check-vulnerable-packages.ps1'),'-ProjectOrSolution','BusinessOS.sln') $Root
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'smoke-test-desktop.ps1'),'-Configuration','Release') $Root
