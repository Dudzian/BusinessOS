$ErrorActionPreference='Stop'
$Root = Split-Path -Parent $PSScriptRoot
$ResolvedPath = Join-Path $Root '.cache/environment.resolved.json'
if(-not(Test-Path $ResolvedPath)){ throw "Resolved environment not found. Run ./eng/setup-environment.ps1 first: $ResolvedPath" }
$Resolved = Get-Content $ResolvedPath -Raw | ConvertFrom-Json
$env:DOTNET_ROOT = $Resolved.dotnetRoot
$env:NUGET_PACKAGES = $Resolved.nugetPackages
$env:DOTNET_CLI_HOME = $Resolved.dotnetCliHome
$env:DOTNET_NOLOGO='1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:NUGET_XMLDOC_MODE='skip'
function Add-PathOnce([string]$Name,[string]$Path) { $sep=[IO.Path]::PathSeparator; $current=(Get-Item -Path "env:$Name" -ErrorAction SilentlyContinue).Value; $parts=($current -split [regex]::Escape($sep))|Where-Object{$_}; if ($parts -notcontains $Path) { Set-Item -Path "env:$Name" -Value (($Path + $sep + ($parts -join $sep)).TrimEnd($sep)) } }
Add-PathOnce PATH $Resolved.dotnetRoot
Add-PathOnce PATH $Resolved.powershellRoot
Add-PathOnce PSModulePath $Resolved.powershellModuleRoot
