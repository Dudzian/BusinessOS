$ErrorActionPreference='Stop';$root=(Resolve-Path "$PSScriptRoot/..").Path;$writer="$PSScriptRoot/write-ci-summary.ps1";$module="$PSScriptRoot/BusinessOS.CiEvidence.psm1";$schema="$PSScriptRoot/schemas/ci-evidence.schema.json";$failed=0
Import-Module $module -Force
$names='GITHUB_ACTIONS','GITHUB_EVENT_NAME','GITHUB_RUN_ID','GITHUB_RUN_ATTEMPT','GITHUB_EVENT_PATH','GITHUB_SHA','GITHUB_REPOSITORY','GITHUB_WORKFLOW','GITHUB_JOB';$saved=@{};foreach($n in $names){$saved[$n]=[Environment]::GetEnvironmentVariable($n)}
function Set-Env($values){foreach($n in $names){[Environment]::SetEnvironmentVariable($n,$null)};foreach($p in $values.GetEnumerator()){[Environment]::SetEnvironmentVariable($p.Key,[string]$p.Value)}}
function Read-Generated{$p="$root/.cache/ci-evidence-source/cross-platform/summary.json";$raw=Get-Content $p -Raw;$x=$raw|ConvertFrom-Json -Depth 30;Test-BusinessOSCiEvidence $x|Out-Null;if(-not($raw|Test-Json -SchemaFile $schema -ErrorAction SilentlyContinue)){throw 'Generated summary failed JSON Schema'};,$x}
function Invoke-Writer([switch]$Minimal){$args=@('-NoProfile','-File',$writer,'-Gate','cross-platform','-Status','FAIL','-FailureStage','controlled','-FailureMessage','controlled failure','-LastCompletedStage','none','-FormatStatus','NOT_RUN','-BuildStatus','NOT_RUN');if($Minimal){$args+='-MinimalFailure'};$output=&pwsh @args;if($output){Write-Host ($output -join [Environment]::NewLine)};return $LASTEXITCODE}
try{
 $head=(&git -C $root rev-parse HEAD).Trim();$base=@{GITHUB_ACTIONS='true';GITHUB_EVENT_NAME='push';GITHUB_RUN_ID='123456';GITHUB_RUN_ATTEMPT='2';GITHUB_SHA=$head;GITHUB_REPOSITORY='owner/repo';GITHUB_WORKFLOW='ci';GITHUB_JOB='cross-platform'}
 Set-Env $base;if((Invoke-Writer -Minimal)-ne 0){throw 'Branch Actions generator failed'};$x=Read-Generated;if($x.checkoutKind-ne'branch-head'-or$x.runId-isnot[long]-or$x.runAttempt-isnot[long]){throw 'Branch run identity was not numeric'};Write-Host 'Generator branch Actions numeric identity PASS'
 $event="$root/.cache/test-pr-event.json";@{number=42;pull_request=@{head=@{sha='a'*40};merge_commit_sha='b'*40}}|ConvertTo-Json -Depth 5|Set-Content $event;$pr=$base.Clone();$pr.GITHUB_EVENT_NAME='pull_request';$pr.GITHUB_EVENT_PATH=$event;Set-Env $pr;if((Invoke-Writer -Minimal)-ne 0){throw 'PR Actions generator failed'};$x=Read-Generated;if($x.checkoutKind-ne'pull-request-merge'-or$x.runId-isnot[long]-or$x.runAttempt-isnot[long]-or$x.pullRequestNumber-ne 42){throw 'PR identity was invalid'};Write-Host 'Generator PR Actions numeric identity PASS'
 foreach($case in @(@{Name='missing-run-id';Id=$null;Attempt='2'},@{Name='missing-run-attempt';Id='1';Attempt=$null},@{Name='zero';Id='0';Attempt='1'},@{Name='negative';Id='-1';Attempt='1'},@{Name='text';Id='abc';Attempt='1'},@{Name='fraction';Id='1.5';Attempt='1'})){$v=$base.Clone();$v.GITHUB_RUN_ID=$case.Id;$v.GITHUB_RUN_ATTEMPT=$case.Attempt;Set-Env $v;if((Invoke-Writer -Minimal)-eq 0){throw "$($case.Name) was accepted"};Write-Host "Generator $($case.Name) rejected PASS"}
 Set-Env @{};if((Invoke-Writer -Minimal)-ne 0){throw 'Local generator failed'};$x=Read-Generated;if($null-ne$x.runId-or$null-ne$x.runAttempt-or$x.checkoutKind-ne'local'){throw 'Local run identity must be null'};Write-Host 'Generator local null identity PASS'
 $trxDir="$root/artifacts/test-results";$scenarioDir="$root/artifacts/smoke-test/scenarios";New-Item $trxDir,$scenarioDir -ItemType Directory -Force|Out-Null
 $passScenario="$scenarioDir/count-pass.json";$failScenario="$scenarioDir/count-fail.json"
 $scenarioFixtures=(Get-Content "$root/tests/fixtures/github-api/missing-manifest/windows/summary.json" -Raw|ConvertFrom-Json -Depth 30).smoke.scenarios
 $scenarioFixtures[0].status='PASS';$scenarioFixtures[0]|ConvertTo-Json -Depth 20|Set-Content $passScenario
 $scenarioFixtures[1].status='FAIL';$scenarioFixtures[1]|ConvertTo-Json -Depth 20|Set-Content $failScenario
 Set-Env @{};if((Invoke-Writer)-ne 0){throw 'Normal scenario count generator failed'};$x=Read-Generated
 if($x.smoke.executedScenarioCount-ne 2-or$x.smoke.passedScenarioCount-ne 1-or$x.smoke.failedScenarioCount-ne 1){throw 'Normal scenario counts were invalid'}
 Remove-Item $passScenario,$failScenario
 Write-Host 'Generator normal scenario counting PASS'
 Get-Command Write-BusinessOSCiEvidenceFallback -ErrorAction Stop|Out-Null
 Set-Env $base
 $fallbackPath=Write-BusinessOSCiEvidenceFallback -Gate cross-platform -Root $root -FailureStage 'build' -FailureMessage 'original gate failure' -GeneratorError 'controlled generator failure' -StartedAtUtc ([DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('o'))
 $fallback=Get-Content $fallbackPath -Raw|ConvertFrom-Json -Depth 30
 Test-BusinessOSCiEvidence $fallback|Out-Null
 if($fallback.gateStatus-ne'FAIL'-or$fallback.failure.stage-ne'build'-or$fallback.failure.message-ne'original gate failure'-or@($fallback.warnings|Where-Object{$_-like'*controlled generator failure*'}).Count-ne 1){throw 'Fallback evidence did not preserve the original gate failure'}
 Write-Host 'Exported fallback evidence preserves gate failure PASS'
 $smokeSource=Get-Content "$PSScriptRoot/smoke-test-desktop.ps1" -Raw
 if($smokeSource-match"Get-AutomationIdElement[^`r`n]*CompanyEditorPanel"-or$smokeSource-match"Get-AutomationIdElement[^`r`n]*BusinessProjectEditorPanel"){throw 'Desktop smoke depends on a layout editor panel'}
 foreach($automationId in 'CompanyLegalNameInput','CompanyDisplayNameInput','SaveCompanyButton','CancelCompanyButton','BusinessProjectNameInput','BusinessProjectTypeInput','SaveBusinessProjectButton','CancelBusinessProjectButton'){if(-not$smokeSource.Contains("'$automationId'")){throw "Desktop smoke is missing stable AutomationId $automationId"}}
 Write-Host 'Desktop smoke interactive editor contract PASS'
 if($smokeSource.Contains("Get-NamedElements `$selector 'BusinessOS Smoke Updated'")){throw 'Desktop smoke uses a descendant name as the selected company contract'}
 $selectionHelper='(?s)function Get-SelectedAutomationItem\(\$Element\).*?SelectionPattern\]::Pattern.*?GetSelection\(\).*?function Test-SelectedAutomationItemName'
 $projectsSelection='(?s)BusinessProjectsCompanySelector.*?Test-SelectedAutomationItemName \$selector ''BusinessOS Smoke Updated'''
 if($smokeSource-notmatch$selectionHelper-or$smokeSource-notmatch$projectsSelection){throw 'Desktop smoke does not connect the BusinessProjects company selector to SelectionPattern.GetSelection()'}
 Write-Host 'Desktop smoke BusinessProjects selected company contract PASS'
 $corruptions=@(
  @{Name='corrupt-trx';Setup={Set-Content "$trxDir/corrupt.trx" '<bad'}},
  @{Name='corrupt-vulnerabilities';Setup={Set-Content "$root/.cache/vulnerable-packages.json" '{bad'}},
  @{Name='corrupt-migrations';Setup={Set-Content "$root/.cache/migration-evidence.json" '{bad'}},
  @{Name='corrupt-smoke';Setup={Set-Content "$scenarioDir/corrupt.json" '{bad'}})
 foreach($case in $corruptions){&$case.Setup;Set-Env @{};if((Invoke-Writer -Minimal)-ne 0){throw "MinimalFailure failed for $($case.Name)"};$x=Read-Generated;if($x.gateStatus-ne'FAIL'-or$x.tests.executed-ne 0-or$x.vulnerabilities.status-ne'NOT_RUN'-or$x.migrations.status-ne'NOT_RUN'-or$x.smoke.executedScenarioCount-ne 0){throw 'MinimalFailure was not canonical'};Write-Host "MinimalFailure $($case.Name) ignored PASS";Remove-Item "$trxDir/corrupt.trx","$root/.cache/vulnerable-packages.json","$root/.cache/migration-evidence.json","$scenarioDir/corrupt.json" -ErrorAction SilentlyContinue}
 Set-Content "$root/.cache/vulnerable-packages.json" '{bad';if((Invoke-Writer)-eq 0){throw 'Normal mode ignored corrupt vulnerabilities'};Remove-Item "$root/.cache/vulnerable-packages.json";Write-Host 'Normal mode corrupt input rejected PASS'
 if(-not$IsWindows){$bin="$root/.cache/test-path";New-Item $bin -ItemType Directory -Force|Out-Null;New-Item -ItemType SymbolicLink -Path "$bin/git" -Target (Get-Command git).Source -Force|Out-Null;New-Item -ItemType SymbolicLink -Path "$bin/pwsh" -Target (Get-Command pwsh).Source -Force|Out-Null;Set-Content "$bin/dotnet" @('#!/bin/sh','exit 1');&chmod +x "$bin/dotnet";$oldPath=$env:PATH;$env:PATH=$bin;try{if((Invoke-Writer -Minimal)-ne 0){throw 'MinimalFailure failed without dotnet'};$x=Read-Generated;if($x.dotnetVersion-ne'NOT_AVAILABLE'){throw 'dotnet fallback missing'}}finally{$env:PATH=$oldPath};Write-Host 'MinimalFailure unavailable dotnet PASS'}
}catch{Write-Error $_;$failed++}finally{foreach($n in $names){[Environment]::SetEnvironmentVariable($n,$saved[$n])};Remove-Item "$root/.cache/test-pr-event.json","$root/.cache/vulnerable-packages.json","$root/.cache/migration-evidence.json","$root/artifacts/test-results/corrupt.trx","$root/artifacts/smoke-test/scenarios/corrupt.json","$root/artifacts/smoke-test/scenarios/count-pass.json","$root/artifacts/smoke-test/scenarios/count-fail.json" -ErrorAction SilentlyContinue}
Write-Host "Generator runtime failures: $failed";exit $(if($failed){1}else{0})
