param([string]$EvidencePath)
$ErrorActionPreference='Stop';Import-Module "$PSScriptRoot/BusinessOS.CiEvidence.psm1" -Force
if($EvidencePath){$e=Get-Content (Join-Path $EvidencePath 'summary.json')-Raw|ConvertFrom-Json -Depth 30;Test-BusinessOSCiEvidence $e|Out-Null;if(Test-Path(Join-Path $EvidencePath 'manifest.json')){$m=Get-Content(Join-Path $EvidencePath 'manifest.json')-Raw|ConvertFrom-Json;Test-BusinessOSManifest $EvidencePath $m|Out-Null};Write-Host 'CI evidence validation PASS';exit 0}
$repoRoot=(Resolve-Path "$PSScriptRoot/..").Path;$failed=0;$backupRoot=Join-Path ([IO.Path]::GetTempPath()) ("businessos-ci-evidence-tests-{0}" -f [guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Force $backupRoot|Out-Null
$protectedPaths=@('.cache/ci-evidence-source/cross-platform','.cache/ci-evidence-source/windows','artifacts/ci-evidence/cross-platform','artifacts/ci-evidence/windows','artifacts/test-results');$protectedState=@();$index=0
foreach($relative in $protectedPaths){$path=Join-Path $repoRoot $relative;$backup=Join-Path $backupRoot ([string]$index++);$exists=Test-Path -LiteralPath $path;if($exists){Copy-Item -LiteralPath $path -Destination $backup -Recurse -Force};$protectedState+=[pscustomobject]@{Path=$path;Backup=$backup;Existed=$exists}}
try{
$cross=Get-Content "$PSScriptRoot/../tests/fixtures/github-api/green-pr/cross-platform/summary.json"-Raw|ConvertFrom-Json -Depth 30;$windows=Get-Content "$PSScriptRoot/../tests/fixtures/github-api/green-pr/windows/summary.json"-Raw|ConvertFrom-Json -Depth 30
function Clone($x){$x|ConvertTo-Json -Depth 30|ConvertFrom-Json -Depth 30}
function Test-Failed([string]$Message){$script:failed++;Write-Warning $Message}
function Pass([string]$Name,[scriptblock]$Body){$passed=$true;$detail=$null;try{&$Body}catch{$passed=$false;$detail=$_};if($passed){Write-Host "$Name passed"}else{Test-Failed "$Name failed: $detail"}}
function Reject([string]$Name,$Base,[scriptblock]$Mutation){$x=Clone $Base;& $Mutation $x;$accepted=$true;try{Test-BusinessOSCiEvidence $x|Out-Null}catch{$accepted=$false};if($accepted){Test-Failed "$Name accepted an invalid document"}else{Write-Host "$Name rejected"}}
function Test-Schema($Value){$json=$Value|ConvertTo-Json -Depth 30;try{[bool]($json|Test-Json -SchemaFile "$PSScriptRoot/schemas/ci-evidence.schema.json" -ErrorAction Stop)}catch{$false}}
function Accept-Both([string]$Name,$Value){$schemaAccepted=Test-Schema $Value;$validatorAccepted=$true;$detail=$null;try{Test-BusinessOSCiEvidence $Value|Out-Null}catch{$validatorAccepted=$false;$detail=$_};if($schemaAccepted-and$validatorAccepted){Write-Host "$Name passed"}else{Test-Failed "$Name failed: schema=$schemaAccepted validator=$validatorAccepted $detail"}}
function Reject-Both([string]$Name,$Base,[scriptblock]$Mutation){$x=Clone $Base;&$Mutation $x;$schemaAccepted=Test-Schema $x;$validatorAccepted=$true;try{Test-BusinessOSCiEvidence $x|Out-Null}catch{$validatorAccepted=$false};if($schemaAccepted-or$validatorAccepted){Test-Failed "$Name contract mismatch: schema=$schemaAccepted validator=$validatorAccepted"}else{Write-Host "$Name rejected by schema and validator"}}
Pass 'negative helper detects unexpected validator acceptance' {$before=$script:failed;Test-Failed 'simulated invalid document acceptance';if($script:failed-ne$before+1){throw 'negative helper did not increment the failure count'};$script:failed=$before}
Reject schema-version $cross {param($x)$x.schemaVersion=2};Reject boolean-string $windows {param($x)$x.smoke.scenarios[0].exited='true'};Reject integer-string $cross {param($x)$x.tests.executed='10'};Reject empty-repository $cross {param($x)$x.repository=''};Reject empty-workflow $cross {param($x)$x.workflowName=''};Reject empty-job-key $cross {param($x)$x.jobKey=''};Reject pass-failure-stage $cross {param($x)$x.failure.stage='tests'};Reject pass-errors $cross {param($x)$x.errors=@('bad')};Reject empty-trx $cross {param($x)$x.tests.trxFiles=@()};Reject duplicate-trx $cross {param($x)$x.tests.trxFiles+=@($x.tests.trxFiles[0])};Reject empty-targets $cross {param($x)$x.vulnerabilities.checkedTargets=@()};Reject sample-count $windows {param($x)$x.smoke.scenarios[0].stableSamplesObserved=4};Reject fixture-consistency $windows {param($x)$x.smoke.scenarios[0].fixturePrepared=$true};Reject recovery-origin $windows {param($x)$x.smoke.scenarios[3].recoveryOrigin='StartupFailure'};Reject shutdown-kill $windows {param($x)$x.smoke.scenarios[0].shutdownMethod='Kill'};Reject empty-diagnostic $windows {param($x)$x.smoke.scenarios[0].diagnosticFile=''}
foreach($scenario in @($windows.smoke.scenarios)){Reject "window-state-$($scenario.name)" $windows {param($x)$s=@($x.smoke.scenarios|Where-Object name -eq $scenario.name)[0];$s.finalWindowCount=2}.GetNewClosure()}
$fail=Clone $cross;$fail.gateStatus='FAIL';$fail.failure.stage='tests';$fail.failure.message='controlled';$fail.lastCompletedStage='build';Reject fail-stage-none $fail {param($x)$x.failure.stage='none'};Reject fail-message-none $fail {param($x)$x.failure.message='none'}
Reject run-id-string $cross {param($x)$x.runId='30590171040'};Reject run-attempt-string $cross {param($x)$x.runAttempt='1'}
Reject-Both local-positive-run-id $cross {param($x)$x.checkoutKind='local';$x.runId=1;$x.runAttempt=0}
Reject-Both local-positive-run-attempt $cross {param($x)$x.checkoutKind='local';$x.runId=0;$x.runAttempt=1}
Reject-Both pr-pass-zero $cross {param($x)$x.checkoutKind='pull-request-merge';$x.runId=0}
Reject-Both pr-fail-zero $fail {param($x)$x.checkoutKind='pull-request-merge';$x.runAttempt=0}
Reject-Both branch-fail-zero $fail {param($x)$x.checkoutKind='branch-head';$x.runId=0}
Reject-Both negative-run-id $fail {param($x)$x.checkoutKind='branch-head';$x.runId=-1}
Reject-Both negative-run-attempt $fail {param($x)$x.checkoutKind='branch-head';$x.runAttempt=-1}
Reject-Both run-id-string-schema $cross {param($x)$x.runId='30590171040'}
Reject-Both run-attempt-string-schema $cross {param($x)$x.runAttempt='1'}
Accept-Both 'local zero run identity' $(New-BusinessOSCiEvidenceSummary @{}|ConvertTo-Json -Depth 30|ConvertFrom-Json -Depth 30)
$branchFail=Clone $fail;$branchFail.checkoutKind='branch-head';$branchFail.runId=[long]42;$branchFail.runAttempt=[long]2;Accept-Both 'branch failure positive run identity' $branchFail
Pass 'constructor rejects empty and nonnumeric GitHub run identity' {foreach($value in '', 'not-a-number'){foreach($name in 'runId','runAttempt'){$values=@{checkoutKind='branch-head';runId='1';runAttempt='1'};$values[$name]=$value;try{New-BusinessOSCiEvidenceSummary $values|Out-Null;throw "$name=$value accepted"}catch{if($_.Exception.Message-like'*accepted'){throw}}}}}
Pass 'Int64 GitHub run identity survives JSON round trip and full validation' {
 $e=Clone $cross;$e.runId=[long]30590171040;$e.runAttempt=[long]1
 $json=$e|ConvertTo-Json -Depth 30;$roundTrip=$json|ConvertFrom-Json -Depth 30
 if($roundTrip.runId-isnot[long]-or$roundTrip.runId-ne[long]30590171040-or$roundTrip.runAttempt-ne 1){throw 'GitHub run identity lost its numeric Int64 value'}
 if(-not($json|Test-Json -SchemaFile "$PSScriptRoot/schemas/ci-evidence.schema.json")){throw 'JSON Schema validation returned false'}
 Test-BusinessOSCiEvidence $roundTrip|Out-Null
}
Pass 'verifiers leave the environment log under one writer' {
 foreach($name in 'verify-cross-platform.ps1','verify-windows.ps1'){$source=Get-Content "$PSScriptRoot/$name" -Raw;if($source-match'Tee-Object[^\r\n]*environment-tests\.log'){throw "$name reintroduced an external environment log writer"};if($source-notmatch'Invoke-CheckedCommand\s+pwsh[^\r\n]*Environment\.Tests\.ps1'){throw "$name does not preserve checked visible execution"}}
}
Pass 'normal verifier runs contract tests and Windows verifier uses fallback' {
 $crossSource=Get-Content "$PSScriptRoot/verify-cross-platform.ps1" -Raw;$windowsSource=Get-Content "$PSScriptRoot/verify-windows.ps1" -Raw
 if($crossSource-notmatch'Run\s+ci-evidence-contract-tests\s+\{Invoke-CheckedCommand\s+pwsh[^\r\n]*test-ci-evidence\.ps1'){throw 'normal cross-platform verifier does not run CI evidence contract tests'}
 if($windowsSource-notmatch'Write-BusinessOSCiEvidenceFallback\s+-Gate\s+windows'){throw 'Windows verifier does not invoke the shared fallback'}
}
Pass 'forced generator failure creates valid stageable fallback evidence' {
 $root=(Resolve-Path "$PSScriptRoot/..").Path;$started=[DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('o');$generatorError=$null;$old=$env:BUSINESSOS_TEST_FORCE_SUMMARY_FAILURE
 try{$env:BUSINESSOS_TEST_FORCE_SUMMARY_FAILURE='1';try{& "$PSScriptRoot/write-ci-summary.ps1" -Gate cross-platform -Status FAIL -FailureStage tests -FailureMessage 'original gate failure' -LastCompletedStage build -StartedAtUtc $started}catch{$generatorError="$($_.Exception.Message)"}}finally{$env:BUSINESSOS_TEST_FORCE_SUMMARY_FAILURE=$old}
 if(-not$generatorError){throw 'normal generator was not forced to fail'}
 Write-BusinessOSCiEvidenceFallback -Gate cross-platform -Root $root -FailureStage tests -FailureMessage 'original gate failure' -GeneratorError $generatorError -StartedAtUtc $started -LastCompletedStage build|Out-Null
 $path="$root/.cache/ci-evidence-source/cross-platform/summary.json";$json=Get-Content $path -Raw;$fallback=$json|ConvertFrom-Json -Depth 30
 foreach($required in (Get-Content "$PSScriptRoot/schemas/ci-evidence.schema.json" -Raw|ConvertFrom-Json).required){if($fallback.PSObject.Properties.Name-notcontains$required){throw "fallback is missing $required"}}
 if(-not$fallback.generatedAtUtc-or$fallback.gateStatus-ne'FAIL'-or$fallback.failure.stage-ne'tests'-or$fallback.failure.message-ne'original gate failure'-or@($fallback.warnings|Where-Object{$_-like'Summary generator error:*'}).Count-ne 1){throw 'fallback did not preserve failure and generator diagnostics'}
 if(-not($json|Test-Json -SchemaFile "$PSScriptRoot/schemas/ci-evidence.schema.json")){throw 'fallback failed JSON Schema validation'};Test-BusinessOSCiEvidence $fallback|Out-Null
 & "$PSScriptRoot/stage-ci-artifacts.ps1" -Gate cross-platform
}
Pass 'generator failure after successful gate creates failing evidence and nonzero verifier exit' {
 $root=(Resolve-Path "$PSScriptRoot/..").Path;$old=$env:BUSINESSOS_TEST_FORCE_SUMMARY_FAILURE
 try{$env:BUSINESSOS_TEST_FORCE_SUMMARY_FAILURE='1';$output=& pwsh -NoProfile -File "$PSScriptRoot/verify-cross-platform.ps1" -SummaryGenerationRegressionTest 2>&1;$code=$LASTEXITCODE}finally{$env:BUSINESSOS_TEST_FORCE_SUMMARY_FAILURE=$old}
 if($code-eq 0){throw 'verifier masked the summary generator failure'}
 $path="$root/.cache/ci-evidence-source/cross-platform/summary.json";if(-not(Test-Path $path)){throw 'fallback was not created'};$json=Get-Content $path -Raw;$fallback=$json|ConvertFrom-Json -Depth 30
 foreach($required in (Get-Content "$PSScriptRoot/schemas/ci-evidence.schema.json" -Raw|ConvertFrom-Json).required){if($fallback.PSObject.Properties.Name-notcontains$required){throw "fallback is missing $required"}}
 if($fallback.gateStatus-ne'FAIL'-or$fallback.failure.stage-ne'summary-generation'-or$fallback.failure.message-eq'none'-or$fallback.failure.message-notlike'*Forced summary generator failure*'){throw "invalid post-success fallback: $($fallback.failure|ConvertTo-Json -Compress)"}
 Test-BusinessOSCiEvidence $fallback|Out-Null;if(-not(Test-Schema $fallback)){throw 'fallback failed JSON Schema'};& "$PSScriptRoot/stage-ci-artifacts.ps1" -Gate cross-platform
}
Pass 'Windows fallback preserves an earlier gate failure' {
 $root=(Resolve-Path "$PSScriptRoot/..").Path;$started=[DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('o');Write-BusinessOSCiEvidenceFallback -Gate windows -Root $root -FailureStage smoke-RecoveryFromReady -FailureMessage 'original Windows gate failure' -GeneratorError 'Windows summary generator exploded' -StartedAtUtc $started -LastCompletedStage build|Out-Null
 $path="$root/.cache/ci-evidence-source/windows/summary.json";$fallback=Get-Content $path -Raw|ConvertFrom-Json -Depth 30
 if($fallback.gateName-ne'windows'-or$fallback.gateStatus-ne'FAIL'-or$fallback.failure.stage-ne'smoke-RecoveryFromReady'-or$fallback.failure.message-ne'original Windows gate failure'-or@($fallback.warnings|Where-Object{$_-like'*Windows summary generator exploded*'}).Count-ne 1){throw 'Windows fallback did not preserve the gate failure and generator diagnostic'}
 Test-BusinessOSCiEvidence $fallback|Out-Null;if(-not(Test-Schema $fallback)){throw 'Windows gate-failure fallback failed JSON Schema'};& "$PSScriptRoot/stage-ci-artifacts.ps1" -Gate windows
}
Pass 'Windows fallback reports generator failure after a successful gate' {
 $root=(Resolve-Path "$PSScriptRoot/..").Path;$started=[DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('o');Write-BusinessOSCiEvidenceFallback -Gate windows -Root $root -FailureStage complete -FailureMessage none -GeneratorError 'Windows post-success generator exploded' -StartedAtUtc $started -LastCompletedStage complete -FormatStatus PASS -BuildStatus PASS|Out-Null
 $path="$root/.cache/ci-evidence-source/windows/summary.json";$fallback=Get-Content $path -Raw|ConvertFrom-Json -Depth 30
 if($fallback.gateName-ne'windows'-or$fallback.gateStatus-ne'FAIL'-or$fallback.failure.stage-ne'summary-generation'-or$fallback.failure.message-notlike'*Windows post-success generator exploded*'-or@($fallback.warnings).Count-eq 0){throw 'Windows fallback did not expose the post-success generator failure'}
 Test-BusinessOSCiEvidence $fallback|Out-Null;if(-not(Test-Schema $fallback)){throw 'Windows post-success fallback failed JSON Schema'};& "$PSScriptRoot/stage-ci-artifacts.ps1" -Gate windows
}
}finally{
 foreach($state in $protectedState){try{if(Test-Path -LiteralPath $state.Path){Remove-Item -LiteralPath $state.Path -Recurse -Force};if($state.Existed){New-Item -ItemType Directory -Force (Split-Path $state.Path -Parent)|Out-Null;Copy-Item -LiteralPath $state.Backup -Destination $state.Path -Recurse -Force}}catch{$script:failed++;Write-Warning "Evidence test cleanup failed for $($state.Path): $_"}}
 try{if(Test-Path -LiteralPath $backupRoot){Remove-Item -LiteralPath $backupRoot -Recurse -Force}}catch{$script:failed++;Write-Warning "Evidence test backup cleanup failed: $_"}
}
Write-Host "failed $failed";exit $(if($failed){1}else{0})
