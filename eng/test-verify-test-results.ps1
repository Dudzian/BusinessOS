$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot
if (-not (Get-Command pwsh -ErrorAction SilentlyContinue)) { throw 'Missing pwsh; cannot self-test TRX verifier.' }
function Invoke-Verifier([string]$Directory) {
    $process = $null

    try {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = 'pwsh'
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.CreateNoWindow = $true
        $startInfo.WorkingDirectory = $repoRoot.Path

        foreach ($argument in @(
            '-NoProfile',
            '-File',
            (Join-Path $repoRoot.Path 'eng/verify-test-results.ps1'),
            '-ResultsDirectory',
            $Directory
        )) {
            [void]$startInfo.ArgumentList.Add([string]$argument)
        }

        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo

        if (-not $process.Start()) {
            throw 'Failed to start TRX verifier process.'
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        $process.WaitForExit()

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut   = $stdout
            StdErr   = $stderr
            Output   = @($stdout, $stderr) -join [Environment]::NewLine
        }
    }
    finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
    }
}
function Write-Trx([string]$Directory, [string]$Content) {
    [System.IO.Directory]::CreateDirectory($Directory) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $Directory 'result.trx'), $Content)
}
function Assert-Succeeds([string]$Directory, [int]$ExpectedTotal = 2) {
    $result = Invoke-Verifier $Directory
    if ($result.ExitCode -ne 0) { throw "Expected TRX verification to succeed: $Directory`n$result.Output" }
    foreach ($line in @("Total tests discovered: $ExpectedTotal", "Executed tests: $ExpectedTotal", "Passed tests: $ExpectedTotal", 'Failed tests: 0')) {
        if ($result.Output -notmatch [regex]::Escape($line)) { throw "Expected verifier output to contain '$line'. Output:`n$result.Output" }
    }
    if ($result.Output -notmatch [regex]::Escape('All discovered tests were executed and passed.')) { throw "Expected verifier output to confirm all tests passed. Output:`n$result.Output" }
}
function Assert-Fails([string]$Directory, [string]$CaseName) {
    $result = Invoke-Verifier $Directory
    if ($result.ExitCode -eq 0) { throw "Expected TRX verification to fail for $CaseName." }
}
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("BusinessOS.TrxVerifier." + [System.Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null
try {
    $valid = Join-Path $tempRoot 'valid'
    $validDirectory = [System.IO.Directory]::CreateDirectory($valid).FullName
    Copy-Item -LiteralPath 'tests/fixtures/trx/minimal.trx' -Destination (Join-Path $validDirectory 'minimal.trx')
    Assert-Succeeds $valid

    $validBracket = Join-Path $tempRoot "valid space[1] apostrof' nawiasy() znak`$dolara"
    [System.IO.Directory]::CreateDirectory($validBracket) | Out-Null
    [System.IO.File]::Copy((Join-Path $repoRoot 'tests/fixtures/trx/minimal.trx'), (Join-Path $validBracket 'result.trx'), $true)
    [System.IO.File]::Copy((Join-Path $repoRoot 'tests/fixtures/trx/minimal.trx'), (Join-Path $validBracket 'result[1].trx'), $true)
    Assert-Succeeds $validBracket 4

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
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}
