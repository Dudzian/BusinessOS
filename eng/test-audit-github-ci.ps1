$ErrorActionPreference='Stop';$root=(Resolve-Path "$PSScriptRoot/..").Path;$auditor="$PSScriptRoot/audit-github-ci.ps1";$failed=0
function Invoke-Case([string]$Name,[string]$Fixture,[int]$Expected){&pwsh -NoProfile -File $auditor -Repository Dudzian/BusinessOS -PullRequest 20 -FixtureRoot $Fixture -OutputDirectory "artifacts/github-audit-tests/$Name";$actual=$LASTEXITCODE;if($actual-ne$Expected){Write-Error "$Name expected $Expected got $actual";$script:failed++}else{Write-Host "$Name PASS exit=$actual"}}
$existing=[ordered]@{'green-pr'=0;'red-windows'=20;'missing-artifact'=30;'expired-artifact'=34;'missing-manifest'=35;'missing-summary'=32;'manifest-extra-file'=35;'manifest-missing-file'=35;'size-mismatch'=35;'checksum-mismatch'=35;'summary-schema-invalid'=32;'windows-empty-scenarios'=32;'windows-four-scenarios'=32;'duplicate-scenario'=32;'not-run'=32;'same-tree-different-commit'=0;'different-tree'=40;'rerun-attempt-2'=0;'wrong-workflow-event'=10;'older-success-newer-failure'=20;'missing-gh'=0;'anonymous-public'=0}
foreach($c in $existing.GetEnumerator()){Invoke-Case $c.Key "$root/tests/fixtures/github-api/$($c.Key)" $c.Value}
function New-MutatedFixture([string]$Name,[scriptblock]$Mutation){$d="$root/artifacts/generated-audit-fixtures/$Name";if(Test-Path $d){Remove-Item $d -Recurse -Force};Copy-Item "$root/tests/fixtures/github-api/green-pr" $d -Recurse; &$Mutation $d;return $d}
function Update-Summary($Directory,[string]$Gate,[scriptblock]$Mutation){$p="$Directory/$Gate/summary.json";$x=Get-Content $p -Raw|ConvertFrom-Json -Depth 30;&$Mutation $x;$x|ConvertTo-Json -Depth 30|Set-Content $p -Encoding utf8NoBOM;$m=Get-Content "$Directory/$Gate/manifest.json"-Raw|ConvertFrom-Json;$e=@($m.files|Where-Object relativePath -eq 'summary.json')[0];$e.sizeBytes=(Get-Item $p).Length;$e.sha256=(Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant();$m|ConvertTo-Json -Depth 10|Set-Content "$Directory/$Gate/manifest.json" -Encoding utf8NoBOM}
$mutations=[ordered]@{
 'wrong-gate-in-artifact'={param($d)Update-Summary $d windows {param($x)$x.gateName='cross-platform'}}
 'swapped-artifacts'={param($d)$i=Get-Content "$d/audit-input.json"-Raw|ConvertFrom-Json;$i.artifacts[0].path='windows';$i.artifacts[1].path='cross-platform';$i|ConvertTo-Json -Depth 20|Set-Content "$d/audit-input.json"}
 'wrong-job-key'={param($d)Update-Summary $d windows {param($x)$x.jobKey='cross-platform'}}
 'wrong-run-id'={param($d)Update-Summary $d windows {param($x)$x.runId=99}}
 'wrong-run-attempt'={param($d)Update-Summary $d windows {param($x)$x.runAttempt=2}}
 'wrong-repository'={param($d)Update-Summary $d windows {param($x)$x.repository='other/repo'}}
 'wrong-pr-number'={param($d)Update-Summary $d windows {param($x)$x.pullRequestNumber=21}}
 'wrong-pr-head-sha'={param($d)Update-Summary $d windows {param($x)$x.pullRequestHeadSha='a'*40}}
 'wrong-pr-merge-sha'={param($d)Update-Summary $d windows {param($x)$x.pullRequestMergeSha='a'*40}}
 'wrong-summary-commit'={param($d)Update-Summary $d windows {param($x)$x.commitSha='a'*40}}
 'wrong-summary-tree'={param($d)Update-Summary $d windows {param($x)$x.commitTreeSha='a'*40}}
 'local-checkout-in-actions-artifact'={param($d)Update-Summary $d windows {param($x)$x.checkoutKind='local'}}
 'workflow-conclusion-failure'={param($d)$i=Get-Content "$d/audit-input.json"-Raw|ConvertFrom-Json;$i|Add-Member runs @([pscustomobject]@{id=100;name='ci';head_sha=$i.remoteHeadSha;event='pull_request';status='completed';conclusion='failure';created_at='2026-01-01T00:00:00Z';updated_at='2026-01-01T00:01:00Z';run_number=1;run_attempt=1;head_branch='x';jobs=$i.jobs;artifacts=$i.artifacts});$i|ConvertTo-Json -Depth 30|Set-Content "$d/audit-input.json"}
 'workflow-conclusion-cancelled'={param($d)$i=Get-Content "$d/audit-input.json"-Raw|ConvertFrom-Json;$i|Add-Member runs @([pscustomobject]@{id=100;name='ci';head_sha=$i.remoteHeadSha;event='pull_request';status='completed';conclusion='cancelled';created_at='2026-01-01T00:00:00Z';updated_at='2026-01-01T00:01:00Z';run_number=1;run_attempt=1;head_branch='x';jobs=$i.jobs;artifacts=$i.artifacts});$i|ConvertTo-Json -Depth 30|Set-Content "$d/audit-input.json"}
 'duplicate-required-job'={param($d)$i=Get-Content "$d/audit-input.json"-Raw|ConvertFrom-Json;$i.jobs+=@($i.jobs[0]);$i|ConvertTo-Json -Depth 20|Set-Content "$d/audit-input.json"}
 'missing-required-job'={param($d)$i=Get-Content "$d/audit-input.json"-Raw|ConvertFrom-Json;$i.jobs=@($i.jobs|Where-Object name -ne 'windows');$i|ConvertTo-Json -Depth 20|Set-Content "$d/audit-input.json"}
}
foreach($m in $mutations.GetEnumerator()){$d=New-MutatedFixture $m.Key $m.Value;Invoke-Case $m.Key $d $(if($m.Key-like'workflow-*'-or$m.Key-like'*-required-job'){20}else{32})}
# Real malicious ZIP fixture
$d=New-MutatedFixture zip-slip {};Add-Type -AssemblyName System.IO.Compression.FileSystem;$zip="$d/malicious.zip";$z=[IO.Compression.ZipFile]::Open($zip,'Create');$entry=$z.CreateEntry('../outside.txt');$w=[IO.StreamWriter]::new($entry.Open());$w.Write('escape');$w.Dispose();$z.Dispose();$i=Get-Content "$d/audit-input.json"-Raw|ConvertFrom-Json;$i.artifacts[0]|Add-Member zipPath 'malicious.zip';$i|ConvertTo-Json -Depth 20|Set-Content "$d/audit-input.json";Invoke-Case zip-slip $d 35;if(Test-Path "$root/artifacts/github-audit-tests/zip-slip/extracted/outside.txt"){Write-Error 'Zip Slip wrote outside extraction';$failed++}
& pwsh -NoProfile -File "$PSScriptRoot/test-evidence-security-boundaries.ps1";if($LASTEXITCODE){$failed++}
Write-Host "failed $failed";exit $(if($failed){1}else{0})
