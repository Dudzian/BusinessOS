# Block 3B3 — Plan vs Actual

Block 3B3 adds a read-only Plan vs Actual workspace. It compares one selected immutable `BudgetVersion` plan snapshot with the project's current active Actual Costs. The result is derived on demand and is never persisted; no table or migration is added.

Only CAPEX and OPEX participate. Revenue and Financing budget lines are deliberately excluded because there are no corresponding actual facts. Archived budgets and all their immutable versions remain valid reference plans, while soft-archived Actual Costs are excluded. The query is project scoped and verifies every line and cost uses `BusinessProject.BaseCurrency`; there is no FX conversion.

For CAPEX, OPEX, and TOTAL EXPENSES, `Variance = Planned - Actual`. A positive result is `UnderBudget`, zero is `OnBudget`, and a negative result is `OverBudget`. When plan is zero and actual is positive, the state is `UnplannedSpend`. Utilization is `Actual / Planned * 100` for a positive plan and `null` for a zero plan. The application keeps full decimal precision; WinUI formats percentages and Polish state labels.

The Application query boundary is `IBudgetVarianceQueryService`, backed by the query-only `IBudgetVarianceReadStore`. The SQLite implementation uses `AsNoTracking`, existing `budgets`, `budget_versions`, `budget_lines`, and `actual_costs`, SQL project/archive filtering, and deterministic in-memory ordering where `DateTimeOffset` translation is unsafe.

The separate **Plan vs wykonanie** WinUI section has project, archived-inclusive budget, and version selectors, currency/status metadata, three metric rows, refresh, safe operation feedback, and stable AutomationIds. It contains no add, edit, or archive controls.

Selector reads are atomic: a project, budget, or version is committed only after its dependent read succeeds. A failed or not-found read preserves the complete prior snapshot and explicitly republishes the canonical selector properties so the one-way WinUI bindings roll back the visual selection. `LastProjectsReloadSucceeded` describes only project-list reloads, while each later successful read clears stale operation feedback.

Unit tests cover formulas, states, exclusions, mismatch, currency safety, cancellation, and safe failures. SQLite integration and architecture/environment contracts protect scoping, immutable-version isolation, schema stability, UI boundaries, selection atomicity, and semantic automation. The existing Windows `Ready` scenario is extended rather than adding a sixth scenario; it validates Version 1, Version 2, archived-cost exclusion, and re-entry.
