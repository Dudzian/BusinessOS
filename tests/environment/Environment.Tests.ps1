param([switch]$Quick)
$ErrorActionPreference='Stop'
$RepoRoot=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
Import-Module (Join-Path $RepoRoot 'eng/BusinessOS.Engineering.psm1') -Force
Import-Module (Join-Path $RepoRoot 'eng/BusinessOS.Provisioning.psm1') -Force
$log=Join-Path $RepoRoot '.cache/environment-tests.log'; New-Item -ItemType Directory -Force (Split-Path $log)|Out-Null; ''|Set-Content -LiteralPath $log
$script:Failures=0
function Assert($Name,[scriptblock]$Body){try{& $Body; "PASS $Name"|Tee-Object -FilePath $log -Append}catch{$script:Failures++; "FAIL $Name :: $($_.Exception.Message)"|Tee-Object -FilePath $log -Append; Write-Error $_}}
function Invoke-ProcessForTest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$File,

        [string[]]$ArgumentList = @(),

        [Parameter(Mandatory)]
        [string]$WorkingDirectory
    )

    $resolvedWorkingDirectory =
        (Resolve-Path -LiteralPath $WorkingDirectory -ErrorAction Stop).Path

    $process = $null

    try {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $File
        $startInfo.WorkingDirectory = $resolvedWorkingDirectory
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.CreateNoWindow = $true

        foreach ($argument in $ArgumentList) {
            [void]$startInfo.ArgumentList.Add([string]$argument)
        }

        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo

        if (-not $process.Start()) {
            throw "Failed to start process: $File"
        }

        $process.StandardInput.Close()

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        $process.WaitForExit()

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        [pscustomobject]@{
            ExitCode         = $process.ExitCode
            Output           = $stdout
            Error            = $stderr
            Combined         = @($stdout, $stderr) -join [Environment]::NewLine
            FileName         = $File
            ArgumentList     = @($ArgumentList)
            WorkingDirectory = $resolvedWorkingDirectory
        }
    }
    finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
    }
}
function Invoke-ExpectSuccess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$File,

        [string[]]$ArgumentList = @(),

        [Parameter(Mandatory)]
        [string]$WorkingDirectory
    )

    $result = Invoke-ProcessForTest `
        -File $File `
        -ArgumentList $ArgumentList `
        -WorkingDirectory $WorkingDirectory

    if ($result.ExitCode -ne 0) {
        throw "Expected success, got $($result.ExitCode): $($result.Combined)"
    }

    return $result
}

function Get-DoctorFailureRecords {
  param([Parameter(Mandatory)]$RunResult)
  $prefix = 'BUSINESSOS_DOCTOR_FAILURE_JSON='
  @(
    foreach ($line in @($RunResult.Combined -split '\r?\n')) {
      if ($line.StartsWith($prefix, [StringComparison]::Ordinal)) {
        $json = $line.Substring($prefix.Length)
        try { $json | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "Invalid doctor failure JSON: $line" }
      }
    }
  )
}
function Assert-DoctorFailureRecord {
  param(
    [Parameter(Mandatory)]$RunResult,
    [Parameter(Mandatory)][string]$Component,
    [Parameter(Mandatory)][string]$Required,
    [Parameter(Mandatory)]$Detected
  )
  $records = @(Get-DoctorFailureRecords -RunResult $RunResult)
  $match = @($records | Where-Object { $_.component -eq $Component -and $_.required -eq $Required -and $_.status -eq 'FAIL' })
  if ($match.Count -ne 1) { throw "Expected exactly one doctor failure record for '$Component'." }
  if ($Detected -is [bool]) {
    if ([bool]$match[0].detected -ne $Detected) { throw "Unexpected doctor detected value for '$Component': $($match[0].detected)" }
  }
  elseif ($match[0].detected -ne $Detected) { throw "Unexpected doctor detected value for '$Component': $($match[0].detected)" }
}

function Invoke-ExpectFailure {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$File,

        [string[]]$ArgumentList = @(),

        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        [string]$Contains
    )

    $result = Invoke-ProcessForTest `
        -File $File `
        -ArgumentList $ArgumentList `
        -WorkingDirectory $WorkingDirectory

    if ($result.ExitCode -eq 0) {
        throw "Expected failure, got success: $($result.Combined)"
    }
    if ($Contains -and $result.Combined -notmatch [regex]::Escape($Contains)) {
        throw "Failure did not contain '$Contains': $($result.Combined)"
    }

    return $result
}
function Copy-FixtureRepo([string]$BaseName = ("BusinessOS fixture " + [Guid]::NewGuid())){ $d=Join-Path ([IO.Path]::GetTempPath()) $BaseName; foreach($dir in 'eng','tests/environment','src/BusinessOS.Desktop','src/BusinessOS.AppHost','src/BuildingBlocks/BusinessOS.BuildingBlocks.Domain','src/BuildingBlocks/BusinessOS.BuildingBlocks.Application','tests/BusinessOS.UnitTests','tests/BusinessOS.ArchitectureTests'){New-Item -ItemType Directory -Force (Join-Path $d $dir)|Out-Null}; foreach($f in 'environment.lock.json','environment.bootstrap.env','BusinessOS.Engineering.psm1','BusinessOS.Provisioning.psm1','doctor.ps1','setup-environment.ps1','setup-environment.sh','activate-environment.ps1','activate-environment.sh','verify-cross-platform.ps1','verify-windows.ps1','smoke-test-desktop.ps1'){Copy-Item -LiteralPath (Join-Path $RepoRoot "eng/$f") -Destination (Join-Path $d "eng/$f")}; Copy-Item -LiteralPath (Join-Path $RepoRoot 'tests/environment/Environment.Tests.ps1') -Destination (Join-Path $d 'tests/environment/Environment.Tests.ps1'); Copy-Item -LiteralPath (Join-Path $RepoRoot 'global.json') -Destination (Join-Path $d 'global.json'); Copy-Item -LiteralPath (Join-Path $RepoRoot 'BusinessOS.CrossPlatform.slnf') -Destination (Join-Path $d 'BusinessOS.CrossPlatform.slnf'); 'Microsoft Visual Studio Solution File, Format Version 12.00','Project("{x}") = "BusinessOS.Desktop", "src\BusinessOS.Desktop\BusinessOS.Desktop.csproj", "{y}"'|Set-Content -LiteralPath (Join-Path $d 'BusinessOS.sln'); foreach($p in 'src/BusinessOS.Desktop/BusinessOS.Desktop.csproj','src/BusinessOS.AppHost/BusinessOS.AppHost.csproj','src/BuildingBlocks/BusinessOS.BuildingBlocks.Domain/BusinessOS.BuildingBlocks.Domain.csproj','src/BuildingBlocks/BusinessOS.BuildingBlocks.Application/BusinessOS.BuildingBlocks.Application.csproj','tests/BusinessOS.UnitTests/BusinessOS.UnitTests.csproj','tests/BusinessOS.ArchitectureTests/BusinessOS.ArchitectureTests.csproj'){'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.WindowsAppSDK" Condition="false" /></ItemGroup></Project>'|Set-Content -LiteralPath (Join-Path $d $p)}; git -C $d init *> $null; git -C $d add . *> $null; $d }
function New-FileWithHash([string]$Text){$f=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()); $Text|Set-Content -NoNewline -LiteralPath $f; $f}
function Read-Resolved($Dir){ Get-Content -LiteralPath (Join-Path $Dir '.cache/environment.resolved.json') -Raw | ConvertFrom-Json }

