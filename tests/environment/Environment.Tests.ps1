param([switch]$Quick)
$ErrorActionPreference='Stop'
$RepoRoot=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
Import-Module (Join-Path $RepoRoot 'eng/BusinessOS.Engineering.psm1') -Force
Import-Module (Join-Path $RepoRoot 'eng/BusinessOS.Provisioning.psm1') -Force
$log=Join-Path $RepoRoot '.cache/environment-tests.log'; New-Item -ItemType Directory -Force (Split-Path $log)|Out-Null; ''|Set-Content -LiteralPath $log
$script:Failures=0
function Assert($Name,[scriptblock]$Body){try{& $Body; "PASS $Name"|Tee-Object -FilePath $log -Append}catch{$script:Failures++; "FAIL $Name :: $($_.Exception.Message)"|Tee-Object -FilePath $log -Append; Write-Error $_}}
Assert 'desktop smoke closes the selected AutomationElement through WindowPattern' {
    $smokePath = Join-Path $RepoRoot 'eng/smoke-test-desktop.ps1'
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($smokePath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "desktop smoke has parser errors: $($parseErrors.Message -join '; ')" }

    $assignments = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true))
    $targetAssignment = @($assignments | Where-Object {
        $_.Left.Extent.Text -eq '$targetWindow' -and $_.Right.Extent.Text -eq '$mainWindowsBeforeClose[0]'
    })
    if ($targetAssignment.Count -ne 1) { throw 'desktop smoke does not select exactly one target window from the validated main-window collection' }

    $invocations = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true))
    if (-not ($invocations | Where-Object {
        $_.Expression.Extent.Text -eq '$targetWindow' -and $_.Member.Extent.Text -eq 'GetCurrentPattern' -and
        $_.Arguments.Count -eq 1 -and $_.Arguments[0].Extent.Text -match 'WindowPattern\]::Pattern'
    })) { throw 'desktop smoke does not obtain WindowPattern from the selected target window' }
    if (-not ($invocations | Where-Object {
        $_.Expression.Extent.Text -eq '$windowPattern' -and $_.Member.Extent.Text -eq 'Close'
    })) { throw 'desktop smoke does not close the WindowPattern obtained for the target window' }
    if ($invocations | Where-Object {
        $_.Expression.Extent.Text -eq '$process' -and $_.Member.Extent.Text -eq 'CloseMainWindow'
    }) { throw 'desktop smoke still dispatches shutdown through Process.CloseMainWindow' }

    $source = $ast.Extent.Text
    foreach ($field in 'ProcessMainWindowHandleBeforeClose','ProcessMainWindowTitleBeforeClose','TargetWindowNativeHandle','TargetWindowTitle','TargetWindowAutomationId','TargetWindowControlType','ProcessAndTargetHandleMatch','CloseDispatchMethod') {
        if ($source -notmatch [regex]::Escape("$field`:")) { throw "desktop smoke does not write diagnostic field $field" }
    }
    if ($source -notmatch "CloseDispatchMethod\s*=\s*'UIAutomation\.WindowPattern\.Close'") { throw 'desktop smoke does not identify the WindowPattern close dispatch method' }
    if ($source -notmatch 'ShutdownMethod: \$shutdownMethod' -or $source -notmatch 'SmokeResult: FAIL' -or $source -notmatch '\$process\.Kill\(\$true\)') {
        throw 'desktop smoke does not retain Kill solely on the diagnosed failure path'
    }
}
Assert 'desktop smoke implements complete recovery scenarios' {
    $source = Get-Content -LiteralPath (Join-Path $RepoRoot 'eng/smoke-test-desktop.ps1') -Raw
    foreach ($scenario in 'RecoveryFromReady','RecoveryFromStartupFailure') {
        if ($source -notmatch "'$scenario'\s*\{") { throw "$scenario does not have a dedicated switch branch" }
    }
    foreach ($required in 'prepare-ready','prepare-startup-failure','validate-restored','OpenRecoveryFromMainButton','OpenRecoveryFromFailureButton','ConfirmRestoreButton','CancelRestoreButton','RequiredConsecutiveSuccesses 5','ShutdownMethod: $shutdownMethod') {
        if (-not $source.Contains($required, [StringComparison]::Ordinal)) { throw "recovery smoke is missing: $required" }
    }
    if (-not $source.Contains("`$shutdownMethod = 'Kill'", [StringComparison]::Ordinal) -or -not $source.Contains('ShutdownMethod: $shutdownMethod', [StringComparison]::Ordinal)) { throw 'recovery smoke does not diagnose emergency Kill' }
}
Assert 'desktop Ready smoke uses semantic BusinessProjects controls without gating on its layout panel' {
    $smokePath = Join-Path $RepoRoot 'eng/smoke-test-desktop.ps1'
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($smokePath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "desktop smoke has parser errors: $($parseErrors.Message -join '; ')" }

    $readinessFunction = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-BusinessProjectsReadinessState'
    }, $true))
    if ($readinessFunction.Count -ne 1) { throw 'desktop smoke must define exactly one BusinessProjects readiness helper' }
    $readiness = $readinessFunction[0].Extent.Text
    foreach ($required in 'BusinessProjectsCompanySelector','Get-ComboBoxSemanticSelection','ExpectedSemanticSelection','AddBusinessProjectButton','AddButtonVisible','AddButtonEnabled','BusinessProjectsEmptyState','EmptyStateVisible') {
        if (-not $readiness.Contains($required, [StringComparison]::Ordinal)) { throw "BusinessProjects readiness is missing semantic condition: $required" }
    }
    if ($readiness.Contains('BusinessProjectsSectionPanel', [StringComparison]::Ordinal)) { throw 'BusinessProjects readiness still depends on the layout-only section panel' }
    if ($readiness -notmatch 'IsReady\s*=\s*\$selectorVisible\s+-and\s+\$semanticSelection\.IsExpected\s+-and\s+\$addVisible\s+-and\s+\$addEnabled\s+-and\s+\$emptyStateVisible') {
        throw 'BusinessProjects readiness does not require all semantic visible/enabled/selection signals'
    }

    $source = $ast.Extent.Text
    if ($source -notmatch "Get-BusinessProjectsReadinessState\s+\`$Main\s+'BusinessOS Smoke Updated'\)\.IsReady") { throw 'Ready scenario does not gate on the semantic readiness helper and expected company' }
    if ($source -notmatch 'BusinessProjectsSectionPanel \(informational only\)') { throw 'layout panel is not retained as explicitly informational timeout diagnostics' }
}
Assert 'desktop BusinessProject editor readiness uses UIA capabilities instead of viewport geometry' {
    $smokePath = Join-Path $RepoRoot 'eng/smoke-test-desktop.ps1'
    $tokens = $null; $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($smokePath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "desktop smoke has parser errors: $($parseErrors.Message -join '; ')" }
    function Get-SmokeFunction([string]$Name) {
        @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name }, $true))[0].Extent.Text
    }
    $open = Get-SmokeFunction 'Test-BusinessProjectEditorOpen'
    $closed = Get-SmokeFunction 'Test-BusinessProjectEditorClosed'
    $valueReady = Get-SmokeFunction 'Test-AutomationValueInputReady'
    foreach ($id in 'BusinessProjectNameInput','BusinessProjectTypeInput','BusinessProjectLocationInput','BusinessProjectCurrencyInput') {
        if (-not $open.Contains("'$id'", [StringComparison]::Ordinal)) { throw "BusinessProject editor readiness omits Ready input $id" }
    }
    if ($open -match 'Test-Visible|IsOffscreen' -or $open -match 'BusinessProjectEditorPanel') { throw 'BusinessProject editor OPEN readiness depends on viewport or layout geometry' }
    if ($valueReady -notmatch 'IsEnabled' -or $valueReady -notmatch 'TryGetCurrentPattern' -or $valueReady -notmatch 'ValuePattern' -or $valueReady -notmatch 'IsReadOnly') { throw 'value input readiness does not require enabled, writable UIA ValuePattern capability' }
    foreach ($signal in 'SaveBusinessProjectButton','CancelBusinessProjectButton','BusinessProjectsStatusFilter') { if (-not $open.Contains("'$signal'", [StringComparison]::Ordinal)) { throw "BusinessProject editor OPEN omits $signal" } }
    if ($closed -match 'Test-Visible|IsOffscreen' -or $closed -notmatch 'BusinessProjectsStatusFilter' -or $closed -notmatch 'AddBusinessProjectButton') { throw 'BusinessProject editor CLOSED is not based on restored interaction capabilities' }
}
Assert 'desktop BusinessProject editor timeout reports control capabilities and emits diagnostics' {
    $source = Get-Content -LiteralPath (Join-Path $RepoRoot 'eng/smoke-test-desktop.ps1') -Raw
    foreach ($field in 'Found=False','IsEnabled=','IsOffscreen=','ControlType=','ValuePatternSupported=') { if (-not $source.Contains($field, [StringComparison]::Ordinal)) { throw "editor diagnostics omit $field" } }
    foreach ($id in 'AddBusinessProjectButton','BusinessProjectsCompanySelector','BusinessProjectsStatusFilter','BusinessProjectOperationMessage') { if (-not $source.Contains("'$id'", [StringComparison]::Ordinal)) { throw "BusinessProject editor diagnostics omit $id" } }
    $wait = [regex]::Match($source, '(?ms)^function Wait-EditorOpen.*?^}').Value
    if ($wait -notmatch 'Write-SmokeDiagnosticsToHost') { throw 'Wait-EditorOpen does not emit artifact diagnostics to CI output' }
}
Assert 'desktop Ready smoke scopes status transition readiness and diagnostics to the expected project row' {
    $smokePath = Join-Path $RepoRoot 'eng/smoke-test-desktop.ps1'
    $tokens = $null; $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($smokePath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "desktop smoke has parser errors: $($parseErrors.Message -join '; ')" }
    function Get-SmokeFunctionAst([string]$Name) {
        @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name }, $true))[0]
    }
    $statusState = Get-SmokeFunctionAst 'Get-BusinessProjectStatusState'
    $statusReady = Get-SmokeFunctionAst 'Test-BusinessProjectStatusReady'
    $statusDiagnostics = Get-SmokeFunctionAst 'Write-BusinessProjectStatusTimeoutDiagnostics'
    foreach ($function in $statusState, $statusReady, $statusDiagnostics) { if ($null -eq $function) { throw 'desktop smoke is missing a status transition helper' } }
    $stateSource = $statusState.Extent.Text
    if ($stateSource -notmatch 'Get-NamedElements\s+\$list\s+\$ProjectName' -or $stateSource -notmatch 'Get-ContainingListItem' -or $stateSource -notmatch 'TreeScope\]::Descendants') {
        throw 'status validation is not scoped from the exact project name through its containing ListItem descendants'
    }
    if ($stateSource -match 'IsOffscreen|BoundingRectangle|Start-Sleep') { throw 'status validation depends on viewport geometry or sleeps' }
    $readySource = $statusReady.Extent.Text
    foreach ($signal in 'ProjectCount -eq 1','StatusConfirmed','BusinessProjectStatusDialog','BusinessProjectsStatusFilter','OpenRecoveryFromMainButton') {
        if (-not $readySource.Contains($signal, [StringComparison]::Ordinal)) { throw "status readiness omits semantic/interaction signal: $signal" }
    }
    if ($readySource -match 'IsOffscreen|BoundingRectangle|Start-Sleep') { throw 'status readiness depends on viewport geometry or sleeps' }
    $source = $ast.Extent.Text
    if ($source -match "Get-NamedElements\s+\(Get-AutomationIdElement\s+\`$Main\s+'BusinessProjectsList'\)\s+'Analysis'") { throw 'global exact-name Analysis lookup remains in the Ready smoke' }
    if ($source -notmatch "Wait-BusinessProjectStatusReady\s+\`$Main\s+'BusinessOS Gym Smoke Updated'\s+'Analysis'") { throw 'Ready scenario does not validate Analysis for the expected project' }
    foreach ($field in 'Scenario:','Expected project name:','Expected status:','BusinessProjectsList:','BusinessProjectStatusDialog still visible:','BusinessProjectOperationMessage.Current.Name:','Project ListItem semantic descendant names:','ChangeBusinessProjectStatusButton','BusinessProjectsStatusFilter','OpenRecoveryFromMainButton') {
        if (-not $statusDiagnostics.Extent.Text.Contains($field, [StringComparison]::Ordinal)) { throw "status timeout diagnostics omit: $field" }
    }
    $confirmCalls = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] -and $node.Extent.Text -match "ConfirmBusinessProjectStatusButton" }, $true))
    if ($confirmCalls.Count -ne 1) { throw 'Ready smoke must confirm the status transition exactly once' }
    $tail = $source.Substring($confirmCalls[0].Extent.EndOffset)
    $nextNavigation = $tail.IndexOf("Invoke-AutomationIdButton `$Main 'CompaniesSectionButton'", [StringComparison]::Ordinal)
    if ($nextNavigation -lt 0) { throw 'Ready smoke status transition is not followed by the archive flow' }
    $statusPath = $tail.Substring(0, $nextNavigation)
    if ($statusPath -match 'Start-Sleep') { throw 'sleep-based workaround was added after status confirmation' }
    if ($statusPath -notmatch 'Wait-BusinessProjectStatusReady') { throw 'status timeout path does not use the diagnostic stable wait helper' }
}
Assert 'desktop Ready smoke stabilizes BusinessProjects re-entry after the company archive guard' {
    $smokePath = Join-Path $RepoRoot 'eng/smoke-test-desktop.ps1'
    $tokens = $null; $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($smokePath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "desktop smoke has parser errors: $($parseErrors.Message -join '; ')" }
    $crud = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-CompaniesCrudSmoke' }, $true))[0]
    if ($null -eq $crud) { throw 'desktop smoke is missing Invoke-CompaniesCrudSmoke' }
    $source = $crud.Extent.Text
    $navigation = "Invoke-AutomationIdButton `$Main 'BusinessProjectsSectionButton'"
    $navigationOffsets = @(); $offset = 0
    while (($offset = $source.IndexOf($navigation, $offset, [StringComparison]::Ordinal)) -ge 0) { $navigationOffsets += $offset; $offset += $navigation.Length }
    if ($navigationOffsets.Count -ne 2) { throw "expected exactly two BusinessProjects navigation calls, found $($navigationOffsets.Count)" }
    $reentry = $source.Substring($navigationOffsets[1])
    $archive = $reentry.IndexOf("Invoke-AutomationIdButton `$Main 'ArchiveBusinessProjectButton'", [StringComparison]::Ordinal)
    if ($archive -lt 0) { throw 'project archive flow is missing after BusinessProjects re-entry' }
    $beforeArchive = $reentry.Substring(0, $archive)
    if ($beforeArchive -notmatch "Wait-BusinessProjectStatusReady\s+\`$Main\s+'BusinessOS Gym Smoke Updated'\s+'Analysis'") { throw 're-entry does not stably require the expected Analysis project' }
    if ($beforeArchive -notmatch 'Select-ContainingListItem\s+\$projectState\.ListItem') { throw 're-entry does not select the safely validated ListItem' }
    if ($beforeArchive -match 'Start-Sleep|IsOffscreen|BoundingRectangle') { throw 're-entry readiness depends on sleep or viewport geometry' }
    if ($beforeArchive -match "\(Get-NamedElements[^\r\n;]*'BusinessOS Gym Smoke Updated'\)\[0\]") { throw 're-entry performs unsafe raw indexing before project selection' }
    $helper = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Wait-BusinessProjectStatusReady' }, $true))[0]
    if ($null -eq $helper -or $helper.Extent.Text -notmatch 'RequiredConsecutiveSuccesses\s+3' -or $helper.Extent.Text -notmatch 'Write-BusinessProjectStatusTimeoutDiagnostics' -or $helper.Extent.Text -notmatch 'Write-SmokeDiagnosticsToHost') { throw 'stable project wait does not provide consecutive semantic readiness and CI diagnostics' }
}
Assert 'recovery smoke selects the exact fixture backup through semantic UIA identity' {
    $smokePath = Join-Path $RepoRoot 'eng/smoke-test-desktop.ps1'
    $tokens = $null; $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($smokePath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "desktop smoke has parser errors: $($parseErrors.Message -join '; ')" }
    $selector = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Select-RecoveryBackupItem' }, $true))
    if ($selector.Count -ne 1) { throw 'semantic recovery selector must be defined exactly once' }
    $selectorSource = $selector[0].Extent.Text
    foreach ($required in '$ExpectedBackupId','$ExpectedInvalidBackupId','RecoveryBackupList','ListItem','EndsWith','MatchesExpected','IsValid','IsInvalid','Expected backup match count','SelectionItemPattern','Write-SmokeDiagnosticsToHost','Scenario:','Origin:','Expected fixture BackupId:','Expected invalid BackupId:','Total count:','Valid count:','Invalid count:','Name=','AutomationId=','HelpText=','ControlType=','IsEnabled=','Selected expected backup identity:') {
        if (-not $selectorSource.Contains($required, [StringComparison]::Ordinal)) { throw "semantic recovery selector omits contract: $required" }
    }
    if ($selectorSource -notmatch '\$expected\.Count\s+-ne\s+1' -or $selectorSource -notmatch '-not\s+\$expected\[0\]\.IsValid') { throw 'selector does not require one valid/restorable expected backup' }
    if ($selectorSource -match '\$valid\[0\]|IsOffscreen|BoundingRectangle|Start-Sleep') { throw 'selector uses order, viewport geometry, or sleep instead of semantic identity' }
    $recovery = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-RecoverySmoke' }, $true))[0].Extent.Text
    $calls = [regex]::Matches($recovery, 'Select-RecoveryBackupItem\s+\$recovery\s+\$fixture\.BackupId\s+\$fixture\.InvalidBackupId\s+\$Origin')
    if ($calls.Count -ne 3) { throw "expected fixture identity in initial and both origin-specific second catalog paths; found $($calls.Count) calls" }
    if ($recovery -match 'ExpectedValidBackupCount|ExpectedInvalidBackupCount|Select-ValidRecoveryItem|\$valid\[0\]') { throw 'recovery flow retains brittle count/order selection' }
    if ($recovery -match 'IsOffscreen|BoundingRectangle|Start-Sleep') { throw 'recovery flow added a viewport or sleep workaround' }
    foreach ($required in "CompanyDisplayName -ne 'Selected Backup Company'","QuickCheck -ne 'ok'",'validate-restored') { if (-not $recovery.Contains($required, [StringComparison]::Ordinal)) { throw "post-restore validation omits: $required" } }
    $viewModel = Get-Content -LiteralPath (Join-Path $RepoRoot 'src/BusinessOS.Desktop/DatabaseRecoveryWindow.xaml.cs') -Raw
    if ($viewModel -notmatch 'AutomationName.*backup\.BackupId' -or $viewModel -notmatch 'prawidłowa.*nieprawidłowa') { throw 'recovery item UIA name does not expose readable status and exact BackupId' }
}
Assert 'recovery shutdown preserves UI context and precise internal transition' {
    $gate = Get-Content -LiteralPath (Join-Path $RepoRoot 'src/BusinessOS.AppHost/DeferredShutdownGate.cs') -Raw
    $window = Get-Content -LiteralPath (Join-Path $RepoRoot 'src/BusinessOS.Desktop/DatabaseRecoveryWindow.xaml.cs') -Raw
    $app = Get-Content -LiteralPath (Join-Path $RepoRoot 'src/BusinessOS.Desktop/App.xaml.cs') -Raw
    if ($gate.Contains('Func<Task> shutdown', [StringComparison]::Ordinal) -or $gate.Contains('await shutdown()', [StringComparison]::Ordinal)) { throw 'DeferredShutdownGate still invokes a UI shutdown callback' }
    if (-not $window.Contains('await shutdownGate.WaitForSafeShutdownAsync', [StringComparison]::Ordinal) -or -not $window.Contains('ConfigureAwait(true)', [StringComparison]::Ordinal)) { throw 'recovery does not resume safe shutdown on its captured UI context' }
    if ($window.IndexOf('if (closeIntent.IsCloseRequested) return;', [StringComparison]::Ordinal) -lt $window.IndexOf('await prepareAfterRestore()', [StringComparison]::Ordinal)) { throw 'post-restore transition is not guarded by close intent after startup completes' }
    if (-not $app.Contains('!recoveryWindow.AuthorizeInternalClose()', [StringComparison]::Ordinal)) { throw 'App does not reject internal transition after an external close request' }
    if (-not $window.Contains('closeTask = ObserveCloseTaskAsync()', [StringComparison]::Ordinal) -or -not $window.Contains('Recovery shutdown callback failed', [StringComparison]::Ordinal)) { throw 'fire-and-forget close task is not observed and logged' }
}
Assert 'close intent wins over post-restore transition in deterministic tests' {
    $tests = Get-Content -LiteralPath (Join-Path $RepoRoot 'tests/BusinessOS.IntegrationTests/DeferredShutdownGateTests.cs') -Raw
    foreach ($name in 'Close_requested_during_post_restore_startup_wins_over_internal_transition','Successful_post_restore_startup_transitions_without_application_shutdown','Idle_external_close_invokes_shutdown_once','Repeated_system_and_button_close_cancel_active_operation_exactly_once') {
        if (-not $tests.Contains($name, [StringComparison]::Ordinal)) { throw "missing close-intent lifecycle test: $name" }
    }
    if ($tests.Contains('var idleGate', [StringComparison]::Ordinal) -or $tests.Contains('internalCloseAuthorized = true', [StringComparison]::Ordinal)) { throw 'lifecycle test bypasses the original close request or manually substitutes transition authorization' }
}
Assert 'recovery restore exposes cancellation before tracking and cancels exactly once' {
    $window = Get-Content -LiteralPath (Join-Path $RepoRoot 'src/BusinessOS.Desktop/DatabaseRecoveryWindow.xaml.cs') -Raw
    $restore = $window.Substring($window.IndexOf('private async void Restore_Click', [StringComparison]::Ordinal), $window.IndexOf('private async Task RestoreCoreAsync', [StringComparison]::Ordinal) - $window.IndexOf('private async void Restore_Click', [StringComparison]::Ordinal))
    $source = $restore.IndexOf('using var source = new CancellationTokenSource()', [StringComparison]::Ordinal)
    $publish = $restore.IndexOf('operation = source', [StringComparison]::Ordinal)
    $create = $restore.IndexOf('currentOperationTask = RestoreCoreAsync(selected, source.Token, start.Task)', [StringComparison]::Ordinal)
    $track = $restore.IndexOf('shutdownGate.Track(currentOperationTask)', [StringComparison]::Ordinal)
    $release = $restore.IndexOf('start.SetResult()', [StringComparison]::Ordinal)
    if ($source -lt 0 -or $publish -lt $source -or $create -lt $publish -or $track -lt $create -or $release -lt $track) { throw 'restore cancellation source is not published before task tracking and start-gate release' }
    $core = $window.Substring($window.IndexOf('private async Task RestoreCoreAsync', [StringComparison]::Ordinal), $window.IndexOf('private async Task<bool> ConfirmRestoreAsync', [StringComparison]::Ordinal) - $window.IndexOf('private async Task RestoreCoreAsync', [StringComparison]::Ordinal))
    if ($core.Contains('new CancellationTokenSource', [StringComparison]::Ordinal)) { throw 'RestoreCoreAsync creates its cancellation source after awaiting the start gate' }
    $request = $window.Substring($window.IndexOf('private void RequestClose()', [StringComparison]::Ordinal), $window.IndexOf('private async Task ObserveCloseTaskAsync', [StringComparison]::Ordinal) - $window.IndexOf('private void RequestClose()', [StringComparison]::Ordinal))
    if ($request.Contains('.Cancel()', [StringComparison]::Ordinal)) { throw 'RequestClose directly cancels in addition to DeferredShutdownGate' }
    if (-not $window.Contains('result.CancellationException', [StringComparison]::Ordinal)) { throw 'recovery does not log cancellation callback failures' }
    $closedStart = $window.IndexOf('Closed +=', [StringComparison]::Ordinal)
    $closedEnd = $window.IndexOf('Activated +=', $closedStart, [StringComparison]::Ordinal)
    if ($closedStart -lt 0 -or $closedEnd -lt $closedStart) { throw 'recovery Closed handler could not be isolated' }
    $closedHandler = $window.Substring($closedStart, $closedEnd - $closedStart)
    foreach ($forbidden in '.Cancel(','RequestClose(','WaitForSafeShutdownAsync(') {
        if ($closedHandler.Contains($forbidden, [StringComparison]::Ordinal)) { throw "recovery Closed handler owns forbidden cancellation behavior: $forbidden" }
    }
}
Assert 'cancellation-window tests mirror the production close path' {
    $tests = Get-Content -LiteralPath (Join-Path $RepoRoot 'tests/BusinessOS.IntegrationTests/DeferredShutdownGateTests.cs') -Raw
    foreach ($name in 'Close_between_restore_tracking_and_workflow_start_cancels_the_same_restore','Repeated_system_and_button_close_cancel_active_operation_exactly_once','Cancellation_callback_failure_is_reported_but_does_not_block_shutdown','External_close_followed_by_window_closed_cancels_operation_exactly_once','Internal_transition_window_closed_does_not_cancel_operation') {
        if (-not $tests.Contains($name, [StringComparison]::Ordinal)) { throw "missing cancellation-window test: $name" }
    }
    foreach ($required in 'RequestExternalClose()','CloseWhenSafeAsync()','WaitForSafeShutdownAsync','CancelAction?.Invoke()','source.Token.Register') {
        if (-not $tests.Contains($required, [StringComparison]::Ordinal)) { throw "cancellation harness does not mirror production behavior: $required" }
    }
}
Assert 'recovery smoke fixture and cancellation stabilization are coherent' {
    $fixture = Get-Content -LiteralPath (Join-Path $RepoRoot 'tests/BusinessOS.RecoverySmokeFixture/Program.cs') -Raw
    $smoke = Get-Content -LiteralPath (Join-Path $RepoRoot 'eng/smoke-test-desktop.ps1') -Raw
    $startupBranch = $fixture.Substring($fixture.IndexOf('command == "prepare-startup-failure"', [StringComparison]::Ordinal))
    if (-not $startupBranch.Contains('InvalidBackupId', [StringComparison]::Ordinal)) { throw 'startup-failure fixture does not create and report its invalid backup identity' }
    if ($fixture.Contains('ExpectedValidBackupCount', [StringComparison]::Ordinal) -or $fixture.Contains('ExpectedInvalidBackupCount', [StringComparison]::Ordinal)) { throw 'recovery fixture still exposes brittle global catalog counts' }
    $cancel = $smoke.IndexOf("Invoke-AutomationIdButton `$recovery 'CancelRestoreButton'", [StringComparison]::Ordinal)
    $stabilized = $smoke.IndexOf("Confirmation dialog did not close cleanly after cancellation.", [StringComparison]::Ordinal)
    $back = $smoke.IndexOf("Invoke-AutomationIdButton `$recovery 'BackFromRecoveryButton'", [StringComparison]::Ordinal)
    if ($cancel -lt 0 -or $stabilized -lt $cancel -or $back -lt $stabilized) { throw 'smoke does not stabilize dialog cancellation before back navigation' }
}
Assert 'solution remains minimal and excludes engineering recovery fixture' {
    $solution = Get-Content -LiteralPath (Join-Path $RepoRoot 'BusinessOS.sln') -Raw
    if ($solution.Contains('BusinessOS.RecoverySmokeFixture', [StringComparison]::Ordinal) -or $solution.Contains('|x86', [StringComparison]::OrdinalIgnoreCase) -or $solution.Contains('|x64', [StringComparison]::OrdinalIgnoreCase)) { throw 'BusinessOS.sln was expanded with engineering fixture or platform matrix' }
}
Assert 'Wait-BusinessOSCondition requires consecutive successes' {
    $state=[pscustomobject]@{ Calls=0; Values=@($false,$true,$true,$false,$true,$true,$true) }
    Wait-BusinessOSCondition -TimeoutMessage 'sequence timed out' -TimeoutSeconds 1 -PollingMilliseconds 1 -RequiredConsecutiveSuccesses 3 -Condition {
        $value=$state.Values[$state.Calls]
        $state.Calls++
        return $value
    }
    if($state.Calls -ne 7){throw "expected 7 condition calls, got $($state.Calls)"}
}
Assert 'Wait-BusinessOSCondition resets consecutive successes after false' {
    $state=[pscustomobject]@{ Calls=0; Values=@($true,$true,$false,$true,$true,$true) }
    Wait-BusinessOSCondition -TimeoutMessage 'reset sequence timed out' -TimeoutSeconds 1 -PollingMilliseconds 1 -RequiredConsecutiveSuccesses 3 -Condition {
        $value=$state.Values[$state.Calls]
        $state.Calls++
        return $value
    }
    if($state.Calls -ne 6){throw "false did not reset the counter; expected 6 calls, got $($state.Calls)"}
}
Assert 'Wait-BusinessOSCondition defaults to one success' {
    $state=[pscustomobject]@{ Calls=0 }
    Wait-BusinessOSCondition -TimeoutMessage 'single success timed out' -TimeoutSeconds 1 -PollingMilliseconds 1 -Condition { $state.Calls++; return $true }
    if($state.Calls -ne 1){throw "expected one condition call, got $($state.Calls)"}
}
Assert 'Wait-BusinessOSCondition reports timeout message' {
    $message='controlled polling timeout'
    try {
        Wait-BusinessOSCondition -TimeoutMessage $message -TimeoutSeconds 1 -PollingMilliseconds 1 -Condition { return $false }
        throw 'expected polling timeout'
    } catch {
        if($_.Exception.Message -notlike "*$message*"){throw "timeout message was not preserved: $($_.Exception.Message)"}
    }
}
function Invoke-ProcessForTest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$File,

        [string[]]$ArgumentList = @(),

        [Parameter(Mandatory)]
        [string]$WorkingDirectory
    )

    $resolvedWorkingDirectory =
        (Resolve-Path -LiteralPath $WorkingDirectory -ErrorAction Stop).Path

    $process = $null

    try {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $File
        $startInfo.WorkingDirectory = $resolvedWorkingDirectory
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.CreateNoWindow = $true

        foreach ($argument in $ArgumentList) {
            [void]$startInfo.ArgumentList.Add([string]$argument)
        }

        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo

        if (-not $process.Start()) {
            throw "Failed to start process: $File"
        }

        $process.StandardInput.Close()

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        $process.WaitForExit()

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        [pscustomobject]@{
            ExitCode         = $process.ExitCode
            Output           = $stdout
            Error            = $stderr
            Combined         = @($stdout, $stderr) -join [Environment]::NewLine
            FileName         = $File
            ArgumentList     = @($ArgumentList)
            WorkingDirectory = $resolvedWorkingDirectory
        }
    }
    finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
    }
}
function Invoke-ExpectSuccess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$File,

        [string[]]$ArgumentList = @(),

        [Parameter(Mandatory)]
        [string]$WorkingDirectory
    )

    $result = Invoke-ProcessForTest `
        -File $File `
        -ArgumentList $ArgumentList `
        -WorkingDirectory $WorkingDirectory

    if ($result.ExitCode -ne 0) {
        throw "Expected success, got $($result.ExitCode): $($result.Combined)"
    }

    return $result
}

function Get-DoctorFailureRecords {
  param([Parameter(Mandatory)]$RunResult)
  $prefix = 'BUSINESSOS_DOCTOR_FAILURE_JSON='
  @(
    foreach ($line in @($RunResult.Combined -split '\r?\n')) {
      if ($line.StartsWith($prefix, [StringComparison]::Ordinal)) {
        $json = $line.Substring($prefix.Length)
        try { $json | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "Invalid doctor failure JSON: $line" }
      }
    }
  )
}
function Assert-DoctorFailureRecord {
  param(
    [Parameter(Mandatory)]$RunResult,
    [Parameter(Mandatory)][string]$Component,
    [Parameter(Mandatory)][string]$Required,
    [Parameter(Mandatory)]$Detected
  )
  $records = @(Get-DoctorFailureRecords -RunResult $RunResult)
  $match = @($records | Where-Object { $_.component -eq $Component -and $_.required -eq $Required -and $_.status -eq 'FAIL' })
  if ($match.Count -ne 1) { throw "Expected exactly one doctor failure record for '$Component'." }
  if ($Detected -is [bool]) {
    if ([bool]$match[0].detected -ne $Detected) { throw "Unexpected doctor detected value for '$Component': $($match[0].detected)" }
  }
  elseif ($match[0].detected -ne $Detected) { throw "Unexpected doctor detected value for '$Component': $($match[0].detected)" }
}

function Invoke-ExpectFailure {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$File,

        [string[]]$ArgumentList = @(),

        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        [string]$Contains
    )

    $result = Invoke-ProcessForTest `
        -File $File `
        -ArgumentList $ArgumentList `
        -WorkingDirectory $WorkingDirectory

    if ($result.ExitCode -eq 0) {
        throw "Expected failure, got success: $($result.Combined)"
    }
    if ($Contains -and $result.Combined -notmatch [regex]::Escape($Contains)) {
        throw "Failure did not contain '$Contains': $($result.Combined)"
    }

    return $result
}
function Copy-FixtureRepo([string]$BaseName = ("BusinessOS fixture " + [Guid]::NewGuid())){ $d=Join-Path ([IO.Path]::GetTempPath()) $BaseName; foreach($dir in 'eng','tests/environment','src/BusinessOS.Desktop','src/BusinessOS.AppHost','src/BuildingBlocks/BusinessOS.BuildingBlocks.Domain','src/BuildingBlocks/BusinessOS.BuildingBlocks.Application','tests/BusinessOS.UnitTests','tests/BusinessOS.ArchitectureTests','src/Modules/Companies/BusinessOS.Modules.Companies.Infrastructure','tests/BusinessOS.IntegrationTests','tests/BusinessOS.MigrationTests'){New-Item -ItemType Directory -Force (Join-Path $d $dir)|Out-Null}; foreach($f in 'environment.lock.json','environment.bootstrap.env','BusinessOS.Engineering.psm1','BusinessOS.Provisioning.psm1','doctor.ps1','setup-environment.ps1','setup-environment.sh','activate-environment.ps1','activate-environment.sh','verify-cross-platform.ps1','verify-windows.ps1','smoke-test-desktop.ps1'){Copy-Item -LiteralPath (Join-Path $RepoRoot "eng/$f") -Destination (Join-Path $d "eng/$f")}; Copy-Item -LiteralPath (Join-Path $RepoRoot 'tests/environment/Environment.Tests.ps1') -Destination (Join-Path $d 'tests/environment/Environment.Tests.ps1'); Copy-Item -LiteralPath (Join-Path $RepoRoot 'global.json') -Destination (Join-Path $d 'global.json'); Copy-Item -LiteralPath (Join-Path $RepoRoot 'BusinessOS.CrossPlatform.slnf') -Destination (Join-Path $d 'BusinessOS.CrossPlatform.slnf'); 'Microsoft Visual Studio Solution File, Format Version 12.00','Project("{x}") = "BusinessOS.Desktop", "src\BusinessOS.Desktop\BusinessOS.Desktop.csproj", "{y}"'|Set-Content -LiteralPath (Join-Path $d 'BusinessOS.sln'); foreach($p in 'src/BusinessOS.Desktop/BusinessOS.Desktop.csproj','src/BusinessOS.AppHost/BusinessOS.AppHost.csproj','src/BuildingBlocks/BusinessOS.BuildingBlocks.Domain/BusinessOS.BuildingBlocks.Domain.csproj','src/BuildingBlocks/BusinessOS.BuildingBlocks.Application/BusinessOS.BuildingBlocks.Application.csproj','tests/BusinessOS.UnitTests/BusinessOS.UnitTests.csproj','tests/BusinessOS.ArchitectureTests/BusinessOS.ArchitectureTests.csproj','src/Modules/Companies/BusinessOS.Modules.Companies.Infrastructure/BusinessOS.Modules.Companies.Infrastructure.csproj','tests/BusinessOS.IntegrationTests/BusinessOS.IntegrationTests.csproj','tests/BusinessOS.MigrationTests/BusinessOS.MigrationTests.csproj'){'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.WindowsAppSDK" Condition="false" /></ItemGroup></Project>'|Set-Content -LiteralPath (Join-Path $d $p)}; git -C $d init *> $null; git -C $d add . *> $null; $d }
function New-FileWithHash([string]$Text){$f=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()); $Text|Set-Content -NoNewline -LiteralPath $f; $f}
function Read-Resolved($Dir){ Get-Content -LiteralPath (Join-Path $Dir '.cache/environment.resolved.json') -Raw | ConvertFrom-Json }

