[CmdletBinding()]
param(
 [Parameter(Mandatory)][ValidateSet('cross-platform','windows')][string]$Gate,
 [Parameter(Mandatory)][ValidateSet('PASS','FAIL','NOT_RUN')][string]$Status,
 [string]$FailureStage='none',[string]$FailureMessage='none',[string]$LastCompletedStage='none',[string]$StartedAtUtc,
 [ValidateSet('PASS','FAIL','NOT_RUN')][string]$FormatStatus='NOT_RUN',[ValidateSet('PASS','FAIL','NOT_RUN')][string]$BuildStatus='NOT_RUN')
$ErrorActionPreference='Stop';$root=(Resolve-Path "$PSScriptRoot/..").Path;$source=Join-Path $root ".cache/ci-evidence-source/$Gate";New-Item -ItemType Directory -Force $source|Out-Null
if($env:BUSINESSOS_TEST_FORCE_SUMMARY_FAILURE-eq'1'){throw 'Forced summary generator failure for regression testing.'}
Import-Module "$PSScriptRoot/BusinessOS.CiEvidence.psm1" -Force
if(-not$StartedAtUtc){$StartedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')};$now=[DateTimeOffset]::UtcNow;$start=[DateTimeOffset]::Parse($StartedAtUtc)
function EnvOr([string]$Name,[string]$Fallback){$v=[Environment]::GetEnvironmentVariable($Name);if([string]::IsNullOrWhiteSpace($v)){$Fallback}else{$v}}
$checkoutSha=(EnvOr GITHUB_SHA (& git -C $root rev-parse HEAD)).Trim();$checkoutTree=(& git -C $root show -s --format=%T $checkoutSha 2>$null).Trim();$event=$null;if($env:GITHUB_EVENT_PATH-and(Test-Path $env:GITHUB_EVENT_PATH)){$event=Get-Content $env:GITHUB_EVENT_PATH -Raw|ConvertFrom-Json}
$isPr=$env:GITHUB_EVENT_NAME-eq'pull_request';$checkoutKind=if($isPr){'pull-request-merge'}elseif($env:GITHUB_ACTIONS){'branch-head'}else{'local'}
$trx=@(Get-ChildItem (Join-Path $root 'artifacts/test-results') -Recurse -File -Filter *.trx -ErrorAction SilentlyContinue);$counts=[ordered]@{
 discovered=0
 executed=0
 passed=0
 failed=0
 skipped=0
 trxFiles=@()
};foreach($f in $trx){[xml]$x=Get-Content -LiteralPath $f.FullName -Raw;$c=$x.TestRun.ResultSummary.Counters;$counts.discovered+=[int]$c.total;$counts.executed+=[int]$c.executed;$counts.passed+=[int]$c.passed;$counts.failed+=[int]$c.failed;$counts.skipped+=([int]$c.notExecuted+[int]$c.inconclusive);$counts.trxFiles+=([IO.Path]::GetRelativePath($root,$f.FullName).Replace('\\','/'))}
$vulnerabilityPath=Join-Path $root '.cache/vulnerable-packages.json';$vulnerabilityStatus='NOT_RUN';$known=0;$targets=@();if(Test-Path $vulnerabilityPath){$v=Get-Content $vulnerabilityPath -Raw|ConvertFrom-Json -Depth 50;$targets=@($v.targets);foreach($report in @($v.reports)){foreach($p in @($report.projects)){foreach($fw in @($p.frameworks|Where-Object{$_})){foreach($pkg in @($fw.topLevelPackages)+@($fw.transitivePackages)|Where-Object{$_}){if($pkg.vulnerabilities){$known+=@($pkg.vulnerabilities).Count}}}}};$vulnerabilityStatus=if($known-eq 0){'PASS'}else{'FAIL'}}
$migrationPath=Join-Path $root '.cache/migration-evidence.json';$migration=[ordered]@{status='NOT_RUN';pendingModelChanges=$false;newMigrationCreated=$false;snapshotModified=$false};if(Test-Path $migrationPath){$mi=Get-Content $migrationPath -Raw|ConvertFrom-Json;$migration=[ordered]@{status=$mi.status;pendingModelChanges=[bool]$mi.pendingModelChanges;newMigrationCreated=[bool]$mi.newMigrationCreated;snapshotModified=[bool]$mi.snapshotModified}}
$scenarioFiles=@(Get-ChildItem (Join-Path $root 'artifacts/smoke-test/scenarios') -File -Filter *.json -ErrorAction SilentlyContinue);$scenarios=@($scenarioFiles|ForEach-Object{Get-Content $_.FullName -Raw|ConvertFrom-Json -Depth 20})
$passedScenarioCount = @(
 $scenarios | Where-Object { $_.status -eq 'PASS' }
).Count
$failedScenarioCount = @(
 $scenarios | Where-Object { $_.status -ne 'PASS' }
).Count
$smoke=[ordered]@{requiredScenarioCount=if($Gate-eq'windows'){5}else{0};executedScenarioCount=$scenarios.Count;passedScenarioCount=$passedScenarioCount;failedScenarioCount=$failedScenarioCount;scenarios=$scenarios}
$warningItems=[string[]]@();$errorItems=[string[]]@();if($Status-eq'FAIL'){$errorItems=@($FailureMessage)}
$summary=New-BusinessOSCiEvidenceSummary @{generatedAtUtc=$now.ToString('o');repository=(EnvOr GITHUB_REPOSITORY 'local');eventName=(EnvOr GITHUB_EVENT_NAME 'local');workflowName=(EnvOr GITHUB_WORKFLOW 'local');runId=$env:GITHUB_RUN_ID;runAttempt=$env:GITHUB_RUN_ATTEMPT;jobKey=(EnvOr GITHUB_JOB $Gate);checkoutKind=$checkoutKind;commitSha=$checkoutSha;commitTreeSha=$checkoutTree;pullRequestNumber=if($isPr){[int]$event.number}else{$null};pullRequestHeadSha=if($isPr){[string]$event.pull_request.head.sha}else{$null};pullRequestMergeSha=if($isPr){[string]$event.pull_request.merge_commit_sha}else{$null};runnerOs=(EnvOr RUNNER_OS ([Runtime.InteropServices.RuntimeInformation]::OSDescription));runnerArch=(EnvOr RUNNER_ARCH ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture));runnerImage=(EnvOr ImageOS 'local');dotnetVersion=(& dotnet --version);powershellVersion=$PSVersionTable.PSVersion.ToString();gateName=$Gate;gateStatus=$Status;startedAtUtc=$start.ToString('o');completedAtUtc=$now.ToString('o');durationSeconds=[math]::Round(($now-$start).TotalSeconds,3);lastCompletedStage=$LastCompletedStage;formatStatus=$FormatStatus;buildStatus=$BuildStatus;warnings=$warningItems;errors=$errorItems;tests=$counts;vulnerabilities=[ordered]@{status=$vulnerabilityStatus;knownVulnerabilityCount=$known;checkedTargets=$targets};migrations=$migration;smoke=$smoke;failure=[ordered]@{stage=$FailureStage;message=$FailureMessage;diagnosticFiles=[string[]]@()}}
$path=Join-Path $source 'summary.json';$json=$summary|ConvertTo-Json -Depth 30;$validated=$json|ConvertFrom-Json -Depth 30;Test-BusinessOSCiEvidence $validated|Out-Null
if(-not($json|Test-Json -SchemaFile "$PSScriptRoot/schemas/ci-evidence.schema.json" -ErrorAction Stop)){throw 'CI summary failed JSON Schema validation'}
$temporary=Join-Path $source ("summary.{0}.tmp" -f [guid]::NewGuid().ToString('N'));try{$json|Set-Content $temporary -Encoding utf8NoBOM;Move-Item -LiteralPath $temporary -Destination $path -Force}finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}};Write-Host "CI summary: $path"
