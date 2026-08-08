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
function Select-ContainingListItem($Element) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $current = $Element
    while ($null -ne $current -and $current.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) { $current = $walker.GetParent($current) }
    if ($null -eq $current) { throw 'Company list item could not be selected.' }
    $current.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Test-Visible($Element) { $null -ne $Element -and -not $Element.Current.IsOffscreen }
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
    $name = Get-AutomationIdElement $Main 'BusinessProjectNameInput'
    $type = Get-AutomationIdElement $Main 'BusinessProjectTypeInput'
    $save = Get-AutomationIdElement $Main 'SaveBusinessProjectButton'
    $cancel = Get-AutomationIdElement $Main 'CancelBusinessProjectButton'
    $filter = Get-AutomationIdElement $Main 'BusinessProjectsStatusFilter'
    (Test-Visible $name) -and (Test-Visible $type) -and $null -ne $save -and $save.Current.IsEnabled -and $null -ne $cancel -and $cancel.Current.IsEnabled -and $null -ne $filter -and -not $filter.Current.IsEnabled
}
function Test-BusinessProjectEditorClosed($Main) {
    @('BusinessProjectNameInput', 'BusinessProjectTypeInput', 'SaveBusinessProjectButton', 'CancelBusinessProjectButton') |
        Where-Object { Test-Visible (Get-AutomationIdElement $Main $_) } |
        Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
}
function Format-AutomationElementState($Element) {
    if ($null -eq $Element) { return 'Found=False; IsEnabled=n/a; IsOffscreen=n/a' }
    "Found=True; IsEnabled=$($Element.Current.IsEnabled); IsOffscreen=$($Element.Current.IsOffscreen)"
}
function Write-EditorTimeoutDiagnostics($Main, [string]$ExpectedAutomationId, [string[]]$EditorAutomationIds) {
    Add-Content $diagnostics "Editor timeout scenario: $Scenario"
    Add-Content $diagnostics "Expected AutomationId: $ExpectedAutomationId; $(Format-AutomationElementState (Get-AutomationIdElement $Main $ExpectedAutomationId))"
    foreach ($id in @('AddCompanyButton', 'CompaniesSectionPanel', 'CompanyOperationMessage')) {
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
function Wait-EditorOpen($Main, [ValidateSet('Company', 'BusinessProject')]$Editor, [string]$Invocation) {
    $ids = if ($Editor -eq 'Company') { @('CompanyLegalNameInput', 'CompanyDisplayNameInput', 'SaveCompanyButton', 'CancelCompanyButton') } else { @('BusinessProjectNameInput', 'BusinessProjectTypeInput', 'SaveBusinessProjectButton', 'CancelBusinessProjectButton', 'BusinessProjectsStatusFilter') }
    try {
        Wait-BusinessOSCondition -TimeoutSeconds 10 -RequiredConsecutiveSuccesses 3 -TimeoutMessage "$Editor editor did not expose its interactive controls after $Invocation invocation." -Condition {
            if ($Editor -eq 'Company') { Test-CompanyEditorOpen $Main } else { Test-BusinessProjectEditorOpen $Main }
        }
    } catch {
        $missing = @($ids | Where-Object {
            $element = Get-AutomationIdElement $Main $_
            $null -eq $element -or $element.Current.IsOffscreen -or (($_ -like 'Save*' -or $_ -like 'Cancel*') -and -not $element.Current.IsEnabled) -or ($_ -eq 'BusinessProjectsStatusFilter' -and $element.Current.IsEnabled)
        })[0]
        Write-EditorTimeoutDiagnostics $Main $missing $ids
        throw "$Editor editor did not expose $missing after $Invocation invocation."
    }
}
function Get-NamedElements($Root, [string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    @($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
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
    Wait-BusinessOSCondition -TimeoutSeconds 15 -RequiredConsecutiveSuccesses 3 -TimeoutMessage 'Projects section did not load the created company.' -Condition {
        $panel=Get-AutomationIdElement $Main 'BusinessProjectsSectionPanel'; $selector=Get-AutomationIdElement $Main 'BusinessProjectsCompanySelector'; $add=Get-AutomationIdElement $Main 'AddBusinessProjectButton'
        return (Test-Visible $panel) -and $null-ne$selector -and (Get-NamedElements $selector 'BusinessOS Smoke Updated').Count-ge 1 -and $null-ne$add -and $add.Current.IsEnabled -and (Test-Visible (Get-AutomationIdElement $Main 'BusinessProjectsEmptyState'))
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
    $projectsList=Get-AutomationIdElement $Main 'BusinessProjectsList'; Select-ContainingListItem (Get-NamedElements $projectsList 'BusinessOS Gym Smoke Updated')[0]
    $statusButton=Get-AutomationIdElement $Main 'ChangeBusinessProjectStatusButton'; if($null-eq$statusButton-or-not$statusButton.Current.IsEnabled){throw 'Status transition button was not enabled for Draft.'}
    Invoke-AutomationIdButton $Main 'ChangeBusinessProjectStatusButton'
    Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Project status dialog did not open.' -Condition { Test-Visible (Get-AutomationIdElement $Main 'BusinessProjectStatusDialog') }
    $statusDialog=Get-AutomationIdElement $Main 'BusinessProjectStatusDialog'; $selector=Get-AutomationIdElement $statusDialog 'BusinessProjectStatusSelector'; $projectsList=Get-AutomationIdElement $Main 'BusinessProjectsList'; $recovery=Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton'; if($projectsList.Current.IsEnabled-or$recovery.Current.IsEnabled){throw 'Status dialog did not lock project selection and recovery.'}
    $selector.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 300
    Select-ContainingListItem (Get-NamedElements $statusDialog 'Analysis')[0]
    Invoke-AutomationIdButton $statusDialog 'ConfirmBusinessProjectStatusButton'
    Wait-BusinessOSCondition -TimeoutSeconds 15 -TimeoutMessage 'Analysis status did not appear in project list.' -Condition { (Get-NamedElements (Get-AutomationIdElement $Main 'BusinessProjectsList') 'Analysis').Count -ge 1 }
    Invoke-AutomationIdButton $Main 'CompaniesSectionButton'
    $list=Get-AutomationIdElement $Main 'CompaniesList'; Select-ContainingListItem (Get-NamedElements $list 'BusinessOS Smoke Updated')[0]
    Invoke-AutomationIdButton $Main 'ArchiveCompanyButton'; Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Company archive guard dialog did not open.' -Condition { Test-Visible (Get-AutomationIdElement $Main 'ArchiveCompanyDialog') }
    $companyArchiveDialog=Get-AutomationIdElement $Main 'ArchiveCompanyDialog'; if((Get-AutomationIdElement $Main 'CompaniesList').Current.IsEnabled-or(Get-AutomationIdElement $Main 'CompaniesSectionButton').Current.IsEnabled-or(Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled){throw 'Company archive dialog did not lock list, navigation, and recovery.'}
    Invoke-AutomationIdButton $companyArchiveDialog 'CancelArchiveCompanyButton'; Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Company archive cancellation did not restore controls.' -Condition { -not(Test-Visible(Get-AutomationIdElement $Main 'ArchiveCompanyDialog')) -and (Get-AutomationIdElement $Main 'CompaniesList').Current.IsEnabled -and (Get-AutomationIdElement $Main 'CompaniesSectionButton').Current.IsEnabled -and (Get-AutomationIdElement $Main 'OpenRecoveryFromMainButton').Current.IsEnabled }
    Invoke-AutomationIdButton $Main 'ArchiveCompanyButton'; Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Company archive guard dialog did not reopen.' -Condition { Test-Visible (Get-AutomationIdElement $Main 'ArchiveCompanyDialog') }
    Invoke-AutomationIdButton (Get-AutomationIdElement $Main 'ArchiveCompanyDialog') 'ConfirmArchiveCompanyButton'
    Wait-BusinessOSCondition -TimeoutSeconds 10 -TimeoutMessage 'Company archive guard did not return a safe message.' -Condition { (Get-NamedElements $Main 'Najpierw zarchiwizuj wszystkie projekty firmy.').Count -ge 1 }
    if ((Get-NamedElements (Get-AutomationIdElement $Main 'CompaniesList') 'BusinessOS Smoke Updated').Count -ne 1) { throw 'Company disappeared despite project archive guard.' }
    Invoke-AutomationIdButton $Main 'BusinessProjectsSectionButton'; $projectsList=Get-AutomationIdElement $Main 'BusinessProjectsList'; Select-ContainingListItem (Get-NamedElements $projectsList 'BusinessOS Gym Smoke Updated')[0]
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
function Select-ValidRecoveryItem($Recovery) {
    $list = Get-AutomationIdElement $Recovery 'RecoveryBackupList'
    if ($null -eq $list) { throw 'Recovery backup list was not found.' }
    $items = @($list.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem })
    $valid = @($items | Where-Object { $_.Current.Name -match 'prawidłowa' -and $_.Current.Name -notmatch 'nieprawidłowa' })
    $invalid = @($items | Where-Object { $_.Current.Name -match 'nieprawidłowa' })
    if ($items.Count -lt 2 -or $valid.Count -ne 1 -or $invalid.Count -lt 1) { throw "Unexpected recovery catalog: total=$($items.Count), valid=$($valid.Count), invalid=$($invalid.Count)." }
    $valid[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    return @{ Total = $items.Count; Valid = $valid.Count; Invalid = $invalid.Count }
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
    $counts = Select-ValidRecoveryItem $recovery
    if ($counts.Valid -ne $fixture.ExpectedValidBackupCount -or $counts.Invalid -ne $fixture.ExpectedInvalidBackupCount) {
        throw "Recovery catalog does not match fixture: valid=$($counts.Valid)/$($fixture.ExpectedValidBackupCount), invalid=$($counts.Invalid)/$($fixture.ExpectedInvalidBackupCount)."
    }
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
        $null = Select-ValidRecoveryItem $recovery
    } else {
        Invoke-AutomationIdButton $recovery 'BackFromRecoveryButton'
        Wait-BusinessOSCondition -TimeoutSeconds 30 -RequiredConsecutiveSuccesses 5 -TimeoutMessage 'Failure window did not become stable after Back.' -Condition {
            $windows=Get-ProcessWindows $Process.Id;return $windows.Count -eq 1 -and $null -ne (Get-NamedElement $windows[0] 'Ponów próbę')
        }
        $failureWindow=(Get-ProcessWindows $Process.Id)[0];$script:backNavigationPassed=$true;Add-Content $diagnostics 'BackNavigationPassed: True'
        Invoke-AutomationIdButton $failureWindow 'OpenRecoveryFromFailureButton';$recovery=Wait-RecoveryWindow $Process
        Wait-BusinessOSCondition -TimeoutSeconds 30 -RequiredConsecutiveSuccesses 5 -TimeoutMessage 'Second recovery catalog did not load.' -Condition { (Get-AutomationIdElement $recovery 'RefreshRecoveryCatalogButton').Current.IsEnabled }
        $null=Select-ValidRecoveryItem $recovery
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
