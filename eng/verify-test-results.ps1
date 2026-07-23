param([string]$ResultsDirectory = 'artifacts/test-results')
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot
if (-not (Test-Path -LiteralPath $ResultsDirectory -PathType Container)) {
    $fullResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory)
    throw "TRX results directory does not exist: $fullResultsDirectory"
}
$resolvedResultsDirectory = (Resolve-Path -LiteralPath $ResultsDirectory -ErrorAction Stop).Path
$trxFiles = @(
    Get-ChildItem `
        -LiteralPath $resolvedResultsDirectory `
        -Recurse `
        -File `
        -Filter '*.trx' |
        Sort-Object FullName
)
if (-not $trxFiles) { throw 'No TRX result files were generated.' }
$total = 0
$executed = 0
$passed = 0
$failed = 0
foreach ($file in $trxFiles) {
    [xml]$document = Get-Content `
        -LiteralPath $file.FullName `
        -Raw `
        -ErrorAction Stop
    $summary = $document.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']")
    if ($null -eq $summary) { throw "TRX file has no ResultSummary element: $($file.FullName)" }
    $outcome = $summary.Attributes['outcome']?.Value
    if (-not [string]::IsNullOrWhiteSpace($outcome) -and $outcome -ne 'Completed') {
        throw "TRX ResultSummary outcome is not Completed: '$outcome' in $($file.FullName)"
    }
    $counters = $summary.SelectSingleNode("*[local-name()='Counters']")
    if ($null -eq $counters) { throw "TRX file has no Counters element: $($file.FullName)" }
    $values = @{}
    foreach ($attribute in @('total', 'executed', 'passed', 'failed')) {
        if ($null -eq $counters.Attributes[$attribute]) { throw "TRX Counters element is missing '$attribute': $($file.FullName)" }
        $values[$attribute] = [int]$counters.Attributes[$attribute].Value
        if ($values[$attribute] -lt 0) { throw "TRX counter '$attribute' cannot be negative: $($file.FullName)" }
    }
    if ($values.total -le 0) { throw "TRX file discovered no tests: $($file.FullName)" }
    if ($values.executed -le 0) { throw "TRX file executed no tests: $($file.FullName)" }
    if ($values.failed -gt 0) { throw "TRX file contains failed tests: $($file.FullName)" }
    if ($values.total -ne $values.executed) { throw "TRX total and executed counters differ: $($file.FullName)" }
    if ($values.executed -ne $values.passed) { throw "TRX executed and passed counters differ: $($file.FullName)" }
    $total += $values.total
    $executed += $values.executed
    $passed += $values.passed
    $failed += $values.failed
}
Write-Host "Total tests discovered: $total"
Write-Host "Executed tests: $executed"
Write-Host "Passed tests: $passed"
Write-Host "Failed tests: $failed"
Write-Host 'All discovered tests were executed and passed.'
