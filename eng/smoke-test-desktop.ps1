param(
    [string]$Configuration = 'Release',
    [ValidateSet('Ready','PersistenceFailure','PersistenceFailureThenReady','RecoveryFromReady','RecoveryFromStartupFailure')]
    [string]$Scenario = 'Ready'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Import-Module (Join-Path $PSScriptRoot 'BusinessOS.Engineering.psm1') -Force
Set-Location $repoRoot
$artifactRoot = Join-Path $repoRoot "artifacts/smoke-test/$($Scenario.ToLowerInvariant())"
if (Test-Path $artifactRoot) { Remove-Item $artifactRoot -Recurse -Force }
New-Item -ItemType Directory -Force $artifactRoot | Out-Null
$oldDatabasePath = $env:BusinessOS__Persistence__DatabasePath
$oldBackupDirectory = $env:BusinessOS__Persistence__BackupDirectory
$oldMaxBackups = $env:BusinessOS__Persistence__MaxBackups
$env:BusinessOS__Persistence__BackupDirectory = Join-Path $artifactRoot 'backups'
$env:BusinessOS__Persistence__MaxBackups = '3'
switch ($Scenario) {
    'Ready' { $env:BusinessOS__Persistence__DatabasePath = Join-Path $artifactRoot 'data/businessos.db' }
    'PersistenceFailure' {
        $blocked = Join-Path $artifactRoot 'blocked'; Set-Content -Path $blocked -Value 'not a directory'
        $env:BusinessOS__Persistence__DatabasePath = Join-Path $blocked 'businessos.db'
    }
    'PersistenceFailureThenReady' {
        $blocked = Join-Path $artifactRoot 'blocked'; Set-Content -Path $blocked -Value 'not a directory'
        $env:BusinessOS__Persistence__DatabasePath = Join-Path $blocked 'businessos.db'
    }
    'RecoveryFromReady' {
        $fixtureJson = dotnet run --project tests/BusinessOS.RecoverySmokeFixture/BusinessOS.RecoverySmokeFixture.csproj -c $Configuration --no-build -- prepare-ready --root $artifactRoot | Select-Object -Last 1
        $fixture = $fixtureJson | ConvertFrom-Json
        $env:BusinessOS__Persistence__DatabasePath = $fixture.DatabasePath
        $env:BusinessOS__Persistence__BackupDirectory = $fixture.BackupDirectory
    }
    'RecoveryFromStartupFailure' {
        $fixtureJson = dotnet run --project tests/BusinessOS.RecoverySmokeFixture/BusinessOS.RecoverySmokeFixture.csproj -c $Configuration --no-build -- prepare-startup-failure --root $artifactRoot | Select-Object -Last 1
        $fixture = $fixtureJson | ConvertFrom-Json
        $blocked = $fixture.BlockedPath
        $env:BusinessOS__Persistence__DatabasePath = $fixture.DatabasePath
        $env:BusinessOS__Persistence__BackupDirectory = $fixture.BackupDirectory
    }
}
$diagnostics = Join-Path $artifactRoot 'desktop-smoke-diagnostics.txt'
Set-Content -Path $diagnostics -Value "BusinessOS desktop smoke test started: $(Get-Date -Format o)"
Add-Content -Path $diagnostics -Value "Scenario: $Scenario"
if (-not $IsWindows) { throw 'Desktop smoke test must run on Windows.' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
function Get-ProcessWindows([int]$ProcessId) {
    @([System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ProcessId -eq $ProcessId })
}
function Get-NamedElement($Window, [string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Invoke-NamedButton($Window, [string]$Name) {
    $button = Get-NamedElement $Window $Name
    if ($null -eq $button) { throw "UI Automation button was not found: $Name" }
    $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}
function Get-AutomationIdElement($Root, [string]$AutomationId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Invoke-AutomationIdButton($Root, [string]$AutomationId) {
    $button = Get-AutomationIdElement $Root $AutomationId
    if ($null -eq $button) { throw "UI Automation button was not found: $AutomationId" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-AutomationValue($Root, [string]$AutomationId, [string]$Value) {
    $element = Get-AutomationIdElement $Root $AutomationId
    if ($null -eq $element) { throw "UI Automation input was not found: $AutomationId" }
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
}
function Get-ContainingListItem($Element) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $current = $Element
    while ($null -ne $current -and $current.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) { $current = $walker.GetParent($current) }
    return $current
}
function Select-ContainingListItem($Element) {
    $current = Get-ContainingListItem $Element
    if ($null -eq $current) { throw 'Company list item could not be selected.' }
    $current.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Test-Visible($Element) { $null -ne $Element -and -not $Element.Current.IsOffscreen }
function Test-AutomationValueInputReady($Element) {
    if ($null -eq $Element -or -not $Element.Current.IsEnabled) { return $false }
    try {
        $valuePattern = $null
        return $Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern) -and -not $valuePattern.Current.IsReadOnly
    } catch { return $false }
}
function Get-ComboBoxSemanticSelection($Element, [string]$ExpectedValue) {
    $result = [ordered]@{
        SelectionSupported = $false; SelectedItemCount = 0; SelectedItemNames = @(); SelectionError = $null
        ValueSupported = $false; Value = $null; ValueIsReadOnly = $null; ValueError = $null; IsExpected = $false
    }
    if ($null -eq $Element) { return [pscustomobject]$result }
    try {
        $selectionPattern = $null
        $result.SelectionSupported = $Element.TryGetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern, [ref]$selectionPattern)
        if ($result.SelectionSupported) {
            $selected = @($selectionPattern.Current.GetSelection())
            $result.SelectedItemCount = $selected.Count
            $result.SelectedItemNames = @($selected | ForEach-Object { $_.Current.Name })
        }
    } catch { $result.SelectionError = $_.Exception.GetType().Name }
    try {
        $valuePattern = $null
        $result.ValueSupported = $Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)
        if ($result.ValueSupported) {
            $result.Value = $valuePattern.Current.Value
            $result.ValueIsReadOnly = $valuePattern.Current.IsReadOnly
        }
    } catch { $result.ValueError = $_.Exception.GetType().Name }
    $selectionMatches = $result.SelectionSupported -and $result.SelectedItemCount -eq 1 -and $result.SelectedItemNames[0] -eq $ExpectedValue
    $valueMatches = $result.ValueSupported -and $result.Value -eq $ExpectedValue
    $result.IsExpected = $selectionMatches -or $valueMatches
    return [pscustomobject]$result
}
function Get-BusinessProjectsReadinessState($Main, [string]$ExpectedCompany) {
    $selector = Get-AutomationIdElement $Main 'BusinessProjectsCompanySelector'
    $semanticSelection = Get-ComboBoxSemanticSelection $selector $ExpectedCompany
    $add = Get-AutomationIdElement $Main 'AddBusinessProjectButton'
    $empty = Get-AutomationIdElement $Main 'BusinessProjectsEmptyState'
    $selectorVisible = Test-Visible $selector
    $addVisible = Test-Visible $add
    $addEnabled = $null -ne $add -and $add.Current.IsEnabled
    $emptyStateVisible = Test-Visible $empty
    [pscustomobject]@{
        Selector = $selector
        SemanticSelection = $semanticSelection
        AddButton = $add
        EmptyState = $empty
        SelectorVisible = $selectorVisible
        ExpectedSemanticSelection = $semanticSelection.IsExpected
        AddButtonVisible = $addVisible
        AddButtonEnabled = $addEnabled
        EmptyStateVisible = $emptyStateVisible
        IsReady = $selectorVisible -and $semanticSelection.IsExpected -and $addVisible -and $addEnabled -and $emptyStateVisible
    }
}
function Test-CompanyEditorOpen($Main) {
    $legalName = Get-AutomationIdElement $Main 'CompanyLegalNameInput'
    $displayName = Get-AutomationIdElement $Main 'CompanyDisplayNameInput'
    $save = Get-AutomationIdElement $Main 'SaveCompanyButton'
    $cancel = Get-AutomationIdElement $Main 'CancelCompanyButton'
    (Test-Visible $legalName) -and (Test-Visible $displayName) -and $null -ne $save -and $save.Current.IsEnabled -and $null -ne $cancel -and $cancel.Current.IsEnabled
}
function Test-CompanyEditorClosed($Main) {
    @('CompanyLegalNameInput', 'CompanyDisplayNameInput', 'SaveCompanyButton', 'CancelCompanyButton') |
        Where-Object { Test-Visible (Get-AutomationIdElement $Main $_) } |
        Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
}
function Test-BusinessProjectEditorOpen($Main) {
    $inputsReady = @('BusinessProjectNameInput', 'BusinessProjectTypeInput', 'BusinessProjectLocationInput', 'BusinessProjectCurrencyInput') |
        Where-Object { -not (Test-AutomationValueInputReady (Get-AutomationIdElement $Main $_)) } |
        Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
    $save = Get-AutomationIdElement $Main 'SaveBusinessProjectButton'
    $cancel = Get-AutomationIdElement $Main 'CancelBusinessProjectButton'
    $filter = Get-AutomationIdElement $Main 'BusinessProjectsStatusFilter'
    $inputsReady -and $null -ne $save -and $save.Current.IsEnabled -and $null -ne $cancel -and $cancel.Current.IsEnabled -and $null -ne $filter -and -not $filter.Current.IsEnabled
}
function Test-BusinessProjectEditorClosed($Main) {
    $save = Get-AutomationIdElement $Main 'SaveBusinessProjectButton'
    $cancel = Get-AutomationIdElement $Main 'CancelBusinessProjectButton'
    $filter = Get-AutomationIdElement $Main 'BusinessProjectsStatusFilter'
    $add = Get-AutomationIdElement $Main 'AddBusinessProjectButton'
    ($null -eq $save -or -not $save.Current.IsEnabled) -and
        ($null -eq $cancel -or -not $cancel.Current.IsEnabled) -and
        $null -ne $filter -and $filter.Current.IsEnabled -and
        $null -ne $add -and $add.Current.IsEnabled
}
function Format-AutomationElementState($Element, [bool]$IncludeValuePattern = $false) {
    if ($null -eq $Element) { return "Found=False; IsEnabled=n/a; IsOffscreen=n/a; ControlType=n/a$(if ($IncludeValuePattern) { '; ValuePatternSupported=n/a' })" }
    $valuePatternState = ''
    if ($IncludeValuePattern) {
        try {
            $valuePattern = $null
            $supported = $Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)
            $isReadOnly = if ($supported) { $valuePattern.Current.IsReadOnly } else { 'n/a' }
            $valuePatternState = "; ValuePatternSupported=$supported; ValuePatternIsReadOnly=$isReadOnly"
        } catch { $valuePatternState = "; ValuePatternSupported=False; ValuePatternError=$($_.Exception.GetType().Name)" }
    }
    "Found=True; IsEnabled=$($Element.Current.IsEnabled); IsOffscreen=$($Element.Current.IsOffscreen); ControlType=$($Element.Current.ControlType.ProgrammaticName)$valuePatternState"
}
function Write-EditorTimeoutDiagnostics($Main, [ValidateSet('Company', 'BusinessProject')]$Editor, [string]$ExpectedAutomationId, [string[]]$EditorAutomationIds) {
    Add-Content $diagnostics "Editor timeout scenario: $Scenario"
    $valueInputIds = if ($Editor -eq 'BusinessProject') { @('BusinessProjectNameInput', 'BusinessProjectTypeInput', 'BusinessProjectLocationInput', 'BusinessProjectCurrencyInput') } else { @('CompanyLegalNameInput', 'CompanyDisplayNameInput') }
    Add-Content $diagnostics "Expected AutomationId: $ExpectedAutomationId"
    foreach ($id in $EditorAutomationIds) {
        Add-Content $diagnostics "$id state: $(Format-AutomationElementState (Get-AutomationIdElement $Main $id) ($id -in $valueInputIds))"
    }
    $contextIds = if ($Editor -eq 'BusinessProject') { @('AddBusinessProjectButton', 'BusinessProjectsCompanySelector', 'BusinessProjectsStatusFilter', 'BusinessProjectOperationMessage') } else { @('AddCompanyButton', 'CompaniesSectionPanel', 'CompanyOperationMessage') }
    foreach ($id in $contextIds) {
        Add-Content $diagnostics "$id state: $(Format-AutomationElementState (Get-AutomationIdElement $Main $id))"
    }
    $foundIds = @($EditorAutomationIds | Where-Object { $null -ne (Get-AutomationIdElement $Main $_) })
    Add-Content $diagnostics "Found editor AutomationIds: $($foundIds -join ', ')"
}
function Write-UpdateTimeoutDiagnostics($Main, [ValidateSet('Companies', 'BusinessProjects')]$Editor, [string]$OldName, [string]$UpdatedName) {
    if ($Editor -eq 'Companies') {
        $list = Get-AutomationIdElement $Main 'CompaniesList'
        $editorClosed = Test-CompanyEditorClosed $Main
        $inputId = 'CompanyDisplayNameInput'
        $saveId = 'SaveCompanyButton'
        $messageId = 'CompanyOperationMessage'
        $editorClosedLabel = 'Test-CompanyEditorClosed'
    } else {
        $list = Get-AutomationIdElement $Main 'BusinessProjectsList'
        $editorClosed = Test-BusinessProjectEditorClosed $Main
        $inputId = 'BusinessProjectNameInput'
        $saveId = 'SaveBusinessProjectButton'
        $messageId = 'BusinessProjectOperationMessage'
        $editorClosedLabel = 'Test-BusinessProjectEditorClosed'
    }
    $message = Get-AutomationIdElement $Main $messageId
    $messageName = if ($null -eq $message) { '<not found>' } else { $message.Current.Name }
    Add-Content $diagnostics "Update timeout scenario: $Scenario / $Editor update"
    Add-Content $diagnostics "old-name count: $((Get-NamedElements $list $OldName).Count)"
    Add-Content $diagnostics "updated-name count: $((Get-NamedElements $list $UpdatedName).Count)"
    Add-Content $diagnostics "${editorClosedLabel}: $editorClosed"
    Add-Content $diagnostics "$inputId state: $(Format-AutomationElementState (Get-AutomationIdElement $Main $inputId))"
    Add-Content $diagnostics "$saveId state: $(Format-AutomationElementState (Get-AutomationIdElement $Main $saveId))"
    Add-Content $diagnostics "$messageId.Current.Name: $messageName"
}
function Write-BusinessProjectsLoadTimeoutDiagnostics($Main) {
    $panel = Get-AutomationIdElement $Main 'BusinessProjectsSectionPanel'
    $readiness = Get-BusinessProjectsReadinessState $Main 'BusinessOS Smoke Updated'
    $selector = $readiness.Selector
    $add = $readiness.AddButton
    $empty = $readiness.EmptyState
    $message = Get-AutomationIdElement $Main 'BusinessProjectOperationMessage'
    $companies = Get-AutomationIdElement $Main 'CompaniesList'
    $semanticSelection = $readiness.SemanticSelection
    $oldCompanyCount = if ($null -eq $companies) { 0 } else { (Get-NamedElements $companies 'BusinessOS Smoke').Count }
    $updatedCompanyCount = if ($null -eq $companies) { 0 } else { (Get-NamedElements $companies 'BusinessOS Smoke Updated').Count }
    Add-Content $diagnostics 'Projects load timeout diagnostics:'
    Add-Content $diagnostics "Scenario: $Scenario"
    Add-Content $diagnostics "Readiness conditions: selector visible=$($readiness.SelectorVisible); expected semantic selection=$($readiness.ExpectedSemanticSelection); add button visible=$($readiness.AddButtonVisible); add button enabled=$($readiness.AddButtonEnabled); empty state visible=$($readiness.EmptyStateVisible); ready=$($readiness.IsReady)"
    Add-Content $diagnostics "BusinessProjectsSectionPanel (informational only): Found=$($null -ne $panel); IsOffscreen=$(if ($null -eq $panel) { 'n/a' } else { $panel.Current.IsOffscreen })"
    Add-Content $diagnostics "BusinessProjectsCompanySelector: Found=$($null -ne $selector); IsEnabled=$(if ($null -eq $selector) { 'n/a' } else { $selector.Current.IsEnabled }); IsOffscreen=$(if ($null -eq $selector) { 'n/a' } else { $selector.Current.IsOffscreen }); ControlType=$(if ($null -eq $selector) { 'n/a' } else { $selector.Current.ControlType.ProgrammaticName }); Current.Name=$(if ($null -eq $selector) { 'n/a' } else { $selector.Current.Name })"
    Add-Content $diagnostics "SelectionPattern: supported=$($semanticSelection.SelectionSupported); selected item count=$($semanticSelection.SelectedItemCount); selected item names=$($semanticSelection.SelectedItemNames -join ', '); error=$(if ($semanticSelection.SelectionError) { $semanticSelection.SelectionError } else { 'none' })"
    Add-Content $diagnostics "ValuePattern: supported=$($semanticSelection.ValueSupported); current value=$($semanticSelection.Value); IsReadOnly=$(if ($null -eq $semanticSelection.ValueIsReadOnly) { 'n/a' } else { $semanticSelection.ValueIsReadOnly }); error=$(if ($semanticSelection.ValueError) { $semanticSelection.ValueError } else { 'none' })"
    Add-Content $diagnostics "semantic-selection result: $($semanticSelection.IsExpected)"
    Add-Content $diagnostics "AddBusinessProjectButton: Found=$($null -ne $add); IsEnabled=$(if ($null -eq $add) { 'n/a' } else { $add.Current.IsEnabled }); IsOffscreen=$(if ($null -eq $add) { 'n/a' } else { $add.Current.IsOffscreen })"
    Add-Content $diagnostics "BusinessProjectsEmptyState: Found=$($null -ne $empty); IsOffscreen=$(if ($null -eq $empty) { 'n/a' } else { $empty.Current.IsOffscreen })"
    Add-Content $diagnostics "BusinessProjectOperationMessage.Current.Name: $(if ($null -eq $message) { '<not found>' } else { $message.Current.Name })"
    Add-Content $diagnostics "CompaniesList: count `"BusinessOS Smoke`"=$oldCompanyCount; count `"BusinessOS Smoke Updated`"=$updatedCompanyCount"
}
function Get-BusinessProjectStatusState($Main, [string]$ProjectName, [string]$ExpectedStatus) {
    $list = Get-AutomationIdElement $Main 'BusinessProjectsList'
    $projects = if ($null -eq $list) { @() } else { @(Get-NamedElements $list $ProjectName) }
    $listItem = if ($projects.Count -eq 1) { Get-ContainingListItem $projects[0] } else { $null }
    $semanticNames = if ($null -eq $listItem) { @() } else {
        @($listItem) + @($listItem.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)) |
            ForEach-Object { $_.Current.Name } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Unique
    }
    $statusConfirmed = @($semanticNames | Where-Object {
        $_ -eq $ExpectedStatus -or @($_ -split '\s*[\u00b7|]\s*') -contains $ExpectedStatus
    }).Count -gt 0
    [pscustomobject]@{
        List = $list
        ProjectCount = $projects.Count
        ListItem = $listItem
        SemanticNames = @($semanticNames)
        StatusConfirmed = $statusConfirmed
    }
}
function Test-BusinessProjectStatusReady($Main, [string]$ProjectName, [string]$ExpectedStatus) {
    $state = Get-BusinessProjectStatusState $Main $ProjectName $ExpectedStatus
    $dialog = Get-AutomationIdElement $Main 'BusinessProjectStatusDialog'
    $filter = Get-AutomationIdElement $Main 'BusinessProjectsStatusFilter'
    $recovery = Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton'
    $state.ProjectCount -eq 1 -and $null -ne $state.ListItem -and $state.StatusConfirmed -and
        $null -eq $dialog -and $null -ne $state.List -and $state.List.Current.IsEnabled -and
        $null -ne $filter -and $filter.Current.IsEnabled -and $null -ne $recovery -and $recovery.Current.IsEnabled
}
function Write-BusinessProjectStatusTimeoutDiagnostics($Main, [string]$ProjectName, [string]$ExpectedStatus) {
    $state = Get-BusinessProjectStatusState $Main $ProjectName $ExpectedStatus
    $dialog = Get-AutomationIdElement $Main 'BusinessProjectStatusDialog'
    $message = Get-AutomationIdElement $Main 'BusinessProjectOperationMessage'
    Add-Content $diagnostics 'BusinessProject status transition timeout diagnostics:'
    Add-Content $diagnostics "Scenario: $Scenario"
    Add-Content $diagnostics "Expected project name: $ProjectName"
    Add-Content $diagnostics "Expected status: $ExpectedStatus"
    Add-Content $diagnostics "BusinessProjectsList: Found=$($null -ne $state.List); IsEnabled=$(if ($null -eq $state.List) { 'n/a' } else { $state.List.Current.IsEnabled })"
    Add-Content $diagnostics "BusinessProjectStatusDialog still visible: $(Test-Visible $dialog)"
    Add-Content $diagnostics "BusinessProjectOperationMessage.Current.Name: $(if ($null -eq $message) { '<not found>' } else { $message.Current.Name })"
    Add-Content $diagnostics "Expected project element count: $($state.ProjectCount)"
    Add-Content $diagnostics "Containing project ListItem found: $($null -ne $state.ListItem)"
    Add-Content $diagnostics "Project ListItem semantic descendant names: $(if ($state.SemanticNames.Count -eq 0) { '<none>' } else { $state.SemanticNames -join ' | ' })"
    Add-Content $diagnostics "Expected status confirmed in project ListItem: $($state.StatusConfirmed)"
    foreach ($id in 'ChangeBusinessProjectStatusButton', 'BusinessProjectsStatusFilter', 'OpenRecoveryFromMainButton') {
        Add-Content $diagnostics "$id state: $(Format-AutomationElementState (Get-AutomationIdElement $Main $id))"
    }
}
function Wait-BusinessProjectStatusReady($Main, [string]$ProjectName, [string]$ExpectedStatus, [string]$TimeoutMessage) {
    try {
        Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage $TimeoutMessage -Condition {
            Test-BusinessProjectStatusReady $Main $ProjectName $ExpectedStatus
        }
    } catch {
        Write-BusinessProjectStatusTimeoutDiagnostics $Main $ProjectName $ExpectedStatus
        Write-SmokeDiagnosticsToHost
        throw $TimeoutMessage
    }

    $state = Get-BusinessProjectStatusState $Main $ProjectName $ExpectedStatus
    if ($state.ProjectCount -ne 1 -or $null -eq $state.ListItem -or -not $state.StatusConfirmed) {
        Write-BusinessProjectStatusTimeoutDiagnostics $Main $ProjectName $ExpectedStatus
        Write-SmokeDiagnosticsToHost
        throw $TimeoutMessage
    }
    return $state
}
function Write-SmokeDiagnosticsToHost {
    Write-Host '--- BEGIN DESKTOP SMOKE DIAGNOSTICS ---'
    if (Test-Path -LiteralPath $diagnostics -PathType Leaf) { Write-Host (Get-Content -LiteralPath $diagnostics -Raw) }
    else { Write-Host 'Diagnostics file was not created.' }
    Write-Host '--- END DESKTOP SMOKE DIAGNOSTICS ---'
}
function Wait-EditorOpen($Main, [ValidateSet('Company', 'BusinessProject')]$Editor, [string]$Invocation) {
    $ids = if ($Editor -eq 'Company') { @('CompanyLegalNameInput', 'CompanyDisplayNameInput', 'SaveCompanyButton', 'CancelCompanyButton') } else { @('BusinessProjectNameInput', 'BusinessProjectTypeInput', 'BusinessProjectLocationInput', 'BusinessProjectCurrencyInput', 'SaveBusinessProjectButton', 'CancelBusinessProjectButton', 'BusinessProjectsStatusFilter') }
    try {
        Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage "$Editor editor did not expose its interactive controls after $Invocation invocation." -Condition {
            if ($Editor -eq 'Company') { Test-CompanyEditorOpen $Main } else { Test-BusinessProjectEditorOpen $Main }
        }
    } catch {
        $missing = @($ids | Where-Object {
            $element = Get-AutomationIdElement $Main $_
            $null -eq $element -or
                ($Editor -eq 'BusinessProject' -and $_ -like 'BusinessProject*Input' -and -not (Test-AutomationValueInputReady $element)) -or
                ($Editor -eq 'Company' -and $element.Current.IsOffscreen) -or
                (($_ -like 'Save*' -or $_ -like 'Cancel*') -and -not $element.Current.IsEnabled) -or
                ($_ -eq 'BusinessProjectsStatusFilter' -and $element.Current.IsEnabled)
        })[0]
        if (-not $missing) { $missing = '<semantic editor contract>' }
        Write-EditorTimeoutDiagnostics $Main $Editor $missing $ids
        Write-SmokeDiagnosticsToHost
        throw "$Editor editor did not satisfy the semantic readiness contract after $Invocation invocation; first failing control: $missing."
    }
}
function Get-NamedElements($Root, [string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    @($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
}
function Select-ComboBoxExactSemanticItem($Main, [string]$SelectorId, [string]$ExpectedName) {
    $selector = Get-AutomationIdElement $Main $SelectorId
    if ($null -eq $selector) { throw "ComboBox not found: $SelectorId" }
    $selector.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $ExpectedName),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty, $true))
    $owned = @([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) | Where-Object {
        try { $container=$_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.SelectionContainer; $null-ne$container -and (@($container.GetRuntimeId()) -join '.') -eq (@($selector.GetRuntimeId()) -join '.') } catch { $false }
    })
    if ($owned.Count -ne 1) { throw "Expected exactly one '$ExpectedName' owned by $SelectorId; found $($owned.Count)." }
    $owned[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage "$SelectorId did not select '$ExpectedName'." -Condition { (Get-ComboBoxSemanticSelection $selector $ExpectedName).IsExpected }
}
function Get-ExactListRow($List, [string]$Name) {
    if($null-eq$List){throw "List was not available while looking for '$Name'."}
    $matches=@(Get-NamedElements $List $Name | ForEach-Object { Get-ContainingListItem $_ } | Where-Object { $null-ne$_ } | Sort-Object { @($_.GetRuntimeId()) -join '.' } -Unique)
    if($matches.Count-ne 1){throw "Expected exactly one row '$Name'; found $($matches.Count)."}; return $matches[0]
}
function Get-BudgetRowState($Main,[string]$BudgetName,[string]$ExpectedStatus,[int]$ExpectedVersion) {
    $list=Get-AutomationIdElement $Main 'BudgetsList';$matches=if($null-eq$list){@()}else{@(Get-NamedElements $list $BudgetName|ForEach-Object{Get-ContainingListItem $_}|Where-Object{$null-ne$_})}
    $row=if($matches.Count-eq1){$matches[0]}else{$null};$names=if($null-eq$row){@()}else{@($row.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.Name}|Where-Object{$_})}
    $tokens=@($names|ForEach-Object{$_-split'[·|]'|ForEach-Object{$_.Trim()}});[pscustomobject]@{BudgetCount=$matches.Count;ListItem=$row;SemanticNames=$names;StatusConfirmed=$tokens-contains$ExpectedStatus;VersionConfirmed=$tokens-contains"Version $ExpectedVersion"}
}
function Get-BudgetLineRowState($Main,[string]$LineName,[decimal]$ExpectedAmount) {
    $list=Get-AutomationIdElement $Main 'BudgetLinesList';$matches=if($null-eq$list){@()}else{@(Get-NamedElements $list $LineName|ForEach-Object{Get-ContainingListItem $_}|Where-Object{$null-ne$_})}
    $row=if($matches.Count-eq1){$matches[0]}else{$null};$names=if($null-eq$row){@()}else{@($row.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.Name}|Where-Object{$_})};$joined=$names-join' | '
    [pscustomobject]@{LineCount=$matches.Count;ListItem=$row;SemanticNames=$names;AmountConfirmed=$joined-match("(^|[^0-9])"+[regex]::Escape($ExpectedAmount.ToString([Globalization.CultureInfo]::InvariantCulture))+"([^0-9]|$)")}
}
function Get-BudgetingReadinessState($Main,[string]$ExpectedProject) {
    $selector=Get-AutomationIdElement $Main 'BudgetingProjectSelector';$add=Get-AutomationIdElement $Main 'AddBudgetButton';$semantic=Get-ComboBoxSemanticSelection $selector $ExpectedProject
    [pscustomobject]@{Selector=$selector;Semantic=$semantic;Add=$add;Budgets=Get-AutomationIdElement $Main 'BudgetsList';Empty=Get-AutomationIdElement $Main 'BudgetsEmptyState';Recovery=Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton';Message=Get-AutomationIdElement $Main 'BudgetingOperationMessage';IsReady=$semantic.IsExpected-and$null-ne$add-and$add.Current.IsEnabled}
}
function Write-BudgetingTimeoutDiagnostics($Main,[string]$Phase,[string]$ExpectedProject) {
    try{$state=Get-BudgetingReadinessState $Main $ExpectedProject;Add-Content $diagnostics "Budgeting phase: $Phase; expected project: $ExpectedProject; semantic selected project: $($state.Semantic.SelectedItemNames -join ',')/$($state.Semantic.Value)";foreach($id in 'BudgetProjectCurrency','BudgetsList','BudgetVersionsList','BudgetLinesList','BudgetCapexTotal','BudgetOpexTotal','BudgetRevenueTotal','BudgetFinancingTotal','BudgetNameInput','BudgetLineNameInput','ActivateBudgetDialog','ArchiveBudgetDialog','AddBudgetButton','RenameBudgetButton','CreateNextBudgetVersionButton','AddBudgetLineButton','EditBudgetLineButton','RemoveBudgetLineButton','BudgetingOperationMessage'){Add-Content $diagnostics "${id}: $(Format-AutomationElementState (Get-AutomationIdElement $Main $id))"};foreach($id in 'BudgetsList','BudgetVersionsList','BudgetLinesList'){$list=Get-AutomationIdElement $Main $id;$count=if($null-eq$list){'n/a'}else{@($list.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)).Count};Add-Content $diagnostics "$id count: $count"};foreach($spec in @(@('BusinessOS Budget Smoke Updated','Active',2),@('Smoke CAPEX',150),@('Smoke Revenue',250))){$row=if($spec.Count-eq3){Get-BudgetRowState $Main $spec[0] $spec[1] $spec[2]}else{Get-BudgetLineRowState $Main $spec[0] $spec[1]};Add-Content $diagnostics "row $($spec[0]): $($row.SemanticNames-join' | ')"}}catch{Add-Content $diagnostics "Budgeting diagnostics failure: $($_.Exception.GetType().Name)"}finally{Write-SmokeDiagnosticsToHost}
}
function Invoke-BudgetingCrudSmoke($Main) {
    $project='BusinessOS Gym Smoke Updated'; Invoke-AutomationIdButton $Main 'BudgetingSectionButton'
    try { Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budgeting selector not ready.' -Condition { $s=Get-AutomationIdElement $Main 'BudgetingProjectSelector';$null-ne$s-and$s.Current.IsEnabled }; Select-ComboBoxExactSemanticItem $Main 'BudgetingProjectSelector' $project; Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budgeting readiness failed.' -Condition { $r=Get-BudgetingReadinessState $Main $project; $r.IsReady-and(Test-Visible $r.Empty)-and(Get-AutomationIdElement $Main 'BudgetProjectCurrency').Current.Name-eq'PLN' } } catch { Write-BudgetingTimeoutDiagnostics $Main 'readiness' $project; throw }
    Add-Content $diagnostics 'BudgetingCrud: readiness PASS'
    Invoke-AutomationIdButton $Main 'AddBudgetButton';Set-AutomationValue $Main 'BudgetNameInput' 'BusinessOS Budget Smoke';Invoke-AutomationIdButton $Main 'SaveBudgetButton';Wait-BusinessOSCondition -TimeoutSeconds 15 -TimeoutMessage 'create' -Condition { try{$null-ne(Get-ExactListRow (Get-AutomationIdElement $Main 'BudgetsList') 'BusinessOS Budget Smoke')}catch{$false} };Add-Content $diagnostics 'BudgetingCrud: create PASS'
    Select-ContainingListItem (Get-NamedElement (Get-ExactListRow (Get-AutomationIdElement $Main 'BudgetsList') 'BusinessOS Budget Smoke') 'BusinessOS Budget Smoke');Invoke-AutomationIdButton $Main 'RenameBudgetButton';Set-AutomationValue $Main 'BudgetNameInput' 'BusinessOS Budget Smoke Updated';Invoke-AutomationIdButton $Main 'SaveBudgetButton';Wait-BusinessOSCondition -TimeoutSeconds 15 -TimeoutMessage 'rename' -Condition { try{$null-ne(Get-ExactListRow (Get-AutomationIdElement $Main 'BudgetsList') 'BusinessOS Budget Smoke Updated')}catch{$false} };Add-Content $diagnostics 'BudgetingCrud: rename PASS'
    Invoke-AutomationIdButton $Main 'CreateInitialBudgetVersionButton';Wait-BusinessOSCondition -TimeoutSeconds 15 -TimeoutMessage 'v1' -Condition {(Get-ComboBoxSemanticSelection (Get-AutomationIdElement $Main 'BudgetVersionsList') 'Version 1').IsExpected -or (Get-NamedElements (Get-AutomationIdElement $Main 'BudgetVersionsList') 'Version 1').Count-eq1};Add-Content $diagnostics 'BudgetingCrud: version 1 PASS'
    Invoke-AutomationIdButton $Main 'AddBudgetLineButton';Set-AutomationValue $Main 'BudgetLineNameInput' 'Smoke CAPEX';Set-AutomationValue $Main 'BudgetLineAmountInput' '100';Set-AutomationValue $Main 'BudgetLineSortOrderInput' '1';Invoke-AutomationIdButton $Main 'SaveBudgetLineButton'
    Invoke-AutomationIdButton $Main 'AddBudgetLineButton';Select-ComboBoxExactSemanticItem $Main 'BudgetLineKindInput' 'Revenue';Set-AutomationValue $Main 'BudgetLineNameInput' 'Smoke Revenue';Set-AutomationValue $Main 'BudgetLineAmountInput' '250';Set-AutomationValue $Main 'BudgetLineSortOrderInput' '2';Invoke-AutomationIdButton $Main 'SaveBudgetLineButton'
    Wait-BusinessOSCondition -TimeoutSeconds 15 -TimeoutMessage 'lines/totals' -Condition { try{$null-ne(Get-ExactListRow (Get-AutomationIdElement $Main 'BudgetLinesList') 'Smoke CAPEX')-and$null-ne(Get-ExactListRow (Get-AutomationIdElement $Main 'BudgetLinesList') 'Smoke Revenue')-and(Get-AutomationIdElement $Main 'BudgetCapexTotal').Current.Name-match'100'-and(Get-AutomationIdElement $Main 'BudgetOpexTotal').Current.Name-match'0'-and(Get-AutomationIdElement $Main 'BudgetRevenueTotal').Current.Name-match'250'-and(Get-AutomationIdElement $Main 'BudgetFinancingTotal').Current.Name-match'0'}catch{$false} };Add-Content $diagnostics 'BudgetingCrud: lines PASS';Add-Content $diagnostics 'BudgetingCrud: totals PASS'
    Invoke-AutomationIdButton $Main 'CreateNextBudgetVersionButton'
    Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'v2 copy' -Condition { (Get-BudgetLineRowState $Main 'Smoke CAPEX' 100).AmountConfirmed -and (Get-BudgetLineRowState $Main 'Smoke Revenue' 250).AmountConfirmed -and (Get-AutomationIdElement $Main 'BudgetCapexTotal').Current.Name-match'100' -and (Get-AutomationIdElement $Main 'BudgetRevenueTotal').Current.Name-match'250' }
    Add-Content $diagnostics 'BudgetingCrud: version 2 copy PASS'
    Select-ContainingListItem (Get-NamedElement (Get-ExactListRow (Get-AutomationIdElement $Main 'BudgetLinesList') 'Smoke CAPEX') 'Smoke CAPEX');Invoke-AutomationIdButton $Main 'EditBudgetLineButton';Set-AutomationValue $Main 'BudgetLineAmountInput' '150';Invoke-AutomationIdButton $Main 'SaveBudgetLineButton'
    Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'v2 update' -Condition { (Get-BudgetLineRowState $Main 'Smoke CAPEX' 150).AmountConfirmed -and (Get-BudgetLineRowState $Main 'Smoke Revenue' 250).AmountConfirmed -and (Get-AutomationIdElement $Main 'BudgetCapexTotal').Current.Name-match'150' -and (Get-AutomationIdElement $Main 'BudgetRevenueTotal').Current.Name-match'250' };Add-Content $diagnostics 'BudgetingCrud: version 2 update PASS'
    Select-ContainingListItem (Get-NamedElement (Get-ExactListRow (Get-AutomationIdElement $Main 'BudgetVersionsList') 'Version 1') 'Version 1')
    Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'history' -Condition { (Get-BudgetLineRowState $Main 'Smoke CAPEX' 100).AmountConfirmed -and (Get-BudgetLineRowState $Main 'Smoke Revenue' 250).AmountConfirmed -and (Get-AutomationIdElement $Main 'BudgetCapexTotal').Current.Name-match'100' -and (Get-AutomationIdElement $Main 'BudgetRevenueTotal').Current.Name-match'250' -and -not(Get-AutomationIdElement $Main 'AddBudgetLineButton').Current.IsEnabled -and -not(Get-AutomationIdElement $Main 'EditBudgetLineButton').Current.IsEnabled -and -not(Get-AutomationIdElement $Main 'RemoveBudgetLineButton').Current.IsEnabled }
    Select-ContainingListItem (Get-NamedElement (Get-ExactListRow (Get-AutomationIdElement $Main 'BudgetVersionsList') 'Version 2') 'Version 2')
    Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'v2 reselection' -Condition { (Get-NamedElements (Get-AutomationIdElement $Main 'BudgetVersionsList') 'Version 2').Count-eq1 -and (Get-BudgetLineRowState $Main 'Smoke CAPEX' 150).AmountConfirmed -and (Get-BudgetLineRowState $Main 'Smoke Revenue' 250).AmountConfirmed -and (Get-AutomationIdElement $Main 'BudgetCapexTotal').Current.Name-match'150' -and (Get-AutomationIdElement $Main 'BudgetRevenueTotal').Current.Name-match'250' }
    Add-Content $diagnostics 'BudgetingCrud: revision isolation PASS'
    Invoke-AutomationIdButton $Main 'ActivateBudgetButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'activate dialog' -Condition { $d=Get-AutomationIdElement $Main 'ActivateBudgetDialog';$null-ne$d-and$null-ne(Get-AutomationIdElement $d 'CancelActivateBudgetButton')-and-not(Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled };Invoke-AutomationIdButton (Get-AutomationIdElement $Main 'ActivateBudgetDialog') 'CancelActivateBudgetButton'
    Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'activate cancel stabilization' -Condition { $null-eq(Get-AutomationIdElement $Main 'ActivateBudgetDialog') -and (Get-AutomationIdElement $Main 'ActivateBudgetButton').Current.IsEnabled -and (Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled -and (Get-AutomationIdElement $Main 'BudgetsList').Current.IsEnabled };Add-Content $diagnostics 'BudgetingCrud: activation cancel PASS'
    Invoke-AutomationIdButton $Main 'ActivateBudgetButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'second activate dialog' -Condition { $d=Get-AutomationIdElement $Main 'ActivateBudgetDialog';$null-ne$d-and$null-ne(Get-AutomationIdElement $d 'ConfirmActivateBudgetButton') };Invoke-AutomationIdButton (Get-AutomationIdElement $Main 'ActivateBudgetDialog') 'ConfirmActivateBudgetButton'
    Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'active' -Condition { $state=Get-BudgetRowState $Main 'BusinessOS Budget Smoke Updated' 'Active' 2;$state.BudgetCount-eq1-and$state.StatusConfirmed-and$state.VersionConfirmed-and$null-eq(Get-AutomationIdElement $Main 'ActivateBudgetDialog')-and-not(Get-AutomationIdElement $Main 'RenameBudgetButton').Current.IsEnabled-and-not(Get-AutomationIdElement $Main 'CreateNextBudgetVersionButton').Current.IsEnabled-and-not(Get-AutomationIdElement $Main 'AddBudgetLineButton').Current.IsEnabled-and-not(Get-AutomationIdElement $Main 'EditBudgetLineButton').Current.IsEnabled-and-not(Get-AutomationIdElement $Main 'RemoveBudgetLineButton').Current.IsEnabled };Add-Content $diagnostics 'BudgetingCrud: activation PASS'
    Invoke-AutomationIdButton $Main 'ArchiveBudgetButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'archive dialog' -Condition { $d=Get-AutomationIdElement $Main 'ArchiveBudgetDialog';$null-ne$d-and$null-ne(Get-AutomationIdElement $d 'CancelArchiveBudgetButton') };Invoke-AutomationIdButton (Get-AutomationIdElement $Main 'ArchiveBudgetDialog') 'CancelArchiveBudgetButton'
    Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'archive cancel stabilization' -Condition { $null-eq(Get-AutomationIdElement $Main 'ArchiveBudgetDialog')-and(Get-AutomationIdElement $Main 'ArchiveBudgetButton').Current.IsEnabled-and(Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled-and(Get-AutomationIdElement $Main 'BudgetsList').Current.IsEnabled };Add-Content $diagnostics 'BudgetingCrud: archive cancel PASS'
    Invoke-AutomationIdButton $Main 'ArchiveBudgetButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'second archive dialog' -Condition { $d=Get-AutomationIdElement $Main 'ArchiveBudgetDialog';$null-ne$d-and$null-ne(Get-AutomationIdElement $d 'ConfirmArchiveBudgetButton') };Invoke-AutomationIdButton (Get-AutomationIdElement $Main 'ArchiveBudgetDialog') 'ConfirmArchiveBudgetButton'
    Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'archive' -Condition {(Get-NamedElements (Get-AutomationIdElement $Main 'BudgetsList') 'BusinessOS Budget Smoke Updated').Count-eq0-and(Test-Visible (Get-AutomationIdElement $Main 'BudgetsEmptyState'))};Add-Content $diagnostics 'BudgetingCrud: archive PASS'
    Invoke-AutomationIdButton $Main 'BusinessProjectsSectionButton'
}

function Invoke-CompaniesCrudSmoke($Main) {
    Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 5 -TimeoutMessage 'Companies UI did not reach its initial empty ready state.' -Condition {
        $add = Get-AutomationIdElement $Main 'AddCompanyButton'
        $list = Get-AutomationIdElement $Main 'CompaniesList'
        $empty = Get-AutomationIdElement $Main 'CompaniesEmptyState'
        return $null -ne $add -and $add.Current.IsEnabled -and $null -ne $list -and (Test-Visible $empty)
    }
    Add-Content $diagnostics 'CompaniesCrud: empty-state confirmed'
    Invoke-AutomationIdButton $Main 'AddCompanyButton'
    Wait-EditorOpen $Main Company 'AddCompanyButton'
    Set-AutomationValue $Main 'CompanyLegalNameInput' 'BusinessOS Smoke Legal'
    Set-AutomationValue $Main 'CompanyDisplayNameInput' 'BusinessOS Smoke'
    Set-AutomationValue $Main 'CompanyTaxIdInput' '5260250995'
    Set-AutomationValue $Main 'CompanyCountryInput' 'PL'
    Set-AutomationValue $Main 'CompanyCurrencyInput' 'PLN'
    Set-AutomationValue $Main 'CompanyTimeZoneInput' 'Europe/Warsaw'
    Invoke-AutomationIdButton $Main 'SaveCompanyButton'
    Wait-BusinessOSCondition -TimeoutSeconds 15 -TimeoutMessage 'Created company did not stabilize in the list.' -Condition {
        $list = Get-AutomationIdElement $Main 'CompaniesList'; $empty = Get-AutomationIdElement $Main 'CompaniesEmptyState'
        return (Get-NamedElements $list 'BusinessOS Smoke').Count -eq 1 -and (Test-CompanyEditorClosed $Main) -and -not (Test-Visible $empty)
    }
    Add-Content $diagnostics 'CompaniesCrud: create PASS'
    $list = Get-AutomationIdElement $Main 'CompaniesList'; $item = (Get-NamedElements $list 'BusinessOS Smoke')[0]
    Select-ContainingListItem $item
    Invoke-AutomationIdButton $Main 'EditCompanyButton'
    Wait-EditorOpen $Main Company 'EditCompanyButton'
    Set-AutomationValue $Main 'CompanyDisplayNameInput' 'BusinessOS Smoke Updated'
    Invoke-AutomationIdButton $Main 'SaveCompanyButton'
    try {
        Wait-BusinessOSCondition -TimeoutSeconds 15 -TimeoutMessage 'Updated company name did not stabilize in the list.' -Condition {
            $list = Get-AutomationIdElement $Main 'CompaniesList'
            return (Get-NamedElements $list 'BusinessOS Smoke').Count -eq 0 -and (Get-NamedElements $list 'BusinessOS Smoke Updated').Count -eq 1 -and (Test-CompanyEditorClosed $Main)
        }
    } catch {
        Write-UpdateTimeoutDiagnostics $Main Companies 'BusinessOS Smoke' 'BusinessOS Smoke Updated'
        throw 'Updated company name did not stabilize in the list.'
    }
    Add-Content $diagnostics 'CompaniesCrud: update PASS'
    Invoke-AutomationIdButton $Main 'BusinessProjectsSectionButton'
    try {
        Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Projects section did not load the created company.' -Condition {
            return (Get-BusinessProjectsReadinessState $Main 'BusinessOS Smoke Updated').IsReady
        }
    } catch {
        Write-BusinessProjectsLoadTimeoutDiagnostics $Main
        Write-SmokeDiagnosticsToHost
        throw 'Projects section did not load the created company.'
    }
    Invoke-AutomationIdButton $Main 'AddBusinessProjectButton'
    Wait-EditorOpen $Main BusinessProject 'AddBusinessProjectButton'
    Set-AutomationValue $Main 'BusinessProjectNameInput' 'BusinessOS Gym Smoke'
    Set-AutomationValue $Main 'BusinessProjectTypeInput' 'Gym 24/7'
    Set-AutomationValue $Main 'BusinessProjectLocationInput' 'Leczyca'
    Set-AutomationValue $Main 'BusinessProjectCurrencyInput' 'PLN'
    Invoke-AutomationIdButton $Main 'SaveBusinessProjectButton'
    Wait-BusinessOSCondition -TimeoutSeconds 15 -TimeoutMessage 'Created project did not stabilize in BusinessProjectsList.' -Condition {
        $projectsList=Get-AutomationIdElement $Main 'BusinessProjectsList'; return (Get-NamedElements $projectsList 'BusinessOS Gym Smoke').Count -eq 1 -and (Test-BusinessProjectEditorClosed $Main)
    }
    $projectsList=Get-AutomationIdElement $Main 'BusinessProjectsList'; Select-ContainingListItem (Get-NamedElements $projectsList 'BusinessOS Gym Smoke')[0]
    Invoke-AutomationIdButton $Main 'EditBusinessProjectButton'
    Wait-EditorOpen $Main BusinessProject 'EditBusinessProjectButton'
    Set-AutomationValue $Main 'BusinessProjectNameInput' 'BusinessOS Gym Smoke Updated'
    Invoke-AutomationIdButton $Main 'SaveBusinessProjectButton'
    try {
        Wait-BusinessOSCondition -TimeoutSeconds 15 -TimeoutMessage 'Updated project did not stabilize in BusinessProjectsList.' -Condition {
            $projectsList=Get-AutomationIdElement $Main 'BusinessProjectsList'; return (Get-NamedElements $projectsList 'BusinessOS Gym Smoke').Count -eq 0 -and (Get-NamedElements $projectsList 'BusinessOS Gym Smoke Updated').Count -eq 1 -and (Test-BusinessProjectEditorClosed $Main)
        }
    } catch {
        Write-UpdateTimeoutDiagnostics $Main BusinessProjects 'BusinessOS Gym Smoke' 'BusinessOS Gym Smoke Updated'
        throw 'Updated project did not stabilize in BusinessProjectsList.'
    }
    Invoke-BudgetingCrudSmoke $Main
    $projectsList=Get-AutomationIdElement $Main 'BusinessProjectsList'; Select-ContainingListItem (Get-NamedElements $projectsList 'BusinessOS Gym Smoke Updated')[0]
    $statusButton=Get-AutomationIdElement $Main 'ChangeBusinessProjectStatusButton'; if($null-eq$statusButton-or-not$statusButton.Current.IsEnabled){throw 'Status transition button was not enabled for Draft.'}
    Invoke-AutomationIdButton $Main 'ChangeBusinessProjectStatusButton'
    Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Project status dialog did not open.' -Condition { Test-Visible (Get-AutomationIdElement $Main 'BusinessProjectStatusDialog') }
    $statusDialog=Get-AutomationIdElement $Main 'BusinessProjectStatusDialog'; $selector=Get-AutomationIdElement $statusDialog 'BusinessProjectStatusSelector'; $projectsList=Get-AutomationIdElement $Main 'BusinessProjectsList'; $recovery=Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton'; if($projectsList.Current.IsEnabled-or$recovery.Current.IsEnabled){throw 'Status dialog did not lock project selection and recovery.'}
    $selector.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 300
    Select-ContainingListItem (Get-NamedElements $statusDialog 'Analysis')[0]
    Invoke-AutomationIdButton $statusDialog 'ConfirmBusinessProjectStatusButton'
    $null = Wait-BusinessProjectStatusReady $Main 'BusinessOS Gym Smoke Updated' 'Analysis' 'Analysis status did not appear for the expected project.'
    Add-Content $diagnostics 'BusinessProjectsCrud: status Analysis PASS'
    Invoke-AutomationIdButton $Main 'CompaniesSectionButton'
    $list=Get-AutomationIdElement $Main 'CompaniesList'; Select-ContainingListItem (Get-NamedElements $list 'BusinessOS Smoke Updated')[0]
    Invoke-AutomationIdButton $Main 'ArchiveCompanyButton'; Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Company archive guard dialog did not open.' -Condition { Test-Visible (Get-AutomationIdElement $Main 'ArchiveCompanyDialog') }
    $companyArchiveDialog=Get-AutomationIdElement $Main 'ArchiveCompanyDialog'; if((Get-AutomationIdElement $Main 'CompaniesList').Current.IsEnabled-or(Get-AutomationIdElement $Main 'CompaniesSectionButton').Current.IsEnabled-or(Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled){throw 'Company archive dialog did not lock list, navigation, and recovery.'}
    Invoke-AutomationIdButton $companyArchiveDialog 'CancelArchiveCompanyButton'; Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Company archive cancellation did not restore controls.' -Condition { -not(Test-Visible(Get-AutomationIdElement $Main 'ArchiveCompanyDialog')) -and (Get-AutomationIdElement $Main 'CompaniesList').Current.IsEnabled -and (Get-AutomationIdElement $Main 'CompaniesSectionButton').Current.IsEnabled -and (Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled }
    Invoke-AutomationIdButton $Main 'ArchiveCompanyButton'; Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Company archive guard dialog did not reopen.' -Condition { Test-Visible (Get-AutomationIdElement $Main 'ArchiveCompanyDialog') }
    Invoke-AutomationIdButton (Get-AutomationIdElement $Main 'ArchiveCompanyDialog') 'ConfirmArchiveCompanyButton'
    Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Company archive guard did not return a safe message.' -Condition { (Get-NamedElements $Main 'Najpierw zarchiwizuj wszystkie projekty firmy.').Count -ge 1 }
    if ((Get-NamedElements (Get-AutomationIdElement $Main 'CompaniesList') 'BusinessOS Smoke Updated').Count -ne 1) { throw 'Company disappeared despite project archive guard.' }
    Add-Content $diagnostics 'CompaniesCrud: project archive guard PASS'
    Invoke-AutomationIdButton $Main 'BusinessProjectsSectionButton'
    $projectState = Wait-BusinessProjectStatusReady $Main 'BusinessOS Gym Smoke Updated' 'Analysis' 'BusinessProjects section did not restore the expected Analysis project after company archive guard.'
    Select-ContainingListItem $projectState.ListItem
    Add-Content $diagnostics 'BusinessProjectsCrud: re-entry after company archive guard PASS'
    Invoke-AutomationIdButton $Main 'ArchiveBusinessProjectButton'; Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Project archive dialog did not open.' -Condition { Test-Visible (Get-AutomationIdElement $Main 'ArchiveBusinessProjectDialog') }
    $projectArchiveDialog=Get-AutomationIdElement $Main 'ArchiveBusinessProjectDialog'; $dialogText=@($projectArchiveDialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.Name})-join' | '; if(-not$dialogText.Contains('BusinessOS Gym Smoke Updated')){throw 'Project archive dialog did not contain captured project name.'}; if((Get-AutomationIdElement $Main 'BusinessProjectsCompanySelector').Current.IsEnabled-or(Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled){throw 'Archive dialog did not lock company selector and recovery.'}
    Invoke-AutomationIdButton $projectArchiveDialog 'CancelArchiveBusinessProjectButton'; Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Archive cancel did not restore controls.' -Condition { -not(Test-Visible(Get-AutomationIdElement $Main 'ArchiveBusinessProjectDialog')) -and (Get-AutomationIdElement $Main 'BusinessProjectsCompanySelector').Current.IsEnabled -and (Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled }
    Invoke-AutomationIdButton $Main 'ArchiveBusinessProjectButton'; Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Project archive dialog did not reopen.' -Condition { Test-Visible (Get-AutomationIdElement $Main 'ArchiveBusinessProjectDialog') }; $projectArchiveDialog=Get-AutomationIdElement $Main 'ArchiveBusinessProjectDialog'
    Invoke-AutomationIdButton $projectArchiveDialog 'ConfirmArchiveBusinessProjectButton'
    Wait-BusinessOSCondition -TimeoutSeconds 15 -TimeoutMessage 'Archived project remained visible.' -Condition { (Get-NamedElements (Get-AutomationIdElement $Main 'BusinessProjectsList') 'BusinessOS Gym Smoke Updated').Count -eq 0 -and (Test-Visible (Get-AutomationIdElement $Main 'BusinessProjectsEmptyState')) }
    Invoke-AutomationIdButton $Main 'CompaniesSectionButton'
    $list = Get-AutomationIdElement $Main 'CompaniesList'; $item = (Get-NamedElements $list 'BusinessOS Smoke Updated')[0]
    Select-ContainingListItem $item
    Invoke-AutomationIdButton $Main 'ArchiveCompanyButton'
    Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Archive confirmation did not open.' -Condition {
        $dialog = Get-AutomationIdElement $Main 'ArchiveCompanyDialog'
        if (-not (Test-Visible $dialog)) { return $false }
        $dialogText = @($dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join ' | '
        return $dialogText.Contains('BusinessOS Smoke Updated') -and $null -ne (Get-AutomationIdElement $dialog 'ConfirmArchiveCompanyButton')
    }
    $dialog = Get-AutomationIdElement $Main 'ArchiveCompanyDialog'
    Invoke-AutomationIdButton $dialog 'ConfirmArchiveCompanyButton'
    Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Archive confirmation did not close.' -Condition { -not (Test-Visible (Get-AutomationIdElement $Main 'ArchiveCompanyDialog')) }
    Wait-BusinessOSCondition -TimeoutSeconds 15 -TimeoutMessage 'Archived company remained visible or empty state did not return.' -Condition {
        $list = Get-AutomationIdElement $Main 'CompaniesList'; $empty = Get-AutomationIdElement $Main 'CompaniesEmptyState'; $recovery = Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton'
        return (Get-NamedElements $list 'BusinessOS Smoke Updated').Count -eq 0 -and (Test-Visible $empty) -and $null -ne $recovery -and $recovery.Current.IsEnabled
    }
    Add-Content $diagnostics 'CompaniesCrud: archive and empty-state PASS'
}
function Wait-RecoveryWindow($Process) {
    Wait-BusinessOSCondition -TimeoutSeconds 30 -RequiredConsecutiveSuccesses 5 -TimeoutMessage 'Recovery window did not become stable.' -Condition {
        $windows = Get-ProcessWindows $Process.Id
        $recovery = @($windows | Where-Object { $null -ne (Get-AutomationIdElement $_ 'RecoveryHeading') })
        return $windows.Count -eq 1 -and $recovery.Count -eq 1
    }
    return @(Get-ProcessWindows $Process.Id | Where-Object { $null -ne (Get-AutomationIdElement $_ 'RecoveryHeading') })[0]
}
function Wait-ReadyWindow($Process) {
    Wait-BusinessOSCondition -TimeoutSeconds 30 -RequiredConsecutiveSuccesses 5 -TimeoutMessage 'Main window did not become stable.' -Condition {
        $windows = Get-ProcessWindows $Process.Id
        $main = @($windows | Where-Object { $_.Current.Name -eq 'BusinessOS' -and $null -ne (Get-NamedElement $_ 'Baza danych jest gotowa') -and $null -ne (Get-AutomationIdElement $_ 'CompaniesList') -and $null -ne (Get-AutomationIdElement $_ 'AddCompanyButton') -and $null -ne (Get-AutomationIdElement $_ 'OpenRecoveryFromMainButton') })
        $failure = @($windows | Where-Object { $null -ne (Get-NamedElement $_ 'Ponów próbę') })
        $recovery = @($windows | Where-Object { $null -ne (Get-AutomationIdElement $_ 'RecoveryHeading') })
        return $windows.Count -eq 1 -and $main.Count -eq 1 -and $failure.Count -eq 0 -and $recovery.Count -eq 0
    }
    return @(Get-ProcessWindows $Process.Id | Where-Object { $_.Current.Name -eq 'BusinessOS' })[0]
}
function Measure-FinalWindowState($Process,[string]$ScenarioName) {
    $required=5;$observed=0;$last=$null;$deadline=(Get-Date).AddSeconds(30)
    do {
        if($Process.HasExited){break}
        $windows=@(Get-ProcessWindows $Process.Id)
        $main=@($windows|Where-Object{$_.Current.Name-eq'BusinessOS'-and$null-ne(Get-NamedElement $_ 'Baza danych jest gotowa')})
        $failure=@($windows|Where-Object{$null-ne(Get-NamedElement $_ 'Ponów próbę')})
        $recovery=@($windows|Where-Object{$null-ne(Get-AutomationIdElement $_ 'RecoveryHeading')})
        $last=[ordered]@{WindowCount=$windows.Count;MainWindowCount=$main.Count;FailureWindowCount=$failure.Count;RecoveryWindowCount=$recovery.Count}
        $expectedFailure=$ScenarioName-eq'PersistenceFailure'
        $valid=$windows.Count-eq 1-and$recovery.Count-eq 0-and(($expectedFailure-and$failure.Count-eq 1-and$main.Count-eq 0)-or(-not$expectedFailure-and$main.Count-eq 1-and$failure.Count-eq 0))
        if($valid){$observed++}else{$observed=0};if($observed-lt$required){Start-Sleep -Milliseconds 200}
    }while($observed-lt$required-and(Get-Date)-lt$deadline)
    [pscustomobject]@{RequiredSamples=$required;ObservedConsecutiveSamples=$observed;Passed=$observed-ge$required;LastObservedWindowCounts=$last}
}
function Select-RecoveryBackupItem($Recovery, [string]$ExpectedBackupId, [string]$ExpectedInvalidBackupId, [string]$Origin) {
    $list = Get-AutomationIdElement $Recovery 'RecoveryBackupList'
    $items = if ($null -eq $list) { @() } else {
        @($list.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem })
    }
    $catalog = @($items | ForEach-Object {
        $name = $_.Current.Name
        [pscustomobject]@{
            Element = $_
            Name = $name
            AutomationId = $_.Current.AutomationId
            HelpText = $_.Current.HelpText
            ControlType = $_.Current.ControlType.ProgrammaticName
            IsEnabled = $_.Current.IsEnabled
            MatchesExpected = $name.EndsWith("identyfikator $ExpectedBackupId", [StringComparison]::Ordinal)
            MatchesExpectedInvalid = $name.EndsWith("identyfikator $ExpectedInvalidBackupId", [StringComparison]::Ordinal)
            IsValid = $name -match 'prawidłowa' -and $name -notmatch 'nieprawidłowa'
            IsInvalid = $name -match 'nieprawidłowa'
        }
    })
    $valid = @($catalog | Where-Object IsValid)
    $invalid = @($catalog | Where-Object IsInvalid)
    $expected = @($catalog | Where-Object MatchesExpected)
    $expectedInvalid = @($catalog | Where-Object MatchesExpectedInvalid)
    $selectedIdentity = '<none>'
    $failure = $null
    $selectionPattern = $null
    if ($null -eq $list) { $failure = 'Recovery backup list was not found.' }
    elseif ($expected.Count -ne 1) { $failure = "Expected backup match count must be exactly one; found $($expected.Count)." }
    elseif (-not $expected[0].IsValid -or -not $expected[0].IsEnabled) { $failure = 'Expected backup is not valid, restorable, and enabled.' }
    elseif ($expectedInvalid.Count -ne 1 -or -not $expectedInvalid[0].IsInvalid) { $failure = "Expected invalid fixture backup was not uniquely classified as invalid; matches=$($expectedInvalid.Count)." }
    elseif (-not $expected[0].Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selectionPattern)) { $failure = 'Expected backup does not support SelectionItemPattern.' }
    if ($null -eq $failure) {
        $selectionPattern.Select()
        $selectedIdentity = $expected[0].Name
    }
    Add-Content $diagnostics "Recovery catalog diagnostics:`nScenario: $Scenario`nOrigin: $Origin`nExpected fixture BackupId: $ExpectedBackupId`nExpected invalid BackupId: $ExpectedInvalidBackupId`nTotal count: $($catalog.Count)`nValid count: $($valid.Count)`nInvalid count: $($invalid.Count)"
    foreach ($item in $catalog) {
        Add-Content $diagnostics "ListItem: Name='$($item.Name)'; AutomationId='$($item.AutomationId)'; HelpText='$($item.HelpText)'; ControlType='$($item.ControlType)'; IsEnabled=$($item.IsEnabled); MatchesExpected=$($item.MatchesExpected); MatchesExpectedInvalid=$($item.MatchesExpectedInvalid); IsValid=$($item.IsValid); IsInvalid=$($item.IsInvalid)"
    }
    Add-Content $diagnostics "Expected backup match count: $($expected.Count)`nSelected expected backup identity: $selectedIdentity"
    if ($null -ne $failure) {
        Write-SmokeDiagnosticsToHost
        throw "$failure Catalog: total=$($catalog.Count), valid=$($valid.Count), invalid=$($invalid.Count), expectedMatches=$($expected.Count)."
    }
    return @{ Total = $catalog.Count; Valid = $valid.Count; Invalid = $invalid.Count; ExpectedMatchCount = $expected.Count; SelectedExpectedBackupIdentity = $selectedIdentity }
}
function Invoke-RecoverySmoke($InitialRoot, $Process, [string]$Origin) {
    Add-Content $diagnostics "FixturePrepared: True`nRecoveryOrigin: $Origin"
    $entryId = if ($Origin -eq 'MainWindow') { 'OpenRecoveryFromMainButton' } else { 'OpenRecoveryFromFailureButton' }
    Invoke-AutomationIdButton $InitialRoot $entryId
    $recovery = Wait-RecoveryWindow $Process
    Add-Content $diagnostics 'RecoveryOpened: True';$script:recoveryOpened=$true
    Wait-BusinessOSCondition -TimeoutSeconds 30 -RequiredConsecutiveSuccesses 5 -TimeoutMessage 'Recovery catalog did not load.' -Condition {
        $refresh = Get-AutomationIdElement $recovery 'RefreshRecoveryCatalogButton'
        return $null -ne $refresh -and $refresh.Current.IsEnabled
    }
    $restore = Get-AutomationIdElement $recovery 'RestoreSelectedBackupButton'
    if ($null -eq $restore -or $restore.Current.IsEnabled) { throw 'Restore must be disabled without selection.' }
    $counts = Select-RecoveryBackupItem $recovery $fixture.BackupId $fixture.InvalidBackupId $Origin
    Add-Content $diagnostics "CatalogLoaded: True`nCatalogItemCount: $($counts.Total)`nValidBackupCount: $($counts.Valid)`nInvalidBackupCount: $($counts.Invalid)`nRestoreDisabledWithoutSelection: True";$script:catalogLoaded=$true;$script:catalogItemCount=$counts.Total;$script:validBackupCount=$counts.Valid;$script:invalidBackupCount=$counts.Invalid;$script:restoreDisabledWithoutSelection=$true
    if (-not (Get-AutomationIdElement $recovery 'RestoreSelectedBackupButton').Current.IsEnabled) { throw 'Restore did not enable for valid backup.' }

    if ($Origin -eq 'MainWindow') {
        Invoke-AutomationIdButton $recovery 'RestoreSelectedBackupButton'
        Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Confirmation dialog did not open.' -Condition { $null -ne (Get-AutomationIdElement $recovery 'CancelRestoreButton') }
        Invoke-AutomationIdButton $recovery 'CancelRestoreButton'
        Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 5 -TimeoutMessage 'Confirmation dialog did not close cleanly after cancellation.' -Condition {
            $dialog = Get-AutomationIdElement $recovery 'ConfirmRestoreDialog'
            $cancel = Get-AutomationIdElement $recovery 'CancelRestoreButton'
            $back = Get-AutomationIdElement $recovery 'BackFromRecoveryButton'
            $restoreAfterCancel = Get-AutomationIdElement $recovery 'RestoreSelectedBackupButton'
            return $null -eq $dialog -and $null -eq $cancel -and $null -ne $back -and $back.Current.IsEnabled -and
                $null -ne $restoreAfterCancel -and $restoreAfterCancel.Current.IsEnabled
        }
        Add-Content $diagnostics 'ConfirmationCancelPassed: True';$script:confirmationCancelPassed=$true
        Invoke-AutomationIdButton $recovery 'BackFromRecoveryButton'
        $main = Wait-ReadyWindow $Process
        Add-Content $diagnostics 'BackNavigationPassed: True';$script:backNavigationPassed=$true
        Invoke-AutomationIdButton $main 'OpenRecoveryFromMainButton'
        $recovery = Wait-RecoveryWindow $Process
        Wait-BusinessOSCondition -TimeoutSeconds 30 -RequiredConsecutiveSuccesses 5 -TimeoutMessage 'Second recovery catalog did not load.' -Condition { (Get-AutomationIdElement $recovery 'RefreshRecoveryCatalogButton').Current.IsEnabled }
        $null = Select-RecoveryBackupItem $recovery $fixture.BackupId $fixture.InvalidBackupId $Origin
    } else {
        Invoke-AutomationIdButton $recovery 'BackFromRecoveryButton'
        Wait-BusinessOSCondition -TimeoutSeconds 30 -RequiredConsecutiveSuccesses 5 -TimeoutMessage 'Failure window did not become stable after Back.' -Condition {
            $windows=Get-ProcessWindows $Process.Id;return $windows.Count -eq 1 -and $null -ne (Get-NamedElement $windows[0] 'Ponów próbę')
        }
        $failureWindow=(Get-ProcessWindows $Process.Id)[0];$script:backNavigationPassed=$true;Add-Content $diagnostics 'BackNavigationPassed: True'
        Invoke-AutomationIdButton $failureWindow 'OpenRecoveryFromFailureButton';$recovery=Wait-RecoveryWindow $Process
        Wait-BusinessOSCondition -TimeoutSeconds 30 -RequiredConsecutiveSuccesses 5 -TimeoutMessage 'Second recovery catalog did not load.' -Condition { (Get-AutomationIdElement $recovery 'RefreshRecoveryCatalogButton').Current.IsEnabled }
        $null=Select-RecoveryBackupItem $recovery $fixture.BackupId $fixture.InvalidBackupId $Origin
        Invoke-AutomationIdButton $recovery 'RestoreSelectedBackupButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Confirmation dialog did not open.' -Condition {$null-ne(Get-AutomationIdElement $recovery 'CancelRestoreButton')}
        Invoke-AutomationIdButton $recovery 'CancelRestoreButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 5 -TimeoutMessage 'Confirmation cancellation did not stabilize.' -Condition {$null-eq(Get-AutomationIdElement $recovery 'ConfirmRestoreDialog') -and (Get-AutomationIdElement $recovery 'RestoreSelectedBackupButton').Current.IsEnabled}
        $script:confirmationCancelPassed=$true;Add-Content $diagnostics 'ConfirmationCancelPassed: True'
        Remove-Item -LiteralPath $blocked -Force;New-Item -ItemType Directory -Path $blocked | Out-Null
    }

    Invoke-AutomationIdButton $recovery 'RestoreSelectedBackupButton'
    Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Confirmation dialog did not open.' -Condition { $null -ne (Get-AutomationIdElement $recovery 'ConfirmRestoreButton') }
    Invoke-AutomationIdButton $recovery 'ConfirmRestoreButton'
    Add-Content $diagnostics 'RestoreStarted: True';$script:restoreStarted=$true
    $main = Wait-ReadyWindow $Process
    $validationJson = dotnet run --project tests/BusinessOS.RecoverySmokeFixture/BusinessOS.RecoverySmokeFixture.csproj -c $Configuration --no-build -- validate-restored --root $artifactRoot | Select-Object -Last 1
    $validation = $validationJson | ConvertFrom-Json
    if ($validation.CompanyDisplayName -ne 'Selected Backup Company' -or $validation.QuickCheck -ne 'ok') { throw 'Fixture validation failed.' }
    Add-Content $diagnostics "RestoreSucceeded: True`nPostRestoreStartupSucceeded: True`nFixtureValidation: PASS"
    $script:restoreSucceeded=$true;$script:postRestoreStartupSucceeded=$true;$script:fixtureValidation='PASS'
    return $main
}
$exe = Get-ChildItem -Path (Join-Path $repoRoot 'src/BusinessOS.Desktop/bin') -Recurse -Filter BusinessOS.Desktop.exe |
    Where-Object { $_.FullName -match [regex]::Escape($Configuration) } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $exe) { throw "BusinessOS.Desktop.exe was not found for configuration $Configuration." }
