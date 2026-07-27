# BusinessOS persistence

Block 2A introduces a local-first SQLite persistence foundation for the Companies module only. BusinessOS uses one local SQLite file per installation while each module owns its own tables and EF migration history. Companies stores migration history in `__EFMigrationsHistory_Companies` and persists companies in `companies` using explicit `snake_case` column names.

Desktop code receives short-lived contexts through `IDbContextFactory<CompaniesDbContext>`; a singleton `DbContext` is intentionally not registered. `CompaniesDatabaseInitializer` is explicit and runs `MigrateAsync` only when called by a future startup workflow. Production code must not use `EnsureCreated()`.

`Company` remains a domain entity without EF attributes. Infrastructure maps strong IDs, `CurrencyCode`, `TaxIdentificationNumber`, and `EntityVersion` with value converters. Soft delete is enforced by a global query filter (`IsDeleted == false`), and tests may use `IgnoreQueryFilters()`. Optimistic concurrency uses `EntityVersion` as an EF concurrency token.

Migrations are generated from `BusinessOS.Modules.Companies.Infrastructure` with local `dotnet-ef` tools restored by `dotnet tool restore`. The design-time factory writes only to `BUSINESSOS_EF_DATABASE_PATH` or `.cache/ef/companies-design-time.db`.

Integration and migration tests use real, isolated SQLite database files and clean `.db`, `.db-shm`, and `.db-wal` files.

## Block 2B1 startup safety

Application startup is ordered as host start → migration inspection → conditional backup → migration → main window. An existing Companies database with pending migrations is copied with SQLite's online `BackupDatabase` API to a temporary file. `PRAGMA quick_check;` must return exactly `ok` before that file is atomically renamed to `businessos-companies-yyyyMMddTHHmmssfffZ-<unique-suffix>.db`.

The default database is `%LocalAppData%/BusinessOS/Data/businessos.db`; backups are stored in `%LocalAppData%/BusinessOS/Backups/Companies`. Configuration keys are `BusinessOS:Persistence:DatabasePath`, `BusinessOS:Persistence:BackupDirectory`, and `BusinessOS:Persistence:MaxBackups` (default 10). Retention recognizes only Companies backup names, keeps the newest configured count, and leaves unrelated files untouched. A retention warning does not invalidate a valid backup.

First startup migrates without a backup. An up-to-date database neither backs up nor migrates. A backup or integrity failure prevents migration and cleans temporary `.tmp`, `-shm`, and `-wal` files. If migration fails, the verified pre-migration backup is preserved. Failures show a safe message and `DiagnosticId`; the UI permits retry or controlled shutdown, while full exception details are logged. Manual restore and Companies CRUD remain outside Block 2B1.
