Set-StrictMode -Version Latest
function Get-BusinessOSRepoRoot { Split-Path -Parent $PSScriptRoot }
function Read-BusinessOSEnvironmentLock {
  param([string]$Path = (Join-Path $PSScriptRoot 'environment.lock.json'))
  if (-not (Test-Path -LiteralPath $Path)) { throw "Environment manifest not found: $Path" }
  $raw = Get-Content -LiteralPath $Path -Raw
  if ([string]::IsNullOrWhiteSpace($raw)) { throw "Environment manifest is empty: $Path" }
  try { $raw | ConvertFrom-Json -ErrorAction Stop } catch { throw "Environment manifest is invalid JSON: $Path. $($_.Exception.Message)" }
}
function Read-BusinessOSBootstrapLock {
  param([string]$Path = (Join-Path $PSScriptRoot 'environment.bootstrap.env'))
  if (-not (Test-Path -LiteralPath $Path)) { throw "Bootstrap manifest not found: $Path" }
  $data=@{}
  foreach($line in Get-Content -LiteralPath $Path){ if($line -match '^\s*#' -or [string]::IsNullOrWhiteSpace($line)){continue}; if($line -notmatch '^([A-Z0-9_]+)=(.*)$'){throw "Invalid bootstrap line: $line"}; $data[$Matches[1]]=$Matches[2] }
  [pscustomobject]$data
}
function Test-VersionAtLeast { param([string]$Detected,[string]$Minimum) try { [version]$Detected -ge [version]$Minimum } catch { $false } }
function Get-BusinessOSToolPath { param([Parameter(Mandatory)][string]$Root,[Parameter(Mandatory)][string]$RelativeRoot,[Parameter(Mandatory)][string]$BaseName) $exe = if ($IsWindows) { "$BaseName.exe" } else { $BaseName }; Join-Path (Join-Path $Root $RelativeRoot) $exe }
function ConvertTo-OutputLines {
  param([AllowNull()][string]$Text)
  if ([string]::IsNullOrEmpty($Text)) { return @() }
  @($Text -split "\r?\n" | Where-Object { $_ -ne '' })
}
function Invoke-CheckedCommand {
  param([Parameter(Mandatory)][string]$FilePath,[string[]]$ArgumentList=@(),[string]$WorkingDirectory=(Get-Location).Path)
  $display = (@($FilePath) + $ArgumentList) -join ' '
  Write-Host "> $display"
  $previous=(Get-Location).Path; $sw=[Diagnostics.Stopwatch]::StartNew(); $process=$null; $code=-1; $stdoutLines=@(); $stderrLines=@()
  try {
    $isPs1=$FilePath -match '\.ps1$'
    $psi=[Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = if($isPs1){(Get-Command pwsh -ErrorAction Stop).Source}else{$FilePath}
    $psi.WorkingDirectory = (Resolve-Path -LiteralPath $WorkingDirectory).Path
    $psi.UseShellExecute=$false; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
    if($isPs1){[void]$psi.ArgumentList.Add('-NoProfile'); [void]$psi.ArgumentList.Add('-File'); [void]$psi.ArgumentList.Add($FilePath)}
    foreach($arg in $ArgumentList){[void]$psi.ArgumentList.Add($arg)}
    Set-Location -LiteralPath $psi.WorkingDirectory
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$psi
    if(-not $process.Start()){throw "Failed to start process: $FilePath"}
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdoutText = $stdoutTask.GetAwaiter().GetResult()
    $stderrText = $stderrTask.GetAwaiter().GetResult()
    $stdoutLines = @(ConvertTo-OutputLines $stdoutText)
    $stderrLines = @(ConvertTo-OutputLines $stderrText)
    foreach($line in $stdoutLines){ Write-Host $line }
    foreach($line in $stderrLines){ Write-Host $line }
    $code=$process.ExitCode
  } finally {
    if($null -ne $process){ $process.Dispose() }
    Set-Location -LiteralPath $previous
    $sw.Stop(); Write-Host ("< exit {0} after {1:n1}s" -f $code,$sw.Elapsed.TotalSeconds)
  }
  $result=[pscustomobject]@{ExitCode=$code;StdOut=@($stdoutLines);StdErr=@($stderrLines);Duration=$sw.Elapsed;WorkingDirectory=$WorkingDirectory;FileName=$FilePath;Arguments=$ArgumentList}
  if($code -ne 0){throw "Command failed with exit code ${code}: $display"}
  $result
}
function Get-BusinessOSProjects { param([string]$Root=(Get-BusinessOSRepoRoot)) @(Get-ChildItem -Path (Join-Path $Root 'src'),(Join-Path $Root 'tests') -Recurse -Filter *.csproj -ErrorAction SilentlyContinue | Where-Object FullName -notmatch '[\/](bin|obj|\.tools|\.cache)[\/]') }
function Get-BusinessOSCrossPlatformFilterProjects {
  param([string]$Root=(Get-BusinessOSRepoRoot),[string]$FilterPath=(Join-Path $Root 'BusinessOS.CrossPlatform.slnf'))
  $json=Get-Content -LiteralPath $FilterPath -Raw | ConvertFrom-Json
  @($json.solution.projects | ForEach-Object { Join-Path $Root $_ })
}
Export-ModuleMember -Function Get-BusinessOSRepoRoot,Read-BusinessOSEnvironmentLock,Read-BusinessOSBootstrapLock,Invoke-CheckedCommand,Test-VersionAtLeast,Get-BusinessOSToolPath,Get-BusinessOSProjects,Get-BusinessOSCrossPlatformFilterProjects