$process = $null
$smokeChecksPassed = $false
$primaryFailure = $null
$cleanupFailure = $null
$shutdownFailure = $null
$closedByButton = $false
$windowCountBeforeClose = 'NOT CAPTURED'
$failureWindowCountBeforeClose = 'NOT CAPTURED'
$mainWindowCountBeforeClose = 'NOT CAPTURED'
$shutdownStarted = $false
$closeMainWindow = 'NOT RUN'
$processMainWindowHandleBeforeClose = 'NOT CAPTURED'
$processMainWindowTitleBeforeClose = 'NOT CAPTURED'
$targetWindowNativeHandle = 'NOT CAPTURED'
$targetWindowTitle = 'NOT CAPTURED'
$targetWindowAutomationId = 'NOT CAPTURED'
$targetWindowControlType = 'NOT CAPTURED'
$processAndTargetHandleMatch = 'NOT CAPTURED'
$closeDispatchMethod = 'NOT RUN'
$shutdownMethod='NOT_RUN'
$finalWindowCount=0;$finalMainWindowCount=0;$finalFailureWindowCount=0;$finalRecoveryWindowCount=0;$stableWindowStatePassed=$false;$catalogItemCount=0;$validBackupCount=0;$invalidBackupCount=0;$recoveryOpened=$false;$catalogLoaded=$false;$restoreDisabledWithoutSelection=$false;$confirmationCancelPassed=$false;$backNavigationPassed=$false;$restoreStarted=$false;$restoreSucceeded=$false;$postRestoreStartupSucceeded=$false;$fixtureValidation='NOT_APPLICABLE';$stableSamplesObserved=0
try {
    Add-Content -Path $diagnostics -Value "EXE: $($exe.FullName)"
    $process = Start-Process -FilePath $exe.FullName -PassThru
    Add-Content -Path $diagnostics -Value "PID: $($process.Id)"
    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw "BusinessOS.Desktop exited early with code $($process.ExitCode)." }
    } while ($process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
    if ($process.MainWindowHandle -eq 0) { throw 'BusinessOS main window handle was not created within 30 seconds.' }
    if ($Scenario -eq 'Ready' -and $process.MainWindowTitle -ne 'BusinessOS') { throw "Unexpected main window title: '$($process.MainWindowTitle)'." }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if ($null -eq $root) { throw 'UI Automation could not attach to the main window.' }
    if ($Scenario -like 'RecoveryFrom*') {
        $origin = if ($Scenario -eq 'RecoveryFromReady') { 'MainWindow' } else { 'StartupFailure' }
        $root = Invoke-RecoverySmoke $root $process $origin
        $texts = [System.Collections.Generic.List[string]]::new()
        $texts.Add('Baza danych jest gotowa')
    } else {
    $elements = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $texts = New-Object System.Collections.Generic.List[string]
    foreach ($element in $elements) {
        $name = $element.Current.Name
        if (-not [string]::IsNullOrWhiteSpace($name)) { $texts.Add($name) }
    }
    $requiredTexts = if ($Scenario -eq 'Ready') { @('BusinessOS','Firmy','Baza danych jest gotowa') } else { @('Nie udało się przygotować bazy danych','Ponów próbę','Zamknij','DiagnosticId') }
    foreach ($required in $requiredTexts) {
        if (-not ($texts -contains $required)) { throw "UI Automation did not find required element: $required." }
    }
    if ($Scenario -eq 'Ready') {
        $databasePath = $env:BusinessOS__Persistence__DatabasePath
        if (-not (Test-Path $databasePath) -or (Get-Item $databasePath).Length -le 0) { throw 'Ready SQLite database was not created.' }
        Invoke-CompaniesCrudSmoke $root
    } else {
        if ($texts -contains 'Foundation') { throw 'Functional main window opened during persistence failure.' }
        $forbiddenText = $texts -join ' | '
        foreach ($forbidden in @('StackTrace','System.IO.','Microsoft.Data.Sqlite','Data Source=',$env:BusinessOS__Persistence__DatabasePath)) {
            if ($forbiddenText.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) { throw "Failure UI exposed forbidden diagnostic text: $forbidden" }
        }

        if ($Scenario -eq 'PersistenceFailureThenReady') {
            Remove-Item -LiteralPath $blocked -Force
            New-Item -ItemType Directory -Path $blocked | Out-Null
            Invoke-NamedButton $root 'Ponów próbę'
            Wait-BusinessOSCondition -TimeoutMessage 'Successful retry did not produce a stable ready BusinessOS main window.' -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 5 -Condition {
                if ($process.HasExited) { return $false }
                $windows = Get-ProcessWindows $process.Id
                $mainWindows = @($windows | Where-Object { $_.Current.Name -eq 'BusinessOS' })
                $failureWindows = @($windows | Where-Object { $null -ne (Get-NamedElement $_ 'Ponów próbę') })
                $ready = $mainWindows.Count -eq 1 -and $failureWindows.Count -eq 0 -and $windows.Count -eq 1
                if ($ready) { $ready = $null -ne (Get-NamedElement $mainWindows[0] 'Baza danych jest gotowa') }
                $databasePath = $env:BusinessOS__Persistence__DatabasePath
                if ($ready) { $ready = (Test-Path $databasePath) -and (Get-Item $databasePath).Length -gt 0 }
                return $ready
            }
            $windows = Get-ProcessWindows $process.Id
            $mainWindows = @($windows | Where-Object { $_.Current.Name -eq 'BusinessOS' })
            $failureWindows = @($windows | Where-Object { $null -ne (Get-NamedElement $_ 'Ponów próbę') })
            $windowCountBeforeClose = $windows.Count
            $failureWindowCountBeforeClose = $failureWindows.Count
            $mainWindowCountBeforeClose = $mainWindows.Count
            if ($mainWindows.Count -ne 1 -or $windows.Count -ne 1) { throw 'Successful retry did not leave exactly one BusinessOS main window.' }
            if ($failureWindows.Count -ne 0) { throw 'Successful retry retained a StartupFailureWindow.' }
            if ($null -eq (Get-NamedElement $mainWindows[0] 'Baza danych jest gotowa')) { throw 'Successful retry did not show database ready status.' }
            $databasePath = $env:BusinessOS__Persistence__DatabasePath
            if (-not (Test-Path $databasePath) -or (Get-Item $databasePath).Length -le 0) { throw 'Retry did not create the SQLite database.' }
        } else {
            foreach ($attempt in 1..2) {
                Invoke-NamedButton $root 'Ponów próbę'
                Wait-BusinessOSCondition -TimeoutMessage "Retry $attempt did not settle on one enabled failure window." -Condition {
                    if ($process.HasExited) { return $false }
                    $retryWindows = Get-ProcessWindows $process.Id
                    if ($retryWindows.Count -ne 1) { return $false }
                    if ($null -ne (Get-NamedElement $retryWindows[0] 'Foundation')) { return $false }
                    $retryButton = Get-NamedElement $retryWindows[0] 'Ponów próbę'
                    $diagnostic = Get-NamedElement $retryWindows[0] 'DiagnosticId'
                    return $null -ne $retryButton -and $retryButton.Current.IsEnabled -and $null -ne $diagnostic
                }
                $windows = Get-ProcessWindows $process.Id
                if ($windows.Count -ne 1) { throw "Retry $attempt left $($windows.Count) top-level windows instead of one." }
                if ($null -ne (Get-NamedElement $windows[0] 'Foundation')) { throw "Retry $attempt opened the functional main window." }
                $retryButton = Get-NamedElement $windows[0] 'Ponów próbę'
                if ($null -eq $retryButton -or -not $retryButton.Current.IsEnabled) { throw "Retry $attempt did not re-enable the retry button." }
                $root = $windows[0]
            }
            $measurement=Measure-FinalWindowState $process $Scenario
            if(-not$measurement.Passed){throw 'Persistence failure window did not produce five stable measured samples.'}
            $stableSamplesObserved=$measurement.ObservedConsecutiveSamples;$stableWindowStatePassed=$measurement.Passed
            $finalWindowCount=$measurement.LastObservedWindowCounts.WindowCount;$finalMainWindowCount=$measurement.LastObservedWindowCounts.MainWindowCount;$finalFailureWindowCount=$measurement.LastObservedWindowCounts.FailureWindowCount;$finalRecoveryWindowCount=$measurement.LastObservedWindowCounts.RecoveryWindowCount
            Invoke-NamedButton $root 'Zamknij'
            if (-not $process.WaitForExit(10000)) { throw 'Close button did not terminate BusinessOS.Desktop.' }
            if ($process.ExitCode -ne 0) { throw "Close button produced exit code $($process.ExitCode)." }
            $closedByButton = $true
            $closeDispatchMethod = 'CloseButton'
        }
    }
    }
    Add-Content -Path $diagnostics -Value "MainWindowHandle: $($process.MainWindowHandle)"
    Add-Content -Path $diagnostics -Value "MainWindowTitle: $($process.MainWindowTitle)"
    Add-Content -Path $diagnostics -Value "UIAutomation: attached"
    Add-Content -Path $diagnostics -Value "Texts: $($texts -join ' | ')"
    Write-Host "EXE: $($exe.FullName)"
    Write-Host "PID: $($process.Id)"
    Write-Host "MainWindowHandle: $($process.MainWindowHandle)"
    Write-Host "MainWindowTitle: $($process.MainWindowTitle)"
    Write-Host "UIAutomation: attached"
    Write-Host "Diagnostics: $diagnostics"
    if($Scenario-ne'PersistenceFailure'){
        $measurement=Measure-FinalWindowState $process $Scenario
        if(-not$measurement.Passed){throw 'Final window state did not produce five stable measured samples.'}
        $stableSamplesObserved=$measurement.ObservedConsecutiveSamples;$stableWindowStatePassed=$measurement.Passed
        $finalWindowCount=$measurement.LastObservedWindowCounts.WindowCount;$finalMainWindowCount=$measurement.LastObservedWindowCounts.MainWindowCount;$finalFailureWindowCount=$measurement.LastObservedWindowCounts.FailureWindowCount;$finalRecoveryWindowCount=$measurement.LastObservedWindowCounts.RecoveryWindowCount
    }
    $smokeChecksPassed = $true
}
catch {
    $primaryFailure = $_
    Add-Content -Path $diagnostics -Value 'SmokeResult: FAIL'
    Add-Content -Path $diagnostics -Value "Exception: $($_.Exception.Message)"
}
finally {
    if ($null -ne $process) {
        try {
            $shutdownMethod = 'AlreadyExited'

            if ($process.HasExited -and $closedByButton) {
                $shutdownMethod = 'CloseButton'
            }
            elseif ($process.HasExited) {
                $shutdownFailure = 'BusinessOS.Desktop exited before CloseMainWindow was requested.'
                Add-Content -Path $diagnostics -Value "ShutdownFailure: $shutdownFailure"
            }
            else {
                $shutdownMethod = 'CloseMainWindow'
                $shutdownStarted = $true
                $windowsBeforeClose = Get-ProcessWindows $process.Id
                $mainWindowsBeforeClose = @($windowsBeforeClose | Where-Object { $_.Current.Name -eq 'BusinessOS' })
                $failureWindowsBeforeClose = @($windowsBeforeClose | Where-Object { $null -ne (Get-NamedElement $_ 'Ponów próbę') })
                $windowCountBeforeClose = $windowsBeforeClose.Count
                $failureWindowCountBeforeClose = $failureWindowsBeforeClose.Count
                $mainWindowCountBeforeClose = $mainWindowsBeforeClose.Count

                if ($windowsBeforeClose.Count -ne 1 -or $mainWindowsBeforeClose.Count -ne 1 -or $failureWindowsBeforeClose.Count -ne 0) {
                    $shutdownFailure = "Cannot close the BusinessOS main window: expected one main window and no failure window, but found $($windowsBeforeClose.Count) total, $($mainWindowsBeforeClose.Count) main, and $($failureWindowsBeforeClose.Count) failure windows."
                    Add-Content -Path $diagnostics -Value "ShutdownFailure: $shutdownFailure"
                }
                else {
                    $targetWindow = $mainWindowsBeforeClose[0]
                    $process.Refresh()
                    $processMainWindowHandleBeforeClose = $process.MainWindowHandle
                    $processMainWindowTitleBeforeClose = $process.MainWindowTitle
                    $targetWindowNativeHandle = $targetWindow.Current.NativeWindowHandle
                    $targetWindowTitle = $targetWindow.Current.Name
                    $targetWindowAutomationId = $targetWindow.Current.AutomationId
                    $targetWindowControlType = $targetWindow.Current.ControlType.ProgrammaticName
                    $processAndTargetHandleMatch = $processMainWindowHandleBeforeClose -eq $targetWindowNativeHandle

                    try {
                        $windowPattern = $targetWindow.GetCurrentPattern(
                            [System.Windows.Automation.WindowPattern]::Pattern
                        )
                        $closeDispatchMethod = 'UIAutomation.WindowPattern.Close'
                        $windowPattern.Close()
                        $closeMainWindow = $true
                        Add-Content -Path $diagnostics -Value 'CloseMainWindow: True'
                    }
                    catch {
                        $shutdownFailure = "The target BusinessOS window does not support WindowPattern.Close: $($_.Exception.Message)"
                        Add-Content -Path $diagnostics -Value "ShutdownFailure: $shutdownFailure"
                    }
                }

                if (-not $process.WaitForExit(10000)) {
                    if ($null -eq $shutdownFailure) {
                        $shutdownFailure = 'BusinessOS.Desktop did not terminate within 10 seconds after CloseMainWindow.'
                        Add-Content -Path $diagnostics -Value "ShutdownFailure: $shutdownFailure"
                    }

                    $shutdownMethod = 'Kill'
                    $process.Kill($true)

                    if (-not $process.WaitForExit(10000)) {
                        throw 'BusinessOS.Desktop did not terminate after emergency Kill.'
                    }
                }
            }

            $process.Refresh()

            if (-not $process.HasExited) {
                throw 'BusinessOS.Desktop process is still running after cleanup.'
            }

            $exitCode = $process.ExitCode
            if ($shutdownMethod -ne 'Kill' -and $exitCode -ne 0) {
                $shutdownFailure = "BusinessOS.Desktop exited with non-zero code $exitCode."
                Add-Content -Path $diagnostics -Value "ShutdownFailure: $shutdownFailure"
            }

            Add-Content -Path $diagnostics -Value "ShutdownMethod: $shutdownMethod"
            Add-Content -Path $diagnostics -Value "WindowCountBeforeClose: $windowCountBeforeClose"
            Add-Content -Path $diagnostics -Value "FailureWindowCountBeforeClose: $failureWindowCountBeforeClose"
            Add-Content -Path $diagnostics -Value "MainWindowCountBeforeClose: $mainWindowCountBeforeClose"
            Add-Content -Path $diagnostics -Value "ShutdownStarted: $shutdownStarted"
            Add-Content -Path $diagnostics -Value "CloseMainWindow: $closeMainWindow"
            Add-Content -Path $diagnostics -Value "ProcessMainWindowHandleBeforeClose: $processMainWindowHandleBeforeClose"
            Add-Content -Path $diagnostics -Value "ProcessMainWindowTitleBeforeClose: $processMainWindowTitleBeforeClose"
            Add-Content -Path $diagnostics -Value "TargetWindowNativeHandle: $targetWindowNativeHandle"
            Add-Content -Path $diagnostics -Value "TargetWindowTitle: $targetWindowTitle"
            Add-Content -Path $diagnostics -Value "TargetWindowAutomationId: $targetWindowAutomationId"
            Add-Content -Path $diagnostics -Value "TargetWindowControlType: $targetWindowControlType"
            Add-Content -Path $diagnostics -Value "ProcessAndTargetHandleMatch: $processAndTargetHandleMatch"
            Add-Content -Path $diagnostics -Value "CloseDispatchMethod: $closeDispatchMethod"
            Add-Content -Path $diagnostics -Value "Exited: $($process.HasExited)"
            Add-Content -Path $diagnostics -Value "ExitCode: $exitCode"
            Write-Host "BusinessOS.Desktop process closed by $shutdownMethod."
        }
        catch {
            $cleanupFailure = $_
            Add-Content -Path $diagnostics -Value 'SmokeResult: FAIL'
            Add-Content -Path $diagnostics -Value "CleanupException: $($_.Exception.Message)"
        }
    }
    $env:BusinessOS__Persistence__DatabasePath = $oldDatabasePath
    $env:BusinessOS__Persistence__BackupDirectory = $oldBackupDirectory
    $env:BusinessOS__Persistence__MaxBackups = $oldMaxBackups
}

