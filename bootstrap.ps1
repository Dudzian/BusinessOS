$ErrorActionPreference='Stop'; Import-Module (Join-Path $PSScriptRoot 'eng/BusinessOS.Engineering.psm1') -Force
Invoke-CheckedCommand pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'eng/setup-environment.ps1')) $PSScriptRoot
