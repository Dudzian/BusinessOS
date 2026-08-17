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
function Set-AutomationCalendarDate($Main, [string]$AutomationId, [DateTime]$Date) {
    $target = $Date.Date
    $targetText = $target.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    $picker = Get-AutomationIdElement $Main $AutomationId
    if ($null -eq $picker) { throw "CalendarDatePicker was not found: $AutomationId target=$targetText" }
    if (-not $picker.Current.IsEnabled) { throw "CalendarDatePicker is disabled: $AutomationId target=$targetText" }

    $invokePattern = $null
    if (-not $picker.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invokePattern)) {
        throw "CalendarDatePicker InvokePattern is missing: $AutomationId target=$targetText"
    }

    $monthViewCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MonthViewScrollViewer')
    $dataItemCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pickerProcessId = $picker.Current.ProcessId
    $invokePattern.Invoke()

    $calendarView = $null
    try {
        Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage "Exactly one semantic active MonthViewScrollViewer was not found for $AutomationId target=$targetText." -Condition {
            try {
                $monthViewsByRuntimeId = @{}
                foreach ($view in @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $monthViewCondition))) {
                    if ($view.Current.ProcessId -eq $pickerProcessId) { $monthViewsByRuntimeId[(@($view.GetRuntimeId()) -join '.')] = $view }
                }
                $activeMonthViews = @{}
                $candidateDiagnostics = @()
                foreach ($entry in $monthViewsByRuntimeId.GetEnumerator()) {
                    $view = $entry.Value
                    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
                    $templateRoot = $view
                    $header = $null;$previous = $null;$next = $null
                    while ($null -ne $templateRoot) {
                        $header = Get-AutomationIdElement $templateRoot 'HeaderButton'
                        $previous = Get-AutomationIdElement $templateRoot 'PreviousButton'
                        $next = Get-AutomationIdElement $templateRoot 'NextButton'
                        if ($null -ne $header -and $null -ne $previous -and $null -ne $next) { break }
                        $templateRoot = $walker.GetParent($templateRoot)
                    }
                    $logicalItems = @($view.FindAll([System.Windows.Automation.TreeScope]::Descendants, $dataItemCondition))
                    $gridItemCount = 0;$selectionItemCount = 0;$semanticDayCount = 0
                    foreach ($item in $logicalItems) {
                        $gridPattern = $null;$selectionPattern = $null
                        $hasGrid = $item.TryGetCurrentPattern([System.Windows.Automation.GridItemPattern]::Pattern, [ref]$gridPattern)
                        $hasSelection = $item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selectionPattern)
                        if ($hasGrid) { $gridItemCount++ }
                        if ($hasSelection) { $selectionItemCount++ }
                        if ($hasGrid -and $hasSelection) { $semanticDayCount++ }
                    }
                    $isSemanticActiveCandidate = $null -ne $templateRoot -and $semanticDayCount -gt 0
                    if ($isSemanticActiveCandidate) {
                        $activeMonthViews[$entry.Key] = [pscustomobject]@{ View=$view;Calendar=$templateRoot;Header=$header;Previous=$previous;Next=$next }
                    }
                    $headerName = if ($null -ne $header) { $header.Current.Name } else { $null }
                    $candidateDiagnostics += "RuntimeId=$($entry.Key) ProcessId=$($view.Current.ProcessId) HeaderButtonFound=$($null-ne$header) HeaderButtonName='$headerName' PreviousButtonFound=$($null-ne$previous) NextButtonFound=$($null-ne$next) LogicalDataItemCount=$($logicalItems.Count) GridItemPatternCount=$gridItemCount SelectionItemPatternCount=$selectionItemCount SemanticActiveCandidate=$isSemanticActiveCandidate"
                }
                $selectedRuntimeId = if ($activeMonthViews.Count -eq 1) { foreach ($entry in $activeMonthViews.GetEnumerator()) { $entry.Key } } else { $null }
                $script:supplierInvoiceMonthViewDiscoveryDiagnostics = "target=$targetText pickerProcessId=$pickerProcessId MonthViewScrollViewerCandidateCount=$($monthViewsByRuntimeId.Count) candidates=[$($candidateDiagnostics-join'; ')] activeMonthViewCandidateCount=$($activeMonthViews.Count) selectedMonthViewRuntimeId=$selectedRuntimeId"
                if ($activeMonthViews.Count -ne 1) { return $false }
                foreach ($activeEntry in $activeMonthViews.GetEnumerator()) { $script:openedSupplierInvoiceCalendar = $activeEntry.Value }
                return $true
            } catch { return $false }
        }
    } catch {
        Add-Content $diagnostics "Calendar MonthView discovery failure: $script:supplierInvoiceMonthViewDiscoveryDiagnostics"
        throw
    }
    $activeCalendar = $script:openedSupplierInvoiceCalendar
    Add-Content $diagnostics "Calendar MonthView discovery: $script:supplierInvoiceMonthViewDiscoveryDiagnostics"
    Remove-Variable openedSupplierInvoiceCalendar -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable supplierInvoiceMonthViewDiscoveryDiagnostics -Scope Script -ErrorAction SilentlyContinue
    if ($null -eq $activeCalendar) { throw "Semantic active MonthViewScrollViewer was not identified for $AutomationId target=$targetText" }
    $calendarView = $activeCalendar.View
    $calendar = $activeCalendar.Calendar
    $header = $activeCalendar.Header
    $previous = $activeCalendar.Previous
    $next = $activeCalendar.Next

    $culture = [Globalization.CultureInfo]::CurrentCulture
    $calendarModel = $culture.DateTimeFormat.Calendar
    $targetEra = $calendarModel.GetEra($target)
    $targetYear = $calendarModel.GetYear($target)
    $targetMonth = $calendarModel.GetMonth($target)
    $targetDay = $calendarModel.GetDayOfMonth($target)
    $daysInTargetMonth = $calendarModel.GetDaysInMonth($targetYear, $targetMonth, $targetEra)
    $normalize = { param([string]$Text) (($Text -replace '[\u200e\u200f\u202a-\u202e\u2066-\u2069]', '') -replace '\s+', ' ').Trim() }
    $targetHeader = & $normalize $target.ToString('Y', $culture)
    $navigationCount = 0
    while ((& $normalize $header.Current.Name) -cne $targetHeader) {
        $displayed = $null
        $headerText = & $normalize $header.Current.Name
        foreach ($offset in -2400..2400) {
            $candidateMonth = $target.AddMonths($offset)
            if ((& $normalize $candidateMonth.ToString('Y', $culture)) -ceq $headerText) { $displayed = $candidateMonth; break }
        }
        if ($null -eq $displayed) { throw "CalendarView header could not be parsed: '$headerText' target=$targetText" }
        $button = if ($displayed -lt $target) { $next } else { $previous }
        $buttonInvoke = $null
        if (-not $button.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$buttonInvoke)) {
            throw "CalendarView navigation InvokePattern is missing; header='$headerText' target=$targetText"
        }
        $oldHeader = $headerText
        $buttonInvoke.Invoke()
        Wait-BusinessOSCondition -TimeoutSeconds 5 -RequiredConsecutiveSuccesses 2 -TimeoutMessage "CalendarView month did not change from '$oldHeader' target=$targetText." -Condition {
            try { (& $normalize $header.Current.Name) -cne $oldHeader } catch { return $false }
        }
        $navigationCount++
        if ($navigationCount -gt 2400) { throw "CalendarView navigation exceeded its semantic limit target=$targetText" }
    }

    $normalizedHeader = & $normalize $header.Current.Name
    if ($calendarView.Current.AutomationId -ne 'MonthViewScrollViewer' -or $normalizedHeader -cne $targetHeader) {
        throw "CalendarView month header mismatch: HeaderButton='$normalizedHeader' view='$($calendarView.Current.AutomationId)' target=$targetText"
    }
    $canonicalByRuntimeId = @{}
    foreach ($day in @($calendarView.FindAll([System.Windows.Automation.TreeScope]::Descendants, $dataItemCondition))) {
        $runtimeId = @($day.GetRuntimeId()) -join '.'
        if ($canonicalByRuntimeId.ContainsKey($runtimeId)) { continue }
        $gridPattern = $null
        $selectionPattern = $null
        $gridAvailable = $day.TryGetCurrentPattern([System.Windows.Automation.GridItemPattern]::Pattern, [ref]$gridPattern)
        $selectionAvailable = $day.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selectionPattern)
        $ordinal = 0
        $ordinalAvailable = [int]::TryParse((& $normalize $day.Current.Name), [Globalization.NumberStyles]::Integer, $culture, [ref]$ordinal)
        $canonicalByRuntimeId[$runtimeId] = [pscustomobject]@{
            Element = $day; RuntimeId = $runtimeId; Name = $day.Current.Name
            GridAvailable = $gridAvailable; Row = if ($gridAvailable) { $gridPattern.Current.Row } else { $null }
            Column = if ($gridAvailable) { $gridPattern.Current.Column } else { $null }
            SelectionAvailable = $selectionAvailable; SelectionPattern = $selectionPattern
            OrdinalAvailable = $ordinalAvailable; Ordinal = if ($ordinalAvailable) { $ordinal } else { $null }
            ExpectedDayLabel = if ($ordinalAvailable) { $ordinal.ToString($culture) } else { $null }
        }
    }
    $logicalDays = @($canonicalByRuntimeId.Values | Sort-Object Row, Column)
    $activeRuns = [Collections.ArrayList]::new()
    $completeRuns = [Collections.ArrayList]::new()
    foreach ($logicalDay in $logicalDays) {
        if (-not $logicalDay.GridAvailable -or -not $logicalDay.OrdinalAvailable) { continue }
        foreach ($run in @($activeRuns)) {
            if ($logicalDay.Ordinal -eq $run.ExpectedOrdinal) {
                [void]$run.Items.Add($logicalDay)
                $run.ExpectedOrdinal++
                if ($run.ExpectedOrdinal -gt $daysInTargetMonth) { [void]$completeRuns.Add($run); [void]$activeRuns.Remove($run) }
            } else { [void]$activeRuns.Remove($run) }
        }
        if ($logicalDay.Ordinal -eq 1) {
            $newRun = [pscustomobject]@{ ExpectedOrdinal = 2; Items = [Collections.ArrayList]::new() }
            [void]$newRun.Items.Add($logicalDay)
            if ($daysInTargetMonth -eq 1) { [void]$completeRuns.Add($newRun) } else { [void]$activeRuns.Add($newRun) }
        }
    }
    $candidateDiagnostics = @($logicalDays | ForEach-Object { "RuntimeId=$($_.RuntimeId) Name='$($_.Name)' Row=$($_.Row) Column=$($_.Column) GridItemPattern=$($_.GridAvailable) SelectionItemPattern=$($_.SelectionAvailable) InferredOrdinal=$($_.Ordinal) ExpectedDayLabel='$($_.ExpectedDayLabel)'" })
    $runDiagnostics = @($completeRuns | ForEach-Object { "RuntimeIds=$(($_.Items | ForEach-Object RuntimeId) -join ',')" })
    Add-Content $diagnostics "Calendar date target=$targetText HeaderButton='$normalizedHeader' targetEra=$targetEra targetYear=$targetYear targetMonth=$targetMonth targetDay=$targetDay daysInTargetMonth=$daysInTargetMonth logicalDataItemCount=$($logicalDays.Count) completeMonthRuns=$($completeRuns.Count) detectedRuns=$($runDiagnostics -join '; ') candidates: $($candidateDiagnostics -join '; ')"
    if ($completeRuns.Count -eq 0) { throw "CalendarView has no complete target-month logical run 1..$daysInTargetMonth; target=$targetText" }
    if ($completeRuns.Count -ne 1) { throw "CalendarView has multiple complete target-month logical runs: $($completeRuns.Count); target=$targetText" }
    $targetItems = @()
    foreach ($run in $completeRuns) { foreach ($item in $run.Items) { if ($item.Ordinal -eq $targetDay) { $targetItems += $item } } }
    if ($targetItems.Count -eq 0) { throw "CalendarView target ordinal is absent from canonical run: ordinal=$targetDay target=$targetText" }
    if ($targetItems.Count -ne 1) { throw "CalendarView target ordinal is duplicated inside canonical run: ordinal=$targetDay count=$($targetItems.Count) target=$targetText" }
    foreach ($targetItem in $targetItems) {
        if (-not $targetItem.SelectionAvailable) { throw "Target CalendarViewDayItem SelectionItemPattern is missing: RuntimeId=$($targetItem.RuntimeId) ordinal=$targetDay target=$targetText" }
        Add-Content $diagnostics "Calendar date selected target RuntimeId=$($targetItem.RuntimeId) target=$targetText"
        $targetItem.SelectionPattern.Select()
    }

    Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage "CalendarDatePicker selection did not update Date binding: $AutomationId target=$targetText." -Condition {
        try {
            $valuePattern = $null
            if (-not $picker.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) { return $false }
            $committed = [DateTime]::MinValue
            return [DateTime]::TryParse((& $normalize $valuePattern.Current.Value), $culture, [Globalization.DateTimeStyles]::AllowWhiteSpaces, [ref]$committed) -and $committed.Date -eq $target
        } catch { return $false }
    }
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
    $selectorRuntimeId = @($selector.GetRuntimeId()) -join '.'
    $selector.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $ExpectedName),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty, $true))
    $rawCandidates = @([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) | Where-Object {
        try {
            $container = $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.SelectionContainer
            $null -ne $container -and (@($container.GetRuntimeId()) -join '.') -eq $selectorRuntimeId
        } catch { $false }
    })
    $logicalItemsByRuntimeId = @{}
    $rawDiagnostics = @($rawCandidates | ForEach-Object {
        $candidate = $_
        $candidatePattern = $candidate.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $candidateContainer = $candidatePattern.Current.SelectionContainer
        $logicalItem = if ($candidate.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem) { $candidate } else { Get-ContainingListItem $candidate }
        $logicalRuntimeId = '<none>'
        if ($null -ne $logicalItem -and $logicalItem.Current.Name -ceq $ExpectedName) {
            try {
                $logicalPattern = $logicalItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                $logicalContainer = $logicalPattern.Current.SelectionContainer
                if ($null -ne $logicalContainer -and (@($logicalContainer.GetRuntimeId()) -join '.') -eq $selectorRuntimeId) {
                    $logicalRuntimeId = @($logicalItem.GetRuntimeId()) -join '.'
                    $logicalItemsByRuntimeId[$logicalRuntimeId] = $logicalItem
                }
            } catch { }
        }
        [pscustomobject]@{
            Name = $candidate.Current.Name
            ControlType = $candidate.Current.ControlType.ProgrammaticName
            AutomationId = $candidate.Current.AutomationId
            RuntimeId = @($candidate.GetRuntimeId()) -join '.'
            IsEnabled = $candidate.Current.IsEnabled
            SelectionContainerRuntimeId = @($candidateContainer.GetRuntimeId()) -join '.'
            CanonicalLogicalListItemRuntimeId = $logicalRuntimeId
        }
    })
    $logicalItems = @($logicalItemsByRuntimeId.Values)
    if ($logicalItems.Count -ne 1) {
        $rawDetails = @($rawDiagnostics | ForEach-Object { "raw: Name='$($_.Name)'; ControlType=$($_.ControlType); AutomationId='$($_.AutomationId)'; RuntimeId=$($_.RuntimeId); IsEnabled=$($_.IsEnabled); SelectionContainerRuntimeId=$($_.SelectionContainerRuntimeId); CanonicalLogicalListItemRuntimeId=$($_.CanonicalLogicalListItemRuntimeId)" }) -join [Environment]::NewLine
        $logicalDetails = @($logicalItems | ForEach-Object {
            $logicalPattern = $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $logicalContainer = $logicalPattern.Current.SelectionContainer
            "logical: Name='$($_.Current.Name)'; ControlType=$($_.Current.ControlType.ProgrammaticName); AutomationId='$($_.Current.AutomationId)'; RuntimeId=$(@($_.GetRuntimeId()) -join '.'); SelectionContainerRuntimeId=$(@($logicalContainer.GetRuntimeId()) -join '.')"
        }) -join [Environment]::NewLine
        throw "Expected exactly one unique logical selectable item. SelectorAutomationId='$SelectorId'; SelectorRuntimeId=$selectorRuntimeId; ExpectedName='$ExpectedName'; RawCandidateCount=$($rawCandidates.Count); UniqueLogicalItemCount=$($logicalItems.Count).$([Environment]::NewLine)$rawDetails$([Environment]::NewLine)$logicalDetails"
    }
    foreach ($logicalItem in $logicalItems) {
        $logicalItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
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
    [pscustomobject]@{Selector=$selector;Semantic=$semantic;Add=$add;Currency=Get-AutomationIdElement $Main 'BudgetProjectCurrency';Budgets=Get-AutomationIdElement $Main 'BudgetsList';Empty=Get-AutomationIdElement $Main 'BudgetsEmptyState';Recovery=Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton';Message=Get-AutomationIdElement $Main 'BudgetingOperationMessage';IsReady=$semantic.IsExpected-and$null-ne$add-and$add.Current.IsEnabled}
}
function Write-BudgetingTimeoutDiagnostics($Main,[string]$Phase,[string]$ExpectedProject) {
    try{$state=Get-BudgetingReadinessState $Main $ExpectedProject;$currencyName=if($null-eq$state.Currency){'<missing>'}else{$state.Currency.Current.Name};$messageName=if($null-eq$state.Message){'<missing>'}else{$state.Message.Current.Name};$addEnabled=if($null-eq$state.Add){'<missing>'}else{$state.Add.Current.IsEnabled};Add-Content $diagnostics "Budgeting phase: $Phase; expected project: $ExpectedProject";Add-Content $diagnostics "BudgetingProjectSelector semantic selected item='$($state.Semantic.SelectedItemNames -join ',')' Value='$($state.Semantic.Value)'";Add-Content $diagnostics "BudgetProjectCurrency Name='$currencyName'";Add-Content $diagnostics "BudgetingOperationMessage Name='$messageName'";Add-Content $diagnostics "AddBudgetButton IsEnabled=$addEnabled";foreach($id in 'BudgetProjectCurrency','BudgetsList','BudgetVersionsList','BudgetLinesList','BudgetCapexTotal','BudgetOpexTotal','BudgetRevenueTotal','BudgetFinancingTotal','BudgetNameInput','BudgetLineNameInput','ActivateBudgetDialog','ArchiveBudgetDialog','AddBudgetButton','RenameBudgetButton','CreateNextBudgetVersionButton','AddBudgetLineButton','EditBudgetLineButton','RemoveBudgetLineButton','BudgetingOperationMessage'){Add-Content $diagnostics "${id}: $(Format-AutomationElementState (Get-AutomationIdElement $Main $id))"};foreach($id in 'BudgetsList','BudgetVersionsList','BudgetLinesList'){$list=Get-AutomationIdElement $Main $id;$count=if($null-eq$list){'n/a'}else{@($list.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)).Count};Add-Content $diagnostics "$id count: $count"};$capexTotal=Get-AutomationIdElement $Main 'BudgetCapexTotal';$capexTotalName=if($null-eq$capexTotal){'<missing>'}else{$capexTotal.Current.Name};Add-Content $diagnostics "BudgetCapexTotal Name='$capexTotalName'";$revenueTotal=Get-AutomationIdElement $Main 'BudgetRevenueTotal';$revenueTotalName=if($null-eq$revenueTotal){'<missing>'}else{$revenueTotal.Current.Name};Add-Content $diagnostics "BudgetRevenueTotal Name='$revenueTotalName'";$versions=Get-AutomationIdElement $Main 'BudgetVersionsList';$selectedNames=@();$selectionError=$null;if($null-ne$versions){try{$selectionPattern=$null;if($versions.TryGetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern,[ref]$selectionPattern)){$selectedNames=@(([System.Windows.Automation.SelectionPattern]$selectionPattern).Current.GetSelection()|ForEach-Object{$_.Current.Name})}}catch{$selectionError=$_.Exception.GetType().Name}};Add-Content $diagnostics "BudgetVersionsList selected logical item names: $($selectedNames-join' | ')";Add-Content $diagnostics "BudgetVersionsList selection diagnostic error: $selectionError";foreach($spec in @(@('BusinessOS Budget Smoke Updated','Active',2),@('Smoke CAPEX',150),@('Smoke Revenue',250))){$row=if($spec.Count-eq3){Get-BudgetRowState $Main $spec[0] $spec[1] $spec[2]}else{Get-BudgetLineRowState $Main $spec[0] $spec[1]};Add-Content $diagnostics "row $($spec[0]): $($row.SemanticNames-join' | ')"}}catch{Add-Content $diagnostics "Budgeting diagnostics failure: $($_.Exception.GetType().Name)"}finally{Write-SmokeDiagnosticsToHost}
}
function Get-ActualCostRowState($Main,[string]$CostName,[decimal]$ExpectedAmount,[string]$ExpectedKind) {
    $list=Get-AutomationIdElement $Main 'ActualCostsList';$byRuntime=@{};if($null-ne$list){Get-NamedElements $list $CostName|ForEach-Object{$row=Get-ContainingListItem $_;if($null-ne$row){$byRuntime[(@($row.GetRuntimeId())-join'.')]=$row}}};$rows=@($byRuntime.Values);$row=if($rows.Count-eq1){$rows|ForEach-Object{$_}}else{$null};$names=if($null-eq$row){@()}else{@($row.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.Name}|Where-Object{$_})};$joined=$names-join' | ';[pscustomobject]@{Count=$rows.Count;ListItem=$row;SemanticNames=$names;Matches=$rows.Count-eq1-and$joined-match[regex]::Escape($ExpectedKind)-and$joined-match("(^|[^0-9])"+[regex]::Escape($ExpectedAmount.ToString([Globalization.CultureInfo]::InvariantCulture))+"([^0-9]|$)")-and$joined-match'PLN'}
}
function Get-ActualCostsReadinessState($Main,[string]$ExpectedProject) {$selector=Get-AutomationIdElement $Main 'ActualCostsProjectSelector';$semantic=Get-ComboBoxSemanticSelection $selector $ExpectedProject;$add=Get-AutomationIdElement $Main 'AddActualCostButton';[pscustomobject]@{Selector=$selector;Semantic=$semantic;Currency=Get-AutomationIdElement $Main 'ActualCostsProjectCurrency';List=Get-AutomationIdElement $Main 'ActualCostsList';Empty=Get-AutomationIdElement $Main 'ActualCostsEmptyState';Add=$add;IsReady=$semantic.IsExpected-and$null-ne$add-and$add.Current.IsEnabled}}
function Test-ActualCostEditorReady($Main) {
    try {$name=Get-AutomationIdElement $Main 'ActualCostNameInput';$amount=Get-AutomationIdElement $Main 'ActualCostAmountInput';$save=Get-AutomationIdElement $Main 'SaveActualCostButton';$cancel=Get-AutomationIdElement $Main 'CancelActualCostButton';(Test-AutomationValueInputReady $name)-and(Test-AutomationValueInputReady $amount)-and$null-ne$save-and$save.Current.IsEnabled-and$null-ne$cancel-and$cancel.Current.IsEnabled}catch{$false}
}
function Test-ActualCostEditorClosed($Main) {try{$save=Get-AutomationIdElement $Main 'SaveActualCostButton';$cancel=Get-AutomationIdElement $Main 'CancelActualCostButton';$list=Get-AutomationIdElement $Main 'ActualCostsList';$recovery=Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton';($null-eq$save-or-not$save.Current.IsEnabled)-and($null-eq$cancel-or-not$cancel.Current.IsEnabled)-and$null-ne$list-and$list.Current.IsEnabled-and$null-ne$recovery-and$recovery.Current.IsEnabled}catch{$false}}
function Write-ActualCostsTimeoutDiagnostics($Main,[string]$Phase,[string]$ExpectedProject) {try{$r=Get-ActualCostsReadinessState $Main $ExpectedProject;Add-Content $diagnostics "Actual Costs phase: $Phase; expected project: $ExpectedProject; semantic selected project='$($r.Semantic.SelectedItemNames-join',')'";foreach($id in 'ActualCostsProjectCurrency','ActualCostsOperationMessage','ActualCostsList','ActualCostCapexTotal','ActualCostOpexTotal','ActualCostTotal','AddActualCostButton','EditActualCostButton','ArchiveActualCostButton','ActualCostEditorPanel','ArchiveActualCostDialog','SaveActualCostButton','CancelActualCostButton','ActualCostNameInput','ActualCostAmountInput','ActualCostKindInput'){Add-Content $diagnostics "${id}: $(Format-AutomationElementState (Get-AutomationIdElement $Main $id))"};$count=if($null-eq$r.List){'n/a'}else{@($r.List.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)).Count};Add-Content $diagnostics "ActualCostsList count: $count";foreach($spec in @(@('Smoke Fit-out',150,'Capex'),@('Smoke Rent',40,'Opex'))){$row=Get-ActualCostRowState $Main $spec[0] $spec[1] $spec[2];Add-Content $diagnostics "row $($spec[0]): $($row.SemanticNames-join' | ')"}}catch{Add-Content $diagnostics "Actual Costs diagnostics failure: $($_.Exception.GetType().Name)"}finally{Write-SmokeDiagnosticsToHost}}
function Invoke-ActualCostsPhase($Main,[string]$Phase,[string]$Project,[scriptblock]$Action) {try{&$Action}catch{Write-ActualCostsTimeoutDiagnostics $Main $Phase $Project;throw}}
function Invoke-ActualCostsCrudSmoke($Main) {
    $project='BusinessOS Gym Smoke Updated';Invoke-AutomationIdButton $Main 'ActualCostsSectionButton'
    Invoke-ActualCostsPhase $Main 'readiness' $project {Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Actual Costs selector not ready.' -Condition{$s=Get-AutomationIdElement $Main 'ActualCostsProjectSelector';$null-ne$s-and$s.Current.IsEnabled};Select-ComboBoxExactSemanticItem $Main 'ActualCostsProjectSelector' $project;Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Actual Costs readiness failed.' -Condition{$r=Get-ActualCostsReadinessState $Main $project;$logicalRows=if($null-eq$r.List){@()}else{@($r.List.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)))};$r.IsReady-and$r.Currency.Current.Name-eq'PLN'-and$null-ne$r.List-and$logicalRows.Count-eq0}};Add-Content $diagnostics 'ActualCostsCrud: readiness PASS'
    Invoke-ActualCostsPhase $Main 'create fit-out' $project {Invoke-AutomationIdButton $Main 'AddActualCostButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Fit-out editor readiness failed.' -Condition{Test-ActualCostEditorReady $Main};Set-AutomationValue $Main 'ActualCostNameInput' 'Smoke Fit-out';Set-AutomationValue $Main 'ActualCostAmountInput' '100';Invoke-AutomationIdButton $Main 'SaveActualCostButton';Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Fit-out post-save semantic wait failed.' -Condition{$add=Get-AutomationIdElement $Main 'AddActualCostButton';(Get-ActualCostRowState $Main 'Smoke Fit-out' 100 'Capex').Matches-and$null-ne$add-and$add.Current.IsEnabled-and(Test-ActualCostEditorClosed $Main)}}
    Invoke-ActualCostsPhase $Main 'create rent' $project {Invoke-AutomationIdButton $Main 'AddActualCostButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Rent editor readiness failed.' -Condition{Test-ActualCostEditorReady $Main};Select-ComboBoxExactSemanticItem $Main 'ActualCostKindInput' 'Opex';Set-AutomationValue $Main 'ActualCostNameInput' 'Smoke Rent';Set-AutomationValue $Main 'ActualCostAmountInput' '40';Invoke-AutomationIdButton $Main 'SaveActualCostButton'}
    Invoke-ActualCostsPhase $Main 'create verification' $project {Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Actual Costs create verification failed.' -Condition{(Get-ActualCostRowState $Main 'Smoke Fit-out' 100 'Capex').Matches-and(Get-ActualCostRowState $Main 'Smoke Rent' 40 'Opex').Matches}};Add-Content $diagnostics 'ActualCostsCrud: create PASS'
    Invoke-ActualCostsPhase $Main 'totals' $project {Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Actual Costs totals failed.' -Condition{(Get-AutomationIdElement $Main 'ActualCostCapexTotal').Current.Name-match'100'-and(Get-AutomationIdElement $Main 'ActualCostOpexTotal').Current.Name-match'40'-and(Get-AutomationIdElement $Main 'ActualCostTotal').Current.Name-match'140'}};Add-Content $diagnostics 'ActualCostsCrud: totals PASS'
    Invoke-ActualCostsPhase $Main 'update' $project {$fit=Get-ActualCostRowState $Main 'Smoke Fit-out' 100 'Capex';Select-ContainingListItem $fit.ListItem;Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Edit Actual Cost selection did not stabilize.' -Condition{$edit=Get-AutomationIdElement $Main 'EditActualCostButton';$null-ne$edit-and$edit.Current.IsEnabled};Invoke-AutomationIdButton $Main 'EditActualCostButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Edit Actual Cost editor readiness failed.' -Condition{Test-ActualCostEditorReady $Main};Set-AutomationValue $Main 'ActualCostAmountInput' '150';Invoke-AutomationIdButton $Main 'SaveActualCostButton';Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Actual Costs update failed.' -Condition{(Get-ActualCostRowState $Main 'Smoke Fit-out' 150 'Capex').Matches-and(Get-AutomationIdElement $Main 'ActualCostCapexTotal').Current.Name-match'150'-and(Get-AutomationIdElement $Main 'ActualCostOpexTotal').Current.Name-match'40'-and(Get-AutomationIdElement $Main 'ActualCostTotal').Current.Name-match'190'}};Add-Content $diagnostics 'ActualCostsCrud: update PASS'
    Invoke-ActualCostsPhase $Main 're-entry' $project {Invoke-AutomationIdButton $Main 'CompaniesSectionButton';Invoke-AutomationIdButton $Main 'ActualCostsSectionButton';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Actual Costs re-entry selector not ready.' -Condition{$selector=Get-AutomationIdElement $Main 'ActualCostsProjectSelector';$null-ne$selector-and$selector.Current.IsEnabled};Select-ComboBoxExactSemanticItem $Main 'ActualCostsProjectSelector' $project;Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Actual Costs re-entry failed.' -Condition{$r=Get-ActualCostsReadinessState $Main $project;$r.Semantic.IsExpected-and$r.Currency.Current.Name-eq'PLN'-and(Get-ActualCostRowState $Main 'Smoke Fit-out' 150 'Capex').Matches-and(Get-ActualCostRowState $Main 'Smoke Rent' 40 'Opex').Matches-and(Get-AutomationIdElement $Main 'ActualCostCapexTotal').Current.Name-match'150'-and(Get-AutomationIdElement $Main 'ActualCostOpexTotal').Current.Name-match'40'-and(Get-AutomationIdElement $Main 'ActualCostTotal').Current.Name-match'190'}};Add-Content $diagnostics 'ActualCostsCrud: re-entry PASS'
    Invoke-ActualCostsPhase $Main 'archive dialog' $project {$rent=Get-ActualCostRowState $Main 'Smoke Rent' 40 'Opex';Select-ContainingListItem $rent.ListItem;Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Archive Actual Cost selection did not stabilize.' -Condition{$archive=Get-AutomationIdElement $Main 'ArchiveActualCostButton';$null-ne$archive-and$archive.Current.IsEnabled};Invoke-AutomationIdButton $Main 'ArchiveActualCostButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Actual Cost archive dialog failed.' -Condition{$null-ne(Get-AutomationIdElement $Main 'ArchiveActualCostDialog')}}
    Invoke-ActualCostsPhase $Main 'archive cancel' $project {Invoke-AutomationIdButton (Get-AutomationIdElement $Main 'ArchiveActualCostDialog') 'CancelArchiveActualCostButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Actual Cost archive cancel failed.' -Condition{$null-eq(Get-AutomationIdElement $Main 'ArchiveActualCostDialog')-and(Get-ActualCostRowState $Main 'Smoke Rent' 40 'Opex').Matches-and(Get-AutomationIdElement $Main 'ArchiveActualCostButton').Current.IsEnabled-and(Get-AutomationIdElement $Main 'ActualCostsList').Current.IsEnabled-and(Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled}};Add-Content $diagnostics 'ActualCostsCrud: archive cancel PASS'
    Invoke-ActualCostsPhase $Main 'archive confirm' $project {Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Archive Actual Cost did not restabilize before confirm.' -Condition{$archive=Get-AutomationIdElement $Main 'ArchiveActualCostButton';$null-ne$archive-and$archive.Current.IsEnabled};Invoke-AutomationIdButton $Main 'ArchiveActualCostButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Actual Cost second archive dialog failed.' -Condition{$null-ne(Get-AutomationIdElement $Main 'ArchiveActualCostDialog')};Invoke-AutomationIdButton (Get-AutomationIdElement $Main 'ArchiveActualCostDialog') 'ConfirmArchiveActualCostButton';Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Actual Cost archive failed.' -Condition{(Get-ActualCostRowState $Main 'Smoke Rent' 40 'Opex').Count-eq0-and(Get-ActualCostRowState $Main 'Smoke Fit-out' 150 'Capex').Matches-and(Get-AutomationIdElement $Main 'ActualCostCapexTotal').Current.Name-match'150'-and(Get-AutomationIdElement $Main 'ActualCostOpexTotal').Current.Name-match'0'-and(Get-AutomationIdElement $Main 'ActualCostTotal').Current.Name-match'150'}};Add-Content $diagnostics 'ActualCostsCrud: archive PASS'
    Invoke-AutomationIdButton $Main 'BusinessProjectsSectionButton'
}
function Get-ForecastCostRowState($Main,[string]$CostName,[decimal]$ExpectedAmount,[string]$ExpectedKind) {
    $list=Get-AutomationIdElement $Main 'ForecastCostsList';$byRuntime=@{};if($null-ne$list){Get-NamedElements $list $CostName|ForEach-Object{$row=Get-ContainingListItem $_;if($null-ne$row){$byRuntime[(@($row.GetRuntimeId())-join'.')]=$row}}};$rows=@($byRuntime.Values);$row=if($rows.Count-eq1){$rows|ForEach-Object{$_}}else{$null};$names=if($null-eq$row){@()}else{@($row.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.Name}|Where-Object{$_})};$joined=$names-join' | ';[pscustomobject]@{Count=$rows.Count;ListItem=$row;SemanticNames=$names;Matches=$rows.Count-eq1-and$joined-match[regex]::Escape($ExpectedKind)-and$joined-match("(^|[^0-9])"+[regex]::Escape($ExpectedAmount.ToString([Globalization.CultureInfo]::InvariantCulture))+"([^0-9]|$)")-and$joined-match'PLN'}
}
function Get-ForecastCostsReadinessState($Main,[string]$ExpectedProject) {$selector=Get-AutomationIdElement $Main 'ForecastCostsProjectSelector';$semantic=Get-ComboBoxSemanticSelection $selector $ExpectedProject;$add=Get-AutomationIdElement $Main 'AddForecastCostButton';[pscustomobject]@{Selector=$selector;Semantic=$semantic;Currency=Get-AutomationIdElement $Main 'ForecastCostsProjectCurrency';List=Get-AutomationIdElement $Main 'ForecastCostsList';Empty=Get-AutomationIdElement $Main 'ForecastCostsEmptyState';Add=$add;IsReady=$semantic.IsExpected-and$null-ne$add-and$add.Current.IsEnabled}}
function Test-ForecastCostEditorReady($Main) {
    try {$name=Get-AutomationIdElement $Main 'ForecastCostNameInput';$amount=Get-AutomationIdElement $Main 'ForecastCostAmountInput';$save=Get-AutomationIdElement $Main 'SaveForecastCostButton';$cancel=Get-AutomationIdElement $Main 'CancelForecastCostButton';(Test-AutomationValueInputReady $name)-and(Test-AutomationValueInputReady $amount)-and$null-ne$save-and$save.Current.IsEnabled-and$null-ne$cancel-and$cancel.Current.IsEnabled}catch{$false}
}
function Test-ForecastCostEditorClosed($Main) {try{$save=Get-AutomationIdElement $Main 'SaveForecastCostButton';$cancel=Get-AutomationIdElement $Main 'CancelForecastCostButton';$list=Get-AutomationIdElement $Main 'ForecastCostsList';$recovery=Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton';($null-eq$save-or-not$save.Current.IsEnabled)-and($null-eq$cancel-or-not$cancel.Current.IsEnabled)-and$null-ne$list-and$list.Current.IsEnabled-and$null-ne$recovery-and$recovery.Current.IsEnabled}catch{$false}}
function Write-ForecastCostsTimeoutDiagnostics($Main,[string]$Phase,[string]$ExpectedProject) {try{$r=Get-ForecastCostsReadinessState $Main $ExpectedProject;Add-Content $diagnostics "Forecast Costs phase: $Phase; expected project: $ExpectedProject; semantic selected project='$($r.Semantic.SelectedItemNames-join',')'";foreach($id in 'ForecastCostsProjectCurrency','ForecastCostsOperationMessage','ForecastCostsList','ForecastCostCapexTotal','ForecastCostOpexTotal','ForecastCostTotal','AddForecastCostButton','EditForecastCostButton','ArchiveForecastCostButton','ForecastCostEditorPanel','ArchiveForecastCostDialog','SaveForecastCostButton','CancelForecastCostButton','ForecastCostNameInput','ForecastCostAmountInput','ForecastCostKindInput','ForecastCostExpectedDateInput','ForecastCostNoteInput'){Add-Content $diagnostics "${id}: $(Format-AutomationElementState (Get-AutomationIdElement $Main $id))"};$count=if($null-eq$r.List){'n/a'}else{@($r.List.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)).Count};Add-Content $diagnostics "ForecastCostsList count: $count";foreach($spec in @(@('Smoke Equipment Forecast',75,'Capex'),@('Smoke Utilities Forecast',20,'Opex'))){$row=Get-ForecastCostRowState $Main $spec[0] $spec[1] $spec[2];Add-Content $diagnostics "row $($spec[0]): $($row.SemanticNames-join' | ')"}}catch{Add-Content $diagnostics "Forecast Costs diagnostics failure: $($_.Exception.GetType().Name)"}finally{Write-SmokeDiagnosticsToHost}}
function Invoke-ForecastCostsPhase($Main,[string]$Phase,[string]$Project,[scriptblock]$Action) {try{&$Action}catch{Write-ForecastCostsTimeoutDiagnostics $Main $Phase $Project;throw}}
function Invoke-ForecastCostsCrudSmoke($Main) {
    $project='BusinessOS Gym Smoke Updated';Invoke-AutomationIdButton $Main 'ForecastCostsSectionButton'
    Invoke-ForecastCostsPhase $Main 'readiness' $project {Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Forecast Costs selector not ready.' -Condition{$s=Get-AutomationIdElement $Main 'ForecastCostsProjectSelector';$null-ne$s-and$s.Current.IsEnabled};Select-ComboBoxExactSemanticItem $Main 'ForecastCostsProjectSelector' $project;Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Forecast Costs readiness failed.' -Condition{$r=Get-ForecastCostsReadinessState $Main $project;$logicalRows=if($null-eq$r.List){@()}else{@($r.List.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)))};$r.IsReady-and$r.Currency.Current.Name-eq'PLN'-and$null-ne$r.List-and$logicalRows.Count-eq0}};Add-Content $diagnostics 'ForecastCostsCrud: readiness PASS'
    Invoke-ForecastCostsPhase $Main 'create equipment' $project {Invoke-AutomationIdButton $Main 'AddForecastCostButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Fit-out editor readiness failed.' -Condition{Test-ForecastCostEditorReady $Main};Select-ComboBoxExactSemanticItem $Main 'ForecastCostKindInput' 'Capex';Set-AutomationValue $Main 'ForecastCostNameInput' 'Smoke Equipment Forecast';Set-AutomationValue $Main 'ForecastCostAmountInput' '60';Set-AutomationValue $Main 'ForecastCostNoteInput' 'smoke forecast';Invoke-AutomationIdButton $Main 'SaveForecastCostButton';Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Fit-out post-save semantic wait failed.' -Condition{$add=Get-AutomationIdElement $Main 'AddForecastCostButton';(Get-ForecastCostRowState $Main 'Smoke Equipment Forecast' 60 'Capex').Matches-and$null-ne$add-and$add.Current.IsEnabled-and(Test-ForecastCostEditorClosed $Main)}}
    Invoke-ForecastCostsPhase $Main 'create utilities' $project {Invoke-AutomationIdButton $Main 'AddForecastCostButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Rent editor readiness failed.' -Condition{Test-ForecastCostEditorReady $Main};Select-ComboBoxExactSemanticItem $Main 'ForecastCostKindInput' 'Opex';Set-AutomationValue $Main 'ForecastCostNameInput' 'Smoke Utilities Forecast';Set-AutomationValue $Main 'ForecastCostAmountInput' '20';Invoke-AutomationIdButton $Main 'SaveForecastCostButton'}
    Invoke-ForecastCostsPhase $Main 'create verification' $project {Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Forecast Costs create verification failed.' -Condition{(Get-ForecastCostRowState $Main 'Smoke Equipment Forecast' 60 'Capex').Matches-and(Get-ForecastCostRowState $Main 'Smoke Utilities Forecast' 20 'Opex').Matches}};Add-Content $diagnostics 'ForecastCostsCrud: create PASS'
    Invoke-ForecastCostsPhase $Main 'totals' $project {Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Forecast Costs totals failed.' -Condition{(Get-AutomationIdElement $Main 'ForecastCostCapexTotal').Current.Name-match'60'-and(Get-AutomationIdElement $Main 'ForecastCostOpexTotal').Current.Name-match'20'-and(Get-AutomationIdElement $Main 'ForecastCostTotal').Current.Name-match'80'}};Add-Content $diagnostics 'ForecastCostsCrud: totals PASS'
    Invoke-ForecastCostsPhase $Main 'update' $project {$fit=Get-ForecastCostRowState $Main 'Smoke Equipment Forecast' 60 'Capex';Select-ContainingListItem $fit.ListItem;Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Edit Forecast Cost selection did not stabilize.' -Condition{$edit=Get-AutomationIdElement $Main 'EditForecastCostButton';$null-ne$edit-and$edit.Current.IsEnabled};Invoke-AutomationIdButton $Main 'EditForecastCostButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Edit Forecast Cost editor readiness failed.' -Condition{Test-ForecastCostEditorReady $Main};Set-AutomationValue $Main 'ForecastCostAmountInput' '75';Invoke-AutomationIdButton $Main 'SaveForecastCostButton';Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Forecast Costs update failed.' -Condition{(Get-ForecastCostRowState $Main 'Smoke Equipment Forecast' 75 'Capex').Matches-and(Get-AutomationIdElement $Main 'ForecastCostCapexTotal').Current.Name-match'75'-and(Get-AutomationIdElement $Main 'ForecastCostOpexTotal').Current.Name-match'20'-and(Get-AutomationIdElement $Main 'ForecastCostTotal').Current.Name-match'95'}};Add-Content $diagnostics 'ForecastCostsCrud: update PASS'
    Invoke-ForecastCostsPhase $Main 're-entry' $project {Invoke-AutomationIdButton $Main 'CompaniesSectionButton';Invoke-AutomationIdButton $Main 'ForecastCostsSectionButton';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Forecast Costs re-entry selector not ready.' -Condition{$selector=Get-AutomationIdElement $Main 'ForecastCostsProjectSelector';$null-ne$selector-and$selector.Current.IsEnabled};Select-ComboBoxExactSemanticItem $Main 'ForecastCostsProjectSelector' $project;Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Forecast Costs re-entry failed.' -Condition{$r=Get-ForecastCostsReadinessState $Main $project;$r.Semantic.IsExpected-and$r.Currency.Current.Name-eq'PLN'-and(Get-ForecastCostRowState $Main 'Smoke Equipment Forecast' 75 'Capex').Matches-and(Get-ForecastCostRowState $Main 'Smoke Utilities Forecast' 20 'Opex').Matches-and(Get-AutomationIdElement $Main 'ForecastCostCapexTotal').Current.Name-match'75'-and(Get-AutomationIdElement $Main 'ForecastCostOpexTotal').Current.Name-match'20'-and(Get-AutomationIdElement $Main 'ForecastCostTotal').Current.Name-match'95'}};Add-Content $diagnostics 'ForecastCostsCrud: re-entry PASS'
    Invoke-ForecastCostsPhase $Main 'archive dialog' $project {$rent=Get-ForecastCostRowState $Main 'Smoke Utilities Forecast' 20 'Opex';Select-ContainingListItem $rent.ListItem;Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Archive Forecast Cost selection did not stabilize.' -Condition{$archive=Get-AutomationIdElement $Main 'ArchiveForecastCostButton';$null-ne$archive-and$archive.Current.IsEnabled};Invoke-AutomationIdButton $Main 'ArchiveForecastCostButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Forecast Cost archive dialog failed.' -Condition{$null-ne(Get-AutomationIdElement $Main 'ArchiveForecastCostDialog')}}
    Invoke-ForecastCostsPhase $Main 'archive cancel' $project {Invoke-AutomationIdButton (Get-AutomationIdElement $Main 'ArchiveForecastCostDialog') 'CancelArchiveForecastCostButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Forecast Cost archive cancel failed.' -Condition{$null-eq(Get-AutomationIdElement $Main 'ArchiveForecastCostDialog')-and(Get-ForecastCostRowState $Main 'Smoke Utilities Forecast' 20 'Opex').Matches-and(Get-AutomationIdElement $Main 'ArchiveForecastCostButton').Current.IsEnabled-and(Get-AutomationIdElement $Main 'ForecastCostsList').Current.IsEnabled-and(Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled}};Add-Content $diagnostics 'ForecastCostsCrud: archive cancel PASS'
    Invoke-ForecastCostsPhase $Main 'archive confirm' $project {Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Archive Forecast Cost did not restabilize before confirm.' -Condition{$archive=Get-AutomationIdElement $Main 'ArchiveForecastCostButton';$null-ne$archive-and$archive.Current.IsEnabled};Invoke-AutomationIdButton $Main 'ArchiveForecastCostButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Forecast Cost second archive dialog failed.' -Condition{$null-ne(Get-AutomationIdElement $Main 'ArchiveForecastCostDialog')};Invoke-AutomationIdButton (Get-AutomationIdElement $Main 'ArchiveForecastCostDialog') 'ConfirmArchiveForecastCostButton';Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Forecast Cost archive failed.' -Condition{(Get-ForecastCostRowState $Main 'Smoke Utilities Forecast' 20 'Opex').Count-eq0-and(Get-ForecastCostRowState $Main 'Smoke Equipment Forecast' 75 'Capex').Matches-and(Get-AutomationIdElement $Main 'ForecastCostCapexTotal').Current.Name-match'75'-and(Get-AutomationIdElement $Main 'ForecastCostOpexTotal').Current.Name-match'0'-and(Get-AutomationIdElement $Main 'ForecastCostTotal').Current.Name-match'75'}};Add-Content $diagnostics 'ForecastCostsCrud: archive PASS'
    Invoke-AutomationIdButton $Main 'BusinessProjectsSectionButton'
}
function Get-BudgetVarianceReadinessState($Main,[string]$ExpectedProject,[string]$ExpectedBudget,[string]$ExpectedVersion) {
    try {
        $project=Get-AutomationIdElement $Main 'BudgetVarianceProjectSelector';$budget=Get-AutomationIdElement $Main 'BudgetVarianceBudgetSelector';$version=Get-AutomationIdElement $Main 'BudgetVarianceVersionSelector'
        $projectSemantic=if([string]::IsNullOrEmpty($ExpectedProject)){$null}else{Get-ComboBoxSemanticSelection $project $ExpectedProject};$budgetSemantic=if([string]::IsNullOrEmpty($ExpectedBudget)){$null}else{Get-ComboBoxSemanticSelection $budget $ExpectedBudget};$versionSemantic=if([string]::IsNullOrEmpty($ExpectedVersion)){$null}else{Get-ComboBoxSemanticSelection $version $ExpectedVersion}
        [pscustomobject]@{Project=$project;Budget=$budget;Version=$version;ProjectSemantic=$projectSemantic;BudgetSemantic=$budgetSemantic;VersionSemantic=$versionSemantic;ProjectReady=$null-ne$project-and$project.Current.IsEnabled;BudgetReady=$null-ne$budget-and$budget.Current.IsEnabled;VersionReady=$null-ne$version-and$version.Current.IsEnabled}
    } catch { $false }
}
function Get-BudgetVarianceSnapshotState($Main,[hashtable]$Expected) {
    try {
        $values=@{};foreach($id in 'BudgetVarianceCurrency','BudgetVarianceBudgetStatus','BudgetVarianceCapexPlanned','BudgetVarianceCapexActual','BudgetVarianceCapexVariance','BudgetVarianceCapexUtilization','BudgetVarianceCapexState','BudgetVarianceOpexPlanned','BudgetVarianceOpexActual','BudgetVarianceOpexVariance','BudgetVarianceOpexUtilization','BudgetVarianceOpexState','BudgetVarianceTotalPlanned','BudgetVarianceTotalActual','BudgetVarianceTotalVariance','BudgetVarianceTotalUtilization','BudgetVarianceTotalState'){$element=Get-AutomationIdElement $Main $id;if($null-eq$element){return $false};$values[$id]=$element.Current.Name}
        $matches=$true;foreach($key in $Expected.Keys){if($values[$key]-ne$Expected[$key]){$matches=$false}};[pscustomobject]@{Values=$values;Matches=$matches}
    } catch { $false }
}
function Write-BudgetVarianceTimeoutDiagnostics($Main,[string]$Phase,[string]$ExpectedProject,[string]$ExpectedBudget,[string]$ExpectedVersion) {
    try {
        $r=Get-BudgetVarianceReadinessState $Main $ExpectedProject $ExpectedBudget $ExpectedVersion;Add-Content $diagnostics "Budget Variance phase: $Phase; expected project: $ExpectedProject; expected budget: $ExpectedBudget; expected version: $ExpectedVersion";Add-Content $diagnostics "semantic selected project='$($r.ProjectSemantic.SelectedItemNames-join',')'; semantic selected budget='$($r.BudgetSemantic.SelectedItemNames-join',')'; semantic selected version='$($r.VersionSemantic.SelectedItemNames-join',')'"
        foreach($id in 'BudgetVarianceCurrency','BudgetVarianceBudgetStatus','BudgetVarianceCapexPlanned','BudgetVarianceCapexActual','BudgetVarianceCapexVariance','BudgetVarianceCapexUtilization','BudgetVarianceCapexState','BudgetVarianceOpexPlanned','BudgetVarianceOpexActual','BudgetVarianceOpexVariance','BudgetVarianceOpexUtilization','BudgetVarianceOpexState','BudgetVarianceTotalPlanned','BudgetVarianceTotalActual','BudgetVarianceTotalVariance','BudgetVarianceTotalUtilization','BudgetVarianceTotalState','RefreshBudgetVarianceButton','BudgetVarianceProjectSelector','BudgetVarianceBudgetSelector','BudgetVarianceVersionSelector','BudgetVarianceOperationMessage'){Add-Content $diagnostics "${id}: $(Format-AutomationElementState (Get-AutomationIdElement $Main $id))"}
    } catch { Add-Content $diagnostics "Budget Variance diagnostics failure: $($_.Exception.GetType().Name)" } finally { Write-SmokeDiagnosticsToHost }
}
function Invoke-BudgetVariancePhase($Main,[string]$Phase,[string]$Project,[string]$Budget,[string]$Version,[scriptblock]$Action) {try{&$Action}catch{Write-BudgetVarianceTimeoutDiagnostics $Main $Phase $Project $Budget $Version;throw}}
function Invoke-BudgetVarianceSmoke($Main) {
    $project='BusinessOS Gym Smoke Updated';$budget='BusinessOS Budget Smoke Updated';Invoke-AutomationIdButton $Main 'BudgetVarianceSectionButton'
    Invoke-BudgetVariancePhase $Main 'readiness' $project $budget '' {Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Variance project selector not ready.' -Condition{try{(Get-BudgetVarianceReadinessState $Main '' '' '').ProjectReady}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'BudgetVarianceProjectSelector' $project;Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Variance project selection failed.' -Condition{try{$r=Get-BudgetVarianceReadinessState $Main $project '' '';$r.ProjectSemantic.IsExpected-and$r.BudgetReady}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'BudgetVarianceBudgetSelector' $budget;Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Variance budget selection failed.' -Condition{try{$r=Get-BudgetVarianceReadinessState $Main $project $budget '';$r.ProjectSemantic.IsExpected-and$r.BudgetSemantic.IsExpected-and$r.VersionReady}catch{$false}}}
    $v1=@{BudgetVarianceCurrency='PLN';BudgetVarianceBudgetStatus='Archived';BudgetVarianceCapexPlanned='100';BudgetVarianceCapexActual='150';BudgetVarianceCapexVariance='-50';BudgetVarianceCapexUtilization='150%';BudgetVarianceCapexState='Powyżej budżetu';BudgetVarianceOpexPlanned='0';BudgetVarianceOpexActual='0';BudgetVarianceOpexVariance='0';BudgetVarianceOpexUtilization='—';BudgetVarianceOpexState='Zgodnie z budżetem';BudgetVarianceTotalPlanned='100';BudgetVarianceTotalActual='150';BudgetVarianceTotalVariance='-50';BudgetVarianceTotalUtilization='150%';BudgetVarianceTotalState='Powyżej budżetu'}
    Invoke-BudgetVariancePhase $Main 'version 1' $project $budget 'Version 1' {Select-ComboBoxExactSemanticItem $Main 'BudgetVarianceVersionSelector' 'Version 1';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Variance Version 1 snapshot failed.' -Condition{try{$r=Get-BudgetVarianceReadinessState $Main $project $budget 'Version 1';$s=Get-BudgetVarianceSnapshotState $Main $v1;$r.VersionSemantic.IsExpected-and$s.Matches}catch{$false}}};Add-Content $diagnostics 'BudgetVariance: version 1 PASS'
    $v2=@{};foreach($key in $v1.Keys){$v2[$key]=$v1[$key]};$v2.BudgetVarianceCapexPlanned='150';$v2.BudgetVarianceCapexVariance='0';$v2.BudgetVarianceCapexUtilization='100%';$v2.BudgetVarianceCapexState='Zgodnie z budżetem';$v2.BudgetVarianceTotalPlanned='150';$v2.BudgetVarianceTotalVariance='0';$v2.BudgetVarianceTotalUtilization='100%';$v2.BudgetVarianceTotalState='Zgodnie z budżetem'
    Invoke-BudgetVariancePhase $Main 'version 2' $project $budget 'Version 2' {Select-ComboBoxExactSemanticItem $Main 'BudgetVarianceVersionSelector' 'Version 2';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Variance Version 2 snapshot failed.' -Condition{try{$r=Get-BudgetVarianceReadinessState $Main $project $budget 'Version 2';$s=Get-BudgetVarianceSnapshotState $Main $v2;$r.VersionSemantic.IsExpected-and$s.Matches}catch{$false}}};Add-Content $diagnostics 'BudgetVariance: version 2 PASS'
    Invoke-BudgetVariancePhase $Main 're-entry' $project $budget 'Version 2' {Invoke-AutomationIdButton $Main 'CompaniesSectionButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Variance navigation did not restabilize.' -Condition{try{$button=Get-AutomationIdElement $Main 'BudgetVarianceSectionButton';$null-ne$button-and$button.Current.IsEnabled}catch{$false}};Invoke-AutomationIdButton $Main 'BudgetVarianceSectionButton';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Variance re-entry project readiness failed.' -Condition{try{(Get-BudgetVarianceReadinessState $Main '' '' '').ProjectReady}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'BudgetVarianceProjectSelector' $project;Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Variance re-entry budget readiness failed.' -Condition{try{$r=Get-BudgetVarianceReadinessState $Main $project '' '';$r.ProjectSemantic.IsExpected-and$r.BudgetReady}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'BudgetVarianceBudgetSelector' $budget;Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Variance re-entry version readiness failed.' -Condition{try{$r=Get-BudgetVarianceReadinessState $Main $project $budget '';$r.BudgetSemantic.IsExpected-and$r.VersionReady}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'BudgetVarianceVersionSelector' 'Version 2';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Variance re-entry snapshot failed.' -Condition{try{$r=Get-BudgetVarianceReadinessState $Main $project $budget 'Version 2';$s=Get-BudgetVarianceSnapshotState $Main $v2;$r.VersionSemantic.IsExpected-and$s.Matches}catch{$false}}};Add-Content $diagnostics 'BudgetVariance: re-entry PASS';Invoke-AutomationIdButton $Main 'BusinessProjectsSectionButton'
}
function Get-BudgetForecastReadinessState($Main,[string]$ExpectedProject,[string]$ExpectedBudget,[string]$ExpectedVersion) { try {$project=Get-AutomationIdElement $Main 'BudgetForecastProjectSelector';$budget=Get-AutomationIdElement $Main 'BudgetForecastBudgetSelector';$version=Get-AutomationIdElement $Main 'BudgetForecastVersionSelector';[pscustomobject]@{Project=$project;Budget=$budget;Version=$version;ProjectSemantic=Get-ComboBoxSemanticSelection $project $ExpectedProject;BudgetSemantic=Get-ComboBoxSemanticSelection $budget $ExpectedBudget;VersionSemantic=Get-ComboBoxSemanticSelection $version $ExpectedVersion;ProjectReady=$null-ne$project-and$project.Current.IsEnabled;BudgetReady=$null-ne$budget-and$budget.Current.IsEnabled;VersionReady=$null-ne$version-and$version.Current.IsEnabled}}catch{$false}}
function Get-BudgetForecastMetricState($Main,[string]$Prefix) {try{$v=@{};foreach($suffix in 'Planned','Actual','Etc','Eac','Vac','Utilization','State'){$v[$suffix]=(Get-AutomationIdElement $Main "BudgetForecast${Prefix}${suffix}").Current.Name};[pscustomobject]$v}catch{$false}}
function Get-BudgetForecastSnapshotState($Main,[hashtable]$Expected) {try{$values=@{};foreach($id in 'BudgetForecastCurrency','BudgetForecastBudgetStatus'){$values[$id]=(Get-AutomationIdElement $Main $id).Current.Name};foreach($prefix in 'Capex','Opex','Total'){$metric=Get-BudgetForecastMetricState $Main $prefix;foreach($suffix in 'Planned','Actual','Etc','Eac','Vac','Utilization','State'){$values["${prefix}${suffix}"]=$metric.$suffix}};$matches=$true;foreach($key in $Expected.Keys){if($values[$key]-ne$Expected[$key]){$matches=$false}};[pscustomobject]@{Values=$values;Matches=$matches}}catch{$false}}
function Write-BudgetForecastTimeoutDiagnostics($Main,[string]$Phase,[string]$ExpectedProject,[string]$ExpectedBudget,[string]$ExpectedVersion) {try{Add-Content $diagnostics "Budget Forecast phase: $Phase; expected project: $ExpectedProject; expected budget: $ExpectedBudget; expected version: $ExpectedVersion";$r=Get-BudgetForecastReadinessState $Main $ExpectedProject $ExpectedBudget $ExpectedVersion;Add-Content $diagnostics "semantic project=$($r.ProjectSemantic.SelectedItemNames-join','); budget=$($r.BudgetSemantic.SelectedItemNames-join','); version=$($r.VersionSemantic.SelectedItemNames-join',')";foreach($id in 'BudgetForecastProjectSelector','BudgetForecastBudgetSelector','BudgetForecastVersionSelector','BudgetForecastCurrency','BudgetForecastBudgetStatus','BudgetForecastCapexPlanned','BudgetForecastCapexActual','BudgetForecastCapexEtc','BudgetForecastCapexEac','BudgetForecastCapexVac','BudgetForecastCapexUtilization','BudgetForecastCapexState','BudgetForecastOpexPlanned','BudgetForecastOpexActual','BudgetForecastOpexEtc','BudgetForecastOpexEac','BudgetForecastOpexVac','BudgetForecastOpexUtilization','BudgetForecastOpexState','BudgetForecastTotalPlanned','BudgetForecastTotalActual','BudgetForecastTotalEtc','BudgetForecastTotalEac','BudgetForecastTotalVac','BudgetForecastTotalUtilization','BudgetForecastTotalState','RefreshBudgetForecastButton','BudgetForecastOperationMessage'){Add-Content $diagnostics "${id}: $(Format-AutomationElementState (Get-AutomationIdElement $Main $id))"}}catch{Add-Content $diagnostics "Budget Forecast diagnostics failure: $($_.Exception.GetType().Name)"}finally{Write-SmokeDiagnosticsToHost}}
function Invoke-BudgetForecastPhase($Main,[string]$Phase,[string]$Project,[string]$Budget,[string]$Version,[scriptblock]$Action) {try{&$Action}catch{Write-BudgetForecastTimeoutDiagnostics $Main $Phase $Project $Budget $Version;throw}}
function Invoke-BudgetForecastSmoke($Main) {
 $project='BusinessOS Gym Smoke Updated';$budget='BusinessOS Budget Smoke Updated';$v1=@{BudgetForecastCurrency='PLN';BudgetForecastBudgetStatus='Archived';CapexPlanned='100';CapexActual='150';CapexEtc='75';CapexEac='225';CapexVac='-125';CapexUtilization='225%';CapexState='Powyżej budżetu';OpexPlanned='0';OpexActual='0';OpexEtc='0';OpexEac='0';OpexVac='0';OpexUtilization='—';OpexState='Zgodnie z budżetem';TotalPlanned='100';TotalActual='150';TotalEtc='75';TotalEac='225';TotalVac='-125';TotalUtilization='225%';TotalState='Powyżej budżetu'};$v2=$v1.Clone();$v2.CapexPlanned='150';$v2.CapexVac='-75';$v2.CapexUtilization='150%';$v2.TotalPlanned='150';$v2.TotalVac='-75';$v2.TotalUtilization='150%'
 Invoke-AutomationIdButton $Main 'BudgetForecastSectionButton';Invoke-BudgetForecastPhase $Main 'readiness' $project $budget '' {Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Forecast project selector not ready.' -Condition{try{(Get-BudgetForecastReadinessState $Main '' '' '').ProjectReady}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'BudgetForecastProjectSelector' $project;Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Forecast budget selector not ready.' -Condition{try{$r=Get-BudgetForecastReadinessState $Main $project '' '';$r.ProjectSemantic.IsExpected-and$r.BudgetReady}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'BudgetForecastBudgetSelector' $budget;Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Forecast version selector not ready.' -Condition{try{$r=Get-BudgetForecastReadinessState $Main $project $budget '';$r.BudgetSemantic.IsExpected-and$r.VersionReady}catch{$false}}}
 Invoke-BudgetForecastPhase $Main 'version 1' $project $budget 'Version 1' {Select-ComboBoxExactSemanticItem $Main 'BudgetForecastVersionSelector' 'Version 1';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Forecast V1 snapshot failed.' -Condition{try{$r=Get-BudgetForecastReadinessState $Main $project $budget 'Version 1';$snapshot=Get-BudgetForecastSnapshotState $Main $v1;$r.VersionSemantic.IsExpected-and$snapshot.Matches}catch{$false}}};Add-Content $diagnostics 'BudgetForecast: version 1 PASS'
 Invoke-BudgetForecastPhase $Main 'version 2' $project $budget 'Version 2' {Select-ComboBoxExactSemanticItem $Main 'BudgetForecastVersionSelector' 'Version 2';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Forecast V2 snapshot failed.' -Condition{try{$r=Get-BudgetForecastReadinessState $Main $project $budget 'Version 2';$snapshot=Get-BudgetForecastSnapshotState $Main $v2;$r.VersionSemantic.IsExpected-and$snapshot.Matches}catch{$false}}};Add-Content $diagnostics 'BudgetForecast: version 2 PASS'
 Invoke-BudgetForecastPhase $Main 're-entry' $project $budget 'Version 2' {Invoke-AutomationIdButton $Main 'CompaniesSectionButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Forecast navigation did not restabilize.' -Condition{try{$button=Get-AutomationIdElement $Main 'BudgetForecastSectionButton';$null-ne$button-and$button.Current.IsEnabled}catch{$false}};Invoke-AutomationIdButton $Main 'BudgetForecastSectionButton';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Forecast re-entry project readiness failed.' -Condition{try{(Get-BudgetForecastReadinessState $Main '' '' '').ProjectReady}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'BudgetForecastProjectSelector' $project;Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Forecast re-entry budget readiness failed.' -Condition{try{$r=Get-BudgetForecastReadinessState $Main $project '' '';$r.ProjectSemantic.IsExpected-and$r.BudgetReady}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'BudgetForecastBudgetSelector' $budget;Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Forecast re-entry version readiness failed.' -Condition{try{$r=Get-BudgetForecastReadinessState $Main $project $budget '';$r.ProjectSemantic.IsExpected-and$r.BudgetSemantic.IsExpected-and$r.VersionReady}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'BudgetForecastVersionSelector' 'Version 2';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Budget Forecast re-entry snapshot failed.' -Condition{try{$r=Get-BudgetForecastReadinessState $Main $project $budget 'Version 2';$snapshot=Get-BudgetForecastSnapshotState $Main $v2;$r.VersionSemantic.IsExpected-and$snapshot.Matches}catch{$false}}};Add-Content $diagnostics 'BudgetForecast: re-entry PASS';Invoke-AutomationIdButton $Main 'BusinessProjectsSectionButton'
}
function Get-CostCashFlowReadinessState($Main,[string]$ExpectedProject) {try{$selector=Get-AutomationIdElement $Main 'CostCashFlowProjectSelector';[pscustomobject]@{Selector=$selector;ProjectSemantic=Get-ComboBoxSemanticSelection $selector $ExpectedProject;Ready=$null-ne$selector-and$selector.Current.IsEnabled}}catch{$false}}
function Get-CostCashFlowMonthRows($Main) {try{$list=Get-AutomationIdElement $Main 'CostCashFlowList';$byRuntimeId=@{};$condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem);foreach($candidate in @($list.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition))){$item=Get-ContainingListItem $candidate;if($null-ne$item){$runtimeId=@($item.GetRuntimeId())-join'.';$name=$item.Current.Name;if($name-match'^(?<Month>\d{4}-\d{2}) \| CAPEX A=(?<CA>-?\d+(?:\.\d+)?) F=(?<CF>-?\d+(?:\.\d+)?) E=(?<CE>-?\d+(?:\.\d+)?) \| OPEX A=(?<OA>-?\d+(?:\.\d+)?) F=(?<OF>-?\d+(?:\.\d+)?) E=(?<OE>-?\d+(?:\.\d+)?) \| TOTAL A=(?<TA>-?\d+(?:\.\d+)?) F=(?<TF>-?\d+(?:\.\d+)?) E=(?<TE>-?\d+(?:\.\d+)?)$'){$byRuntimeId[$runtimeId]=[pscustomobject]@{SemanticName=$name;RuntimeId=$runtimeId;Month=$Matches.Month;CA=[decimal]$Matches.CA;CF=[decimal]$Matches.CF;CE=[decimal]$Matches.CE;OA=[decimal]$Matches.OA;OF=[decimal]$Matches.OF;OE=[decimal]$Matches.OE;TA=[decimal]$Matches.TA;TF=[decimal]$Matches.TF;TE=[decimal]$Matches.TE}}}};@($byRuntimeId.Values|Sort-Object Month)}catch{$false}}
function Get-CostCashFlowSnapshotState($Main,[string]$ExpectedProject) {try{$rows=@(Get-CostCashFlowMonthRows $Main);$months=@($rows|ForEach-Object{$_.Month});$sorted=@($months|Sort-Object -Unique);$summary=@{};foreach($id in 'CostCashFlowProjectCurrency','CostCashFlowCapexActualTotal','CostCashFlowCapexForecastTotal','CostCashFlowCapexExpectedTotal','CostCashFlowOpexActualTotal','CostCashFlowOpexForecastTotal','CostCashFlowOpexExpectedTotal','CostCashFlowActualTotal','CostCashFlowForecastTotal','CostCashFlowExpectedTotal'){$summary[$id]=(Get-AutomationIdElement $Main $id).Current.Name};$semantic=(Get-CostCashFlowReadinessState $Main $ExpectedProject).ProjectSemantic;$valid=$semantic.IsExpected-and$summary.CostCashFlowProjectCurrency-eq'PLN'-and$summary.CostCashFlowCapexActualTotal-eq'150'-and$summary.CostCashFlowCapexForecastTotal-eq'75'-and$summary.CostCashFlowCapexExpectedTotal-eq'225'-and$summary.CostCashFlowOpexActualTotal-eq'0'-and$summary.CostCashFlowOpexForecastTotal-eq'0'-and$summary.CostCashFlowOpexExpectedTotal-eq'0'-and$summary.CostCashFlowActualTotal-eq'150'-and$summary.CostCashFlowForecastTotal-eq'75'-and$summary.CostCashFlowExpectedTotal-eq'225'-and$rows.Count-ge1-and$months.Count-eq$sorted.Count-and(@($months)-join',')-eq(@($sorted)-join',')-and($rows|Measure-Object CA -Sum).Sum-eq150-and($rows|Measure-Object CF -Sum).Sum-eq75-and($rows|Measure-Object CE -Sum).Sum-eq225-and($rows|Measure-Object OA -Sum).Sum-eq0-and($rows|Measure-Object OF -Sum).Sum-eq0-and($rows|Measure-Object OE -Sum).Sum-eq0-and($rows|Measure-Object TA -Sum).Sum-eq150-and($rows|Measure-Object TF -Sum).Sum-eq75-and($rows|Measure-Object TE -Sum).Sum-eq225-and@($rows|Where-Object CA -eq 150).Count-eq1-and@($rows|Where-Object CF -eq 75).Count-eq1;[pscustomobject]@{Matches=$valid;Rows=$rows;Summary=$summary;Semantic=$semantic}}catch{$false}}
function Write-CostCashFlowTimeoutDiagnostics($Main,[string]$Phase,[string]$ExpectedProject) {try{Add-Content $diagnostics "Cost Cash Flow phase=$Phase expected project=$ExpectedProject";$state=Get-CostCashFlowSnapshotState $Main $ExpectedProject;Add-Content $diagnostics "semantic selected project=$($state.Semantic.SelectedItemNames-join',') logical month count=$($state.Rows.Count)";foreach($id in 'CostCashFlowProjectSelector','CostCashFlowProjectCurrency','CostCashFlowCapexActualTotal','CostCashFlowCapexForecastTotal','CostCashFlowCapexExpectedTotal','CostCashFlowOpexActualTotal','CostCashFlowOpexForecastTotal','CostCashFlowOpexExpectedTotal','CostCashFlowActualTotal','CostCashFlowForecastTotal','CostCashFlowExpectedTotal','RefreshCostCashFlowButton','CostCashFlowOperationMessage'){Add-Content $diagnostics "${id}: $(Format-AutomationElementState (Get-AutomationIdElement $Main $id))"};foreach($row in @($state.Rows)){Add-Content $diagnostics "SemanticName=$($row.SemanticName) RuntimeId=$($row.RuntimeId) Month=$($row.Month) CAPEX=$($row.CA)/$($row.CF)/$($row.CE) OPEX=$($row.OA)/$($row.OF)/$($row.OE) TOTAL=$($row.TA)/$($row.TF)/$($row.TE)"}}catch{Add-Content $diagnostics "Cost Cash Flow diagnostics failure: $($_.Exception.GetType().Name)"}finally{Write-SmokeDiagnosticsToHost}}
function Invoke-CostCashFlowPhase($Main,[string]$Phase,[string]$Project,[scriptblock]$Action) {try{&$Action}catch{Write-CostCashFlowTimeoutDiagnostics $Main $Phase $Project;throw}}
function Invoke-CostCashFlowSmoke($Main) {$project='BusinessOS Gym Smoke Updated';Invoke-AutomationIdButton $Main 'CostCashFlowSectionButton';Invoke-CostCashFlowPhase $Main 'readiness' $project {Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Cost Cash Flow project selector not ready.' -Condition{try{(Get-CostCashFlowReadinessState $Main '').Ready}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'CostCashFlowProjectSelector' $project;Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Cost Cash Flow snapshot failed.' -Condition{try{(Get-CostCashFlowSnapshotState $Main $project).Matches}catch{$false}}};Add-Content $diagnostics 'CostCashFlow: snapshot PASS';Invoke-CostCashFlowPhase $Main 're-entry' $project {Invoke-AutomationIdButton $Main 'CompaniesSectionButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Cost Cash Flow navigation did not restabilize.' -Condition{try{$button=Get-AutomationIdElement $Main 'CostCashFlowSectionButton';$null-ne$button-and$button.Current.IsEnabled}catch{$false}};Invoke-AutomationIdButton $Main 'CostCashFlowSectionButton';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Cost Cash Flow re-entry project readiness failed.' -Condition{try{(Get-CostCashFlowReadinessState $Main '').Ready}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'CostCashFlowProjectSelector' $project;Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Cost Cash Flow re-entry snapshot failed.' -Condition{try{(Get-CostCashFlowSnapshotState $Main $project).Matches}catch{$false}}};Add-Content $diagnostics 'CostCashFlow: re-entry PASS';Invoke-AutomationIdButton $Main 'BusinessProjectsSectionButton'}
function Get-SupplierInvoiceRowState($Main,[string]$Supplier,[string]$Invoice,[decimal]$Amount,[string]$Currency,[string]$InvoiceDate,[string]$DueDate) {
    try {
        $list=Get-AutomationIdElement $Main 'SupplierInvoicesList';$byRuntimeId=@{};$expected="Supplier=$Supplier | Invoice=$Invoice | Amount=$($Amount.ToString('0.##',[Globalization.CultureInfo]::InvariantCulture)) $Currency | InvoiceDate=$InvoiceDate | DueDate=$DueDate"
        if($null-ne$list){foreach($candidate in @(Get-NamedElements $list $expected)){$item=Get-ContainingListItem $candidate;if($null-ne$item){$runtimeId=@($item.GetRuntimeId())-join'.';$name=$item.Current.Name;if($name-match'^Supplier=(?<Supplier>.+) \| Invoice=(?<Invoice>.+) \| Amount=(?<Amount>-?\d+(?:\.\d+)?) (?<Currency>[A-Z]{3}) \| InvoiceDate=(?<InvoiceDate>\d{4}-\d{2}-\d{2}) \| DueDate=(?<DueDate>\d{4}-\d{2}-\d{2})$'){$byRuntimeId[$runtimeId]=[pscustomobject]@{ListItem=$item;SemanticName=$name;RuntimeId=$runtimeId;Supplier=$Matches.Supplier;Invoice=$Matches.Invoice;Amount=[decimal]$Matches.Amount;Currency=$Matches.Currency;InvoiceDate=$Matches.InvoiceDate;DueDate=$Matches.DueDate}}}}}
        $rows=@($byRuntimeId.Values);[pscustomobject]@{Count=$rows.Count;Rows=$rows;ListItem=if($rows.Count-eq1){foreach($row in $rows){$row.ListItem}}else{$null};Matches=$rows.Count-eq1}
    } catch {$false}
}
function Get-SupplierInvoicesReadinessState($Main,[string]$ExpectedProject) {
    try {$selector=Get-AutomationIdElement $Main 'SupplierInvoicesProjectSelector';$list=Get-AutomationIdElement $Main 'SupplierInvoicesList';$add=Get-AutomationIdElement $Main 'AddSupplierInvoiceButton';$currency=Get-AutomationIdElement $Main 'SupplierInvoicesProjectCurrency';$semantic=Get-ComboBoxSemanticSelection $selector $ExpectedProject;$logical=@();if($null-ne$list){$condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem);$logical=@($list.FindAll([System.Windows.Automation.TreeScope]::Children,$condition))};$logicalRowNames=@($logical|ForEach-Object{$_.Current.Name});[pscustomobject]@{Selector=$selector;Semantic=$semantic;Currency=$currency;List=$list;Add=$add;LogicalRows=$logical;LogicalRowNames=$logicalRowNames;Ready=$semantic.IsExpected-and$null-ne$currency-and$currency.Current.Name-eq'PLN'-and$null-ne$list-and$logical.Count-eq0-and$null-ne$add-and$add.Current.IsEnabled}}catch{$false}
}
function Test-SupplierInvoiceEditorReady($Main) {
    try {$supplier=Get-AutomationIdElement $Main 'SupplierInvoiceSupplierInput';$number=Get-AutomationIdElement $Main 'SupplierInvoiceNumberInput';$amount=Get-AutomationIdElement $Main 'SupplierInvoiceAmountInput';$invoiceDate=Get-AutomationIdElement $Main 'SupplierInvoiceInvoiceDateInput';$dueDate=Get-AutomationIdElement $Main 'SupplierInvoiceDueDateInput';$invoiceInvoke=$null;$dueInvoke=$null;$save=Get-AutomationIdElement $Main 'SaveSupplierInvoiceButton';$cancel=Get-AutomationIdElement $Main 'CancelSupplierInvoiceButton';(Test-AutomationValueInputReady $supplier)-and(Test-AutomationValueInputReady $number)-and(Test-AutomationValueInputReady $amount)-and$null-ne$invoiceDate-and$invoiceDate.Current.IsEnabled-and$invoiceDate.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$invoiceInvoke)-and$null-ne$dueDate-and$dueDate.Current.IsEnabled-and$dueDate.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$dueInvoke)-and$null-ne$save-and$save.Current.IsEnabled-and$null-ne$cancel-and$cancel.Current.IsEnabled}catch{$false}
}
function Test-SupplierInvoiceEditorClosed($Main) {try{$save=Get-AutomationIdElement $Main 'SaveSupplierInvoiceButton';$cancel=Get-AutomationIdElement $Main 'CancelSupplierInvoiceButton';$list=Get-AutomationIdElement $Main 'SupplierInvoicesList';($null-eq$save-or-not$save.Current.IsEnabled)-and($null-eq$cancel-or-not$cancel.Current.IsEnabled)-and$null-ne$list-and$list.Current.IsEnabled}catch{$false}}
function Write-SupplierInvoicesTimeoutDiagnostics($Main,[string]$Phase,[string]$ExpectedProject) {
    try {
        Add-Content $diagnostics "Supplier Invoices phase=$Phase expected project=$ExpectedProject"
        $selector=Get-AutomationIdElement $Main 'SupplierInvoicesProjectSelector';$semantic=Get-ComboBoxSemanticSelection $selector $ExpectedProject
        Add-Content $diagnostics "semantic selected project=$($semantic.SelectedItemNames-join',')"
        foreach($id in 'SupplierInvoicesProjectCurrency','SupplierInvoicesTotal','AddSupplierInvoiceButton','EditSupplierInvoiceButton','ArchiveSupplierInvoiceButton','SupplierInvoiceEditorPanel','SupplierInvoiceSupplierInput','SupplierInvoiceNumberInput','SupplierInvoiceAmountInput','SupplierInvoiceInvoiceDateInput','SupplierInvoiceDueDateInput','SupplierInvoiceNoteInput','ArchiveSupplierInvoiceDialog','CancelArchiveSupplierInvoiceButton','ConfirmArchiveSupplierInvoiceButton','PostSupplierInvoiceButton','PostSupplierInvoiceDialog','PostSupplierInvoiceSourceSupplier','PostSupplierInvoiceSourceNumber','PostSupplierInvoiceSourceAmount','PostSupplierInvoiceSourceInvoiceDate','PostSupplierInvoiceKindSelector','CancelPostSupplierInvoiceButton','ConfirmPostSupplierInvoiceButton','ActualCostsProjectSelector','ActualCostsList','ActualCostCapexTotal','ActualCostOpexTotal','ActualCostTotal','SupplierInvoicesOperationMessage'){Add-Content $diagnostics "${id}: $(Format-AutomationElementState (Get-AutomationIdElement $Main $id))"}
        foreach ($id in 'SupplierInvoiceInvoiceDateInput','SupplierInvoiceDueDateInput') {
            $picker=Get-AutomationIdElement $Main $id;$valuePattern=$null;$invokePattern=$null
            if($null-eq$picker){Add-Content $diagnostics "CalendarDatePicker AutomationId=$id not found";continue}
            $hasValue=$picker.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern,[ref]$valuePattern);$hasInvoke=$picker.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$invokePattern)
            $readOnly=if($hasValue){$valuePattern.Current.IsReadOnly}else{$null};$value=if($hasValue){$valuePattern.Current.Value}else{$null}
            Add-Content $diagnostics "CalendarDatePicker AutomationId=$id ControlType=$($picker.Current.ControlType.ProgrammaticName) IsEnabled=$($picker.Current.IsEnabled) ValuePattern=$hasValue ValuePattern.IsReadOnly=$readOnly Value='$value' InvokePattern=$hasInvoke"
        }
        $calendarRoot=[System.Windows.Automation.AutomationElement]::RootElement;$monthCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'MonthViewScrollViewer');$calendarViews=@($calendarRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants,$monthCondition));Add-Content $diagnostics "calendar flyout found=$($calendarViews.Count-gt0) logical month view count=$($calendarViews.Count)"
        foreach($view in $calendarViews){$calendar=$view;$header=$null;$walker=[System.Windows.Automation.TreeWalker]::ControlViewWalker;while($null-ne$calendar-and$null-eq$header){$header=Get-AutomationIdElement $calendar 'HeaderButton';$calendar=$walker.GetParent($calendar)};Add-Content $diagnostics "HeaderButton name='$($header.Current.Name)' active calendar view=month"}
        $list=Get-AutomationIdElement $Main 'SupplierInvoicesList';$rows=@();if($null-ne$list){$condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem);$byRuntime=@{};foreach($candidate in @($list.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition))){$item=Get-ContainingListItem $candidate;if($null-ne$item){$runtime=@($item.GetRuntimeId())-join'.';$byRuntime[$runtime]=$item}};$rows=@($byRuntime.GetEnumerator())};Add-Content $diagnostics "logical invoice row count=$($rows.Count)";foreach($entry in $rows){$name=$entry.Value.Current.Name;Add-Content $diagnostics "SemanticName=$name RuntimeId=$($entry.Key)"}
    }catch{Add-Content $diagnostics "Supplier Invoices diagnostics failure: $($_.Exception.GetType().Name)"}finally{Write-SmokeDiagnosticsToHost}
}
function Invoke-SupplierInvoicesPhase($Main,[string]$Phase,[string]$Project,[scriptblock]$Action) {try{&$Action}catch{Write-SupplierInvoicesTimeoutDiagnostics $Main $Phase $Project;throw}}
function Get-SupplierInvoicePostingPostState($Main,[string]$ExpectedProject,[string]$ExpectedInvoiceRow) {
    try {
        $row=Get-SupplierInvoiceRowState $Main 'Smoke Equipment Vendor' 'SMOKE-INV-001' 135 'PLN' '2026-01-10' '2026-02-10';$readiness=Get-SupplierInvoicesReadinessState $Main $ExpectedProject;$exactInvoiceRow=$row.ListItem
        $postedStatus=if($null-ne$exactInvoiceRow){Get-NamedElement $exactInvoiceRow 'Zaksięgowana'}else{$null};$post=Get-AutomationIdElement $Main 'PostSupplierInvoiceButton';$edit=Get-AutomationIdElement $Main 'EditSupplierInvoiceButton';$archive=Get-AutomationIdElement $Main 'ArchiveSupplierInvoiceButton';$total=Get-AutomationIdElement $Main 'SupplierInvoicesTotal';$message=Get-AutomationIdElement $Main 'SupplierInvoicesOperationMessage';$companies=Get-AutomationIdElement $Main 'CompaniesSectionButton';$recovery=Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton';$dialog=Get-AutomationIdElement $Main 'PostSupplierInvoiceDialog'
        $postEnabled=$null-ne$post-and$post.Current.IsEnabled;$editEnabled=$null-ne$edit-and$edit.Current.IsEnabled;$archiveEnabled=$null-ne$archive-and$archive.Current.IsEnabled;$companiesEnabled=$null-ne$companies-and$companies.Current.IsEnabled;$recoveryEnabled=$null-ne$recovery-and$recovery.Current.IsEnabled;$totalName=if($null-ne$total){$total.Current.Name}else{$null};$operationMessageName=if($null-ne$message){$message.Current.Name}else{$null}
        $matches=$null-ne$exactInvoiceRow-and$readiness.LogicalRows.Count-eq1-and$readiness.LogicalRowNames.Count-eq1-and($readiness.LogicalRowNames-join'')-ceq$ExpectedInvoiceRow-and$null-ne$postedStatus-and$null-ne$post-and-not$postEnabled-and$null-ne$edit-and-not$editEnabled-and$null-ne$archive-and-not$archiveEnabled-and$totalName-ceq'Total: 135'-and$operationMessageName-ceq'Faktura została zaksięgowana jako koszt.'-and$companiesEnabled-and$recoveryEnabled
        [pscustomobject]@{Matches=$matches;ExactInvoiceRow=$exactInvoiceRow;LogicalRowCount=$readiness.LogicalRows.Count;LogicalRowNames=@($readiness.LogicalRowNames);PostedStatusFound=$null-ne$postedStatus;PostButtonFound=$null-ne$post;PostButtonEnabled=$postEnabled;EditButtonFound=$null-ne$edit;EditButtonEnabled=$editEnabled;ArchiveButtonFound=$null-ne$archive;ArchiveButtonEnabled=$archiveEnabled;TotalFound=$null-ne$total;TotalName=$totalName;OperationMessageFound=$null-ne$message;OperationMessageName=$operationMessageName;CompaniesButtonFound=$null-ne$companies;CompaniesButtonEnabled=$companiesEnabled;RecoveryButtonFound=$null-ne$recovery;RecoveryButtonEnabled=$recoveryEnabled;DialogMarkerFound=$null-ne$dialog;DialogMarkerName=if($null-ne$dialog){$dialog.Current.Name}else{$null};DialogMarkerIsOffscreen=if($null-ne$dialog){$dialog.Current.IsOffscreen}else{$null}}
    } catch {[pscustomobject]@{Matches=$false;TransientException=$_.Exception.GetType().Name}}
}
function Write-SupplierInvoicePostingTimeoutDiagnostics($Main,[string]$Phase,[string]$ExpectedProject,[string]$ExpectedInvoiceRow) {
    try {
        Add-Content $diagnostics "Supplier Invoice posting phase=$Phase expected project=$ExpectedProject expected semantic invoice row=$ExpectedInvoiceRow";$state=Get-SupplierInvoicePostingPostState $Main $ExpectedProject $ExpectedInvoiceRow
        Add-Content $diagnostics "SupplierInvoicesOperationMessage: Found=$($state.OperationMessageFound) Name='$($state.OperationMessageName)'";Add-Content $diagnostics "PostSupplierInvoiceDialog: Found=$($state.DialogMarkerFound) Name='$($state.DialogMarkerName)' IsOffscreen=$($state.DialogMarkerIsOffscreen) diagnostic only";Add-Content $diagnostics "logical invoice row count=$($state.LogicalRowCount)";foreach($name in @($state.LogicalRowNames)){Add-Content $diagnostics "logical SemanticName=$name"}
        $runtimeId=if($null-ne$state.ExactInvoiceRow){@($state.ExactInvoiceRow.GetRuntimeId())-join'.'}else{$null};Add-Content $diagnostics "exact invoice row: Found=$($null-ne$state.ExactInvoiceRow) RuntimeId=$runtimeId";if($null-ne$state.ExactInvoiceRow){$trueCondition=[System.Windows.Automation.Condition]::TrueCondition;foreach($descendant in @($state.ExactInvoiceRow.FindAll([System.Windows.Automation.TreeScope]::Descendants,$trueCondition))){if(-not[string]::IsNullOrEmpty($descendant.Current.Name)){Add-Content $diagnostics "exact invoice descendant Name='$($descendant.Current.Name)'"}}};Add-Content $diagnostics "exact descendant Zaksięgowana found=$($state.PostedStatusFound)"
        Add-Content $diagnostics "PostSupplierInvoiceButton: Found=$($state.PostButtonFound) IsEnabled=$($state.PostButtonEnabled)";Add-Content $diagnostics "EditSupplierInvoiceButton: Found=$($state.EditButtonFound) IsEnabled=$($state.EditButtonEnabled)";Add-Content $diagnostics "ArchiveSupplierInvoiceButton: Found=$($state.ArchiveButtonFound) IsEnabled=$($state.ArchiveButtonEnabled)";Add-Content $diagnostics "SupplierInvoicesTotal: Found=$($state.TotalFound) Name='$($state.TotalName)'";Add-Content $diagnostics "CompaniesSectionButton: Found=$($state.CompaniesButtonFound) IsEnabled=$($state.CompaniesButtonEnabled)";Add-Content $diagnostics "OpenRecoveryFromMainButton: Found=$($state.RecoveryButtonFound) IsEnabled=$($state.RecoveryButtonEnabled)"
        foreach($id in 'ActualCostsProjectSelector','ActualCostsList','ActualCostCapexTotal','ActualCostOpexTotal','ActualCostTotal'){Add-Content $diagnostics "${id}: $(Format-AutomationElementState (Get-AutomationIdElement $Main $id))"};$actualList=Get-AutomationIdElement $Main 'ActualCostsList';if($null-ne$actualList){$condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem);foreach($actualRow in @($actualList.FindAll([System.Windows.Automation.TreeScope]::Children,$condition))){Add-Content $diagnostics "Actual Cost SemanticName=$($actualRow.Current.Name) RuntimeId=$(@($actualRow.GetRuntimeId())-join'.')"}}
    } catch {Add-Content $diagnostics "Supplier Invoice posting diagnostics failure: $($_.Exception.GetType().Name)"} finally {Write-SmokeDiagnosticsToHost}
}
function Invoke-SupplierInvoicePostingPhase($Main,[string]$Phase,[string]$Project,[string]$ExpectedInvoiceRow,[scriptblock]$Action) {try{&$Action}catch{Write-SupplierInvoicePostingTimeoutDiagnostics $Main $Phase $Project $ExpectedInvoiceRow;throw}}
function Invoke-SupplierInvoicesCrudSmoke($Main) {
    $project='BusinessOS Gym Smoke Updated';$orderedSeparator=' <ordered> ';$initialOrderedRows='Supplier=Smoke Utilities Vendor | Invoice=SMOKE-UTIL-001 | Amount=30 PLN | InvoiceDate=2026-01-15 | DueDate=2026-01-31'+$orderedSeparator+'Supplier=Smoke Equipment Vendor | Invoice=SMOKE-INV-001 | Amount=120 PLN | InvoiceDate=2026-01-10 | DueDate=2026-02-10';$updatedOrderedRows='Supplier=Smoke Utilities Vendor | Invoice=SMOKE-UTIL-001 | Amount=30 PLN | InvoiceDate=2026-01-15 | DueDate=2026-01-31'+$orderedSeparator+'Supplier=Smoke Equipment Vendor | Invoice=SMOKE-INV-001 | Amount=135 PLN | InvoiceDate=2026-01-10 | DueDate=2026-02-10';$archivedOrderedRows='Supplier=Smoke Equipment Vendor | Invoice=SMOKE-INV-001 | Amount=135 PLN | InvoiceDate=2026-01-10 | DueDate=2026-02-10';Invoke-AutomationIdButton $Main 'SupplierInvoicesSectionButton'
    Invoke-SupplierInvoicesPhase $Main 'readiness' $project {Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoices project selector not ready.' -Condition{try{$selector=Get-AutomationIdElement $Main 'SupplierInvoicesProjectSelector';$null-ne$selector-and$selector.Current.IsEnabled}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'SupplierInvoicesProjectSelector' $project;Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoices readiness failed.' -Condition{try{(Get-SupplierInvoicesReadinessState $Main $project).Ready}catch{$false}}};Add-Content $diagnostics 'SupplierInvoicesCrud: readiness PASS'
    Invoke-SupplierInvoicesPhase $Main 'create invoice 1' $project {Invoke-AutomationIdButton $Main 'AddSupplierInvoiceButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoice editor readiness failed.' -Condition{Test-SupplierInvoiceEditorReady $Main};Set-AutomationValue $Main 'SupplierInvoiceSupplierInput' 'Smoke Equipment Vendor';Set-AutomationValue $Main 'SupplierInvoiceNumberInput' 'SMOKE-INV-001';Set-AutomationValue $Main 'SupplierInvoiceAmountInput' '120';Set-AutomationCalendarDate $Main 'SupplierInvoiceInvoiceDateInput' ([DateTime]'2026-01-10');Set-AutomationCalendarDate $Main 'SupplierInvoiceDueDateInput' ([DateTime]'2026-02-10');Set-AutomationValue $Main 'SupplierInvoiceNoteInput' 'smoke invoice';Invoke-AutomationIdButton $Main 'SaveSupplierInvoiceButton';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoice create failed.' -Condition{try{(Get-SupplierInvoiceRowState $Main 'Smoke Equipment Vendor' 'SMOKE-INV-001' 120 'PLN' '2026-01-10' '2026-02-10').Matches-and(Get-AutomationIdElement $Main 'SupplierInvoicesTotal').Current.Name-match'120'-and(Test-SupplierInvoiceEditorClosed $Main)}catch{$false}}};Add-Content $diagnostics 'SupplierInvoicesCrud: create PASS'
    Invoke-SupplierInvoicesPhase $Main 'create invoice 2' $project {Invoke-AutomationIdButton $Main 'AddSupplierInvoiceButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Second Supplier Invoice editor readiness failed.' -Condition{Test-SupplierInvoiceEditorReady $Main};Set-AutomationValue $Main 'SupplierInvoiceSupplierInput' 'Smoke Utilities Vendor';Set-AutomationValue $Main 'SupplierInvoiceNumberInput' 'SMOKE-UTIL-001';Set-AutomationValue $Main 'SupplierInvoiceAmountInput' '30';Set-AutomationCalendarDate $Main 'SupplierInvoiceInvoiceDateInput' ([DateTime]'2026-01-15');Set-AutomationCalendarDate $Main 'SupplierInvoiceDueDateInput' ([DateTime]'2026-01-31');Invoke-AutomationIdButton $Main 'SaveSupplierInvoiceButton';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoice totals failed.' -Condition{try{$utilities=Get-SupplierInvoiceRowState $Main 'Smoke Utilities Vendor' 'SMOKE-UTIL-001' 30 'PLN' '2026-01-15' '2026-01-31';$equipment=Get-SupplierInvoiceRowState $Main 'Smoke Equipment Vendor' 'SMOKE-INV-001' 120 'PLN' '2026-01-10' '2026-02-10';$state=Get-SupplierInvoicesReadinessState $Main $project;$utilities.Matches-and$equipment.Matches-and$state.LogicalRows.Count-eq2-and($state.LogicalRowNames-join$orderedSeparator)-ceq$initialOrderedRows-and(Get-AutomationIdElement $Main 'SupplierInvoicesTotal').Current.Name-match'150'}catch{$false}}};Add-Content $diagnostics 'SupplierInvoicesCrud: totals PASS'
    Invoke-SupplierInvoicesPhase $Main 'edit invoice' $project {$row=Get-SupplierInvoiceRowState $Main 'Smoke Equipment Vendor' 'SMOKE-INV-001' 120 'PLN' '2026-01-10' '2026-02-10';Select-ContainingListItem $row.ListItem;Invoke-AutomationIdButton $Main 'EditSupplierInvoiceButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Edit Supplier Invoice editor readiness failed.' -Condition{Test-SupplierInvoiceEditorReady $Main};Set-AutomationValue $Main 'SupplierInvoiceAmountInput' '135';Invoke-AutomationIdButton $Main 'SaveSupplierInvoiceButton';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoice update failed.' -Condition{try{(Get-SupplierInvoiceRowState $Main 'Smoke Equipment Vendor' 'SMOKE-INV-001' 135 'PLN' '2026-01-10' '2026-02-10').Matches-and(($state=Get-SupplierInvoicesReadinessState $Main $project).LogicalRows.Count-eq2)-and($state.LogicalRowNames-join$orderedSeparator)-ceq$updatedOrderedRows-and(Get-AutomationIdElement $Main 'SupplierInvoicesTotal').Current.Name-match'165'}catch{$false}}};Add-Content $diagnostics 'SupplierInvoicesCrud: update PASS'
    Invoke-SupplierInvoicesPhase $Main 're-entry' $project {Invoke-AutomationIdButton $Main 'CompaniesSectionButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoices navigation did not restabilize.' -Condition{try{$button=Get-AutomationIdElement $Main 'SupplierInvoicesSectionButton';$null-ne$button-and$button.Current.IsEnabled}catch{$false}};Invoke-AutomationIdButton $Main 'SupplierInvoicesSectionButton';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoices re-entry selector not ready.' -Condition{try{$selector=Get-AutomationIdElement $Main 'SupplierInvoicesProjectSelector';$null-ne$selector-and$selector.Current.IsEnabled}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'SupplierInvoicesProjectSelector' $project;Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoices re-entry failed.' -Condition{try{(Get-SupplierInvoiceRowState $Main 'Smoke Utilities Vendor' 'SMOKE-UTIL-001' 30 'PLN' '2026-01-15' '2026-01-31').Matches-and(Get-SupplierInvoiceRowState $Main 'Smoke Equipment Vendor' 'SMOKE-INV-001' 135 'PLN' '2026-01-10' '2026-02-10').Matches-and(($state=Get-SupplierInvoicesReadinessState $Main $project).Semantic.IsExpected)-and$state.Currency.Current.Name-eq'PLN'-and$state.LogicalRows.Count-eq2-and($state.LogicalRowNames-join$orderedSeparator)-ceq$updatedOrderedRows-and(Get-AutomationIdElement $Main 'SupplierInvoicesTotal').Current.Name-match'165'}catch{$false}}};Add-Content $diagnostics 'SupplierInvoicesCrud: re-entry PASS'
    Invoke-SupplierInvoicesPhase $Main 'archive dialog' $project {$row=Get-SupplierInvoiceRowState $Main 'Smoke Utilities Vendor' 'SMOKE-UTIL-001' 30 'PLN' '2026-01-15' '2026-01-31';Select-ContainingListItem $row.ListItem;Invoke-AutomationIdButton $Main 'ArchiveSupplierInvoiceButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoice archive dialog not ready.' -Condition{try{$dialog=Get-AutomationIdElement $Main 'ArchiveSupplierInvoiceDialog';$cancel=Get-AutomationIdElement $dialog 'CancelArchiveSupplierInvoiceButton';$confirm=Get-AutomationIdElement $dialog 'ConfirmArchiveSupplierInvoiceButton';$null-ne$dialog-and$null-ne$cancel-and$cancel.Current.IsEnabled-and$null-ne$confirm-and$confirm.Current.IsEnabled-and-not(Get-AutomationIdElement $Main 'CompaniesSectionButton').Current.IsEnabled-and-not(Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled}catch{$false}};Invoke-AutomationIdButton (Get-AutomationIdElement $Main 'ArchiveSupplierInvoiceDialog') 'CancelArchiveSupplierInvoiceButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoice archive cancel failed.' -Condition{try{(Get-SupplierInvoiceRowState $Main 'Smoke Utilities Vendor' 'SMOKE-UTIL-001' 30 'PLN' '2026-01-15' '2026-01-31').Matches-and(($state=Get-SupplierInvoicesReadinessState $Main $project).LogicalRows.Count-eq2)-and($state.LogicalRowNames-join$orderedSeparator)-ceq$updatedOrderedRows-and$null-eq(Get-AutomationIdElement $Main 'ArchiveSupplierInvoiceDialog')-and(Get-AutomationIdElement $Main 'SupplierInvoicesTotal').Current.Name-match'165'-and(Get-AutomationIdElement $Main 'CompaniesSectionButton').Current.IsEnabled-and(Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled}catch{$false}}};Add-Content $diagnostics 'SupplierInvoicesCrud: archive cancel PASS'
    Invoke-SupplierInvoicesPhase $Main 'archive confirm' $project {$row=Get-SupplierInvoiceRowState $Main 'Smoke Utilities Vendor' 'SMOKE-UTIL-001' 30 'PLN' '2026-01-15' '2026-01-31';Select-ContainingListItem $row.ListItem;Invoke-AutomationIdButton $Main 'ArchiveSupplierInvoiceButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoice archive dialog did not reopen.' -Condition{try{$dialog=Get-AutomationIdElement $Main 'ArchiveSupplierInvoiceDialog';$null-ne(Get-AutomationIdElement $dialog 'ConfirmArchiveSupplierInvoiceButton')}catch{$false}};Invoke-AutomationIdButton (Get-AutomationIdElement $Main 'ArchiveSupplierInvoiceDialog') 'ConfirmArchiveSupplierInvoiceButton';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoice archive failed.' -Condition{try{(($state=Get-SupplierInvoicesReadinessState $Main $project).LogicalRows.Count-eq1)-and($state.LogicalRowNames-join$orderedSeparator)-ceq$archivedOrderedRows-and(Get-AutomationIdElement $Main 'SupplierInvoicesTotal').Current.Name-match'135'-and(Get-AutomationIdElement $Main 'CompaniesSectionButton').Current.IsEnabled-and(Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled}catch{$false}}};Add-Content $diagnostics 'SupplierInvoicesCrud: archive PASS';Invoke-SupplierInvoicePostingSmoke $Main;Invoke-AutomationIdButton $Main 'BusinessProjectsSectionButton'
}
function Invoke-SupplierInvoicePostingSmoke($Main) {
    $project='BusinessOS Gym Smoke Updated';$expectedInvoiceRow='Supplier=Smoke Equipment Vendor | Invoice=SMOKE-INV-001 | Amount=135 PLN | InvoiceDate=2026-01-10 | DueDate=2026-02-10'
    Invoke-SupplierInvoicePostingPhase $Main 'posting dialog' $project $expectedInvoiceRow {$row=Get-SupplierInvoiceRowState $Main 'Smoke Equipment Vendor' 'SMOKE-INV-001' 135 'PLN' '2026-01-10' '2026-02-10';Select-ContainingListItem $row.ListItem;Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Posting action not ready.' -Condition{try{(Get-AutomationIdElement $Main 'PostSupplierInvoiceButton').Current.IsEnabled-and(Get-AutomationIdElement $Main 'EditSupplierInvoiceButton').Current.IsEnabled-and(Get-AutomationIdElement $Main 'ArchiveSupplierInvoiceButton').Current.IsEnabled}catch{$false}};Invoke-AutomationIdButton $Main 'PostSupplierInvoiceButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Posting dialog not ready.' -Condition{try{$dialogMarker=Get-AutomationIdElement $Main 'PostSupplierInvoiceDialog';$null-ne$dialogMarker-and$dialogMarker.Current.Name-eq'Księgowanie faktury'-and(Get-AutomationIdElement $Main 'PostSupplierInvoiceSourceSupplier').Current.Name-eq'Smoke Equipment Vendor'-and(Get-AutomationIdElement $Main 'PostSupplierInvoiceSourceNumber').Current.Name-eq'SMOKE-INV-001'-and(Get-AutomationIdElement $Main 'PostSupplierInvoiceSourceAmount').Current.Name-match'135 PLN'-and(Get-AutomationIdElement $Main 'PostSupplierInvoiceSourceInvoiceDate').Current.Name-eq'2026-01-10'-and(Get-AutomationIdElement $Main 'PostSupplierInvoiceKindSelector').Current.IsEnabled-and(Get-AutomationIdElement $Main 'CancelPostSupplierInvoiceButton').Current.IsEnabled-and-not(Get-AutomationIdElement $Main 'ConfirmPostSupplierInvoiceButton').Current.IsEnabled-and-not(Get-AutomationIdElement $Main 'CompaniesSectionButton').Current.IsEnabled-and-not(Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled}catch{$false}}};Add-Content $diagnostics 'SupplierInvoicePosting: dialog PASS'
    Invoke-SupplierInvoicePostingPhase $Main 'posting confirm' $project $expectedInvoiceRow {Select-ComboBoxExactSemanticItem $Main 'PostSupplierInvoiceKindSelector' 'Capex';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Posting kind did not stabilize.' -Condition{try{(Get-ComboBoxSemanticSelection (Get-AutomationIdElement $Main 'PostSupplierInvoiceKindSelector') 'Capex').IsExpected-and(Get-AutomationIdElement $Main 'ConfirmPostSupplierInvoiceButton').Current.IsEnabled}catch{$false}};Invoke-AutomationIdButton $Main 'ConfirmPostSupplierInvoiceButton';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Invoice posting failed.' -Condition{(Get-SupplierInvoicePostingPostState $Main $project $expectedInvoiceRow).Matches}};Add-Content $diagnostics 'SupplierInvoicePosting: post PASS'
    Invoke-SupplierInvoicePostingPhase $Main 'actual cost verification' $project $expectedInvoiceRow {Invoke-AutomationIdButton $Main 'ActualCostsSectionButton';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Actual Costs posting selector not ready.' -Condition{try{(Get-AutomationIdElement $Main 'ActualCostsProjectSelector').Current.IsEnabled}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'ActualCostsProjectSelector' $project;Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Posted Actual Cost missing.' -Condition{try{(Get-ActualCostRowState $Main 'Faktura SMOKE-INV-001' 135 'Capex').Matches-and(Get-AutomationIdElement $Main 'ActualCostCapexTotal').Current.Name-eq'CAPEX: 285'-and(Get-AutomationIdElement $Main 'ActualCostOpexTotal').Current.Name-eq'OPEX: 0'-and(Get-AutomationIdElement $Main 'ActualCostTotal').Current.Name-eq'Total: 285'}catch{$false}}};Add-Content $diagnostics 'SupplierInvoicePosting: actual cost PASS'
    Invoke-SupplierInvoicePostingPhase $Main 'posting re-entry' $project $expectedInvoiceRow {Invoke-AutomationIdButton $Main 'CompaniesSectionButton';Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoices posting re-entry navigation not ready.' -Condition{try{(Get-AutomationIdElement $Main 'SupplierInvoicesSectionButton').Current.IsEnabled}catch{$false}};Invoke-AutomationIdButton $Main 'SupplierInvoicesSectionButton';Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoices posting re-entry selector not ready.' -Condition{try{(Get-AutomationIdElement $Main 'SupplierInvoicesProjectSelector').Current.IsEnabled}catch{$false}};Select-ComboBoxExactSemanticItem $Main 'SupplierInvoicesProjectSelector' $project;Wait-BusinessOSCondition -TimeoutSeconds 20 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Supplier Invoice posting re-entry failed.' -Condition{try{$row=Get-SupplierInvoiceRowState $Main 'Smoke Equipment Vendor' 'SMOKE-INV-001' 135 'PLN' '2026-01-10' '2026-02-10';Select-ContainingListItem $row.ListItem;$state=Get-SupplierInvoicesReadinessState $Main $project;$state.LogicalRows.Count-eq1-and$state.LogicalRowNames.Count-eq1-and($state.LogicalRowNames-join'')-ceq$expectedInvoiceRow-and$null-ne(Get-NamedElement $row.ListItem 'Zaksięgowana')-and-not(Get-AutomationIdElement $Main 'PostSupplierInvoiceButton').Current.IsEnabled-and-not(Get-AutomationIdElement $Main 'EditSupplierInvoiceButton').Current.IsEnabled-and-not(Get-AutomationIdElement $Main 'ArchiveSupplierInvoiceButton').Current.IsEnabled-and(Get-AutomationIdElement $Main 'SupplierInvoicesTotal').Current.Name-eq'Total: 135'}catch{$false}}};Add-Content $diagnostics 'SupplierInvoicePosting: re-entry PASS'
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
    try {
        Select-ContainingListItem (Get-NamedElement (Get-ExactListRow (Get-AutomationIdElement $Main 'BudgetVersionsList') 'Version 1') 'Version 1')
        Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'history' -Condition { (Get-BudgetLineRowState $Main 'Smoke CAPEX' 100).AmountConfirmed -and (Get-BudgetLineRowState $Main 'Smoke Revenue' 250).AmountConfirmed -and (Get-AutomationIdElement $Main 'BudgetCapexTotal').Current.Name-match'100' -and (Get-AutomationIdElement $Main 'BudgetRevenueTotal').Current.Name-match'250' -and -not(Get-AutomationIdElement $Main 'AddBudgetLineButton').Current.IsEnabled -and -not(Get-AutomationIdElement $Main 'EditBudgetLineButton').Current.IsEnabled -and -not(Get-AutomationIdElement $Main 'RemoveBudgetLineButton').Current.IsEnabled }
        Select-ContainingListItem (Get-NamedElement (Get-ExactListRow (Get-AutomationIdElement $Main 'BudgetVersionsList') 'Version 2') 'Version 2')
        Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'v2 reselection' -Condition { (Get-ComboBoxSemanticSelection (Get-AutomationIdElement $Main 'BudgetVersionsList') 'Version 2').IsExpected -and (Get-BudgetLineRowState $Main 'Smoke CAPEX' 150).AmountConfirmed -and (Get-BudgetLineRowState $Main 'Smoke Revenue' 250).AmountConfirmed -and (Get-AutomationIdElement $Main 'BudgetCapexTotal').Current.Name-match'150' -and (Get-AutomationIdElement $Main 'BudgetRevenueTotal').Current.Name-match'250' }
    } catch { Write-BudgetingTimeoutDiagnostics $Main $_.Exception.Message $project; throw }
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
    $projectState = Wait-BusinessProjectStatusReady $Main 'BusinessOS Gym Smoke Updated' 'Draft' 'BusinessProjects section did not restore the expected Draft project after Budgeting.'
    Select-ContainingListItem $projectState.ListItem
    Add-Content $diagnostics 'BusinessProjectsCrud: re-entry after Budgeting PASS'
    Invoke-ActualCostsCrudSmoke $Main
    $projectState = Wait-BusinessProjectStatusReady $Main 'BusinessOS Gym Smoke Updated' 'Draft' 'BusinessProjects section did not restore the expected Draft project after Actual Costs.'
    Select-ContainingListItem $projectState.ListItem
    Add-Content $diagnostics 'BusinessProjectsCrud: re-entry after Actual Costs PASS'
    Invoke-BudgetVarianceSmoke $Main
    $projectState = Wait-BusinessProjectStatusReady $Main 'BusinessOS Gym Smoke Updated' 'Draft' 'BusinessProjects section did not restore the expected Draft project after Budget Variance.'
    Select-ContainingListItem $projectState.ListItem
    Add-Content $diagnostics 'BusinessProjectsCrud: re-entry after Budget Variance PASS'
    Invoke-ForecastCostsCrudSmoke $Main
    $projectState = Wait-BusinessProjectStatusReady $Main 'BusinessOS Gym Smoke Updated' 'Draft' 'BusinessProjects section did not restore the expected Draft project after Forecast Costs.'
    Select-ContainingListItem $projectState.ListItem
    Add-Content $diagnostics 'BusinessProjectsCrud: re-entry after Forecast Costs PASS'
    Invoke-BudgetForecastSmoke $Main
    $projectState = Wait-BusinessProjectStatusReady $Main 'BusinessOS Gym Smoke Updated' 'Draft' 'BusinessProjects section did not restore the expected Draft project after Budget Forecast.'
    Select-ContainingListItem $projectState.ListItem
    Add-Content $diagnostics 'BusinessProjectsCrud: re-entry after Budget Forecast PASS'
    Invoke-CostCashFlowSmoke $Main
    $projectState = Wait-BusinessProjectStatusReady $Main 'BusinessOS Gym Smoke Updated' 'Draft' 'BusinessProjects section did not restore the expected Draft project after Cost Cash Flow.'
    Select-ContainingListItem $projectState.ListItem
    Add-Content $diagnostics 'BusinessProjectsCrud: re-entry after Cost Cash Flow PASS'
    Invoke-SupplierInvoicesCrudSmoke $Main
    $projectState = Wait-BusinessProjectStatusReady $Main 'BusinessOS Gym Smoke Updated' 'Draft' 'BusinessProjects section did not restore the expected Draft project after Supplier Invoices.'
    Select-ContainingListItem $projectState.ListItem
    Add-Content $diagnostics 'BusinessProjectsCrud: re-entry after Supplier Invoices PASS'
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