function Assert-ResolvedFileExists([string]$Path,$RunResult,[string]$SetupMessage){
  if(Test-Path -LiteralPath $Path){return}
  $cacheDir=Split-Path -Parent $Path
  $cacheListing='(cache directory missing)'
  if(Test-Path -LiteralPath $cacheDir){$cacheListing=(Get-ChildItem -LiteralPath $cacheDir -Force | ForEach-Object { $_.FullName }) -join "`n"}
  throw "Expected resolved file missing: $Path`nSTDOUT:`n$($RunResult.Output)`nSTDERR:`n$($RunResult.Error)`nSETUP MESSAGE:`n$SetupMessage`nCACHE CONTENTS:`n$cacheListing"
}
function Assert-ExpectedFixtureResolvedState($Resolved,[string]$Dir){
  $expectedNuget=Join-Path $Dir '.cache/nuget'
  $expectedDotnetHome=Join-Path $Dir '.cache/dotnet-home'
  $expectedPsModule=Join-Path $Dir '.tools/powershell-modules'
  if($Resolved.nugetPackages -ne $expectedNuget){throw "PowerShell setup did not resolve expected fixture NuGet cache: $($Resolved.nugetPackages)"}
  if($Resolved.dotnetCliHome -ne $expectedDotnetHome){throw "PowerShell setup did not resolve expected fixture dotnet-home: $($Resolved.dotnetCliHome)"}
  if($Resolved.powershellModuleRoot -ne $expectedPsModule){throw "PowerShell setup did not resolve expected fixture PSModule path: $($Resolved.powershellModuleRoot)"}
  foreach($value in $Resolved.nugetPackages,$Resolved.dotnetCliHome,$Resolved.powershellModuleRoot){
    if(-not $value.StartsWith($Dir,[StringComparison]::Ordinal)){throw "resolved path escaped current fixture: $value"}
    if($value.StartsWith($RepoRoot,[StringComparison]::Ordinal)){throw "resolved path points to canonical repository: $value"}
  }
}

