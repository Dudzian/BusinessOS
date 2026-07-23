# Windows setup

Run `./eng/setup-environment.ps1`, dot-source `./eng/activate-environment.ps1`, then run `./eng/doctor.ps1 -Mode Windows`.

The doctor checks the pinned .NET SDK, PowerShell 7.4+, NuGet cache, `BusinessOS.sln`, Git, ripgrep, Windows SDK 10.0.19041.0+, x64 architecture, Windows-targeted build readiness, UI Automation loading, WinUI/Windows App SDK readiness, and interactive desktop capability.

Use `./eng/verify-windows.ps1` as the complete Windows gate.
