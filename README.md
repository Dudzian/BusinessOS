# BusinessOS

Block 3A (BusinessProjects) is complete. Block 3B1 (planned/versioned project budgeting) is **COMPLETE**. Block 3B2 Actual Costs is **COMPLETE**. Block 3B3 Plan vs Actual is **COMPLETE**. Block 3B4 Forecast Costs is **COMPLETE**. Block 3B5 EAC / Plan vs Forecast is **COMPLETE**. Block 3B6 Cost Cash Flow is **COMPLETE**. Block 3B7 Supplier Invoices is **COMPLETE**. Block 3B8 Supplier Invoice → Actual Cost Posting is implemented, pending full Windows verification.

BusinessOS is a local-first Windows desktop application foundation.

## Persistence blocks

**Block 2A — persistence foundation** introduced the Companies SQLite model, generated EF migration, nullable foreign tax IDs, and Polish NIP checksum validation. **Block 2C — Companies application CRUD** adds create, list, details, edit, status changes and soft-delete archiving through domain, application, SQLite infrastructure and WinUI. See [Companies CRUD](docs/companies-crud.md).

On first startup no backup is created; an up-to-date database is opened without a backup or migration. Backup or migration failures lead to a safe retry/close window with a `DiagnosticId`, while technical exception details remain in logs. Blocks 2B2a and 2B2b provide the safe Companies restore engine, backup catalog, and recovery UI. See [persistence documentation](docs/persistence.md).

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

Windows-first validation:

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

## Run the desktop app

```powershell
dotnet run --project src/BusinessOS.Desktop/BusinessOS.Desktop.csproj -c Debug
```

Expected window title: `BusinessOS`.
Expected visible elements include `BusinessOS`, `Baza danych jest gotowa`, the `Firmy` section, `CompaniesList`, `AddCompanyButton`, and `OpenRecoveryFromMainButton`. The desktop app is unpackaged (`WindowsPackageType=None`) and intentionally does not include MSIX or installer assets.

## Current scope

Implemented in this block:

- solution membership for all source and test projects;
- modular project layout;
- composition root library used by the WinUI app;
- WinUI 3 Companies list/editor and testable `CompaniesViewModel`;
- domain primitives and basic company/project domain models;
- unit tests for current domain primitives and domain entities;
- architecture tests for domain boundaries and forbidden production dependencies;
- Windows desktop smoke test that launches the built app and checks the BusinessOS window through UI Automation;
- Windows CI checks for solution membership, restore, format, build, smoke test, tests and TRX verification.

Deferred to later blocks:

- audit log;
- background jobs and durable queues;
- MSIX installer;
- GymOS compatibility and financial engine.


## Delivery blocks

- **Block 1 — foundation**: modular monolith structure, diagnostics, environment checks, test result verification, and dependency vulnerability scanning.
- **Block 2A — Companies SQLite persistence foundation**: EF Core 10 + SQLite infrastructure for the Companies module, including `CompaniesDbContext`, explicit mapping, local migrations, integration tests, and migration tests.
- **Block 2B2b — Companies recovery UI**: recovery is available from the main shell and safe persistence-startup failures. It lists only BusinessOS-managed backups, never accepts an arbitrary path or file picker selection, requires destructive-action confirmation, and reports safe messages with a `DiagnosticId`. After restore, Block 2B1 verifies the database and applies pending migrations.

The default local database path is `%LocalAppData%/BusinessOS/Data/businessos.db`. It can be overridden with configuration key `BusinessOS:Persistence:DatabasePath`. AppHost coordinates safe startup and migrations before opening the implemented Companies CRUD UI.

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

## Delivery status

- Block 1 — complete
- Block 2A — complete
- Block 2B1 — complete
- Block 2B2a — complete
- Block 2B2b — complete
- Block 2B3 engineering readiness — complete
- Block 2C Companies application CRUD — complete
- Block 3A BusinessProjects persistence and lifecycle CRUD — current/implemented

BusinessOS uses the [Windows-first CI policy](docs/ci-policy.md). Gate evidence is staged under `artifacts/ci-evidence`, and CI can be audited read-only without GitHub CLI using `eng/audit-github-ci.ps1`.

Business projects belong to an active company and follow a controlled lifecycle. See [BusinessProjects](docs/business-projects.md).

- Block 3B3 Plan vs Actual — COMPLETE
- Block 3B4 Forecast Costs — COMPLETE
- Block 3B5 EAC / Plan vs Forecast — COMPLETE
- Block 3B6 Cost Cash Flow — COMPLETE
- Block 3B7 Supplier Invoices — COMPLETE
- Block 3B8 Supplier Invoice → Actual Cost Posting — implemented, pending full Windows verification
- Deferred: invoice file/OCR ingestion, invoice attachments, vendors master data, VAT/tax accounting, payments, banking/reconciliation, revenue/financing cash flow, full/net cash flow, and advanced forecast analytics.
