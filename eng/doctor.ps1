param([ValidateSet('CrossPlatform','Windows')][string]$Mode='CrossPlatform')
$ErrorActionPreference='Continue'; Import-Module (Join-Path $PSScriptRoot 'BusinessOS.Engineering.psm1') -Force
$Root=Get-BusinessOSRepoRoot; $Lock=Read-BusinessOSEnvironmentLock; $Rows=@(); $Ready=$true
function Add-Check($c,$r,$d,$ok,$skip=$false){script:Rows += [pscustomobject]@{Component=$c;Required=$r;Detected=$d;Status= if($skip){'SKIPPED'}elseif($ok){'OK'}else{'FAIL'}}; if(-not $ok -and -not $skip){$script:Ready=$false}}
$os=if($IsWindows){'Windows'}elseif($IsLinux){'Linux'}elseif($IsMacOS){'macOS'}else{[Runtime.InteropServices.RuntimeInformation]::OSDescription}
Add-Check 'Operating system' 'Windows/Linux' $os $true
Add-Check 'Architecture' $(if($Mode -eq 'Windows'){'x64'}else{'any'}) ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture) ($Mode -ne 'Windows' -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'X64')
$dotnet=(Get-Command dotnet -ErrorAction SilentlyContinue); $dv= if($dotnet){ try{& dotnet --version}catch{'error'} } else {'missing'}; Add-Check '.NET SDK' $Lock.dotnetSdk $dv ($dv -eq $Lock.dotnetSdk)
$gv='missing'; try{$gj=Get-Content (Join-Path $Root 'global.json') -Raw|ConvertFrom-Json; $gv=$gj.sdk.version}catch{}; Add-Check 'global.json SDK' $Lock.dotnetSdk $gv ($gv -eq $Lock.dotnetSdk)
$pw=(Get-Command pwsh -ErrorAction SilentlyContinue); $pv=if($pw){& pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'}else{'missing'}; Add-Check 'PowerShell' ">= $($Lock.powershell.minimumVersion)" $pv (Test-VersionAtLeast $pv $Lock.powershell.minimumVersion)
Add-Check 'BusinessOS.sln' 'present' (Test-Path (Join-Path $Root 'BusinessOS.sln')) (Test-Path (Join-Path $Root 'BusinessOS.sln'))
$cache=Join-Path $Root $Lock.nugetCache; New-Item -ItemType Directory -Force -Path $cache|Out-Null; $w=$false; try{$t=Join-Path $cache '.write-test'; 'x'|Set-Content $t; Remove-Item $t; $w=$true}catch{}; Add-Check 'NuGet cache' 'writable' $cache $w
Add-Check 'Git' 'available' ($(if(Get-Command git -ErrorAction SilentlyContinue){'available'}else{'missing'})) ([bool](Get-Command git -ErrorAction SilentlyContinue))
Add-Check 'rg' 'available' ($(if(Get-Command rg -ErrorAction SilentlyContinue){'available'}else{'missing'})) ([bool](Get-Command rg -ErrorAction SilentlyContinue))
$projects=@(Get-ChildItem $Root -Recurse -Filter *.csproj -ErrorAction SilentlyContinue | ? FullName -notmatch '[\\/](bin|obj|.tools|.cache)[\\/]')
Add-Check 'Solution projects' 'readable' "$($projects.Count) project(s)" $true
$tfmOk=$true; $ridOk=$true; foreach($p in $projects){$x=Get-Content $p.FullName -Raw; if($x -match '<TargetFramework>(.*?)</TargetFramework>' -and $Matches[1] -ne $Lock.targetFramework){$tfmOk=$false}; if($x -match '<RuntimeIdentifier>(.*?)</RuntimeIdentifier>' -and $Matches[1] -ne $Lock.targetRuntime){$ridOk=$false}}
Add-Check 'Target Framework' $Lock.targetFramework ($(if($projects.Count){'checked'}else{'no projects'})) $tfmOk
Add-Check 'RuntimeIdentifier' $Lock.targetRuntime ($(if($projects.Count){'checked'}else{'no projects'})) $ridOk
foreach($f in 'environment.lock.json','setup-environment.ps1','setup-environment.sh','activate-environment.ps1','activate-environment.sh'){Add-Check "eng/$f" 'present' (Test-Path (Join-Path $PSScriptRoot $f)) (Test-Path (Join-Path $PSScriptRoot $f))}
$tracked=@(& git ls-files 2>$null); $bad=$tracked|?{$_ -match '^(.tools|.cache)/|\.(pfx|pem|key)$|secret|token'}; Add-Check 'Tracked binaries/secrets' 'none' ($(if($bad){$bad -join ','}else{'none'})) (-not $bad)
if($Mode -eq 'Windows'){Add-Check 'Windows SDK' $Lock.windowsSdkMinimum 'not probed in non-Windows' $IsWindows; Add-Check 'Windows WinUI runtime' 'Windows only' ($(if($IsWindows){'probe required'}else{'non-Windows'})) $IsWindows; Add-Check 'UI Automation' 'loadable' ($(if($IsWindows){'probe required'}else{'non-Windows'})) $IsWindows; Add-Check 'Interactive desktop' 'required' ([Environment]::UserInteractive) ([Environment]::UserInteractive)} else { Add-Check 'Windows WinUI runtime' 'Windows only' 'non-Windows gate' $false $true }
$Rows|Format-Table Component,Required,Detected,Status -AutoSize
Write-Host "Environment ready: $(if($Ready){'YES'}else{'NO'})"
if(-not $Ready){exit 1}
