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

## Block 2B2a safe restore engine

Block 2B2a provides a backup catalog and restore engine without UI. It accepts only a canonical `businessos-companies-yyyyMMddTHHmmssfffZ-<32 lowercase hex>.db` `BackupId`, resolves it inside the configured backup directory, rejects traversal and reparse points, and validates every candidate with read-only, non-pooled `PRAGMA quick_check` plus `__EFMigrationsHistory_Companies` compatibility checks. Older known schemas are accepted and Block 2B1 applies their pending migrations at the next startup; a backup containing an unknown newer migration is rejected.

Restore creates and validates same-volume staging before asking the existing backup service for a safety backup, so retention (including `MaxBackups == 1`) cannot invalidate the staged restore. Immediately before the critical section SQLite pools are cleared and live WAL/SHM sidecars are removed. Existing databases use atomic `File.Replace` with a same-directory rollback file; a missing database uses an atomic same-volume move. The installed database is validated again. A post-replacement failure restores and validates the rollback, while the safety backup is always retained. Cancellation is honored through staging and safety backup, then deferred from the final pre-replacement check until replacement, post-validation, rollback if required, and safety cleanup reach a stable state. Block 2B2b provides the recovery UI described below.

Migration history must be an ordinal, duplicate-free prefix of the migrations known to the running application. If `File.Replace` reports an error, restore reconciles the actual live, staging, and rollback files rather than assuming no mutation occurred. A verified installed database may be accepted; otherwise the previous database is restored. When no database existed before a failed installation, the invalid live file and its sidecars are removed. Safety backups are retained on every failure path.

Before removing live WAL/SHM sidecars, restore performs a non-pooled read-write `PRAGMA wal_checkpoint(TRUNCATE)` and refuses replacement when SQLite reports a busy or incomplete checkpoint. After staging validation, SHA-256 fingerprints of staging and the checkpointed original database make ambiguous `Replace` and `Move` outcomes deterministic: only the expected restored database or the known original database is accepted. Unknown states retain recovery artifacts for operator recovery.

## Block 2B2b recovery UI

The Desktop recovery window consumes only the AppHost recovery facade and presentation DTOs; Desktop does not reference Companies Infrastructure. Recovery can be opened from the main shell or from safe persistence-startup failures. The catalog is limited to canonical backups in the BusinessOS backup directory: there is no file picker and no arbitrary-path import. Invalid entries remain visible with a safe explanation but cannot be restored.

The user must select a valid backup and confirm that the current database will be replaced. The restore engine first attempts a safety backup of the live database. A successful restore is followed by the existing Block 2B1 startup coordinator, which inspects the restored database and applies pending migrations before a fresh main window is shown. Failures expose only a safe message and correlated `DiagnosticId`; paths, exception names, connection strings, and infrastructure codes remain in diagnostic logs.

CI evidence and Windows-first merge requirements are documented in [the CI policy](ci-policy.md).
