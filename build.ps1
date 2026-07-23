$ErrorActionPreference='Stop'; Import-Module (Join-Path $PSScriptRoot 'eng/BusinessOS.Engineering.psm1') -Force
Invoke-CheckedCommand dotnet @('build','BusinessOS.sln','-c','Release') $PSScriptRoot
