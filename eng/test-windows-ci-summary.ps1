$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
. "$PSScriptRoot/activate-environment.ps1"
Import-Module "$PSScriptRoot/BusinessOS.CiEvidence.psm1" -Force

$relativePaths = @(
    'artifacts/smoke-test',
    'artifacts/test-results',
    '.cache/ci-evidence-source/windows',
    'artifacts/ci-evidence/windows',
    '.cache/vulnerable-packages.json',
    '.cache/migration-evidence.json',
    '.cache/setup.log',
    '.cache/doctor.txt',
    '.cache/tool-versions.log',
    '.cache/verify-windows.log',
    '.cache/environment-tests.log'
)
$backupRoot = Join-Path ([IO.Path]::GetTempPath()) ("businessos-windows-summary-test-{0}" -f [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $backupRoot | Out-Null
$state = @()
try {
    for ($index = 0; $index -lt $relativePaths.Count; $index++) {
        $path = Join-Path $root $relativePaths[$index]
        $backup = Join-Path $backupRoot ([string]$index)
        $exists = Test-Path -LiteralPath $path
        if ($exists) { Copy-Item -LiteralPath $path -Destination $backup -Recurse -Force }
        $state += [pscustomobject]@{ Path = $path; Backup = $backup; Existed = $exists }
        if ($exists) { Remove-Item -LiteralPath $path -Recurse -Force }
    }

    $scenarioDirectory = Join-Path $root 'artifacts/smoke-test/scenarios'
    $testResultDirectory = Join-Path $root 'artifacts/test-results/windows'
    New-Item -ItemType Directory -Force $scenarioDirectory, $testResultDirectory | Out-Null
    $fixture = Get-Content "$root/tests/fixtures/github-api/green-pr/windows/summary.json" -Raw | ConvertFrom-Json -Depth 30
    $scenarios = @($fixture.smoke.scenarios)
    if ($scenarios.Count -ne 5) { throw 'The Windows fixture must contain exactly five scenarios.' }
    for ($index = 0; $index -lt $scenarios.Count; $index++) {
        $scenarios[$index] | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $scenarioDirectory ("scenario-{0}.json" -f ($index + 1))) -Encoding utf8NoBOM
    }
    Set-Content (Join-Path $root 'artifacts/smoke-test/desktop-smoke-diagnostics.txt') 'Windows smoke diagnostics regression fixture.'
    @'
<?xml version="1.0" encoding="utf-8"?>
<TestRun><ResultSummary><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" inconclusive="0" /></ResultSummary></TestRun>
'@ | Set-Content (Join-Path $testResultDirectory 'windows.trx') -Encoding utf8NoBOM
    foreach ($name in 'setup.log','doctor.txt','tool-versions.log','verify-windows.log','environment-tests.log') {
        Set-Content (Join-Path $root ".cache/$name") "Windows summary regression $name"
    }
    [ordered]@{ targets = @('BusinessOS.CrossPlatform.slnf'); reports = @() } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root '.cache/vulnerable-packages.json') -Encoding utf8NoBOM
    [ordered]@{ status = 'PASS'; pendingModelChanges = $false; newMigrationCreated = $false; snapshotModified = $false } | ConvertTo-Json | Set-Content (Join-Path $root '.cache/migration-evidence.json') -Encoding utf8NoBOM

    & "$PSScriptRoot/write-ci-summary.ps1" -Gate windows -Status PASS -FailureStage none -FailureMessage none -LastCompletedStage complete -FormatStatus PASS -BuildStatus PASS
    $source = Join-Path $root '.cache/ci-evidence-source/windows/summary.json'
    $json = Get-Content -LiteralPath $source -Raw
    $summary = $json | ConvertFrom-Json -Depth 30
    if ($summary.smoke.executedScenarioCount -ne 5) { throw 'Windows summary did not detect five executed scenarios.' }
    if ($summary.smoke.passedScenarioCount -ne 5) { throw 'Windows summary did not count five passed scenarios.' }
    if ($summary.smoke.failedScenarioCount -ne 0) { throw 'Windows summary counted failed scenarios.' }
    if ($summary.gateName -ne 'windows' -or $summary.gateStatus -ne 'PASS') { throw 'Windows summary has an incorrect gate identity or status.' }
    Test-BusinessOSCiEvidence $summary | Out-Null
    if (-not ($json | Test-Json -SchemaFile "$PSScriptRoot/schemas/ci-evidence.schema.json" -ErrorAction Stop)) { throw 'Windows summary failed JSON Schema.' }
    & "$PSScriptRoot/stage-ci-artifacts.ps1" -Gate windows
} finally {
    foreach ($item in $state) {
        if (Test-Path -LiteralPath $item.Path) { Remove-Item -LiteralPath $item.Path -Recurse -Force }
        if ($item.Existed) {
            New-Item -ItemType Directory -Force (Split-Path $item.Path -Parent) | Out-Null
            Copy-Item -LiteralPath $item.Backup -Destination $item.Path -Recurse -Force
        }
    }
    if (Test-Path -LiteralPath $backupRoot) { Remove-Item -LiteralPath $backupRoot -Recurse -Force }
}

Write-Host 'Windows PASS summary regression PASS'
