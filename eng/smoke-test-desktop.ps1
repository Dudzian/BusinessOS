param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot
if (-not $IsWindows) { throw 'Desktop smoke test must run on Windows.' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$exe = Get-ChildItem -Path (Join-Path $repoRoot 'src/BusinessOS.Desktop/bin') -Recurse -Filter BusinessOS.Desktop.exe |
    Where-Object { $_.FullName -match [regex]::Escape($Configuration) } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $exe) { throw "BusinessOS.Desktop.exe was not found for configuration $Configuration." }
$process = $null
try {
    $process = Start-Process -FilePath $exe.FullName -PassThru
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
    $condition = [System.Windows.Automation.Condition]::TrueCondition
    $elements = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $texts = New-Object System.Collections.Generic.List[string]
    foreach ($element in $elements) {
        $name = $element.Current.Name
        if (-not [string]::IsNullOrWhiteSpace($name)) { $texts.Add($name) }
    }
    if (-not ($texts -contains 'BusinessOS')) { throw 'UI Automation did not find the BusinessOS heading.' }
    if (-not ($texts -contains 'Fundament aplikacji został uruchomiony')) { throw 'UI Automation did not find the startup confirmation text.' }
    Write-Host "EXE: $($exe.FullName)"
    Write-Host "PID: $($process.Id)"
    Write-Host "MainWindowHandle: $($process.MainWindowHandle)"
    Write-Host "MainWindowTitle: $($process.MainWindowTitle)"
    Write-Host "Texts: $($texts -join ' | ')"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(5000)) { $process.Kill($true) }
        Write-Host 'BusinessOS.Desktop process closed.'
    }
}
