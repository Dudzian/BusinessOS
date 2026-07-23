$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot
if (-not (Get-Command pwsh -ErrorAction SilentlyContinue)) { throw 'Missing pwsh; cannot self-test TRX verifier.' }
function Invoke-Verifier([string]$Directory) {
    $outputFile = Join-Path ([System.IO.Path]::GetTempPath()) ("BusinessOS.TrxVerifier.Output." + [System.Guid]::NewGuid().ToString('N') + '.txt')
    try {
        $process = Start-Process -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-File', './eng/verify-test-results.ps1', '-ResultsDirectory', $Directory) -RedirectStandardOutput $outputFile -RedirectStandardError $outputFile -NoNewWindow -Wait -PassThru
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = Get-Content $outputFile -Raw }
    }
    finally {
        if (Test-Path $outputFile) { Remove-Item $outputFile -Force }
    }
}
function Write-Trx([string]$Directory, [string]$Content) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $Content | Set-Content (Join-Path $Directory 'result.trx')
}
function Assert-Succeeds([string]$Directory) {
    $result = Invoke-Verifier $Directory
    if ($result.ExitCode -ne 0) { throw "Expected TRX verification to succeed: $Directory`n$result.Output" }
    foreach ($line in @('Total tests discovered: 2', 'Executed tests: 2', 'Passed tests: 2', 'Failed tests: 0')) {
        if ($result.Output -notmatch [regex]::Escape($line)) { throw "Expected verifier output to contain '$line'. Output:`n$result.Output" }
    }
}
function Assert-Fails([string]$Directory, [string]$CaseName) {
    $result = Invoke-Verifier $Directory
    if ($result.ExitCode -eq 0) { throw "Expected TRX verification to fail for $CaseName." }
}
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("BusinessOS.TrxVerifier." + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    $valid = Join-Path $tempRoot 'valid'
    Copy-Item tests/fixtures/trx/minimal.trx (Join-Path (New-Item -ItemType Directory -Path $valid).FullName 'minimal.trx')
    Assert-Succeeds $valid

    $missing = Join-Path $tempRoot 'missing'
    Assert-Fails $missing 'missing directory or TRX files'

    Write-Trx (Join-Path $tempRoot 'zero-total') '<TestRun><ResultSummary outcome="Completed"><Counters total="0" executed="0" passed="0" failed="0" /></ResultSummary></TestRun>'
    Assert-Fails (Join-Path $tempRoot 'zero-total') 'total=0'

    Write-Trx (Join-Path $tempRoot 'zero-executed') '<TestRun><ResultSummary outcome="Completed"><Counters total="2" executed="0" passed="0" failed="0" /></ResultSummary></TestRun>'
    Assert-Fails (Join-Path $tempRoot 'zero-executed') 'executed=0'

    Write-Trx (Join-Path $tempRoot 'failed') '<TestRun><ResultSummary outcome="Completed"><Counters total="2" executed="2" passed="1" failed="1" /></ResultSummary></TestRun>'
    Assert-Fails (Join-Path $tempRoot 'failed') 'failed=1'

    Write-Trx (Join-Path $tempRoot 'skipped') '<TestRun><ResultSummary outcome="Completed"><Counters total="2" executed="1" passed="1" failed="0" /></ResultSummary></TestRun>'
    Assert-Fails (Join-Path $tempRoot 'skipped') 'skipped or not executed tests'

    Write-Trx (Join-Path $tempRoot 'inconsistent-passed') '<TestRun><ResultSummary outcome="Completed"><Counters total="2" executed="2" passed="1" failed="0" /></ResultSummary></TestRun>'
    Assert-Fails (Join-Path $tempRoot 'inconsistent-passed') 'executed != passed'

    Write-Trx (Join-Path $tempRoot 'aborted') '<TestRun><ResultSummary outcome="Aborted"><Counters total="2" executed="2" passed="2" failed="0" /></ResultSummary></TestRun>'
    Assert-Fails (Join-Path $tempRoot 'aborted') 'aborted outcome'

    Write-Trx (Join-Path $tempRoot 'negative') '<TestRun><ResultSummary outcome="Completed"><Counters total="2" executed="2" passed="-1" failed="0" /></ResultSummary></TestRun>'
    Assert-Fails (Join-Path $tempRoot 'negative') 'negative counter'

    Write-Trx (Join-Path $tempRoot 'broken') '<TestRun><ResultSummary>'
    Assert-Fails (Join-Path $tempRoot 'broken') 'malformed XML'

    Write-Trx (Join-Path $tempRoot 'no-counters') '<TestRun><ResultSummary outcome="Completed" /></TestRun>'
    Assert-Fails (Join-Path $tempRoot 'no-counters') 'missing Counters'

    Write-Trx (Join-Path $tempRoot 'no-executed') '<TestRun><ResultSummary outcome="Completed"><Counters total="2" passed="2" failed="0" /></ResultSummary></TestRun>'
    Assert-Fails (Join-Path $tempRoot 'no-executed') 'missing executed attribute'

    Write-Trx (Join-Path $tempRoot 'no-failed') '<TestRun><ResultSummary outcome="Completed"><Counters total="2" executed="2" passed="2" /></ResultSummary></TestRun>'
    Assert-Fails (Join-Path $tempRoot 'no-failed') 'missing failed attribute'

    Write-Host 'TRX verifier self-test passed.'
}
finally {
    if (Test-Path $tempRoot) { Remove-Item $tempRoot -Recurse -Force }
}
