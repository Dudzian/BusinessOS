# Budgeting — Block 3B1

Block 3B1 implements planned project budgets only. A `Budget` belongs to one Business Project, has a trimmed, project-unique active name, optimistic version, and Draft → Active → Archived lifecycle. Archive is a soft delete; archived budgets cannot be edited.

Each `BudgetVersion` is a numbered immutable snapshot. Creating the next revision atomically copies every line to new `BudgetLine` identifiers, leaving the preceding snapshot unchanged. Only the latest version of a Draft budget is editable; Active and Archived budgets and historical versions are read-only.

Lines use non-negative decimal `Money` values and one of CAPEX, OPEX, Revenue, or Financing. Their currency must equal the project's `BaseCurrency`; FX is deliberately absent. Category totals are derived from snapshot lines and are not persisted.

## Persistence and lifecycle

SQLite stores `budgets`, `budget_versions`, and `budget_lines` in the shared `businessos.db`. Budgeting owns `__EFMigrationsHistory_Budgeting`. The schema has project and foreign-key indexes, a partial unique active-name index per project, and a unique budget/version-number index. Connections explicitly use `Pooling=false`.

The existing application startup coordinator inspects and migrates Companies, BusinessProjects, then Budgeting. Backup and recovery continue to operate once on the shared physical database.

## API and UI contract

`IBudgetingCrudService` exposes safe DTO/results for create, read, rename, activate, archive, revision, and line operations. Technical persistence exceptions do not cross the application boundary. The contract supports project-filtered lists, version lists, snapshot lines, and totals for the WinUI Budget section and stable automation identifiers.

Domain, application, SQLite integration, migrations, startup coordination, concurrency, transaction, and architecture boundaries are covered by the repository's test gates. Windows Ready smoke is the home for the end-to-end WinUI workflow.

Actual costs, invoices, purchase orders, forecasting, cash flow, P&L, analytics, AI, banking, POS, ERP, and GymOS integrations are deferred.
