$ErrorActionPreference='Stop'; if($IsWindows){& (Join-Path $PSScriptRoot 'verify-windows.ps1')}else{& (Join-Path $PSScriptRoot 'verify-cross-platform.ps1')}; exit $LASTEXITCODE
