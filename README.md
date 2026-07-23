# BusinessOS

BusinessOS is a local-first Windows desktop application foundation. This repository currently contains **Block 1** only: a project structure, domain primitives and a minimal WinUI 3 shell.

## Environment requirements

- Windows 11 or Windows Server runner with Visual Studio/Windows App SDK support for WinUI 3 builds.
- Stable .NET SDK compatible with `global.json`.
- PowerShell 7 (`pwsh`) for engineering scripts.
- No Python or Excel runtime is required by production projects.

## Verification

Cross-platform validation (does not build or smoke-test the WinUI project):

```powershell
pwsh -NoProfile -File ./eng/verify-cross-platform.ps1
```

Full Windows validation for Block 1:

```powershell
pwsh -NoProfile -File ./eng/verify-windows.ps1
```

The Windows verification performs restore, formatting checks, Release build, unit tests, architecture tests, TRX verification, environment tests, vulnerable package scanning and the real WinUI smoke test. Smoke-test diagnostics are written to `artifacts/smoke-test/`.

## Restore

```powershell
./eng/bootstrap.ps1
# or
dotnet restore BusinessOS.sln
```

## Build

```powershell
./eng/build.ps1 -Configuration Release
# or
dotnet build BusinessOS.sln -c Release --no-restore
```

## Test

```powershell
./eng/test.ps1 -Configuration Release
```

## Run the minimal desktop app

```powershell
dotnet run --project src/BusinessOS.Desktop/BusinessOS.Desktop.csproj -c Debug
```

Expected window title: `BusinessOS`.
Expected visible text: `BusinessOS`, `Foundation`, `Fundament aplikacji został uruchomiony` and the assembly metadata version. The Block 1 desktop app is unpackaged (`WindowsPackageType=None`) and intentionally does not include MSIX or installer assets.

## Current scope

Implemented in this block:

- solution membership for all source and test projects;
- modular project layout;
- composition root library used by the WinUI app;
- minimal WinUI 3 window and `MainViewModel`;
- domain primitives and basic company/project domain models;
- unit tests for current domain primitives and domain entities;
- architecture tests for domain boundaries and forbidden production dependencies;
- Windows desktop smoke test that launches the built app and checks the BusinessOS window through UI Automation;
- Windows CI checks for solution membership, restore, format, build, smoke test, tests and TRX verification.

Deferred to later blocks:

- audit log;
- background jobs and durable queues;
- integration and migration test projects, created when the persistence block starts;
- production SQLite schema, mappings and migrations;
- company persistence and UI workflows;
- business project UI workflows;
- backup and restore;
- MSIX installer;
- GymOS compatibility and financial engine.
