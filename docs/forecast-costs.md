# Block 3B4 — Forecast Costs

Forecast Costs are project-owned, manual remaining-cost expectations in the project's base currency. Each `ForecastCost` records CAPEX or OPEX, a positive amount, `ExpectedOn` (the currently expected spending date, including past dates), an optional note, audit timestamps, optimistic-concurrency version, and soft archive state. Active totals are derived and never persisted. There is no FX.

Persistence uses the existing `BudgetingDbContext`, `businessos.db`, and `__EFMigrationsHistory_Budgeting`. The generated `AddForecastCosts` migration creates `forecast_costs` and project/project-date indexes. The WinUI workspace supports list, create, edit, refresh, totals, and confirmed archive.

A forecast remains active after an Actual Cost is entered until the user archives it. Block 3B4 does **not** implement Forecast → Actual conversion, automatic realization, EAC, ETC analytics beyond forecast totals, plan-vs-forecast, actual+forecast-vs-plan, monthly cash-flow, banking, invoice ingestion, purchase orders, vendors, VAT/tax, P&L, AI, POS, ERP, or GymOS. `ForecastCost` is input for later analytics.

Coverage includes domain/application unit tests, real SQLite integration tests, migration and architecture/environment verification, plus the semantic Windows Ready smoke workflow (pending a real Windows host).
