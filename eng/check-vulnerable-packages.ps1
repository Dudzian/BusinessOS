[CmdletBinding()]
param(
    [string]$ProjectOrSolution = 'BusinessOS.sln',

    [string]$DotnetExecutable = 'dotnet'
)

$ErrorActionPreference = 'Stop'

Import-Module (
    Join-Path $PSScriptRoot 'BusinessOS.Engineering.psm1'
) -Force

$repoRoot = (
    Resolve-Path -LiteralPath (
        Join-Path $PSScriptRoot '..'
    )
).Path

$artifactPath = Join-Path $repoRoot '.cache/vulnerable-packages.json'
New-Item -ItemType Directory -Force -Path (Split-Path $artifactPath) | Out-Null

function Resolve-ScanInputPath {
    param([Parameter(Mandatory)][string]$Path)

    $candidate = if ([System.IO.Path]::IsPathRooted($Path)) {
        $Path
    }
    else {
        Join-Path $repoRoot $Path
    }

    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Project or solution file does not exist: $candidate"
    }

    (Resolve-Path -LiteralPath $candidate).Path
}

function Get-VulnerabilityScanTargets {
    param([Parameter(Mandatory)][string]$InputPath)

    $extension = [System.IO.Path]::GetExtension($InputPath).ToLowerInvariant()
    switch ($extension) {
        { $_ -in '.csproj', '.fsproj', '.vbproj', '.sln', '.slnx' } { return @($InputPath) }
        '.slnf' {
            try {
                $filter = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json -ErrorAction Stop
            }
            catch {
                throw "Solution filter is not valid JSON: $InputPath. $($_.Exception.Message)"
            }

            $projectEntries = @($filter.solution.projects)
            if ($projectEntries.Count -le 0) {
                throw "Solution filter does not contain any projects: $InputPath"
            }

            $filterDirectory = Split-Path -Parent $InputPath
            $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            $targets = [System.Collections.Generic.List[string]]::new()

            foreach ($projectEntry in $projectEntries) {
                $projectPath = [string]$projectEntry
                $candidate = if ([System.IO.Path]::IsPathRooted($projectPath)) {
                    $projectPath
                }
                else {
                    Join-Path $filterDirectory $projectPath
                }

                if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
                    throw "Solution filter project does not exist: $candidate"
                }

                $resolvedProject = (Resolve-Path -LiteralPath $candidate).Path
                if ($seen.Add($resolvedProject)) {
                    [void]$targets.Add($resolvedProject)
                }
            }

            return @($targets)
        }
        default { throw "Unsupported project or solution file extension '$extension': $InputPath" }
    }
}

function ConvertFrom-VulnerabilityReportJson {
    param(
        [Parameter(Mandatory)][string]$Raw,
        [Parameter(Mandatory)][string]$Target
    )

    if ([string]::IsNullOrWhiteSpace($Raw)) {
        throw "Vulnerability report is empty for target: $Target"
    }

    try {
        $report = $Raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Vulnerability report is not valid JSON for target ${Target}: $($_.Exception.Message)"
    }

    if ($null -eq $report.projects) {
        throw "Vulnerability report does not contain the expected projects collection for target: $Target"
    }

    if (@($report.projects).Count -le 0) {
        throw "Vulnerability report does not contain any projects for target: $Target"
    }

    foreach ($project in @($report.projects)) {
        if ([string]::IsNullOrWhiteSpace([string]$project.path)) {
            throw "Project entry is missing path for target: $Target"
        }
    }

    $report
}

$inputPath = Resolve-ScanInputPath -Path $ProjectOrSolution
$targets = @(Get-VulnerabilityScanTargets -InputPath $inputPath)
$reports = [System.Collections.Generic.List[object]]::new()
$vulnerablePackages = [System.Collections.Generic.List[object]]::new()

foreach ($target in $targets) {
    $argumentList = @(
        'package'
        'list'
        '--project'
        $target
        '--vulnerable'
        '--include-transitive'
        '--format'
        'json'
        '--output-version'
        '1'
        '--no-restore'
    )

    try {
        $result = Invoke-CheckedCommand `
            -FilePath $DotnetExecutable `
            -ArgumentList $argumentList `
            -WorkingDirectory $repoRoot
    }
    catch {
        throw "Vulnerability scan failed for target '$target'. $($_.Exception.Message)"
    }

    $raw = $result.StdOut -join [Environment]::NewLine
    $report = ConvertFrom-VulnerabilityReportJson -Raw $raw -Target $target
    [void]$reports.Add($report)

    foreach ($project in @($report.projects)) {
        $frameworks = @($project.frameworks)

        if ($frameworks.Count -eq 0) {
            continue
        }

        foreach ($framework in $frameworks) {
            if ($null -eq $framework) {
                continue
            }

            foreach ($collectionName in 'topLevelPackages', 'transitivePackages') {
                foreach ($package in @($framework.$collectionName)) {
                    if ($null -eq $package -or $null -eq $package.vulnerabilities -or @($package.vulnerabilities).Count -le 0) {
                        continue
                    }

                    foreach ($vulnerability in @($package.vulnerabilities)) {
                        [void]$vulnerablePackages.Add([pscustomobject]@{
                            Project     = $project.path
                            Framework   = $framework.framework
                            Scope       = $collectionName
                            Name        = $package.id
                            Version     = $package.resolvedVersion
                            Severity    = $vulnerability.severity
                            AdvisoryUrl = $vulnerability.advisoryurl
                        })
                    }
                }
            }
        }
    }
}

$artifact = [pscustomobject]@{
    input   = $inputPath
    targets = @($targets)
    reports = @($reports)
}

$artifact | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $artifactPath

if ($vulnerablePackages.Count -gt 0) {
    $vulnerablePackages | Format-Table -AutoSize | Out-String | Write-Host
    throw "Vulnerable NuGet packages were reported: $($vulnerablePackages.Count)."
}

Write-Host "No vulnerable NuGet packages were reported for $($targets.Count) target(s) from $inputPath."
