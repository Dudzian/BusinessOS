# BusinessOS persistence

Block 2A introduces a local-first SQLite persistence foundation for the Companies module only. BusinessOS uses one local SQLite file per installation while each module owns its own tables and EF migration history. Companies stores migration history in `__EFMigrationsHistory_Companies` and persists companies in `companies` using explicit `snake_case` column names.

Desktop code receives short-lived contexts through `IDbContextFactory<CompaniesDbContext>`; a singleton `DbContext` is intentionally not registered. `CompaniesDatabaseInitializer` is explicit and runs `MigrateAsync` only when called by a future startup workflow. Production code must not use `EnsureCreated()`.

`Company` remains a domain entity without EF attributes. Infrastructure maps strong IDs, `CurrencyCode`, `TaxIdentificationNumber`, and `EntityVersion` with value converters. Soft delete is enforced by a global query filter (`IsDeleted == false`), and tests may use `IgnoreQueryFilters()`. Optimistic concurrency uses `EntityVersion` as an EF concurrency token.

Migrations are generated from `BusinessOS.Modules.Companies.Infrastructure` with local `dotnet-ef` tools restored by `dotnet tool restore`. The design-time factory writes only to `BUSINESSOS_EF_DATABASE_PATH` or `.cache/ef/companies-design-time.db`.

Integration and migration tests use real, isolated SQLite database files and clean `.db`, `.db-shm`, and `.db-wal` files. Block 2B will address startup migration orchestration, backup/restore policy, error handling, and Companies management UI.
