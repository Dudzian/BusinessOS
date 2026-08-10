# Block 3B2 — Actual Costs

Actual Costs are manually entered CAPEX and OPEX facts owned directly by a Business Project. They are independent of `Budget`, immutable `BudgetVersion` snapshots, and `BudgetLine`.

Each cost contains a typed identifier, project identifier, CAPEX/OPEX kind, trimmed name, positive `Money`, required `DateOnly` incurred date, optional normalized note, UTC audit timestamps, optimistic-concurrency version, and optional archive timestamp. Currency must equal the available project's normalized base currency; there is no FX.

## Application and SQLite

`IActualCostsCrudService` exposes project-scoped list, get, create, update, and archive operations with safe validation, unavailable-project, archived, concurrency, cancellation, and persistence results. It reuses `IBudgetingProjectLookup`.

The existing `BudgetingDbContext`, physical database, lifecycle, and `__EFMigrationsHistory_Budgeting` own `actual_costs`. The `AddActualCosts` migration creates project and project/date indexes. SQLite filters active project rows; deterministic incurred-date/update/id ordering happens in memory. Updates and soft archives increment `Version`; archived rows remain stored but leave active lists.

## WinUI and verification

The top-level **Koszty rzeczywiste** workspace selects a project, displays its currency, active costs, derived CAPEX/OPEX/total values, and a manual editor with a calendar date. Archive requires confirmation. Event data drives selection and stale cost snapshots are canonicalized by id. Coverage includes domain/application tests, real SQLite and migration tests, architecture checks, desktop view-model tests, source contracts, and the semantic Windows Ready workflow.

## Outside the block

Plan vs Actual was outside Block 3B2 and is now implemented separately in Block 3B3. Invoice ingestion, purchase orders, vendors master data, tax/VAT accounting, banking, forecasting, cash flow, P&L, AI, POS, ERP, and GymOS integration remain outside Block 3B2.
