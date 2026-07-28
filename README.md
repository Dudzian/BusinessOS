# BusinessOS

BusinessOS is a local-first Windows desktop application foundation.

## Persistence blocks

**Block 2A — persistence foundation** introduced the Companies SQLite model, generated EF migration, nullable foreign tax IDs, and Polish NIP checksum validation. **Block 2B1 — safe startup and pre-migration backup** adds a coordinated startup sequence: inspect migrations, create and verify an online backup when an existing database needs migration, migrate, and only then open the main window.

On first startup no backup is created; an up-to-date database is opened without a backup or migration. Backup or migration failures lead to a safe retry/close window with a `DiagnosticId`, while technical exception details remain in logs. Block 2B2a adds the infrastructure-only safe Companies restore engine and backup catalog; restore UI remains deferred to Block 2B2b. See [persistence documentation](docs/persistence.md).

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
- Companies CRUD UI;
- BusinessProjects persistence and UI;
- Budgeting persistence and UI;
- restore UI (the safe infrastructure restore engine is available);
- MSIX installer;
- GymOS compatibility and financial engine.


## Delivery blocks

- **Block 1 — foundation**: modular monolith structure, diagnostics, environment checks, test result verification, and dependency vulnerability scanning.
- **Block 2A — Companies SQLite persistence foundation**: EF Core 10 + SQLite infrastructure for the Companies module, including `CompaniesDbContext`, explicit mapping, local migrations, integration tests, and migration tests.

The default local database path is `%LocalAppData%/BusinessOS/Data/businessos.db`. It can be overridden with configuration key `BusinessOS:Persistence:DatabasePath`. Host construction only registers services; migrations are **not** automatically executed at application startup in Block 2A. Companies CRUD UI remains a later stage.

Restore local tools with:

```bash
dotnet tool restore
```

List Companies migrations with:

```bash
dotnet ef migrations list --project ./src/Modules/Companies/BusinessOS.Modules.Companies.Infrastructure/BusinessOS.Modules.Companies.Infrastructure.csproj --startup-project ./src/Modules/Companies/BusinessOS.Modules.Companies.Infrastructure/BusinessOS.Modules.Companies.Infrastructure.csproj --context CompaniesDbContext
```

Run persistence tests with:

```bash
dotnet test ./tests/BusinessOS.IntegrationTests/BusinessOS.IntegrationTests.csproj --configuration Release
dotnet test ./tests/BusinessOS.MigrationTests/BusinessOS.MigrationTests.csproj --configuration Release
```

See [docs/persistence.md](docs/persistence.md) for persistence design notes.