function Compare-Resolved($A,$B){ foreach($p in 'dotnetExecutable','dotnetRoot','dotnetSource','powershellExecutable','powershellRoot','powershellSource','nugetPackages','dotnetCliHome','powershellModuleRoot'){ if($A.$p -ne $B.$p){throw "resolved state changed: $p"} } }
function HostToolsMatch($Lock){ if(-not(Get-Command dotnet -ErrorAction SilentlyContinue) -or -not(Get-Command pwsh -ErrorAction SilentlyContinue)){return $false}; ((& dotnet --version) -eq $Lock.dotnetSdk) -and ((& pwsh -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion.ToString()') -eq $Lock.powershell.version) }
function New-ZipArchive($Path,[string]$EntryName){ Add-Type -AssemblyName System.IO.Compression.FileSystem; $dir=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()); New-Item -ItemType Directory -Force $dir|Out-Null; if($EntryName){'x'|Set-Content -NoNewline -LiteralPath (Join-Path $dir $EntryName)}; [IO.Compression.ZipFile]::CreateFromDirectory($dir,$Path) }
function New-TarGzArchive($Path,[string]$EntryName){ $dir=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()); New-Item -ItemType Directory -Force $dir|Out-Null; if($EntryName){'x'|Set-Content -NoNewline -LiteralPath (Join-Path $dir $EntryName)}; Invoke-ExpectSuccess -File 'tar' -ArgumentList @('-czf',$Path,'-C',$dir,'.') -WorkingDirectory $RepoRoot|Out-Null }
Assert 'manifest, bootstrap, Dockerfile, and global.json are coherent' { $b=Read-BusinessOSBootstrapLock; $m=Read-BusinessOSEnvironmentLock; $g=Get-Content (Join-Path $RepoRoot 'global.json') -Raw|ConvertFrom-Json; $docker=Get-Content (Join-Path $RepoRoot '.devcontainer/Dockerfile') -Raw; $expectedDotnet=@{'linux-x64'='F78DBAC30C9AF2230D67FF5C224DE3A5DBF63F8A78D1C206594DEDB80E6909D2CC8A9D865D5105C72C2FD2AA266FC0C6C77DEDAC60408CBCCF272B116BD11B07';'osx-x64'='595C3C661A705A256F52E03E3AEEB86753AD6F9AA3D59F487304CDBBB744A39F4E3FA6445A60CDED6BC78E12F51D52ED5A183EA70A0560B96BED61FB83958F81';'win-x64'='24B033418A3969EFFD49B4651EF7EBBFFEB284773B99545D78DCE61A82E57F38DB7FACDB013C609BA15573C072F0E093363AE470824A6847F3C6111078C1FB64'}; if($b.DOTNET_SDK_VERSION -ne $m.dotnetSdk -or $m.dotnetSdk -ne $g.sdk.version){throw 'dotnet version mismatch'}; foreach($rid in 'linux-x64','osx-x64','win-x64'){ $envRid=$rid.ToUpperInvariant().Replace('-','_'); $urlProp="DOTNET_${envRid}_URL"; $shaProp="DOTNET_${envRid}_SHA512"; if($b.$urlProp -ne $m.dotnet.archives.$rid.url){throw "dotnet url mismatch $rid"}; if($b.$shaProp -ne $m.dotnet.archives.$rid.sha512){throw "dotnet sha mismatch $rid"}; if($m.dotnet.archives.$rid.sha512 -ne $expectedDotnet[$rid]){throw "unexpected official dotnet sha $rid"}; if($m.dotnet.archives.$rid.sha512 -match '^(.)\1+$'){throw "placeholder dotnet hash $rid"} }; if($b.POWERSHELL_LINUX_X64_URL -ne $m.powershell.archives.'linux-x64'.urls[0] -or $b.POWERSHELL_LINUX_X64_SHA256 -ne $m.powershell.archives.'linux-x64'.sha256){throw 'PowerShell linux mismatch'}; if($docker -notmatch [regex]::Escape($m.dotnetSdk) -or $docker -notmatch [regex]::Escape($m.powershell.version) -or $docker -notmatch [regex]::Escape($m.powershell.archives.'linux-x64'.sha256)){throw 'Dockerfile mismatch'}; if($b.DOTNET_ROOT_REL -ne $m.dotnetRoot -or $b.POWERSHELL_ROOT_REL -ne $m.powershellRoot -or $b.NUGET_CACHE_REL -ne $m.nugetCache -or $b.DOTNET_HOME_REL -ne $m.dotnetHome){throw 'tool/cache root mismatch'} }
Assert 'Invoke-ProcessForTest forwards every explicit argument' {
  $marker = "argument space apostrof' [1] nawiasy() znak`$dolara"
  $probeDirectory = Join-Path ([IO.Path]::GetTempPath()) ("argument probe space apostrof' [1] nawiasy() znak`$dolara " + [Guid]::NewGuid())
  $probePath = Join-Path $probeDirectory 'argument-forwarding-probe.ps1'
  try {
    New-Item -ItemType Directory -Force -Path $probeDirectory | Out-Null
    @'
param(
    [Parameter(Mandatory)]
    [string]$Marker
)

[Console]::Out.WriteLine($Marker)
'@ | Set-Content -LiteralPath $probePath

    $result = Invoke-ProcessForTest `
        -File 'pwsh' `
        -ArgumentList @(
            '-NoLogo'
            '-NoProfile'
            '-NonInteractive'
            '-File'
            $probePath
            '-Marker'
            $marker
        ) `
        -WorkingDirectory $RepoRoot

    if($result.ExitCode -ne 0){throw "argument forwarding process failed: $($result.Combined)"}
    if($result.Output.Trim() -ne $marker){throw "stdout did not contain exactly marker: $($result.Output)"}
    if($result.ArgumentList.Count -ne 7){throw "unexpected argument count: $($result.ArgumentList.Count)"}
    foreach($expected in '-NoProfile','-NonInteractive','-File',$probePath,'-Marker',$marker){if($result.ArgumentList -notcontains $expected){throw "missing forwarded argument: $expected"}}
    if($result.Output -match [regex]::Escape("PS $($result.WorkingDirectory)>")){throw 'stdout contained interactive prompt'}
  }
  finally {
    Remove-Item -LiteralPath $probeDirectory -Recurse -Force -ErrorAction SilentlyContinue
  }
}
Assert 'Invoke-ProcessForTest accepts empty arguments for noninteractive process' { $result = Invoke-ProcessForTest -File 'dotnet' -ArgumentList @() -WorkingDirectory $RepoRoot; if($result.ArgumentList.Count -ne 0){throw 'empty argument list was not preserved'} }
Assert 'dotnet SDK compatibility follows latestFeature policy' {
  $cases=@(
    @{Detected='10.0.100';Required='10.0.100';RollForward=$null;Expected=$true},
    @{Detected='10.0.110';Required='10.0.100';RollForward='latestFeature';Expected=$true},
    @{Detected='10.0.099';Required='10.0.100';RollForward='latestFeature';Expected=$false},
    @{Detected='9.0.999';Required='10.0.100';RollForward='latestFeature';Expected=$false},
    @{Detected='11.0.100';Required='10.0.100';RollForward='latestFeature';Expected=$false},
    @{Detected='10.0.110';Required='10.0.100';RollForward=$null;Expected=$false},
    @{Detected='10.0.110-preview.1';Required='10.0.100';RollForward='latestFeature';Expected=$false},
    @{Detected='not-a-version';Required='10.0.100';RollForward='latestFeature';Expected=$false}
  )
  foreach($case in $cases){
    $actual=Test-BusinessOSDotnetSdkCompatibility -Detected $case.Detected -Required $case.Required -RollForward $case.RollForward
    if($actual -ne $case.Expected){throw "SDK compatibility mismatch for detected=$($case.Detected), required=$($case.Required), rollForward=$($case.RollForward): expected $($case.Expected), got $actual"}
  }
}
Assert 'doctor accepts a complete valid fixture' { $d=Copy-FixtureRepo; $doctorResult=Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/doctor.ps1'),'-Mode','CrossPlatform','-SkipEnvironmentTests') -WorkingDirectory $d; if($doctorResult.Combined -notmatch [regex]::Escape('Environment ready: YES')){throw 'doctor output did not confirm ready environment'}; if($doctorResult.Combined -notmatch [regex]::Escape('BusinessOS.sln')){throw 'doctor output did not mention BusinessOS.sln'}; if(@(Get-DoctorFailureRecords -RunResult $doctorResult).Count -ne 0){throw 'valid doctor emitted failure records'} }
Assert 'PowerShell activation is idempotent' {
  $harnessBefore=[pscustomobject]@{RepoRoot=$RepoRoot;Path=$env:PATH;PSModulePath=$env:PSModulePath;DOTNET_ROOT=$env:DOTNET_ROOT;NUGET_PACKAGES=$env:NUGET_PACKAGES;DOTNET_CLI_HOME=$env:DOTNET_CLI_HOME;Location=(Get-Location).Path}
  $d=Copy-FixtureRepo
  New-Item -ItemType Directory -Force (Join-Path $d '.cache')|Out-Null
  $resolved=[pscustomobject]@{dotnetExecutable=(Join-Path $d '.tools/dotnet/dotnet');dotnetRoot=(Join-Path $d '.tools/dotnet');dotnetSource='local';powershellExecutable=(Join-Path $d '.tools/powershell/pwsh');powershellRoot=(Join-Path $d '.tools/powershell');powershellSource='local';nugetPackages=(Join-Path $d '.cache/nuget');dotnetCliHome=(Join-Path $d '.cache/dotnet-home');powershellModuleRoot=(Join-Path $d '.tools/powershell-modules')}
  $resolved|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $d '.cache/environment.resolved.json')
  $probePath=Join-Path $d 'tests/environment/activation-idempotency-probe.ps1'
  @'
param([Parameter(Mandatory=$true)][string]$ActivationPath)
$ErrorActionPreference='Stop'
$beforePath=$env:PATH
$beforeModulePath=$env:PSModulePath
. $ActivationPath
$first=[pscustomobject]@{Path=$env:PATH;PSModulePath=$env:PSModulePath;DOTNET_ROOT=$env:DOTNET_ROOT;NUGET_PACKAGES=$env:NUGET_PACKAGES;DOTNET_CLI_HOME=$env:DOTNET_CLI_HOME}
. $ActivationPath
$second=[pscustomobject]@{Path=$env:PATH;PSModulePath=$env:PSModulePath;DOTNET_ROOT=$env:DOTNET_ROOT;NUGET_PACKAGES=$env:NUGET_PACKAGES;DOTNET_CLI_HOME=$env:DOTNET_CLI_HOME}
if($second.Path -ne $first.Path){throw 'activation duplicated PATH entries'}
if($second.PSModulePath -ne $first.PSModulePath){throw 'activation duplicated PSModulePath entries'}
foreach($p in 'DOTNET_ROOT','NUGET_PACKAGES','DOTNET_CLI_HOME'){
  if($second.$p -ne $first.$p){throw "activation changed resolved root: $p"}
}
if([string]::IsNullOrEmpty($beforePath) -and [string]::IsNullOrEmpty($first.Path)){throw 'PATH was not captured'}
if([string]::IsNullOrEmpty($beforeModulePath) -and [string]::IsNullOrEmpty($first.PSModulePath)){throw 'PSModulePath was not captured'}
Write-Output 'ACTIVATION_IDEMPOTENCY_PROBE_OK'
'@ | Set-Content -LiteralPath $probePath
  $probeResult=Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',$probePath,(Join-Path $d 'eng/activate-environment.ps1')) -WorkingDirectory $d; if($probeResult.Output -notmatch [regex]::Escape('ACTIVATION_IDEMPOTENCY_PROBE_OK')){throw 'activation probe marker missing'}
  $harnessAfter=[pscustomobject]@{RepoRoot=$RepoRoot;Path=$env:PATH;PSModulePath=$env:PSModulePath;DOTNET_ROOT=$env:DOTNET_ROOT;NUGET_PACKAGES=$env:NUGET_PACKAGES;DOTNET_CLI_HOME=$env:DOTNET_CLI_HOME;Location=(Get-Location).Path}
  foreach($p in 'RepoRoot','Path','PSModulePath','DOTNET_ROOT','NUGET_PACKAGES','DOTNET_CLI_HOME','Location'){
    if($harnessAfter.$p -ne $harnessBefore.$p){throw "activation probe mutated test harness: $p"}
  }
}
Assert 'PowerShell activation probe does not mutate test harness' { if($RepoRoot -ne (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path){throw 'canonical repo root changed'} }
Assert 'PowerShell setup is idempotent with matching host tools' {
  $lock=Read-BusinessOSEnvironmentLock
  if(-not(HostToolsMatch $lock)){Write-Host 'SKIPPED host tools do not match manifest'; return}
  $d=Copy-FixtureRepo
  $setupPath=Join-Path $d 'eng/setup-environment.ps1'
  $jsonPath=Join-Path $d '.cache/environment.resolved.json'
  $envPath=Join-Path $d '.cache/environment.resolved.env'
  $run1=Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',$setupPath) -WorkingDirectory $d
  Assert-ResolvedFileExists $jsonPath $run1 'first PowerShell setup run'
  Assert-ResolvedFileExists $envPath $run1 'first PowerShell setup run'
  if($run1.Output -notmatch [regex]::Escape($jsonPath)){throw "setup stdout did not contain expected resolved path $jsonPath. STDOUT:`n$($run1.Output)"}; if($run1.Output -match '(?s)^PowerShell .+PS .+>'){throw "setup stdout looked like an interactive PowerShell session without setup output. STDOUT:`n$($run1.Output)"}; if($run1.Output -notmatch [regex]::Escape('BusinessOS environment setup completed.')){throw "setup stdout did not contain completion message. STDOUT:`n$($run1.Output)"}
  $r1=Read-Resolved $d
  Assert-ExpectedFixtureResolvedState $r1 $d
  $run2=Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',$setupPath) -WorkingDirectory $d
  Assert-ResolvedFileExists $jsonPath $run2 'second PowerShell setup run'
  Assert-ResolvedFileExists $envPath $run2 'second PowerShell setup run'
  $r2=Read-Resolved $d
  Assert-ExpectedFixtureResolvedState $r2 $d
  Compare-Resolved $r1 $r2
}
Assert 'Bash setup is idempotent with matching host tools and special path' { if($IsWindows -or -not(Get-Command bash -ErrorAction SilentlyContinue)){Write-Host 'SKIPPED bash setup test on this platform'; return}; $lock=Read-BusinessOSEnvironmentLock; if(-not(HostToolsMatch $lock)){Write-Host 'SKIPPED host tools do not match manifest'; return}; $expectedDotnetExecutable=(Get-Command dotnet -ErrorAction Stop).Source; $expectedDotnetRoot=Split-Path -Parent $expectedDotnetExecutable; $expectedPwshExecutable=(Get-Command pwsh -ErrorAction Stop).Source; $expectedPwshRoot=Split-Path -Parent $expectedPwshExecutable; $d=Copy-FixtureRepo "Business OS a'b`$c\d(e) $([Guid]::NewGuid())"; Invoke-ExpectSuccess -File 'bash' -ArgumentList @('-c','./eng/setup-environment.sh') -WorkingDirectory $d|Out-Null; $r1=Read-Resolved $d; $firstEnv=Get-Content -LiteralPath (Join-Path $d '.cache/environment.resolved.env') -Raw; $firstJson=Get-Content -LiteralPath (Join-Path $d '.cache/environment.resolved.json') -Raw; Invoke-ExpectSuccess -File 'bash' -ArgumentList @('-c','./eng/setup-environment.sh') -WorkingDirectory $d|Out-Null; $r2=Read-Resolved $d; $secondEnv=Get-Content -LiteralPath (Join-Path $d '.cache/environment.resolved.env') -Raw; $secondJson=Get-Content -LiteralPath (Join-Path $d '.cache/environment.resolved.json') -Raw; if($secondEnv -ne $firstEnv){throw 'Resolved ENV changed after second Bash setup'}; Compare-Resolved $r1 $r2; if($r2.dotnetExecutable -ne $expectedDotnetExecutable){throw 'Bash setup did not resolve expected host dotnet executable'}; if($r2.dotnetRoot -ne $expectedDotnetRoot){throw 'Bash setup did not resolve expected host dotnet root'}; if($r2.powershellExecutable -ne $expectedPwshExecutable){throw 'Bash setup did not resolve expected host PowerShell executable'}; if($r2.powershellRoot -ne $expectedPwshRoot){throw 'Bash setup did not resolve expected host PowerShell root'}; $expectedNuget=Join-Path $d '.cache/nuget'; $expectedDotnetHome=Join-Path $d '.cache/dotnet-home'; $expectedPsModule=Join-Path $d '.tools/powershell-modules'; if($r2.nugetPackages -ne $expectedNuget){throw 'Bash setup did not resolve expected fixture NuGet cache'}; if($r2.dotnetCliHome -ne $expectedDotnetHome){throw 'Bash setup did not resolve expected fixture dotnet-home'}; if($r2.powershellModuleRoot -ne $expectedPsModule){throw 'Bash setup did not resolve expected fixture PSModule path'}; $envResult=Invoke-ExpectSuccess -File 'bash' -ArgumentList @('-c','set -euo pipefail; set -a; source .cache/environment.resolved.env; set +a; pwsh -NoLogo -NoProfile -NonInteractive -Command ''[ordered]@{ dotnetExecutable = $env:DOTNET_EXE; dotnetRoot = $env:DOTNET_ROOT; dotnetSource = $env:DOTNET_SOURCE; powershellExecutable = $env:PWSH_EXE; powershellRoot = $env:POWERSHELL_ROOT; powershellSource = $env:POWERSHELL_SOURCE; nugetPackages = $env:NUGET_PACKAGES; dotnetCliHome = $env:DOTNET_CLI_HOME; powershellModuleRoot = $env:PSMODULE_ROOT } | ConvertTo-Json -Compress''') -WorkingDirectory $d; if([string]::IsNullOrWhiteSpace($envResult.Output)){throw 'Bash sourced environment produced empty JSON output'}; $resolvedFromEnv=$envResult.Output | ConvertFrom-Json; Compare-Resolved $resolvedFromEnv $r2 }
Assert 'doctor rejects empty solution with concrete output' {
  $d=Copy-FixtureRepo
  'Microsoft Visual Studio Solution File'|Set-Content -LiteralPath (Join-Path $d 'BusinessOS.sln')
  $r=Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/doctor.ps1'),'-Mode','CrossPlatform','-SkipEnvironmentTests') -WorkingDirectory $d -Contains 'BusinessOS.sln'
  if($r.Combined -notmatch [regex]::Escape('Environment ready: NO')){throw 'doctor output did not reject environment'}
  Assert-DoctorFailureRecord `
    -RunResult $r `
    -Component 'BusinessOS.sln' `
    -Required 'non-empty solution' `
    -Detected '0 project entries'
}
Assert 'doctor rejects missing UnitTests with concrete output' { $d=Copy-FixtureRepo; Remove-Item (Join-Path $d 'tests/BusinessOS.UnitTests/BusinessOS.UnitTests.csproj'); $r=Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/doctor.ps1'),'-Mode','CrossPlatform','-SkipEnvironmentTests') -WorkingDirectory $d -Contains 'tests/BusinessOS.UnitTests/BusinessOS.UnitTests.csproj'; Assert-DoctorFailureRecord -RunResult $r -Component 'tests/BusinessOS.UnitTests/BusinessOS.UnitTests.csproj' -Required 'present' -Detected $false }
Assert 'doctor rejects global mismatch with concrete output' { $d=Copy-FixtureRepo; $lock=Read-BusinessOSEnvironmentLock; '{"sdk":{"version":"0.0.1"}}'|Set-Content -LiteralPath (Join-Path $d 'global.json'); $r=Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/doctor.ps1'),'-Mode','CrossPlatform','-SkipEnvironmentTests') -WorkingDirectory $d -Contains 'global.json SDK'; if($r.Combined -notmatch [regex]::Escape('Environment ready: NO')){throw 'doctor output did not reject environment'}; Assert-DoctorFailureRecord -RunResult $r -Component 'global.json SDK' -Required $lock.dotnetSdk -Detected '0.0.1' }
Assert 'doctor failure diagnostics are independent from formatted table width' {
  $d=Copy-FixtureRepo
  'Microsoft Visual Studio Solution File'|Set-Content -LiteralPath (Join-Path $d 'BusinessOS.sln')
  $r=Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/doctor.ps1'),'-Mode','CrossPlatform','-SkipEnvironmentTests') -WorkingDirectory $d
  Assert-DoctorFailureRecord `
    -RunResult $r `
    -Component 'BusinessOS.sln' `
    -Required 'non-empty solution' `
    -Detected '0 project entries'
}
Assert 'Assert-FileHash rejects placeholders and accepts valid checksum' { $f=New-FileWithHash 'abc'; $sha=(Get-FileHash $f -Algorithm SHA256).Hash; Assert-FileHash $f $sha SHA256|Out-Null; foreach($bad in '', 'abc', ('0'*64), ('1'*64), ('2'*64), ('3'*64), ('A'*64)){ $failed=$false; try{Assert-FileHash $f $bad SHA256|Out-Null}catch{$failed=$true}; if(-not $failed){throw "accepted bad checksum $bad"} } }
Assert 'Invoke-DownloadWithFallback uses second adapter after first failure' { $f=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()); $source=New-FileWithHash 'ok'; $sha=(Get-FileHash $source -Algorithm SHA256).Hash; $adapter={param($url,$out) if($url -eq 'bad'){[pscustomobject]@{StatusCode=500;Bytes=0}}else{Copy-Item $source $out;[pscustomobject]@{StatusCode=200;Bytes=(Get-Item $out).Length}}}; $r=Invoke-DownloadWithFallback @('bad','good') $f $sha SHA256 $adapter; if($r.Url -ne 'good'){throw 'fallback did not reach good source'} }
Assert 'Invoke-DownloadWithFallback rejects empty and all-failed downloads' { $f=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()); $sha='A'*64; $adapter={param($url,$out)''|Set-Content -NoNewline $out;[pscustomobject]@{StatusCode=200;Bytes=0}}; $failed=$false; try{Invoke-DownloadWithFallback @('empty') $f $sha SHA256 $adapter|Out-Null}catch{$failed=$true}; if(-not $failed){throw 'accepted empty download'} }
Assert 'Expand-VerifiedArchive accepts valid ZIP with tool.exe' { $zip=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString()+'.zip'); New-ZipArchive $zip 'tool.exe'; $dest=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()); $exe=Expand-VerifiedArchive $zip $dest zip 'tool.exe'; if(-not(Test-Path -LiteralPath $exe)){throw 'tool.exe missing after valid ZIP extraction'} }
Assert 'Expand-VerifiedArchive rejects valid ZIP without tool.exe' { $zip=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString()+'.zip'); New-ZipArchive $zip 'readme.txt'; $failed=$false; try{Expand-VerifiedArchive $zip (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid())) zip 'tool.exe'|Out-Null}catch{if($_.Exception.Message -match 'Expected executable missing'){$failed=$true}}; if(-not $failed){throw 'valid ZIP without tool.exe was not rejected'} }
Assert 'Expand-VerifiedArchive rejects corrupt and empty ZIP' { $bad=New-FileWithHash 'not zip'; $empty=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString()+'.zip'); New-Item -ItemType File -Path $empty|Out-Null; foreach($case in @(@($bad,'Invalid ZIP archive'),@($empty,'Archive is empty'))){$failed=$false; try{Expand-VerifiedArchive $case[0] (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid())) zip 'tool.exe'|Out-Null}catch{if($_.Exception.Message -match [regex]::Escape($case[1])){$failed=$true}}; if(-not $failed){throw "ZIP case was not rejected: $($case[1])"}} }
Assert 'Expand-VerifiedArchive accepts valid TAR.GZ with tool' { if(-not(Get-Command tar -ErrorAction SilentlyContinue)){Write-Host 'SKIPPED tar unavailable'; return}; $tgz=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString()+'.tar.gz'); New-TarGzArchive $tgz 'tool'; $dest=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()); $exe=Expand-VerifiedArchive $tgz $dest 'tar.gz' 'tool'; if(-not(Test-Path -LiteralPath $exe)){throw 'tool missing after valid TAR.GZ extraction'} }
Assert 'Expand-VerifiedArchive rejects valid TAR.GZ without tool' { if(-not(Get-Command tar -ErrorAction SilentlyContinue)){Write-Host 'SKIPPED tar unavailable'; return}; $tgz=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString()+'.tar.gz'); New-TarGzArchive $tgz 'readme.txt'; $failed=$false; try{Expand-VerifiedArchive $tgz (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid())) 'tar.gz' 'tool'|Out-Null}catch{if($_.Exception.Message -match 'Expected executable missing'){$failed=$true}}; if(-not $failed){throw 'valid TAR.GZ without tool was not rejected'} }
Assert 'Expand-VerifiedArchive rejects corrupt and empty TAR.GZ' { if(-not(Get-Command tar -ErrorAction SilentlyContinue)){Write-Host 'SKIPPED tar unavailable'; return}; $bad=New-FileWithHash 'not tar'; $empty=Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString()+'.tar.gz'); New-Item -ItemType File -Path $empty|Out-Null; foreach($case in @(@($bad,'Invalid TAR.GZ archive'),@($empty,'Archive is empty'))){$failed=$false; try{Expand-VerifiedArchive $case[0] (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid())) 'tar.gz' 'tool'|Out-Null}catch{if($_.Exception.Message -match [regex]::Escape($case[1])){$failed=$true}}; if(-not $failed){throw "TAR.GZ case was not rejected: $($case[1])"}} }

