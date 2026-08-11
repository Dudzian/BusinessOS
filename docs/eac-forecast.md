# Block 3B5 — EAC / Plan vs Forecast

Block 3B5 is a read-only derived query over the selected immutable `BudgetVersion`, current active `ActualCost` records, and current active `ForecastCost` records. ETC is the active remaining forecast, EAC is `Actual + ETC`, VAC is `Planned - EAC`, and EAC utilization is `EAC / Planned × 100` (or unavailable when plan is zero).

The snapshot reports CAPEX, OPEX, and a recomputed total. Revenue and financing plan lines are excluded. Archived budgets and their immutable versions remain analyzable; archived actual and forecast costs are excluded. An active forecast remains in ETC even when `ExpectedOn` is in the past: there is no time cutoff, realization inference, deduplication, or Forecast-to-Actual conversion.

Every amount must match the project's base currency. Mixed-currency corruption causes a safe all-or-nothing read failure; FX is not provided.

This slice adds no domain aggregate, persisted analytics snapshot, table, database, migration, or mutation control. It is not cash-flow phasing, earned-value management, scenario simulation, or revenue/financing forecasting.

Coverage includes application and ViewModel unit tests, a real SQLite integration suite for scoping, archive/date semantics and all three currency-corruption sources, architecture/environment source contracts, and a semantic Windows Ready smoke for V1, V2 and re-entry. The slice makes no schema change and retains the existing three Budgeting migrations.