function Assert-ResolvedFileExists([string]$Path,$RunResult,[string]$SetupMessage){
  if(Test-Path -LiteralPath $Path){return}
  $cacheDir=Split-Path -Parent $Path
  $cacheListing='(cache directory missing)'
  if(Test-Path -LiteralPath $cacheDir){$cacheListing=(Get-ChildItem -LiteralPath $cacheDir -Force | ForEach-Object { $_.FullName }) -join "`n"}
  throw "Expected resolved file missing: $Path`nSTDOUT:`n$($RunResult.Output)`nSTDERR:`n$($RunResult.Error)`nSETUP MESSAGE:`n$SetupMessage`nCACHE CONTENTS:`n$cacheListing"
}
function Assert-ExpectedFixtureResolvedState($Resolved,[string]$Dir){
  $expectedNuget=Join-Path $Dir '.cache/nuget'
  $expectedDotnetHome=Join-Path $Dir '.cache/dotnet-home'
  $expectedPsModule=Join-Path $Dir '.tools/powershell-modules'
  if($Resolved.nugetPackages -ne $expectedNuget){throw "PowerShell setup did not resolve expected fixture NuGet cache: $($Resolved.nugetPackages)"}
  if($Resolved.dotnetCliHome -ne $expectedDotnetHome){throw "PowerShell setup did not resolve expected fixture dotnet-home: $($Resolved.dotnetCliHome)"}
  if($Resolved.powershellModuleRoot -ne $expectedPsModule){throw "PowerShell setup did not resolve expected fixture PSModule path: $($Resolved.powershellModuleRoot)"}
  foreach($value in $Resolved.nugetPackages,$Resolved.dotnetCliHome,$Resolved.powershellModuleRoot){
    if(-not $value.StartsWith($Dir,[StringComparison]::Ordinal)){throw "resolved path escaped current fixture: $value"}
    if($value.StartsWith($RepoRoot,[StringComparison]::Ordinal)){throw "resolved path points to canonical repository: $value"}
  }
}