Assert 'Invoke-CheckedCommand captures stdout and stderr without Runspace errors' { $r=Invoke-CheckedCommand pwsh @('-NoLogo','-NoProfile','-NonInteractive','-Command',"[Console]::Out.WriteLine('stdout-marker'); [Console]::Error.WriteLine('stderr-marker'); exit 0") $RepoRoot; if($r.ExitCode -ne 0){throw 'exit code was not 0'}; if($r.StdOut -notcontains 'stdout-marker'){throw 'stdout marker missing'}; if($r.StdErr -notcontains 'stderr-marker'){throw 'stderr marker missing'} }
Assert 'Invoke-CheckedCommand handles large dual-stream output without deadlock' {
  $count=5000
  $probeDirectory = Join-Path ([IO.Path]::GetTempPath()) ("large stream probe space apostrof' [1] nawiasy() znak`$dolara " + [Guid]::NewGuid())
  $probePath = Join-Path $probeDirectory 'large-stream-probe.ps1'
  try {
    New-Item -ItemType Directory -Force -Path $probeDirectory | Out-Null
    @'
param(
    [Parameter(Mandatory)]
    [int]$Count
)

for ($i = 1; $i -le $Count; $i++) {
    [Console]::Out.WriteLine("stdout-large-$i")
    [Console]::Error.WriteLine("stderr-large-$i")
}

exit 0
'@ | Set-Content -LiteralPath $probePath

    $timer=[Diagnostics.Stopwatch]::StartNew()
    $r=Invoke-CheckedCommand pwsh @(
      '-NoLogo'
      '-NoProfile'
      '-NonInteractive'
      '-File'
      $probePath
      '-Count'
      $count.ToString([Globalization.CultureInfo]::InvariantCulture)
    ) $RepoRoot
    $timer.Stop()
    "LARGE_STREAM_SECONDS=$($timer.Elapsed.TotalSeconds)"|Tee-Object -FilePath $log -Append
    if($timer.Elapsed.TotalSeconds -gt 30){throw "large stream test exceeded 30s: $($timer.Elapsed.TotalSeconds)"}
    if($r.ExitCode -ne 0){throw 'exit code was not 0'}
    foreach($marker in 'stdout-large-1',"stdout-large-$count"){if($r.StdOut -notcontains $marker){throw "missing stdout marker $marker"}}
    foreach($marker in 'stderr-large-1',"stderr-large-$count"){if($r.StdErr -notcontains $marker){throw "missing stderr marker $marker"}}
  }
  finally {
    Remove-Item -LiteralPath $probeDirectory -Recurse -Force -ErrorAction SilentlyContinue
  }
}
Assert 'Invoke-CheckedCommand throws command and code for non-zero exit' { $failed=$false; try{Invoke-CheckedCommand pwsh @('-NoLogo','-NoProfile','-NonInteractive','-Command',"[Console]::Out.WriteLine('nonzero-stdout'); [Console]::Error.WriteLine('nonzero-stderr'); exit 23") $RepoRoot|Out-Null}catch{$failed=$true; if($_.Exception.Message -notmatch '23' -or $_.Exception.Message -notmatch 'pwsh'){throw "unexpected exception message: $($_.Exception.Message)"}}; if(-not $failed){throw 'non-zero command did not throw'} }
Assert 'Invoke-CheckedCommand invokes direct ps1 and preserves spaces' { $d=Join-Path ([IO.Path]::GetTempPath()) ('direct ps1 '+[Guid]::NewGuid()); New-Item -ItemType Directory -Force $d|Out-Null; try{ $script=Join-Path $d 'script with space.ps1'; 'param([string]$Value) [Console]::Out.WriteLine("direct-value=$Value")'|Set-Content -LiteralPath $script; $r=Invoke-CheckedCommand $script @('hello world') $d; if($r.ExitCode -ne 0){throw 'exit code was not 0'}; if($r.StdOut -notcontains 'direct-value=hello world'){throw 'direct ps1 output missing argument with spaces'} } finally { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'Invoke-CheckedCommand honors special working directory' { $d=Join-Path ([IO.Path]::GetTempPath()) ("spacje apostrof' nawiasy() znak`$dolara " + [Guid]::NewGuid()); New-Item -ItemType Directory -Force $d|Out-Null; try{ $r=Invoke-CheckedCommand pwsh @('-NoLogo','-NoProfile','-NonInteractive','-Command','[Console]::Out.WriteLine((Get-Location).Path); exit 0') $d; if($r.ExitCode -ne 0){throw 'exit code was not 0'}; if($r.WorkingDirectory -ne $d){throw "result working directory mismatch: $($r.WorkingDirectory)"}; if($r.StdOut -notcontains $d){throw 'process did not run in special working directory'} } finally { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'doctor rejects forced NuGet cache cleanup failure' {
  $d=Copy-FixtureRepo
  $old=$env:BUSINESSOS_DOCTOR_FORCE_CACHE_FAILURE
  try{
    $env:BUSINESSOS_DOCTOR_FORCE_CACHE_FAILURE='forced cache failure for test'
    $r=Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/doctor.ps1'),'-Mode','CrossPlatform','-SkipEnvironmentTests') -WorkingDirectory $d -Contains 'NuGet cache'
    Assert-DoctorFailureRecord -RunResult $r -Component 'NuGet cache' -Required 'writable' -Detected 'forced cache failure for test'
    if($r.Combined -match 'Environment ready: YES'){throw 'doctor reported ready despite failing cache'}
  }
  finally { $env:BUSINESSOS_DOCTOR_FORCE_CACHE_FAILURE=$old }
}
Assert 'Invoke-CheckedCommand handles script path and argument with spaces' { $d=Join-Path ([IO.Path]::GetTempPath()) ('space dir '+[Guid]::NewGuid()); New-Item -ItemType Directory -Force $d|Out-Null; $script=Join-Path $d 'script with space.ps1'; 'param([string]$Value) Write-Output "VALUE=$Value"'|Set-Content -LiteralPath $script; $r=Invoke-CheckedCommand $script @('hello world') $d; if(($r.StdOut -join '') -notmatch 'hello world'){throw 'argument with space was not preserved'} }
Assert 'Invoke-ProcessForTest handles large stdout and stderr concurrently' { $r=Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-Command','1..2000|%{Write-Output "out $_"; Write-Error "err $_" -ErrorAction Continue}') -WorkingDirectory $RepoRoot; if($r.Output.Length -lt 1000 -or $r.Error.Length -lt 1000){throw 'missing stream output'} }
Assert 'cross-platform filter contains required projects and excludes deferred projects' { $p=Get-BusinessOSCrossPlatformFilterProjects $RepoRoot; foreach($r in 'BusinessOS.AppHost.csproj','BusinessOS.BuildingBlocks.Domain.csproj','BusinessOS.BuildingBlocks.Application.csproj','BusinessOS.Modules.Companies.Infrastructure.csproj','BusinessOS.UnitTests.csproj','BusinessOS.ArchitectureTests.csproj','BusinessOS.IntegrationTests.csproj','BusinessOS.MigrationTests.csproj'){if(-not(($p|Split-Path -Leaf)-contains $r)){throw "missing $r"}}; if(($p -join ';') -match 'Desktop|BusinessProjects.Infrastructure|BuildingBlocks.Infrastructure'){throw 'deferred project present'} }

function New-VulnerabilityScanFixture {
  $d=Join-Path ([IO.Path]::GetTempPath()) ("vulnerability scan fixture space apostrof' [1] nawiasy() znak`$dolara "+[Guid]::NewGuid())
  New-Item -ItemType Directory -Force $d|Out-Null
  Copy-Item -LiteralPath (Join-Path $RepoRoot 'BusinessOS.CrossPlatform.slnf') -Destination (Join-Path $d 'BusinessOS.CrossPlatform.slnf')
  Copy-Item -LiteralPath (Join-Path $RepoRoot 'BusinessOS.sln') -Destination (Join-Path $d 'BusinessOS.sln')
  New-Item -ItemType Directory -Force (Join-Path $d 'eng')|Out-Null
  Copy-Item -LiteralPath (Join-Path $RepoRoot 'eng/check-vulnerable-packages.ps1') -Destination (Join-Path $d 'eng/check-vulnerable-packages.ps1')
  Copy-Item -LiteralPath (Join-Path $RepoRoot 'eng/BusinessOS.Engineering.psm1') -Destination (Join-Path $d 'eng/BusinessOS.Engineering.psm1')
  $filter=Get-Content -LiteralPath (Join-Path $d 'BusinessOS.CrossPlatform.slnf') -Raw|ConvertFrom-Json
  foreach($project in @($filter.solution.projects)+@('src/BusinessOS.Desktop/BusinessOS.Desktop.csproj','src/Infrastructure/Infrastructure.csproj')){
    $path=Join-Path $d $project
    New-Item -ItemType Directory -Force (Split-Path -Parent $path)|Out-Null
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'|Set-Content -LiteralPath $path
  }
  $d
}
function New-FakeDotnetProbe([string]$Directory,[string]$Mode='clean'){
  $path=Join-Path $Directory 'fake dotnet probe.ps1'
  @'
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$ProbeArgumentList)
$ErrorActionPreference='Stop'
$record=[pscustomobject]@{ arguments=@($ProbeArgumentList) }
$record|ConvertTo-Json -Compress -Depth 20|Add-Content -LiteralPath $env:BUSINESSOS_FAKE_DOTNET_LOG
[Console]::Error.WriteLine("fake dotnet warning on stderr")
if($env:BUSINESSOS_FAKE_DOTNET_MODE -eq 'malformed') { [Console]::Out.WriteLine('{ not json'); exit 0 }
if($env:BUSINESSOS_FAKE_DOTNET_MODE -eq 'missing-path') { [pscustomobject]@{version=1;projects=@([pscustomobject]@{})}|ConvertTo-Json -Depth 50; exit 0 }
$projectIndex=[Array]::IndexOf($ProbeArgumentList,'--project')
$project=if($projectIndex -ge 0 -and $projectIndex + 1 -lt $ProbeArgumentList.Count){$ProbeArgumentList[$projectIndex+1]}else{'missing-project'}
$package=@()
if($env:BUSINESSOS_FAKE_DOTNET_MODE -eq 'vulnerable'){
  $package=@([pscustomobject]@{id='Vulnerable.Package';resolvedVersion='1.2.3';vulnerabilities=@([pscustomobject]@{severity='High';advisoryurl='https://example.test/advisory'})})
  [pscustomobject]@{version=1;parameters='--vulnerable --include-transitive';sources=@('https://api.nuget.org/v3/index.json');projects=@([pscustomobject]@{path=$project;frameworks=@([pscustomobject]@{framework='net10.0';topLevelPackages=$package;transitivePackages=@()})})}|ConvertTo-Json -Depth 50
  exit 0
}
[pscustomobject]@{version=1;parameters='--vulnerable --include-transitive';sources=@('https://api.nuget.org/v3/index.json');projects=@([pscustomobject]@{path=$project})}|ConvertTo-Json -Depth 50
'@|Set-Content -LiteralPath $path
  $path
}
function Read-FakeDotnetInvocations([string]$Path){ if(-not(Test-Path -LiteralPath $Path)){return @()}; @(Get-Content -LiteralPath $Path|Where-Object{$_}|ForEach-Object{$_|ConvertFrom-Json}) }
function Assert-VulnerabilityInvocationSyntax($Invocation){
  $a=@($Invocation.arguments)
  $expected='package','list','--project',$a[3],'--vulnerable','--include-transitive','--format','json','--output-version','1','--no-restore'
  if($a.Count -ne $expected.Count){throw "unexpected argument count: $($a -join '|')"}
  for($i=0;$i -lt $expected.Count;$i++){if($a[$i] -ne $expected[$i]){throw "argument $i expected '$($expected[$i])' got '$($a[$i])'"}}
  $target=$a[3]
  if(@($a|Where-Object{$_ -eq $target}).Count -ne 1){throw 'target does not appear exactly once'}
  if($a[2] -ne '--project'){throw '--project does not immediately precede target'}
  if($a[0] -ne 'package' -or $a[1] -ne 'list'){throw 'missing package list prefix'}
  if($a[2] -eq $target){throw 'old positional target layout detected'}
  if(@($a|Where-Object{[string]::IsNullOrEmpty($_)}).Count -gt 0){throw 'empty argument detected'}
  if($a.Count -eq 1 -and $a[0] -match 'package\s+list'){throw 'single composed command detected'}
}
Assert 'vulnerability scanner uses .NET 10 package list syntax' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='clean'; Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.sln'),'-DotnetExecutable',$fake) -WorkingDirectory $d|Out-Null; foreach($i in Read-FakeDotnetInvocations $logPath){Assert-VulnerabilityInvocationSyntax $i} } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'vulnerability scanner expands solution filter projects' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='clean'; Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.CrossPlatform.slnf'),'-DotnetExecutable',$fake) -WorkingDirectory $d|Out-Null; $filter=Get-Content -LiteralPath (Join-Path $d 'BusinessOS.CrossPlatform.slnf') -Raw|ConvertFrom-Json; $expected=@($filter.solution.projects|ForEach-Object{(Resolve-Path -LiteralPath (Join-Path $d $_)).Path}|Select-Object -Unique); $inv=Read-FakeDotnetInvocations $logPath; if($inv.Count -ne $expected.Count){throw "unexpected invocation count $($inv.Count) expected $($expected.Count)"}; $artifact=Get-Content -LiteralPath (Join-Path $d '.cache/vulnerable-packages.json') -Raw|ConvertFrom-Json; if(@($artifact.targets).Count -ne $expected.Count){throw "unexpected target count $(@($artifact.targets).Count) expected $($expected.Count)"}; if(@($artifact.reports).Count -ne @($artifact.targets).Count){throw 'report count does not match target count'}; $firstProject=@(@($artifact.reports)[0].projects)[0]; if($null -ne $firstProject.frameworks){throw 'clean report unexpectedly contained frameworks'}; foreach($i in $inv){Assert-VulnerabilityInvocationSyntax $i; $target=@($i.arguments)[3]; if($target -notlike '*.csproj'){throw "target is not csproj: $target"}; if($target -like '*.slnf' -or $target -like '*.sln'){throw "solution file scanned: $target"}; if($target -match 'BusinessOS\.Desktop|BusinessProjects\.Infrastructure|BuildingBlocks\.Infrastructure'){throw "excluded target scanned: $target"}; if($target -like '*BusinessOS.sln'){throw "BusinessOS.sln scanned: $target"}; if(-not [IO.Path]::IsPathRooted($target)){throw "target is not absolute: $target"}; if(-not(Test-Path -LiteralPath $target)){throw "target missing: $target"}; if($expected -notcontains $target){throw "unexpected target: $target"} }; foreach($report in @($artifact.reports)){ $project=@($report.projects)[0]; if([string]::IsNullOrWhiteSpace([string]$project.path)){throw 'artifact project path missing'}; if($expected -notcontains $project.path){throw "artifact report path was not expected: $($project.path)"}; if($null -ne $project.frameworks){throw "clean artifact report unexpectedly contained frameworks: $($project.path)"} } } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'vulnerability scanner scans solution as one target' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='clean'; Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.sln'),'-DotnetExecutable',$fake) -WorkingDirectory $d|Out-Null; $inv=Read-FakeDotnetInvocations $logPath; if($inv.Count -ne 1){throw "expected one invocation"}; Assert-VulnerabilityInvocationSyntax $inv[0]; if(@($inv[0].arguments)[3] -ne (Resolve-Path -LiteralPath (Join-Path $d 'BusinessOS.sln')).Path){throw 'solution target mismatch'} } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'vulnerability scanner keeps stderr separate from JSON' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='clean'; Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.sln'),'-DotnetExecutable',$fake) -WorkingDirectory $d|Out-Null; $artifact=Get-Content -LiteralPath (Join-Path $d '.cache/vulnerable-packages.json') -Raw; if($artifact -match 'fake dotnet warning'){throw 'stderr leaked into JSON artifact'}; $artifact|ConvertFrom-Json|Out-Null } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'vulnerability scanner accepts report without frameworks' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='clean'; $r=Invoke-ExpectSuccess -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.sln'),'-DotnetExecutable',$fake) -WorkingDirectory $d; if($r.Combined -notmatch [regex]::Escape('No vulnerable NuGet packages were reported')){throw 'success output did not report clean vulnerability result'}; $artifact=Get-Content -LiteralPath (Join-Path $d '.cache/vulnerable-packages.json') -Raw|ConvertFrom-Json; if(@($artifact.reports).Count -ne 1){throw "expected one report, got $(@($artifact.reports).Count)"}; $projects=@(@($artifact.reports)[0].projects); if($projects.Count -le 0){throw 'artifact report did not contain projects'}; $project=$projects[0]; if([string]::IsNullOrWhiteSpace([string]$project.path)){throw 'artifact project path missing'}; if($null -ne $project.frameworks){throw 'clean report unexpectedly contained frameworks'}; $failurePrefix='Vulnerable NuGet packages were reported:'; if($r.Combined -match [regex]::Escape($failurePrefix)){throw 'scanner reported vulnerabilities for clean report'} } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'vulnerability scanner rejects vulnerable package report' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='vulnerable'; $r=Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.sln'),'-DotnetExecutable',$fake) -WorkingDirectory $d -Contains 'Vulnerable NuGet packages were reported: 1'; if($r.Combined -notmatch 'Vulnerable\.Package'){throw 'vulnerable package name missing'}; $artifactPath=Join-Path $d '.cache/vulnerable-packages.json'; if(-not(Test-Path -LiteralPath $artifactPath)){throw 'artifact missing'}; $artifact=Get-Content -LiteralPath $artifactPath -Raw|ConvertFrom-Json; $framework=@(@(@($artifact.reports)[0].projects)[0].frameworks)[0]; if($null -eq $framework){throw 'vulnerable report framework missing'}; $package=@($framework.topLevelPackages)[0]; if($null -eq $package.vulnerabilities -or @($package.vulnerabilities).Count -ne 1){throw 'vulnerable package vulnerabilities missing'} } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'vulnerability scanner rejects malformed JSON' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='malformed'; Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.sln'),'-DotnetExecutable',$fake) -WorkingDirectory $d -Contains 'Vulnerability report is not valid JSON'|Out-Null } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
Assert 'vulnerability scanner rejects project entry without path' {
  $d=New-VulnerabilityScanFixture; try{ $logPath=Join-Path $d 'args.jsonl'; $fake=New-FakeDotnetProbe $d; $oldLog=$env:BUSINESSOS_FAKE_DOTNET_LOG; $oldMode=$env:BUSINESSOS_FAKE_DOTNET_MODE; $env:BUSINESSOS_FAKE_DOTNET_LOG=$logPath; $env:BUSINESSOS_FAKE_DOTNET_MODE='missing-path'; Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $d 'eng/check-vulnerable-packages.ps1'),'-ProjectOrSolution',(Join-Path $d 'BusinessOS.sln'),'-DotnetExecutable',$fake) -WorkingDirectory $d -Contains 'Project entry is missing path'|Out-Null } finally { $env:BUSINESSOS_FAKE_DOTNET_LOG=$oldLog; $env:BUSINESSOS_FAKE_DOTNET_MODE=$oldMode; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }

