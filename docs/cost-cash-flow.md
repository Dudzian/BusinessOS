# Block 3B6 — Cost Cash Flow

Block 3B6 is a read-only, project-level cost-outflow phasing view. It derives a sparse monthly series from active costs: an Actual is assigned to the first day of its `IncurredOn` month and a Forecast to the first day of its `ExpectedOn` month. Months without records are not generated, and months are ordered ascending.

Each month and the overall snapshot expose CAPEX, OPEX, and TOTAL metrics. Every metric contains Actual, Forecast, and Expected, where `Expected = Actual + Forecast`. A past active forecast remains in its literal `ExpectedOn` month. Archived Actual and Forecast records are excluded.

An active Forecast remains a forecast until manually archived. There is no deduplication, matching, automatic realization, or Forecast → Actual conversion. All active records must use the project's base currency (case-insensitive); mixed currency is an all-or-nothing safe read failure and no FX conversion is provided.

The slice does not use a Budget, BudgetVersion, or BudgetLine and is not a cash-flow plan. It adds no Domain aggregate, table, migration, or persisted snapshot. It reuses `BudgetingDbContext` only to read `actual_costs` and `forecast_costs`. The UI is analytics-only: it has no add, edit, archive, delete, save, or date editor operations.