function Compare-Resolved($A,$B){ foreach($p in 'dotnetExecutable','dotnetRoot','dotnetSource','powershellExecutable','powershellRoot','powershellSource','nugetPackages','dotnetCliHome','powershellModuleRoot'){ if($A.$p -ne $B.$p){throw "resolved state changed: $p"} } }
function HostToolsMatch($Lock){ if(-not(Get-Command dotnet -ErrorAction SilentlyContinue) -or -not(Get-Command pwsh -ErrorAction SilentlyContinue)){return $false}; ((& dotnet --version) -eq $Lock.dotnetSdk) -and ((& pwsh -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion.ToString()') -eq $Lock.powershell.version) }
function New-ZipArchive($Path,[string]$EntryName){ Add-Type -AssemblyName System.IO.Compression.FileSystem; $dir=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()); New-Item -ItemType Directory -Force $dir|Out-Null; if($EntryName){'x'|Set-Content -NoNewline -LiteralPath (Join-Path $dir $EntryName)}; [IO.Compression.ZipFile]::CreateFromDirectory($dir,$Path) }
function New-TarGzArchive($Path,[string]$EntryName){ $dir=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()); New-Item -ItemType Directory -Force $dir|Out-Null; if($EntryName){'x'|Set-Content -NoNewline -LiteralPath (Join-Path $dir $EntryName)}; Invoke-ExpectSuccess -File 'tar' -ArgumentList @('-czf',$Path,'-C',$dir,'.') -WorkingDirectory $RepoRoot|Out-Null }
Assert 'manifest, bootstrap, Dockerfile, and global.json are coherent' { $b=Read-BusinessOSBootstrapLock; $m=Read-BusinessOSEnvironmentLock; $g=Get-Content (Join-Path $RepoRoot 'global.json') -Raw|ConvertFrom-Json; $docker=Get-Content (Join-Path $RepoRoot '.devcontainer/Dockerfile') -Raw; $expectedDotnet=@{'linux-x64'='F78DBAC30C9AF2230D67FF5C224DE3A5DBF63F8A78D1C206594DEDB80E6909D2CC8A9D865D5105C72C2FD2AA266FC0C6C77DEDAC60408CBCCF272B116BD11B07';'osx-x64'='595C3C661A705A256F52E03E3AEEB86753AD6F9AA3D59F487304CDBBB744A39F4E3FA6445A60CDED6BC78E12F51D52ED5A183EA70A0560B96BED61FB83958F81';'win-x64'='24B033418A3969EFFD49B4651EF7EBBFFEB284773B99545D78DCE61A82E57F38DB7FACDB013C609BA15573C072F0E093363AE470824A6847F3C6111078C1FB64'}; if($b.DOTNET_SDK_VERSION -ne $m.dotnetSdk -or $m.dotnetSdk -ne $g.sdk.version){throw 'dotnet version mismatch'}; foreach($rid in 'linux-x64','osx-x64','win-x64'){ $envRid=$rid.ToUpperInvariant().Replace('-','_'); $urlProp="DOTNET_${envRid}_URL"; $shaProp="DOTNET_${envRid}_SHA512"; if($b.$urlProp -ne $m.dotnet.archives.$rid.url){throw "dotnet url mismatch $rid"}; if($b.$shaProp -ne $m.dotnet.archives.$rid.sha512){throw "dotnet sha mismatch $rid"}; if($m.dotnet.archives.$rid.sha512 -ne $expectedDotnet[$rid]){throw "unexpected official dotnet sha $rid"}; if($m.dotnet.archives.$rid.sha512 -match '^(.)\1+$'){throw "placeholder dotnet hash $rid"} }; if($b.POWERSHELL_LINUX_X64_URL -ne $m.powershell.archives.'linux-x64'.urls[0] -or $b.POWERSHELL_LINUX_X64_SHA256 -ne $m.powershell.archives.'linux-x64'.sha256){throw 'PowerShell linux mismatch'}; if($docker -notmatch [regex]::Escape($m.dotnetSdk) -or $docker -notmatch [regex]::Escape($m.powershell.version) -or $docker -notmatch [regex]::Escape($m.powershell.archives.'linux-x64'.sha256)){throw 'Dockerfile mismatch'}; if($b.DOTNET_ROOT_REL -ne $m.dotnetRoot -or $b.POWERSHELL_ROOT_REL -ne $m.powershellRoot -or $b.NUGET_CACHE_REL -ne $m.nugetCache -or $b.DOTNET_HOME_REL -ne $m.dotnetHome){throw 'tool/cache root mismatch'} }
Assert 'Invoke-ProcessForTest forwards every explicit argument' {
  $marker = "argument space apostrof' [1] nawiasy() znak`$dolara"
  $probeDirectory = Join-Path ([IO.Path]::GetTempPath()) ("argument probe space apostrof' [1] nawiasy() znak`$dolara " + [Guid]::NewGuid())
  $probePath = Join-Path $probeDirectory 'argument-forwarding-probe.ps1'
  try {
    New-Item -ItemType Directory -Force -Path $probeDirectory | Out-Null
    @'
param(
    [Parameter(Mandatory)]
    [string]$Marker
)

[Console]::Out.WriteLine($Marker)
'@ | Set-Content -LiteralPath $probePath

    $result = Invoke-ProcessForTest `
        -File 'pwsh' `
        -ArgumentList @(
            '-NoLogo'
            '-NoProfile'
            '-NonInteractive'
            '-File'
            $probePath
            '-Marker'
            $marker
        ) `
        -WorkingDirectory $RepoRoot

    if($result.ExitCode -ne 0){throw "argument forwarding process failed: $($result.Combined)"}
    if($result.Output.Trim() -ne $marker){throw "stdout did not contain exactly marker: $($result.Output)"}
    if($result.ArgumentList.Count -ne 7){throw "unexpected argument count: $($result.ArgumentList.Count)"}
    foreach($expected in '-NoProfile','-NonInteractive','-File',$probePath,'-Marker',$marker){if($result.ArgumentList -notcontains $expected){throw "missing forwarded argument: $expected"}}
    if($result.Output -match [regex]::Escape("PS $($result.WorkingDirectory)>")){throw 'stdout contained interactive prompt'}
  }
  finally {
    Remove-Item -LiteralPath $probeDirectory -Recurse -Force -ErrorAction SilentlyContinue
  }
}
Assert 'Invoke-ProcessForTest accepts empty arguments for noninteractive process' { $result = Invoke-ProcessForTest -File 'dotnet' -ArgumentList @() -WorkingDirectory $RepoRoot; if($result.ArgumentList.Count -ne 0){throw 'empty argument list was not preserved'} }
Assert 'doctor accepts a complete valid fixture' { $d=Copy-FixtureRepo; $doctorResult=Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/doctor.ps1'),'-Mode','CrossPlatform','-SkipEnvironmentTests') -WorkingDirectory $d; if($doctorResult.Combined -notmatch [regex]::Escape('Environment ready: YES')){throw 'doctor output did not confirm ready environment'}; if($doctorResult.Combined -notmatch [regex]::Escape('BusinessOS.sln')){throw 'doctor output did not mention BusinessOS.sln'}; if(@(Get-DoctorFailureRecords -RunResult $doctorResult).Count -ne 0){throw 'valid doctor emitted failure records'} }
Assert 'PowerShell activation is idempotent' {
  $harnessBefore=[pscustomobject]@{RepoRoot=$RepoRoot;Path=$env:PATH;PSModulePath=$env:PSModulePath;DOTNET_ROOT=$env:DOTNET_ROOT;NUGET_PACKAGES=$env:NUGET_PACKAGES;DOTNET_CLI_HOME=$env:DOTNET_CLI_HOME;Location=(Get-Location).Path}
  $d=Copy-FixtureRepo
  New-Item -ItemType Directory -Force (Join-Path $d '.cache')|Out-Null
  $resolved=[pscustomobject]@{dotnetExecutable=(Join-Path $d '.tools/dotnet/dotnet');dotnetRoot=(Join-Path $d '.tools/dotnet');dotnetSource='local';powershellExecutable=(Join-Path $d '.tools/powershell/pwsh');powershellRoot=(Join-Path $d '.tools/powershell');powershellSource='local';nugetPackages=(Join-Path $d '.cache/nuget');dotnetCliHome=(Join-Path $d '.cache/dotnet-home');powershellModuleRoot=(Join-Path $d '.tools/powershell-modules')}
  $resolved|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $d '.cache/environment.resolved.json')
  $probePath=Join-Path $d 'tests/environment/activation-idempotency-probe.ps1'
  @'
param([Parameter(Mandatory=$true)][string]$ActivationPath)
$ErrorActionPreference='Stop'
$beforePath=$env:PATH
$beforeModulePath=$env:PSModulePath
. $ActivationPath
$first=[pscustomobject]@{Path=$env:PATH;PSModulePath=$env:PSModulePath;DOTNET_ROOT=$env:DOTNET_ROOT;NUGET_PACKAGES=$env:NUGET_PACKAGES;DOTNET_CLI_HOME=$env:DOTNET_CLI_HOME}
. $ActivationPath
$second=[pscustomobject]@{Path=$env:PATH;PSModulePath=$env:PSModulePath;DOTNET_ROOT=$env:DOTNET_ROOT;NUGET_PACKAGES=$env:NUGET_PACKAGES;DOTNET_CLI_HOME=$env:DOTNET_CLI_HOME}
if($second.Path -ne $first.Path){throw 'activation duplicated PATH entries'}
if($second.PSModulePath -ne $first.PSModulePath){throw 'activation duplicated PSModulePath entries'}
foreach($p in 'DOTNET_ROOT','NUGET_PACKAGES','DOTNET_CLI_HOME'){
  if($second.$p -ne $first.$p){throw "activation changed resolved root: $p"}
}
if([string]::IsNullOrEmpty($beforePath) -and [string]::IsNullOrEmpty($first.Path)){throw 'PATH was not captured'}
if([string]::IsNullOrEmpty($beforeModulePath) -and [string]::IsNullOrEmpty($first.PSModulePath)){throw 'PSModulePath was not captured'}
Write-Output 'ACTIVATION_IDEMPOTENCY_PROBE_OK'
'@ | Set-Content -LiteralPath $probePath
  $probeResult=Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',$probePath,(Join-Path $d 'eng/activate-environment.ps1')) -WorkingDirectory $d; if($probeResult.Output -notmatch [regex]::Escape('ACTIVATION_IDEMPOTENCY_PROBE_OK')){throw 'activation probe marker missing'}
  $harnessAfter=[pscustomobject]@{RepoRoot=$RepoRoot;Path=$env:PATH;PSModulePath=$env:PSModulePath;DOTNET_ROOT=$env:DOTNET_ROOT;NUGET_PACKAGES=$env:NUGET_PACKAGES;DOTNET_CLI_HOME=$env:DOTNET_CLI_HOME;Location=(Get-Location).Path}
  foreach($p in 'RepoRoot','Path','PSModulePath','DOTNET_ROOT','NUGET_PACKAGES','DOTNET_CLI_HOME','Location'){
    if($harnessAfter.$p -ne $harnessBefore.$p){throw "activation probe mutated test harness: $p"}
  }
}
Assert 'PowerShell activation probe does not mutate test harness' { if($RepoRoot -ne (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path){throw 'canonical repo root changed'} }
Assert 'PowerShell setup is idempotent with matching host tools' {
  $lock=Read-BusinessOSEnvironmentLock
  if(-not(HostToolsMatch $lock)){Write-Host 'SKIPPED host tools do not match manifest'; return}
  $d=Copy-FixtureRepo
  $setupPath=Join-Path $d 'eng/setup-environment.ps1'
  $jsonPath=Join-Path $d '.cache/environment.resolved.json'
  $envPath=Join-Path $d '.cache/environment.resolved.env'
  $run1=Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',$setupPath) -WorkingDirectory $d
  Assert-ResolvedFileExists $jsonPath $run1 'first PowerShell setup run'
  Assert-ResolvedFileExists $envPath $run1 'first PowerShell setup run'
  if($run1.Output -notmatch [regex]::Escape($jsonPath)){throw "setup stdout did not contain expected resolved path $jsonPath. STDOUT:`n$($run1.Output)"}; if($run1.Output -match '(?s)^PowerShell .+PS .+>'){throw "setup stdout looked like an interactive PowerShell session without setup output. STDOUT:`n$($run1.Output)"}; if($run1.Output -notmatch [regex]::Escape('BusinessOS environment setup completed.')){throw "setup stdout did not contain completion message. STDOUT:`n$($run1.Output)"}
  $r1=Read-Resolved $d
  Assert-ExpectedFixtureResolvedState $r1 $d
  $run2=Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',$setupPath) -WorkingDirectory $d
  Assert-ResolvedFileExists $jsonPath $run2 'second PowerShell setup run'
  Assert-ResolvedFileExists $envPath $run2 'second PowerShell setup run'
  $r2=Read-Resolved $d
  Assert-ExpectedFixtureResolvedState $r2 $d
  Compare-Resolved $r1 $r2
}
Assert 'Bash setup is idempotent with matching host tools and special path' { if($IsWindows -or -not(Get-Command bash -ErrorAction SilentlyContinue)){Write-Host 'SKIPPED bash setup test on this platform'; return}; $lock=Read-BusinessOSEnvironmentLock; if(-not(HostToolsMatch $lock)){Write-Host 'SKIPPED host tools do not match manifest'; return}; $expectedDotnetExecutable=(Get-Command dotnet -ErrorAction Stop).Source; $expectedDotnetRoot=Split-Path -Parent $expectedDotnetExecutable; $expectedPwshExecutable=(Get-Command pwsh -ErrorAction Stop).Source; $expectedPwshRoot=Split-Path -Parent $expectedPwshExecutable; $d=Copy-FixtureRepo "Business OS a'b`$c\d(e) $([Guid]::NewGuid())"; Invoke-ExpectSuccess -File 'bash' -ArgumentList @('-c','./eng/setup-environment.sh') -WorkingDirectory $d|Out-Null; $r1=Read-Resolved $d; $firstEnv=Get-Content -LiteralPath (Join-Path $d '.cache/environment.resolved.env') -Raw; $firstJson=Get-Content -LiteralPath (Join-Path $d '.cache/environment.resolved.json') -Raw; Invoke-ExpectSuccess -File 'bash' -ArgumentList @('-c','./eng/setup-environment.sh') -WorkingDirectory $d|Out-Null; $r2=Read-Resolved $d; $secondEnv=Get-Content -LiteralPath (Join-Path $d '.cache/environment.resolved.env') -Raw; $secondJson=Get-Content -LiteralPath (Join-Path $d '.cache/environment.resolved.json') -Raw; if($secondEnv -ne $firstEnv){throw 'Resolved ENV changed after second Bash setup'}; Compare-Resolved $r1 $r2; if($r2.dotnetExecutable -ne $expectedDotnetExecutable){throw 'Bash setup did not resolve expected host dotnet executable'}; if($r2.dotnetRoot -ne $expectedDotnetRoot){throw 'Bash setup did not resolve expected host dotnet root'}; if($r2.powershellExecutable -ne $expectedPwshExecutable){throw 'Bash setup did not resolve expected host PowerShell executable'}; if($r2.powershellRoot -ne $expectedPwshRoot){throw 'Bash setup did not resolve expected host PowerShell root'}; $expectedNuget=Join-Path $d '.cache/nuget'; $expectedDotnetHome=Join-Path $d '.cache/dotnet-home'; $expectedPsModule=Join-Path $d '.tools/powershell-modules'; if($r2.nugetPackages -ne $expectedNuget){throw 'Bash setup did not resolve expected fixture NuGet cache'}; if($r2.dotnetCliHome -ne $expectedDotnetHome){throw 'Bash setup did not resolve expected fixture dotnet-home'}; if($r2.powershellModuleRoot -ne $expectedPsModule){throw 'Bash setup did not resolve expected fixture PSModule path'}; $envResult=Invoke-ExpectSuccess -File 'bash' -ArgumentList @('-c','set -euo pipefail; set -a; source .cache/environment.resolved.env; set +a; pwsh -NoLogo -NoProfile -NonInteractive -Command ''[ordered]@{ dotnetExecutable = $env:DOTNET_EXE; dotnetRoot = $env:DOTNET_ROOT; dotnetSource = $env:DOTNET_SOURCE; powershellExecutable = $env:PWSH_EXE; powershellRoot = $env:POWERSHELL_ROOT; powershellSource = $env:POWERSHELL_SOURCE; nugetPackages = $env:NUGET_PACKAGES; dotnetCliHome = $env:DOTNET_CLI_HOME; powershellModuleRoot = $env:PSMODULE_ROOT } | ConvertTo-Json -Compress''') -WorkingDirectory $d; if([string]::IsNullOrWhiteSpace($envResult.Output)){throw 'Bash sourced environment produced empty JSON output'}; $resolvedFromEnv=$envResult.Output | ConvertFrom-Json; Compare-Resolved $resolvedFromEnv $r2 }
Assert 'doctor rejects empty solution with concrete output' {
  $d=Copy-FixtureRepo
  'Microsoft Visual Studio Solution File'|Set-Content -LiteralPath (Join-Path $d 'BusinessOS.sln')
  $r=Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/doctor.ps1'),'-Mode','CrossPlatform','-SkipEnvironmentTests') -WorkingDirectory $d -Contains 'BusinessOS.sln'
  if($r.Combined -notmatch [regex]::Escape('Environment ready: NO')){throw 'doctor output did not reject environment'}
  Assert-DoctorFailureRecord `
    -RunResult $r `
    -Component 'BusinessOS.sln' `
    -Required 'non-empty solution' `
    -Detected '0 project entries'
}
Assert 'doctor rejects missing UnitTests with concrete output' { $d=Copy-FixtureRepo; Remove-Item (Join-Path $d 'tests/BusinessOS.UnitTests/BusinessOS.UnitTests.csproj'); $r=Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/doctor.ps1'),'-Mode','CrossPlatform','-SkipEnvironmentTests') -WorkingDirectory $d -Contains 'tests/BusinessOS.UnitTests/BusinessOS.UnitTests.csproj'; Assert-DoctorFailureRecord -RunResult $r -Component 'tests/BusinessOS.UnitTests/BusinessOS.UnitTests.csproj' -Required 'present' -Detected $false }
Assert 'doctor rejects global mismatch with concrete output' { $d=Copy-FixtureRepo; $lock=Read-BusinessOSEnvironmentLock; '{"sdk":{"version":"0.0.1"}}'|Set-Content -LiteralPath (Join-Path $d 'global.json'); $r=Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/doctor.ps1'),'-Mode','CrossPlatform','-SkipEnvironmentTests') -WorkingDirectory $d -Contains 'global.json SDK'; if($r.Combined -notmatch [regex]::Escape('Environment ready: NO')){throw 'doctor output did not reject environment'}; Assert-DoctorFailureRecord -RunResult $r -Component 'global.json SDK' -Required $lock.dotnetSdk -Detected '0.0.1' }
Assert 'doctor failure diagnostics are independent from formatted table width' {
  $d=Copy-FixtureRepo
  'Microsoft Visual Studio Solution File'|Set-Content -LiteralPath (Join-Path $d 'BusinessOS.sln')
  $r=Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/doctor.ps1'),'-Mode','CrossPlatform','-SkipEnvironmentTests') -WorkingDirectory $d
  Assert-DoctorFailureRecord `
    -RunResult $r `
    -Component 'BusinessOS.sln' `
    -Required 'non-empty solution' `
    -Detected '0 project entries'
}
Assert 'Assert-FileHash rejects placeholders and accepts valid checksum' { $f=New-FileWithHash 'abc'; $sha=(Get-FileHash $f -Algorithm SHA256).Hash; Assert-FileHash $f $sha SHA256|Out-Null; foreach($bad in '', 'abc', ('0'*64), ('1'*64), ('2'*64), ('3'*64), ('A'*64)){ $failed=$false; try{Assert-FileHash $f $bad SHA256|Out-Null}catch{$failed=$true}; if(-not $failed){throw "accepted bad checksum $bad"} } }
Assert 'Invoke-DownloadWithFallback uses second adapter after first failure' { $f=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()); $source=New-FileWithHash 'ok'; $sha=(Get-FileHash $source -Algorithm SHA256).Hash; $adapter={param($url,$out) if($url -eq 'bad'){[pscustomobject]@{StatusCode=500;Bytes=0}}else{Copy-Item $source $out;[pscustomobject]@{StatusCode=200;Bytes=(Get-Item $out).Length}}}; $r=Invoke-DownloadWithFallback @('bad','good') $f $sha SHA256 $adapter; if($r.Url -ne 'good'){throw 'fallback did not reach good source'} }
Assert 'Invoke-DownloadWithFallback rejects empty and all-failed downloads' { $f=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()); $sha='A'*64; $adapter={param($url,$out)''|Set-Content -NoNewline $out;[pscustomobject]@{StatusCode=200;Bytes=0}}; $failed=$false; try{Invoke-DownloadWithFallback @('empty') $f $sha SHA256 $adapter|Out-Null}catch{$failed=$true}; if(-not $failed){throw 'accepted empty download'} }
Assert 'Expand-VerifiedArchive accepts valid ZIP with tool.exe' { $zip=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString()+'.zip'); New-ZipArchive $zip 'tool.exe'; $dest=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()); $exe=Expand-VerifiedArchive $zip $dest zip 'tool.exe'; if(-not(Test-Path -LiteralPath $exe)){throw 'tool.exe missing after valid ZIP extraction'} }
Assert 'Expand-VerifiedArchive rejects valid ZIP without tool.exe' { $zip=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString()+'.zip'); New-ZipArchive $zip 'readme.txt'; $failed=$false; try{Expand-VerifiedArchive $zip (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid())) zip 'tool.exe'|Out-Null}catch{if($_.Exception.Message -match 'Expected executable missing'){$failed=$true}}; if(-not $failed){throw 'valid ZIP without tool.exe was not rejected'} }
Assert 'Expand-VerifiedArchive rejects corrupt and empty ZIP' { $bad=New-FileWithHash 'not zip'; $empty=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString()+'.zip'); New-Item -ItemType File -Path $empty|Out-Null; foreach($case in @(@($bad,'Invalid ZIP archive'),@($empty,'Archive is empty'))){$failed=$false; try{Expand-VerifiedArchive $case[0] (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid())) zip 'tool.exe'|Out-Null}catch{if($_.Exception.Message -match [regex]::Escape($case[1])){$failed=$true}}; if(-not $failed){throw "ZIP case was not rejected: $($case[1])"}} }
Assert 'Expand-VerifiedArchive accepts valid TAR.GZ with tool' { if(-not(Get-Command tar -ErrorAction SilentlyContinue)){Write-Host 'SKIPPED tar unavailable'; return}; $tgz=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString()+'.tar.gz'); New-TarGzArchive $tgz 'tool'; $dest=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()); $exe=Expand-VerifiedArchive $tgz $dest 'tar.gz' 'tool'; if(-not(Test-Path -LiteralPath $exe)){throw 'tool missing after valid TAR.GZ extraction'} }
Assert 'Expand-VerifiedArchive rejects valid TAR.GZ without tool' { if(-not(Get-Command tar -ErrorAction SilentlyContinue)){Write-Host 'SKIPPED tar unavailable'; return}; $tgz=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString()+'.tar.gz'); New-TarGzArchive $tgz 'readme.txt'; $failed=$false; try{Expand-VerifiedArchive $tgz (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid())) 'tar.gz' 'tool'|Out-Null}catch{if($_.Exception.Message -match 'Expected executable missing'){$failed=$true}}; if(-not $failed){throw 'valid TAR.GZ without tool was not rejected'} }
Assert 'Expand-VerifiedArchive rejects corrupt and empty TAR.GZ' { if(-not(Get-Command tar -ErrorAction SilentlyContinue)){Write-Host 'SKIPPED tar unavailable'; return}; $bad=New-FileWithHash 'not tar'; $empty=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString()+'.tar.gz'); New-Item -ItemType File -Path $empty|Out-Null; foreach($case in @(@($bad,'Invalid TAR.GZ archive'),@($empty,'Archive is empty'))){$failed=$false; try{Expand-VerifiedArchive $case[0] (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid())) 'tar.gz' 'tool'|Out-Null}catch{if($_.Exception.Message -match [regex]::Escape($case[1])){$failed=$true}}; if(-not $failed){throw "TAR.GZ case was not rejected: $($case[1])"}} }

