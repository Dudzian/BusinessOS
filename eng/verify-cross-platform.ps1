$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'activate-environment.ps1')
Import-Module (Join-Path $PSScriptRoot 'BusinessOS.Engineering.psm1') -Force
$Root=Get-BusinessOSRepoRoot; $art=Join-Path $Root 'artifacts/test-results'; if(Test-Path $art){Remove-Item $art -Recurse -Force}; New-Item -ItemType Directory -Force $art|Out-Null
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'doctor.ps1'),'-Mode','CrossPlatform','-SkipEnvironmentTests') $Root
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'check-solution-projects.ps1')) $Root
$filter=Join-Path $Root 'BusinessOS.CrossPlatform.slnf'; $filterProjects=Get-BusinessOSCrossPlatformFilterProjects $Root $filter
$required=@('BusinessOS.BuildingBlocks.Domain.csproj','BusinessOS.BuildingBlocks.Application.csproj','BusinessOS.UnitTests.csproj','BusinessOS.ArchitectureTests.csproj')
foreach($r in $required){ if(-not(($filterProjects|Split-Path -Leaf) -contains $r)){ throw "Cross-platform solution filter is missing required project: $r" } }
if(($filterProjects -join ';') -match 'BusinessOS\.Desktop|Infrastructure'){throw 'Cross-platform solution filter contains a Windows/Desktop/Infrastructure project'}
Invoke-CheckedCommand dotnet @('restore',$filter) $Root
Invoke-CheckedCommand dotnet @('format',$filter,'--verify-no-changes') $Root
Invoke-CheckedCommand dotnet @('build',$filter,'-c','Release','--no-restore') $Root
Invoke-CheckedCommand dotnet @('test',$filter,'-c','Release','--no-build','--logger','trx','--results-directory',$art) $Root
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'verify-test-results.ps1'),'-ResultsDirectory',$art) $Root
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'test-verify-test-results.ps1')) $Root
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $Root 'tests/environment/Environment.Tests.ps1')) $Root
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'doctor.ps1'),'-Mode','CrossPlatform','-SkipEnvironmentTests') $Root
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'check-vulnerable-packages.ps1'),'-ProjectOrSolution',$filter) $Root
$tracked=@(& git ls-files); if($tracked|Where-Object{$_ -match 'secret|token|\.pfx$|\.pem$|\.key$'}){throw 'Tracked secret-like file detected'}
$forbidden=Select-String -Path (Join-Path $Root 'Directory.Packages.props') -Pattern 'Newtonsoft.Json|Dapper' -ErrorAction SilentlyContinue; if($forbidden){throw 'Forbidden dependency detected'}
Write-Host 'WinUI build: SKIPPED — requires Windows verification'
Write-Host 'WinUI smoke test: SKIPPED — requires Windows verification'
