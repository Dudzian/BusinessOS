$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
$actionPath = Join-Path $root '.github/actions/setup-businessos/action.yml'
$action = Get-Content -LiteralPath $actionPath -Raw

foreach ($pattern in @(
    [regex]::Escape("Join-Path (Get-Location) '.cache/setup.log'"),
    'Set-Content\s+-LiteralPath\s+\$setupLog',
    '&\s+\./eng/setup-environment\.ps1\s+\*>&1',
    'Tee-Object\s+-FilePath\s+\$setupLog\s+-Append'
)) {
    if ($action -notmatch $pattern) { throw "Setup action is missing required log handling: $pattern" }
}
if ($action -match 'setup-environment\.ps1\s+2>&1') {
    throw 'Setup action still captures only the error stream.'
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ("businessos-setup-log-test-{0}" -f [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $temporary | Out-Null
try {
    $probe = Join-Path $temporary 'probe.ps1'
    $log = Join-Path $temporary 'setup.log'
    Set-Content -LiteralPath $probe -Value "Write-Host 'host-only-marker'" -Encoding utf8NoBOM
    Set-Content -LiteralPath $log -Value '' -NoNewline
    & $probe *>&1 | Tee-Object -FilePath $log -Append | Out-Null
    if (-not (Test-Path -LiteralPath $log -PathType Leaf)) { throw 'The stream probe did not leave a setup log.' }
    if ((Get-Content -LiteralPath $log -Raw) -notmatch 'host-only-marker') { throw 'Write-Host output was not captured.' }

    Set-Content -LiteralPath $probe -Value "throw 'forced-setup-failure'" -Encoding utf8NoBOM
    $failed = $false
    try { & $probe *>&1 | Tee-Object -FilePath $log -Append | Out-Null } catch { $failed = $_.Exception.Message -like '*forced-setup-failure*' }
    if (-not $failed) { throw 'The all-stream pipeline masked a forced setup failure.' }
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}

Write-Host 'Setup action log regression PASS'