$scenarioDirectory=Join-Path $repoRoot 'artifacts/smoke-test/scenarios';New-Item -ItemType Directory -Force $scenarioDirectory|Out-Null
$scenarioStatus=if($smokeChecksPassed-and$null-eq$primaryFailure-and$null-eq$cleanupFailure-and$null-eq$shutdownFailure){'PASS'}else{'FAIL'}
$scenarioEvidence=[ordered]@{name=$Scenario;status=$scenarioStatus;fixturePrepared=($Scenario-like'RecoveryFrom*');recoveryOrigin=if($Scenario-eq'RecoveryFromReady'){'MainWindow'}elseif($Scenario-eq'RecoveryFromStartupFailure'){'StartupFailure'}else{'None'};recoveryOpened=[bool]$recoveryOpened;catalogLoaded=[bool]$catalogLoaded;catalogItemCount=[int]$catalogItemCount;validBackupCount=[int]$validBackupCount;invalidBackupCount=[int]$invalidBackupCount;restoreDisabledWithoutSelection=[bool]$restoreDisabledWithoutSelection;confirmationCancelPassed=[bool]$confirmationCancelPassed;backNavigationPassed=[bool]$backNavigationPassed;restoreStarted=[bool]$restoreStarted;restoreSucceeded=[bool]$restoreSucceeded;postRestoreStartupSucceeded=[bool]$postRestoreStartupSucceeded;fixtureValidation=$fixtureValidation;stableSamplesRequired=5;stableSamplesObserved=[int]$stableSamplesObserved;stableWindowStatePassed=[bool]$stableWindowStatePassed;finalWindowCount=[int]$finalWindowCount;finalMainWindowCount=[int]$finalMainWindowCount;finalFailureWindowCount=[int]$finalFailureWindowCount;finalRecoveryWindowCount=[int]$finalRecoveryWindowCount;closeDispatchMethod=[string]$closeDispatchMethod;shutdownMethod=[string]$shutdownMethod;exited=[bool]($process-and$process.HasExited);exitCode=if($process-and$process.HasExited){[int]$process.ExitCode}else{1};diagnosticFile=[IO.Path]::GetRelativePath($repoRoot,$diagnostics).Replace('\','/')}
$scenarioEvidence|ConvertTo-Json -Depth 10|Set-Content (Join-Path $scenarioDirectory "$Scenario.json") -Encoding utf8NoBOM

if ($null -ne $primaryFailure) {
    throw $primaryFailure
}

if ($null -ne $cleanupFailure) {
    throw $cleanupFailure
}

if ($null -ne $shutdownFailure) {
    Add-Content -Path $diagnostics -Value 'SmokeResult: FAIL'
    throw $shutdownFailure
}

if ($smokeChecksPassed) {
    Add-Content -Path $diagnostics -Value 'SmokeResult: PASS'
}
