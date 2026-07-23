param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot
$artifactRoot = Join-Path $repoRoot 'artifacts/smoke-test'
New-Item -ItemType Directory -Force $artifactRoot | Out-Null
$diagnostics = Join-Path $artifactRoot 'desktop-smoke-diagnostics.txt'
Set-Content -Path $diagnostics -Value "BusinessOS desktop smoke test started: $(Get-Date -Format o)"
if (-not $IsWindows) { throw 'Desktop smoke test must run on Windows.' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
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
    if ($process.MainWindowTitle -ne 'BusinessOS') { throw "Unexpected main window title: '$($process.MainWindowTitle)'." }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if ($null -eq $root) { throw 'UI Automation could not attach to the main window.' }
    $elements = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $texts = New-Object System.Collections.Generic.List[string]
    foreach ($element in $elements) {
        $name = $element.Current.Name
        if (-not [string]::IsNullOrWhiteSpace($name)) { $texts.Add($name) }
    }
    foreach ($required in @('BusinessOS','Foundation','Fundament aplikacji został uruchomiony')) {
        if (-not ($texts -contains $required)) { throw "UI Automation did not find required element: $required." }
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

            if ($process.HasExited) {
                $shutdownFailure = 'BusinessOS.Desktop exited before CloseMainWindow was requested.'
                Add-Content -Path $diagnostics -Value "ShutdownFailure: $shutdownFailure"
            }
            else {
                $shutdownMethod = 'CloseMainWindow'
                $closeRequested = $process.CloseMainWindow()
                Add-Content -Path $diagnostics -Value "CloseMainWindow: $closeRequested"

                if (-not $closeRequested) {
                    $shutdownFailure = 'CloseMainWindow did not accept the shutdown request.'
                    Add-Content -Path $diagnostics -Value "ShutdownFailure: $shutdownFailure"
                }

                if (-not $process.WaitForExit(5000)) {
                    if ($null -eq $shutdownFailure) {
                        $shutdownFailure = 'BusinessOS.Desktop did not terminate within 5 seconds after CloseMainWindow.'
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
