param([ValidateSet('CrossPlatform','Windows')][string]$Mode='CrossPlatform',[switch]$SkipEnvironmentTests)
$ErrorActionPreference='Continue'; Import-Module (Join-Path $PSScriptRoot 'BusinessOS.Engineering.psm1') -Force
$Root=Get-BusinessOSRepoRoot; $Ready=$true; $Rows=@(); $Lock=$null
function Write-DoctorReport {
  param([Parameter(Mandatory)][object[]]$Rows,[Parameter(Mandatory)][bool]$Ready)
  $Rows | Format-Table Component,Required,Detected,Status -AutoSize
  foreach($row in @($Rows | Where-Object { $_.Status -eq 'FAIL' })){
    $payload=[ordered]@{
      component=[string]$row.Component
      required=[string]$row.Required
      detected=$row.Detected
      status=[string]$row.Status
    }
    $json=$payload | ConvertTo-Json -Compress -Depth 4
    [Console]::Out.WriteLine("BUSINESSOS_DOCTOR_FAILURE_JSON=$json")
  }
  [Console]::Out.WriteLine("Environment ready: $(if($Ready){'YES'}else{'NO'})")
}
function Add-Check($c,$r,$d,$ok,$skip=$false){$script:Rows += [pscustomobject]@{Component=$c;Required=$r;Detected=$d;Status=if($skip){'SKIPPED'}elseif($ok){'OK'}else{'FAIL'}}; if(-not $ok -and -not $skip){$script:Ready=$false}}
try{$Lock=Read-BusinessOSEnvironmentLock; Add-Check 'Environment manifest' 'valid JSON' 'loaded' $true}catch{Add-Check 'Environment manifest' 'valid JSON' $_.Exception.Message $false}
if($null -eq $Lock){Write-DoctorReport -Rows $Rows -Ready $Ready; exit 1}
$expected=@('src/BusinessOS.Desktop/BusinessOS.Desktop.csproj','src/BusinessOS.AppHost/BusinessOS.AppHost.csproj','tests/BusinessOS.UnitTests/BusinessOS.UnitTests.csproj','tests/BusinessOS.ArchitectureTests/BusinessOS.ArchitectureTests.csproj')
$solution=Join-Path $Root 'BusinessOS.sln'; $projectLines=if(Test-Path $solution){@((Select-String -Path $solution -Pattern '^Project\('))}else{@()}
Add-Check 'BusinessOS.sln' 'non-empty solution' "$($projectLines.Count) project entries" ($projectLines.Count -gt 0)
foreach($p in $expected){Add-Check $p 'present' (Test-Path (Join-Path $Root $p)) (Test-Path (Join-Path $Root $p))}
try{$g=Get-Content (Join-Path $Root 'global.json') -Raw|ConvertFrom-Json; $gv=$g.sdk.version}catch{$gv='invalid'}; Add-Check 'global.json SDK' $Lock.dotnetSdk $gv ($gv -eq $Lock.dotnetSdk)
$dnLocal=Get-BusinessOSToolPath $Root $Lock.dotnetRoot 'dotnet'; $dn=if(Test-Path $dnLocal){$dnLocal}elseif(Get-Command dotnet -ErrorAction SilentlyContinue){(Get-Command dotnet).Source}else{$null}; $dv=if($dn){try{& $dn --version}catch{'error'}}else{'missing'}; Add-Check '.NET SDK' $Lock.dotnetSdk $dv ($dv -eq $Lock.dotnetSdk)
$pwLocal=Get-BusinessOSToolPath $Root $Lock.powershellRoot 'pwsh'; $pw=if(Test-Path $pwLocal){$pwLocal}elseif(Get-Command pwsh -ErrorAction SilentlyContinue){(Get-Command pwsh).Source}else{$null}; $pv=if($pw){try{& $pw -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'}catch{'error'}}else{'missing'}; Add-Check 'PowerShell' ">= $($Lock.powershell.minimumVersion)" $pv (Test-VersionAtLeast $pv $Lock.powershell.minimumVersion)
function Test-NuGetCacheWritable {
  param([Parameter(Mandatory)][string]$CachePath)
  $testFile = Join-Path $CachePath ('.write-test-' + [Guid]::NewGuid().ToString())
  $content = 'BusinessOS cache write test'
  try {
    if ($env:BUSINESSOS_DOCTOR_FORCE_CACHE_FAILURE) { throw $env:BUSINESSOS_DOCTOR_FORCE_CACHE_FAILURE }
    New-Item -ItemType Directory -Force -Path $CachePath -ErrorAction Stop | Out-Null
    Set-Content -LiteralPath $testFile -Value $content -NoNewline -ErrorAction Stop
    $detected = Get-Content -LiteralPath $testFile -Raw -ErrorAction Stop
    if ($detected -ne $content) { throw "Cache write test read back unexpected content." }
    Remove-Item -LiteralPath $testFile -Force -ErrorAction Stop
    if (Test-Path -LiteralPath $testFile) { throw "Cache write test file still exists after removal: $testFile" }
    [pscustomobject]@{ Ok = $true; Detected = $CachePath }
  } catch {
    [pscustomobject]@{ Ok = $false; Detected = $_.Exception.Message }
  } finally {
    try { if (Test-Path -LiteralPath $testFile) { Remove-Item -LiteralPath $testFile -Force -ErrorAction Stop } } catch { }
  }
}
$cache=Join-Path $Root $Lock.nugetCache; $cacheResult=Test-NuGetCacheWritable -CachePath $cache; Add-Check 'NuGet cache' 'writable' $cacheResult.Detected $cacheResult.Ok
Add-Check 'Git' 'available' ($(if(Get-Command git -ErrorAction SilentlyContinue){'available'}else{'missing'})) ([bool](Get-Command git -ErrorAction SilentlyContinue))
Add-Check 'ripgrep' 'optional' ($(if(Get-Command rg -ErrorAction SilentlyContinue){'available'}else{'missing'})) $true $true
foreach($f in 'setup-environment.ps1','setup-environment.sh','activate-environment.ps1','activate-environment.sh','verify-cross-platform.ps1','verify-windows.ps1','smoke-test-desktop.ps1'){Add-Check "eng/$f" 'present' (Test-Path (Join-Path $PSScriptRoot $f)) (Test-Path (Join-Path $PSScriptRoot $f))}
$tracked=@(& git ls-files 2>$null); $bad=$tracked|Where-Object{$_ -match '^(\.tools|\.cache)/|\.(pfx|pem|key|zip|tar|gz)$|secret|token'}; Add-Check 'Tracked secrets/binaries' 'none' ($(if($bad){$bad -join ','}else{'none'})) (-not $bad)
if(-not $SkipEnvironmentTests){try{& $pw -NoProfile -File (Join-Path $Root 'tests/environment/Environment.Tests.ps1') -Quick; Add-Check 'Environment self-tests' 'pass' 'passed' ($LASTEXITCODE -eq 0)}catch{Add-Check 'Environment self-tests' 'pass' $_.Exception.Message $false}}
if($Mode -eq 'Windows'){
 Add-Check 'Windows OS' 'Windows' $IsWindows $IsWindows; Add-Check 'Windows architecture' 'x64' ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture) ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'X64')
 $sdkRoot='C:\Program Files (x86)\Windows Kits\10\Lib'; $sdks=if(Test-Path $sdkRoot){@(Get-ChildItem $sdkRoot -Directory|% Name)}else{@()}; Add-Check 'Windows SDK' ">= $($Lock.windowsSdkMinimum)" ($sdks -join ',') ($IsWindows -and ($sdks|?{[version]($_ -replace '[^0-9\.]','') -ge [version]$Lock.windowsSdkMinimum}))
 foreach($asm in 'UIAutomationClient','UIAutomationTypes'){try{Add-Type -AssemblyName $asm -ErrorAction Stop; $ok=$true}catch{$ok=$false}; Add-Check $asm 'loadable' $ok $ok}
 Add-Check 'Interactive desktop' 'true' ([Environment]::UserInteractive) ([Environment]::UserInteractive)
 $desktop=Join-Path $Root 'src/BusinessOS.Desktop/BusinessOS.Desktop.csproj'; $dx=if(Test-Path $desktop){Get-Content $desktop -Raw}else{''}; Add-Check 'WinUI Desktop project' 'present' (Test-Path $desktop) (Test-Path $desktop); Add-Check 'Windows App SDK reference' 'present' ($dx -match 'Microsoft.WindowsAppSDK') ($dx -match 'Microsoft.WindowsAppSDK')
} else { Add-Check 'WinUI gate' 'Windows verification' 'SKIPPED — requires Windows verification' $false $true }
Write-DoctorReport -Rows $Rows -Ready $Ready; if(-not $Ready){exit 1}