Assert 'Block 2B3 CI hardening is present' {
 $ci=Get-Content (Join-Path $RepoRoot '.github/workflows/ci.yml') -Raw
 foreach($bad in 'ubuntu-latest','windows-latest','work/block-1-foundation','path: .cache'){if($ci.Contains($bad)){throw "forbidden workflow text: $bad"}}
 if($ci -match 'uses:\s+[^\s]+@v\d+'){throw 'external action is not SHA pinned'}
 foreach($required in 'workflow_dispatch:','permissions:','contents: read','actions: read','concurrency:','timeout-minutes:','required-gates:','if-no-files-found: error','artifacts/ci-evidence/cross-platform/**','artifacts/ci-evidence/windows/**'){if(-not$ci.Contains($required)){throw "missing workflow setting: $required"}}
 if(-not(Test-Path (Join-Path $RepoRoot 'eng/schemas/ci-evidence.schema.json'))){throw 'summary schema missing'}
 $audit=Get-Content (Join-Path $RepoRoot 'eng/audit-github-ci.ps1') -Raw;if($audit -match '(^|[;&| ])gh([ ;&|]|$)'){throw 'auditor invokes forbidden CLI'}
 $lock=Get-Content (Join-Path $RepoRoot 'eng/environment.lock.json') -Raw|ConvertFrom-Json;if(-not$lock.githubCli.version){throw 'GitHub CLI not pinned'}
 $doctor=Get-Content (Join-Path $RepoRoot 'eng/doctor.ps1') -Raw;if($doctor-notmatch "'Audit'"){throw 'doctor Audit missing'}
 $readme=Get-Content (Join-Path $RepoRoot 'README.md') -Raw;if($readme-match 'restore UI remains deferred to Block 2B2b|Full Windows validation for Block 1'){throw 'README stale'}
}

