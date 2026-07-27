param(
    [string]$Configuration = 'Release',
    [ValidateSet('Ready','PersistenceFailure','PersistenceFailureThenReady')]
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
if ($Scenario -eq 'Ready') {
    $env:BusinessOS__Persistence__DatabasePath = Join-Path $artifactRoot 'data/businessos.db'
} else {
    $blocked = Join-Path $artifactRoot 'blocked'
    Set-Content -Path $blocked -Value 'not a directory'
    $env:BusinessOS__Persistence__DatabasePath = Join-Path $blocked 'businessos.db'
}
$diagnostics = Join-Path $artifactRoot 'desktop-smoke-diagnostics.txt'
Set-Content -Path $diagnostics -Value "BusinessOS desktop smoke test started: $(Get-Date -Format o)"
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
    $elements = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $texts = New-Object System.Collections.Generic.List[string]
    foreach ($element in $elements) {
        $name = $element.Current.Name
        if (-not [string]::IsNullOrWhiteSpace($name)) { $texts.Add($name) }
    }
    $requiredTexts = if ($Scenario -eq 'Ready') { @('BusinessOS','Foundation','Fundament aplikacji został uruchomiony','Baza danych jest gotowa') } else { @('Nie udało się przygotować bazy danych','Ponów próbę','Zamknij','DiagnosticId') }
    foreach ($required in $requiredTexts) {
        if (-not ($texts -contains $required)) { throw "UI Automation did not find required element: $required." }
    }
    if ($Scenario -eq 'Ready') {
        $databasePath = $env:BusinessOS__Persistence__DatabasePath
        if (-not (Test-Path $databasePath) -or (Get-Item $databasePath).Length -le 0) { throw 'Ready SQLite database was not created.' }
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
            Invoke-NamedButton $root 'Zamknij'
            if (-not $process.WaitForExit(10000)) { throw 'Close button did not terminate BusinessOS.Desktop.' }
            if ($process.ExitCode -ne 0) { throw "Close button produced exit code $($process.ExitCode)." }
            $closedByButton = $true
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
                $closeRequested = $process.CloseMainWindow()
                $closeMainWindow = $closeRequested
                Add-Content -Path $diagnostics -Value "CloseMainWindow: $closeRequested"

                if (-not $closeRequested) {
                    $shutdownFailure = 'CloseMainWindow did not accept the shutdown request.'
                    Add-Content -Path $diagnostics -Value "ShutdownFailure: $shutdownFailure"
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
