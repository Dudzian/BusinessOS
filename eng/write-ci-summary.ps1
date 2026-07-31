[CmdletBinding()]
param(
 [Parameter(Mandatory)][ValidateSet('cross-platform','windows')][string]$Gate,
 [Parameter(Mandatory)][ValidateSet('PASS','FAIL','NOT_RUN')][string]$Status,
 [string]$FailureStage='none',[string]$FailureMessage='none',[string]$LastCompletedStage='none',[string]$StartedAtUtc,
 [ValidateSet('PASS','FAIL','NOT_RUN')][string]$FormatStatus='NOT_RUN',[ValidateSet('PASS','FAIL','NOT_RUN')][string]$BuildStatus='NOT_RUN',[switch]$MinimalFailure)
$ErrorActionPreference='Stop'
$root=(Resolve-Path "$PSScriptRoot/..").Path
$source=Join-Path $root ".cache/ci-evidence-source/$Gate"
New-Item -ItemType Directory -Force $source|Out-Null
if(-not$StartedAtUtc){$StartedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
$now=[DateTimeOffset]::UtcNow;$start=[DateTimeOffset]::Parse($StartedAtUtc)
function EnvOr([string]$Name,[string]$Fallback){$v=[Environment]::GetEnvironmentVariable($Name);if([string]::IsNullOrWhiteSpace($v)){$Fallback}else{$v}}
function Read-PositiveRunNumber([string]$Name){$raw=[Environment]::GetEnvironmentVariable($Name);[long]$value=0;if([string]::IsNullOrWhiteSpace($raw)-or-not[long]::TryParse($raw,[Globalization.NumberStyles]::None,[Globalization.CultureInfo]::InvariantCulture,[ref]$value)-or$value-lt 1){throw "$Name must be a positive integer"};$value}
$checkoutSha=(EnvOr GITHUB_SHA (& git -C $root rev-parse HEAD)).Trim();$checkoutTree=(& git -C $root show -s --format=%T $checkoutSha 2>$null).Trim()
$isActions=[string]::Equals($env:GITHUB_ACTIONS,'true',[StringComparison]::OrdinalIgnoreCase)
$isPr=$isActions-and$env:GITHUB_EVENT_NAME-eq'pull_request'
$checkoutKind=if($isPr){'pull-request-merge'}elseif($isActions){'branch-head'}else{'local'}
$runId=if($isActions){Read-PositiveRunNumber GITHUB_RUN_ID}else{$null};$runAttempt=if($isActions){Read-PositiveRunNumber GITHUB_RUN_ATTEMPT}else{$null}
$event=$null;if($isPr){if(-not$env:GITHUB_EVENT_PATH-or-not(Test-Path $env:GITHUB_EVENT_PATH -PathType Leaf)){throw 'GITHUB_EVENT_PATH is required for pull_request'};$event=Get-Content $env:GITHUB_EVENT_PATH -Raw|ConvertFrom-Json}
$counts=[ordered]@{discovered=0;executed=0;passed=0;failed=0;skipped=0;trxFiles=[object[]]@()}
$vulnerabilityStatus='NOT_RUN';$known=0;$targets=[object[]]@()
$migration=[ordered]@{status='NOT_RUN';pendingModelChanges=$null;newMigrationCreated=$null;snapshotModified=$null}
$scenarios=[object[]]@();$smoke=[ordered]@{requiredScenarioCount=if($Gate-eq'windows'){5}else{0};executedScenarioCount=0;passedScenarioCount=0;failedScenarioCount=0;scenarios=$scenarios}
if(-not$MinimalFailure){
 $trx=@(Get-ChildItem (Join-Path $root 'artifacts/test-results') -Recurse -File -Filter *.trx -ErrorAction SilentlyContinue)
 $trxFiles=@();foreach($f in $trx){[xml]$x=Get-Content -LiteralPath $f.FullName -Raw;$c=$x.TestRun.ResultSummary.Counters;$counts.discovered+=[int]$c.total;$counts.executed+=[int]$c.executed;$counts.passed+=[int]$c.passed;$counts.failed+=[int]$c.failed;$counts.skipped+=([int]$c.notExecuted+[int]$c.inconclusive);$trxFiles+=([IO.Path]::GetRelativePath($root,$f.FullName).Replace('\','/'))};$counts.trxFiles=[object[]]$trxFiles
 $vulnerabilityPath=Join-Path $root '.cache/vulnerable-packages.json';if(Test-Path $vulnerabilityPath){$v=Get-Content $vulnerabilityPath -Raw|ConvertFrom-Json -Depth 50;$targets=[object[]]@($v.targets);foreach($report in @($v.reports)){foreach($p in @($report.projects)){foreach($fw in @($p.frameworks|Where-Object{$_})){foreach($pkg in @($fw.topLevelPackages)+@($fw.transitivePackages)|Where-Object{$_}){if($pkg.vulnerabilities){$known+=@($pkg.vulnerabilities).Count}}}}};$vulnerabilityStatus=if($known-eq 0){'PASS'}else{'FAIL'}}
 $migrationPath=Join-Path $root '.cache/migration-evidence.json';if(Test-Path $migrationPath){$mi=Get-Content $migrationPath -Raw|ConvertFrom-Json;$migration=[ordered]@{status=$mi.status;pendingModelChanges=$mi.pendingModelChanges;newMigrationCreated=$mi.newMigrationCreated;snapshotModified=$mi.snapshotModified}}
 $scenarioFiles=@(Get-ChildItem (Join-Path $root 'artifacts/smoke-test/scenarios') -File -Filter *.json -ErrorAction SilentlyContinue);$scenarios=[object[]]@($scenarioFiles|ForEach-Object{Get-Content $_.FullName -Raw|ConvertFrom-Json -Depth 20});$smoke=[ordered]@{requiredScenarioCount=if($Gate-eq'windows'){5}else{0};executedScenarioCount=$scenarios.Count;passedScenarioCount=@($scenarios|Where-Object status -eq'PASS').Count;failedScenarioCount=@($scenarios|Where-Object status -ne'PASS').Count;scenarios=$scenarios}
}
$warningItems=[object[]]@();[object[]]$errorItems=@();if($Status-eq'FAIL'){$errorItems=[object[]]@($FailureMessage)}
$dotnetVersion=try{(& dotnet --version 2>$null)}catch{'NOT_AVAILABLE'};if([string]::IsNullOrWhiteSpace(($dotnetVersion-join''))){$dotnetVersion='NOT_AVAILABLE'}
$summary=[ordered]@{schemaVersion=1;generatedAtUtc=$now.ToString('o');repository=(EnvOr GITHUB_REPOSITORY 'local');eventName=(EnvOr GITHUB_EVENT_NAME 'local');workflowName=(EnvOr GITHUB_WORKFLOW 'local');runId=$runId;runAttempt=$runAttempt;jobKey=(EnvOr GITHUB_JOB $Gate);checkoutKind=$checkoutKind;commitSha=$checkoutSha;commitTreeSha=$checkoutTree;pullRequestNumber=if($isPr){[int]$event.number}else{$null};pullRequestHeadSha=if($isPr){[string]$event.pull_request.head.sha}else{$null};pullRequestMergeSha=if($isPr){[string]$event.pull_request.merge_commit_sha}else{$null};runnerOs=(EnvOr RUNNER_OS ([Runtime.InteropServices.RuntimeInformation]::OSDescription));runnerArch=(EnvOr RUNNER_ARCH ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture));runnerImage=(EnvOr ImageOS 'local');dotnetVersion=[string]($dotnetVersion-join'');powershellVersion=$PSVersionTable.PSVersion.ToString();gateName=$Gate;gateStatus=$Status;startedAtUtc=$start.ToString('o');completedAtUtc=$now.ToString('o');durationSeconds=[math]::Round(($now-$start).TotalSeconds,3);lastCompletedStage=$LastCompletedStage;formatStatus=$FormatStatus;buildStatus=$BuildStatus;warnings=$warningItems;errors=$errorItems;tests=$counts;vulnerabilities=[ordered]@{status=$vulnerabilityStatus;knownVulnerabilityCount=$known;checkedTargets=$targets};migrations=$migration;smoke=$smoke;failure=[ordered]@{stage=$FailureStage;message=$FailureMessage;diagnosticFiles=[object[]]@()}}
$path=Join-Path $source 'summary.json';$summary|ConvertTo-Json -Depth 30|Set-Content $path -Encoding utf8NoBOM
Import-Module "$PSScriptRoot/BusinessOS.CiEvidence.psm1" -Force;Test-BusinessOSCiEvidence ($summary|ConvertTo-Json -Depth 30|ConvertFrom-Json -Depth 30)|Out-Null;Write-Host "CI summary: $path"