Assert 'desktop Ready smoke protects the complete semantic Budgeting workflow' {
 $path=Join-Path $RepoRoot 'eng/smoke-test-desktop.ps1';$tokens=$null;$errors=$null;$ast=[System.Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
 if($errors.Count){throw 'desktop smoke does not parse'}
 $fn=@($ast.FindAll({param($n)$n-is[System.Management.Automation.Language.FunctionDefinitionAst]-and$n.Name-eq'Invoke-BudgetingCrudSmoke'},$true));if($fn.Count-ne1){throw 'missing unique Invoke-BudgetingCrudSmoke'};$body=$fn[0].Extent.Text;$diagnosticFunction=@($ast.FindAll({param($n)$n-is[System.Management.Automation.Language.FunctionDefinitionAst]-and$n.Name-eq'Write-BudgetingTimeoutDiagnostics'},$true));if($diagnosticFunction.Count-ne1){throw 'missing unique Budgeting diagnostics'};$contract=$body+$diagnosticFunction[0].Extent.Text
 $selectorFunction=@($ast.FindAll({param($n)$n-is[System.Management.Automation.Language.FunctionDefinitionAst]-and$n.Name-eq'Select-ComboBoxExactSemanticItem'},$true));if($selectorFunction.Count-ne1){throw 'missing unique semantic ComboBox selector'};$selectorContract=$selectorFunction[0].Extent.Text
 foreach($forbidden in 'Select-Object -First','Select-Object -Last','Start-Sleep','IsOffscreen','BoundingRectangle'){if($body.Contains($forbidden)){throw "forbidden Budgeting smoke construct: $forbidden"}}
 foreach($forbidden in 'Select-Object -First','Select-Object -Last','Start-Sleep','IsOffscreen','BoundingRectangle','$owned[0]'){if($selectorContract.Contains($forbidden)){throw "forbidden semantic ComboBox selector construct: $forbidden"}}
 foreach($required in 'NameProperty, $ExpectedName','IsSelectionItemPatternAvailableProperty','SelectionItemPattern','SelectionContainer','Get-ContainingListItem','ControlType]::ListItem','GetRuntimeId()) -join','logicalItemsByRuntimeId','if ($logicalItems.Count -ne 1)','CanonicalLogicalListItemRuntimeId','RawCandidateCount','UniqueLogicalItemCount','RequiredConsecutiveSuccesses 3','Get-ComboBoxSemanticSelection'){if(-not$selectorContract.Contains($required)){throw "semantic ComboBox selector contract missing: $required"}}
 $uniqueness=$selectorContract.IndexOf('if ($logicalItems.Count -ne 1)',[StringComparison]::Ordinal);$selection=$selectorContract.IndexOf('.Select()',[StringComparison]::Ordinal);if($uniqueness-lt0-or$selection-lt$uniqueness){throw 'semantic ComboBox selector selects before logical uniqueness is proven'}
 foreach($required in 'Select-ComboBoxExactSemanticItem','Get-BudgetingReadinessState','Get-BudgetRowState','Get-BudgetLineRowState','Write-BudgetingTimeoutDiagnostics','Write-SmokeDiagnosticsToHost','BusinessOS Gym Smoke Updated','Smoke CAPEX','Smoke Revenue','BudgetCapexTotal','BudgetOpexTotal','BudgetRevenueTotal','BudgetFinancingTotal','Version 1','Version 2','150','250','activate cancel stabilization','second activate dialog','archive cancel stabilization','second archive dialog','CancelActivateBudgetButton','ConfirmActivateBudgetButton','CancelArchiveBudgetButton','ConfirmArchiveBudgetButton','BudgetingCrud: archive PASS','BusinessProjectsSectionButton',"BudgetProjectCurrency Name='","BudgetingOperationMessage Name='",'AddBudgetButton IsEnabled=','BudgetingProjectSelector semantic selected item='){if(-not$contract.Contains($required)){throw "Budgeting smoke missing: $required"}}
 if($body.Contains("-contains'Active'")-or$body.Contains("-contains 'Active'")){throw 'Budgeting status uses fragile exact descendant Active check'}
 foreach($flow in @(@('CancelActivateBudgetButton','activate cancel stabilization','BudgetingCrud: activation cancel PASS','second activate dialog','ConfirmActivateBudgetButton'),@('CancelArchiveBudgetButton','archive cancel stabilization','BudgetingCrud: archive cancel PASS','second archive dialog','ConfirmArchiveBudgetButton'))){$previous=-1;foreach($marker in $flow){$next=$body.IndexOf($marker,$previous+1,[StringComparison]::Ordinal);if($next-lt0-or$next-lt$previous){throw "Budgeting async order invalid at $marker"};$previous=$next}}
 $archive=$body.IndexOf('BudgetingCrud: archive PASS',[StringComparison]::Ordinal);$back=$body.IndexOf('BusinessProjectsSectionButton',[StringComparison]::Ordinal);if($archive-lt0-or$back-lt$archive){throw 'Budgeting smoke returns before archive PASS'}
}

Assert 'Budgeting project selection consumes the unambiguous event project' {
 $source=Get-Content (Join-Path $RepoRoot 'src/BusinessOS.Desktop/MainWindow.xaml.cs') -Raw
 $match=[regex]::Match($source,'(?s)private async void BudgetingProjectSelector_SelectionChanged\b.*?(?=\s*private async void BudgetsList_SelectionChanged\b)')
 if(-not$match.Success){throw 'Budgeting project selection handler not found'};$handler=$match.Value
 foreach($required in 'e.AddedItems','OfType<BudgetProjectInfo>()','addedProjects.Length != 1','Budgeting.SelectProjectAsync(project)','RunUiOperationAsync','Budgeting.ReportPresentationFailure'){if(-not$handler.Contains($required)){throw "Budgeting project selection contract missing: $required"}}
 if($handler.Contains('(sender as ComboBox)?.SelectedItem')){throw 'Budgeting project selection rereads ComboBox.SelectedItem'}
}

Assert 'Budgeting version selection consumes the unambiguous event version' {
 $source=Get-Content (Join-Path $RepoRoot 'src/BusinessOS.Desktop/MainWindow.xaml.cs') -Raw
 $match=[regex]::Match($source,'(?s)private async void BudgetVersionsList_SelectionChanged\b.*?(?=\s*private async void BudgetRefresh_Click\b)')
 if(-not$match.Success){throw 'Budgeting version selection handler not found'};$handler=$match.Value
 foreach($required in 'e.AddedItems','OfType<BudgetVersionItem>()','addedVersions.Length != 1','Budgeting.SelectVersionAsync(version)','RunUiOperationAsync','Budgeting.ReportPresentationFailure'){if(-not$handler.Contains($required)){throw "Budgeting version selection contract missing: $required"}}
 if($handler.Contains('(sender as ListView)?.SelectedItem')){throw 'Budgeting version selection rereads ListView.SelectedItem'}
}

Assert 'Windows verifier fails outside Windows' { if(-not $IsWindows){ Invoke-ExpectFailure -File 'pwsh' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $RepoRoot 'eng/verify-windows.ps1')) -WorkingDirectory $RepoRoot -Contains 'must run on Windows' | Out-Null } }
if($script:Failures -gt 0){exit 1}
