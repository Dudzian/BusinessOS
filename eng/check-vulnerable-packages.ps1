$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot
$json = dotnet package list --project BusinessOS.sln --vulnerable --include-transitive --format json --output-version 1 2>&1
$exitCode = $LASTEXITCODE
$json | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) { throw "dotnet vulnerable package scan failed with exit code $exitCode." }
$report = ($json -join "`n") | ConvertFrom-Json
if ($null -eq $report) { throw 'Vulnerability report is empty.' }
if ($null -eq $report.projects) { throw 'Vulnerability report does not contain the expected projects collection.' }
if (@($report.projects).Count -le 0) { throw 'Vulnerability report does not contain any projects.' }
$vulnerablePackages = New-Object System.Collections.Generic.List[object]
foreach ($project in @($report.projects)) {
    foreach ($framework in @($project.frameworks)) {
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            if ($null -ne $package -and $null -ne $package.vulnerabilities -and @($package.vulnerabilities).Count -gt 0) {
                foreach ($vulnerability in @($package.vulnerabilities)) {
                    $vulnerablePackages.Add([pscustomobject]@{
                        Project = $project.path
                        Framework = $framework.framework
                        Name = $package.id
                        Version = $package.resolvedVersion
                        Severity = $vulnerability.severity
                        AdvisoryUrl = $vulnerability.advisoryurl
                    })
                }
            }
        }
    }
}
if ($vulnerablePackages.Count -gt 0) {
    $vulnerablePackages | Format-Table -AutoSize | Out-String | Write-Host
    throw "Vulnerable NuGet packages were reported: $($vulnerablePackages.Count)."
}
Write-Host 'No vulnerable NuGet packages were reported.'
