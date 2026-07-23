param([string]$ProjectOrSolution = 'BusinessOS.sln')
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot
$tmp = Join-Path $repoRoot '.cache/vulnerable-packages.json'
New-Item -ItemType Directory -Force -Path (Split-Path $tmp) | Out-Null
$args=@('package','list',$ProjectOrSolution,'--vulnerable','--include-transitive','--format','json','--output-version','1')
$jsonText = & dotnet @args 2>&1
$exitCode = $LASTEXITCODE
$jsonText | Set-Content $tmp
$jsonText | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) { throw "dotnet vulnerable package scan failed with exit code $exitCode for $ProjectOrSolution." }
$raw = ($jsonText -join "`n")
if ([string]::IsNullOrWhiteSpace($raw)) { throw 'Vulnerability report is empty.' }
try { $report = $raw | ConvertFrom-Json -ErrorAction Stop } catch { throw "Vulnerability report is not valid JSON: $($_.Exception.Message)" }
if ($null -eq $report.projects) { throw 'Vulnerability report does not contain the expected projects collection.' }
if (@($report.projects).Count -le 0) { throw 'Vulnerability report does not contain any projects.' }
$vulnerablePackages = New-Object System.Collections.Generic.List[object]
foreach ($project in @($report.projects)) {
  if ($null -eq $project.frameworks) { throw "Project entry is missing frameworks: $($project.path)" }
  foreach ($framework in @($project.frameworks)) {
    foreach ($collectionName in 'topLevelPackages','transitivePackages') {
      $packages = @($framework.$collectionName)
      foreach ($package in $packages) {
        if ($null -ne $package -and $null -ne $package.vulnerabilities -and @($package.vulnerabilities).Count -gt 0) {
          foreach ($vulnerability in @($package.vulnerabilities)) {
            $vulnerablePackages.Add([pscustomobject]@{ Project=$project.path; Framework=$framework.framework; Scope=$collectionName; Name=$package.id; Version=$package.resolvedVersion; Severity=$vulnerability.severity; AdvisoryUrl=$vulnerability.advisoryurl })
          }
        }
      }
    }
  }
}
if ($vulnerablePackages.Count -gt 0) { $vulnerablePackages | Format-Table -AutoSize | Out-String | Write-Host; throw "Vulnerable NuGet packages were reported: $($vulnerablePackages.Count)." }
Write-Host "No vulnerable NuGet packages were reported for $ProjectOrSolution."