Assert 'Invoke-CheckedCommand captures stdout and stderr without Runspace errors' { $r=Invoke-CheckedCommand pwsh @('-NoLogo','-NoProfile','-NonInteractive','-Command',"[Console]::Out.WriteLine('stdout-marker'); [Console]::Error.WriteLine('stderr-marker'); exit 0") $RepoRoot; if($r.ExitCode -ne 0){throw 'exit code was not 0'}; if($r.StdOut -notcontains 'stdout-marker'){throw 'stdout marker missing'}; if($r.StdErr -notcontains 'stderr-marker'){throw 'stderr marker missing'} }
Assert 'Invoke-CheckedCommand handles large dual-stream output without deadlock' {
  $count=5000
  $probeDirectory = Join-Path ([IO.Path]::GetTempPath()) ("large stream probe space apostrof' [1] nawiasy() znak`$dolara " + [Guid]::NewGuid())
  $probePath = Join-Path $probeDirectory 'large-stream-probe.ps1'
  try {
    New-Item -ItemType Directory -Force -Path $probeDirectory | Out-Null
    @'
param(
    [Parameter(Mandatory)]
    [int]$Count
)

for ($i = 1; $i -le $Count; $i++) {
    [Console]::Out.WriteLine("stdout-large-$i")
    [Console]::Error.WriteLine("stderr-large-$i")
}

exit 0
'@ | Set-Content -LiteralPath $probePath

    $timer=[Diagnostics.Stopwatch]::StartNew()
    $r=Invoke-CheckedCommand pwsh @(
      '-NoLogo'
      '-NoProfile'
      '-NonInteractive'
      '-File'
      $probePath
      '-Count'
      $count.ToString([Globalization.CultureInfo]::InvariantCulture)
    ) $RepoRoot
    $timer.Stop()
    "LARGE_STREAM_SECONDS=$($timer.Elapsed.TotalSeconds)"|Tee-Object -FilePath $log -Append
    if($timer.Elapsed.TotalSeconds -gt 30){throw "large stream test exceeded 30s: $($timer.Elapsed.TotalSeconds)"}
    if($r.ExitCode -ne 0){throw 'exit code was not 0'}
    foreach($marker in 'stdout-large-1',"stdout-large-$count"){if($r.StdOut -notcontains $marker){throw "missing stdout marker $marker"}}
    foreach($marker in 'stderr-large-1',"stderr-large-$count"){if($r.StdErr -notcontains $marker){throw "missing stderr marker $marker"}}
  }
  finally {
    Remove-Item -LiteralPath $probeDirectory -Recurse -Force -ErrorAction SilentlyContinue
  }
}
Assert 'Invoke-CheckedCommand throws command and code for non-zero exit' { $failed=$false; try{Invoke-CheckedCommand pwsh @('-NoLogo','-NoProfile','-NonInteractive','-Command',"[Console]::Out.WriteLine('nonzero-stdout'); [Console]::Error.WriteLine('nonzero-stderr'); exit 23") $RepoRoot|Out-Null}catch{$failed=$true; if($_.Exception.Message -notmatch '23' -or $_.Exception.Message -notmatch 'pwsh'){throw "unexpected exception message: $($_.Exception.Message)"}}; if(-not $failed){throw 'non-zero command did not throw'} }
Assert 'Invoke-CheckedCommand invokes direct ps1 and preserves spaces' { $d=Join-Path ([IO.Path]::GetTempPath()) ('direct ps1 '+[Guid]::NewGuid()); New-Item -ItemType Directory -Force $d|Out-Null; try{ $script=Join-Path $d 'script with space.ps1'; 'param([string]$Value) [Console]::Out.WriteLine("direct-value=$Value")'|Set-Content -LiteralPath $script; $r=Invoke-CheckedCommand $script @('hello world') $d; if($r.ExitCode -ne 0){throw 'exit code was not 0'}; if($r.StdOut -notcontains 'direct-value=hello world'){throw 'direct ps1 output missing argument with spaces'} } finally { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'Invoke-CheckedCommand honors special working directory' { $d=Join-Path ([IO.Path]::GetTempPath()) ("spacje apostrof' nawiasy() znak`$dolara " + [Guid]::NewGuid()); New-Item -ItemType Directory -Force $d|Out-Null; try{ $r=Invoke-CheckedCommand pwsh @('-NoLogo','-NoProfile','-NonInteractive','-Command','[Console]::Out.WriteLine((Get-Location).Path); exit 0') $d; if($r.ExitCode -ne 0){throw 'exit code was not 0'}; if($r.WorkingDirectory -ne $d){throw "result working directory mismatch: $($r.WorkingDirectory)"}; if($r.StdOut -notcontains $d){throw 'process did not run in special working directory'} } finally { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'doctor rejects forced NuGet cache cleanup failure' {
  $d=Copy-FixtureRepo
  $old=$env:BUSINESSOS_DOCTOR_FORCE_CACHE_FAILURE
  try{
    $env:BUSINESSOS_DOCTOR_FORCE_CACHE_FAILURE='forced cache failure for test'
    $r=Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/doctor.ps1'),'-Mode','CrossPlatform','-SkipEnvironmentTests') -WorkingDirectory $d -Contains 'NuGet cache'
    Assert-DoctorFailureRecord -RunResult $r -Component 'NuGet cache' -Required 'writable' -Detected 'forced cache failure for test'
    if($r.Combined -match 'Environment ready: YES'){throw 'doctor reported ready despite failing cache'}
  }
  finally { $env:BUSINESSOS_DOCTOR_FORCE_CACHE_FAILURE=$old }
}
Assert 'Invoke-CheckedCommand handles script path and argument with spaces' { $d=Join-Path ([IO.Path]::GetTempPath()) ('space dir '+[Guid]::NewGuid()); New-Item -ItemType Directory -Force $d|Out-Null; $script=Join-Path $d 'script with space.ps1'; 'param([string]$Value) Write-Output "VALUE=$Value"'|Set-Content -LiteralPath $script; $r=Invoke-CheckedCommand $script @('hello world') $d; if(($r.StdOut -join '') -notmatch 'hello world'){throw 'argument with space was not preserved'} }
Assert 'Invoke-ProcessForTest handles large stdout and stderr concurrently' { $r=Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-Command','1..2000|%{Write-Output "out $_"; Write-Error "err $_" -ErrorAction Continue}') -WorkingDirectory $RepoRoot; if($r.Output.Length -lt 1000 -or $r.Error.Length -lt 1000){throw 'missing stream output'} }
Assert 'cross-platform filter contains required projects and excludes Desktop/Infrastructure' { $p=Get-BusinessOSCrossPlatformFilterProjects $RepoRoot; foreach($r in 'BusinessOS.BuildingBlocks.Domain.csproj','BusinessOS.BuildingBlocks.Application.csproj','BusinessOS.UnitTests.csproj','BusinessOS.ArchitectureTests.csproj'){if(-not(($p|Split-Path -Leaf)-contains $r)){throw "missing $r"}}; if(($p -join ';') -match 'Desktop|Infrastructure'){throw 'excluded project present'} }

function New-VulnerabilityScanFixture {
  $d=Join-Path ([IO.Path]::GetTempPath()) ("vulnerability scan fixture space apostrof' [1] nawiasy() znak`$dolara "+[Guid]::NewGuid())
  New-Item -ItemType Directory -Force $d|Out-Null
  Copy-Item -LiteralPath (Join-Path $RepoRoot 'BusinessOS.CrossPlatform.slnf') -Destination (Join-Path $d 'BusinessOS.CrossPlatform.slnf')
  Copy-Item -LiteralPath (Join-Path $RepoRoot 'BusinessOS.sln') -Destination (Join-Path $d 'BusinessOS.sln')
  New-Item -ItemType Directory -Force (Join-Path $d 'eng')|Out-Null
  Copy-Item -LiteralPath (Join-Path $RepoRoot 'eng/check-vulnerable-packages.ps1') -Destination (Join-Path $d 'eng/check-vulnerable-packages.ps1')
  Copy-Item -LiteralPath (Join-Path $RepoRoot 'eng/BusinessOS.Engineering.psm1') -Destination (Join-Path $d 'eng/BusinessOS.Engineering.psm1')
  $filter=Get-Content -LiteralPath (Join-Path $d 'BusinessOS.CrossPlatform.slnf') -Raw|ConvertFrom-Json
  foreach($project in @($filter.solution.projects)+@('src/BusinessOS.Desktop/BusinessOS.Desktop.csproj','src/Infrastructure/Infrastructure.csproj')){
    $path=Join-Path $d $project
    New-Item -ItemType Directory -Force (Split-Path -Parent $path)|Out-Null
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'|Set-Content -LiteralPath $path
  }
  $d
}
function New-FakeDotnetProbe([string]$Directory,[string]$Mode='clean'){
  $path=Join-Path $Directory 'fake dotnet probe.ps1'
  @'
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$ProbeArgumentList)
$ErrorActionPreference='Stop'
$record=[pscustomobject]@{ arguments=@($ProbeArgumentList) }
$record|ConvertTo-Json -Compress -Depth 20|Add-Content -LiteralPath $env:BUSINESSOS_FAKE_DOTNET_LOG
[Console]::Error.WriteLine("fake dotnet warning on stderr")
if($env:BUSINESSOS_FAKE_DOTNET_MODE -eq 'malformed') { [Console]::Out.WriteLine('{ not json'); exit 0 }
if($env:BUSINESSOS_FAKE_DOTNET_MODE -eq 'missing-path') { [pscustomobject]@{version=1;projects=@([pscustomobject]@{})}|ConvertTo-Json -Depth 50; exit 0 }
$projectIndex=[Array]::IndexOf($ProbeArgumentList,'--project')
$project=if($projectIndex -ge 0 -and $projectIndex + 1 -lt $ProbeArgumentList.Count){$ProbeArgumentList[$projectIndex+1]}else{'missing-project'}
$package=@()
if($env:BUSINESSOS_FAKE_DOTNET_MODE -eq 'vulnerable'){
  $package=@([pscustomobject]@{id='Vulnerable.Package';resolvedVersion='1.2.3';vulnerabilities=@([pscustomobject]@{severity='High';advisoryurl='https://example.test/advisory'})})
  [pscustomobject]@{version=1;parameters='--vulnerable --include-transitive';sources=@('https://api.nuget.org/v3/index.json');projects=@([pscustomobject]@{path=$project;frameworks=@([pscustomobject]@{framework='net10.0';topLevelPackages=$package;transitivePackages=@()})})}|ConvertTo-Json -Depth 50
  exit 0
}
[pscustomobject]@{version=1;parameters='--vulnerable --include-transitive';sources=@('https://api.nuget.org/v3/index.json');projects=@([pscustomobject]@{path=$project})}|ConvertTo-Json -Depth 50
'@|Set-Content -LiteralPath $path
  $path
}
function Read-FakeDotnetInvocations([string]$Path){ if(-not(Test-Path -LiteralPath $Path)){return @()}; @(Get-Content -LiteralPath $Path|Where-Object{$_}|ForEach-Object{$_|ConvertFrom-Json}) }
function Assert-VulnerabilityInvocationSyntax($Invocation){
  $a=@($Invocation.arguments)
  $expected='package','list','--project',$a[3],'--vulnerable','--include-transitive','--format','json','--output-version','1','--no-restore'
  if($a.Count -ne $expected.Count){throw "unexpected argument count: $($a -join '|')"}
  for($i=0;$i -lt $expected.Count;$i++){if($a[$i] -ne $expected[$i]){throw "argument $i expected '$($expected[$i])' got '$($a[$i])'"}}
  $target=$a[3]
  if(@($a|Where-Object{$_ -eq $target}).Count -ne 1){throw 'target does not appear exactly once'}
  if($a[2] -ne '--project'){throw '--project does not immediately precede target'}
  if($a[0] -ne 'package' -or $a[1] -ne 'list'){throw 'missing package list prefix'}
  if($a[2] -eq $target){throw 'old positional target layout detected'}
  if(@($a|Where-Object{[string]::IsNullOrEmpty($_)}).Count -gt 0){throw 'empty argument detected'}
  if($a.Count -eq 1 -and $a[0] -match 'package\s+list'){throw 'single composed command detected'}
}
Assert 'vulnerability scanner uses .NET 10 package list syntax' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='clean'; Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.sln'),'-DotnetExecutable',$fake) -WorkingDirectory $d|Out-Null; foreach($i in Read-FakeDotnetInvocations $logPath){Assert-VulnerabilityInvocationSyntax $i} } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'vulnerability scanner expands solution filter projects' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='clean'; Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.CrossPlatform.slnf'),'-DotnetExecutable',$fake) -WorkingDirectory $d|Out-Null; $filter=Get-Content -LiteralPath (Join-Path $d 'BusinessOS.CrossPlatform.slnf') -Raw|ConvertFrom-Json; $expected=@($filter.solution.projects|ForEach-Object{(Resolve-Path -LiteralPath (Join-Path $d $_)).Path}|Select-Object -Unique); $inv=Read-FakeDotnetInvocations $logPath; if($inv.Count -ne $expected.Count){throw "unexpected invocation count $($inv.Count) expected $($expected.Count)"}; $artifact=Get-Content -LiteralPath (Join-Path $d '.cache/vulnerable-packages.json') -Raw|ConvertFrom-Json; if(@($artifact.targets).Count -ne $expected.Count){throw "unexpected target count $(@($artifact.targets).Count) expected $($expected.Count)"}; if(@($artifact.reports).Count -ne @($artifact.targets).Count){throw 'report count does not match target count'}; $firstProject=@(@($artifact.reports)[0].projects)[0]; if($null -ne $firstProject.frameworks){throw 'clean report unexpectedly contained frameworks'}; foreach($i in $inv){Assert-VulnerabilityInvocationSyntax $i; $target=@($i.arguments)[3]; if($target -notlike '*.csproj'){throw "target is not csproj: $target"}; if($target -like '*.slnf' -or $target -like '*.sln'){throw "solution file scanned: $target"}; if($target -match 'BusinessOS\.Desktop|Infrastructure'){throw "excluded target scanned: $target"}; if($target -like '*BusinessOS.sln'){throw "BusinessOS.sln scanned: $target"}; if(-not [IO.Path]::IsPathRooted($target)){throw "target is not absolute: $target"}; if(-not(Test-Path -LiteralPath $target)){throw "target missing: $target"}; if($expected -notcontains $target){throw "unexpected target: $target"} }; foreach($report in @($artifact.reports)){ $project=@($report.projects)[0]; if([string]::IsNullOrWhiteSpace([string]$project.path)){throw 'artifact project path missing'}; if($expected -notcontains $project.path){throw "artifact report path was not expected: $($project.path)"}; if($null -ne $project.frameworks){throw "clean artifact report unexpectedly contained frameworks: $($project.path)"} } } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'vulnerability scanner scans solution as one target' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='clean'; Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.sln'),'-DotnetExecutable',$fake) -WorkingDirectory $d|Out-Null; $inv=Read-FakeDotnetInvocations $logPath; if($inv.Count -ne 1){throw "expected one invocation"}; Assert-VulnerabilityInvocationSyntax $inv[0]; if(@($inv[0].arguments)[3] -ne (Resolve-Path -LiteralPath (Join-Path $d 'BusinessOS.sln')).Path){throw 'solution target mismatch'} } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'vulnerability scanner keeps stderr separate from JSON' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='clean'; Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.sln'),'-DotnetExecutable',$fake) -WorkingDirectory $d|Out-Null; $artifact=Get-Content -LiteralPath (Join-Path $d '.cache/vulnerable-packages.json') -Raw; if($artifact -match 'fake dotnet warning'){throw 'stderr leaked into JSON artifact'}; $artifact|ConvertFrom-Json|Out-Null } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'vulnerability scanner accepts report without frameworks' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='clean'; $r=Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.sln'),'-DotnetExecutable',$fake) -WorkingDirectory $d; if($r.Combined -notmatch [regex]::Escape('No vulnerable NuGet packages were reported')){throw 'success output did not report clean vulnerability result'}; $artifact=Get-Content -LiteralPath (Join-Path $d '.cache/vulnerable-packages.json') -Raw|ConvertFrom-Json; if(@($artifact.reports).Count -ne 1){throw "expected one report, got $(@($artifact.reports).Count)"}; $projects=@(@($artifact.reports)[0].projects); if($projects.Count -le 0){throw 'artifact report did not contain projects'}; $project=$projects[0]; if([string]::IsNullOrWhiteSpace([string]$project.path)){throw 'artifact project path missing'}; if($null -ne $project.frameworks){throw 'clean report unexpectedly contained frameworks'}; $failurePrefix='Vulnerable NuGet packages were reported:'; if($r.Combined -match [regex]::Escape($failurePrefix)){throw 'scanner reported vulnerabilities for clean report'} } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'vulnerability scanner rejects vulnerable package report' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='vulnerable'; $r=Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.sln'),'-DotnetExecutable',$fake) -WorkingDirectory $d -Contains 'Vulnerable NuGet packages were reported: 1'; if($r.Combined -notmatch 'Vulnerable\.Package'){throw 'vulnerable package name missing'}; $artifactPath=Join-Path $d '.cache/vulnerable-packages.json'; if(-not(Test-Path -LiteralPath $artifactPath)){throw 'artifact missing'}; $artifact=Get-Content -LiteralPath $artifactPath -Raw|ConvertFrom-Json; $framework=@(@(@($artifact.reports)[0].projects)[0].frameworks)[0]; if($null -eq $framework){throw 'vulnerable report framework missing'}; $package=@($framework.topLevelPackages)[0]; if($null -eq $package.vulnerabilities -or @($package.vulnerabilities).Count -ne 1){throw 'vulnerable package vulnerabilities missing'} } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'vulnerability scanner rejects malformed JSON' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='malformed'; Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.sln'),'-DotnetExecutable',$fake) -WorkingDirectory $d -Contains 'Vulnerability report is not valid JSON'|Out-Null } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'vulnerability scanner rejects project entry without path' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='missing-path'; Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.sln'),'-DotnetExecutable',$fake) -WorkingDirectory $d -Contains 'Project entry is missing path'|Out-Null } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }

Assert 'Windows verifier fails outside Windows' { if(-not $IsWindows){ Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $RepoRoot 'eng/verify-windows.ps1')) -WorkingDirectory $RepoRoot -Contains 'must run on Windows' | Out-Null } }
if($script:Failures -gt 0){exit 1}
