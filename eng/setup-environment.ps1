$ErrorActionPreference='Stop'
$Root=Split-Path -Parent $PSScriptRoot; $LockPath=Join-Path $PSScriptRoot 'environment.lock.json'
if(-not(Test-Path $LockPath)){throw "Missing $LockPath"}; $raw=Get-Content $LockPath -Raw; if([string]::IsNullOrWhiteSpace($raw)){throw 'Empty environment manifest'}; $Lock=$raw|ConvertFrom-Json
@('.tools','.cache',$Lock.dotnetRoot,$Lock.powershellRoot,$Lock.nugetCache,'.cache/dotnet-home')|%{New-Item -ItemType Directory -Force -Path (Join-Path $Root $_)|Out-Null}
$sdks=@(); if(Get-Command dotnet -ErrorAction SilentlyContinue){$sdks=& dotnet --list-sdks | % { ($_ -split ' ')[0] }}
if($sdks -notcontains $Lock.dotnetSdk -and -not(Test-Path (Join-Path $Root ($Lock.dotnetRoot + '/dotnet.exe')))){ $url='https://dot.net/v1/dotnet-install.ps1'; $tmp=Join-Path $Root '.cache/dotnet-install.ps1'; $r=Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing -PassThru; if($r.StatusCode -ne 200 -or (Get-Item $tmp).Length -eq 0){throw "Download failed: $url"}; & $tmp -Version $Lock.dotnetSdk -InstallDir (Join-Path $Root $Lock.dotnetRoot) -NoPath }
$ok=$false; if(Get-Command pwsh -ErrorAction SilentlyContinue){$ok=([version](& pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()') -ge [version]$Lock.powershell.minimumVersion)}
if(-not $ok -and -not(Test-Path (Join-Path $Root ($Lock.powershellRoot + '/pwsh.exe')))){throw "PowerShell >= $($Lock.powershell.minimumVersion) not found; provision portable PowerShell under $($Lock.powershellRoot)."}
@("DOTNET_ROOT=$(Join-Path $Root $Lock.dotnetRoot)","NUGET_PACKAGES=$(Join-Path $Root $Lock.nugetCache)","DOTNET_CLI_HOME=$(Join-Path $Root '.cache/dotnet-home')")|Set-Content (Join-Path $Root '.cache/environment.env')
Write-Host 'BusinessOS environment setup completed.'
