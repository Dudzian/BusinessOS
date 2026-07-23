Set-StrictMode -Version Latest
function Get-BusinessOSRepoRoot { Split-Path -Parent $PSScriptRoot }
function Read-BusinessOSEnvironmentLock {
  param([string]$Path = (Join-Path $PSScriptRoot 'environment.lock.json'))
  if (-not (Test-Path -LiteralPath $Path)) { throw "Environment manifest not found: $Path" }
  $raw = Get-Content -LiteralPath $Path -Raw
  if ([string]::IsNullOrWhiteSpace($raw)) { throw "Environment manifest is empty: $Path" }
  $raw | ConvertFrom-Json
}
function Invoke-CheckedCommand {
  param([Parameter(Mandatory)][string]$FilePath,[string[]]$ArgumentList=@(),[string]$WorkingDirectory=(Get-Location).Path)
  $display = @($FilePath) + $ArgumentList -join ' '
  Write-Host "> $display"
  $sw = [Diagnostics.Stopwatch]::StartNew()
  & $FilePath @ArgumentList
  $code = if ($LASTEXITCODE -ne $null) { $LASTEXITCODE } else { 0 }
  $sw.Stop(); Write-Host ("< exit {0} after {1:n1}s" -f $code,$sw.Elapsed.TotalSeconds)
  if ($code -ne 0) { throw "Command failed with exit code ${code}: $display" }
}
function Test-VersionAtLeast { param([string]$Detected,[string]$Minimum) try { [version]$Detected -ge [version]$Minimum } catch { $false } }
Export-ModuleMember -Function Get-BusinessOSRepoRoot,Read-BusinessOSEnvironmentLock,Invoke-CheckedCommand,Test-VersionAtLeast
