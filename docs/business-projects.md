# BusinessProjects lifecycle CRUD (Block 3A)

A business project belongs to one active, organization-accessible company. It stores name, business type, location, description, planned start and opening dates, and a three-letter base currency. New projects always begin in `Draft`.

## Lifecycle

Allowed transitions are: Draft → Analysis or Cancelled; Analysis → Draft, Approved or Cancelled; Approved → InPreparation or Cancelled; InPreparation → InProgress, Paused or Cancelled; InProgress → ReadyToOpen, Paused or Cancelled; ReadyToOpen → Operating, Paused or Cancelled; Operating → Paused or Closed; and Paused → InPreparation, InProgress, ReadyToOpen, Operating or Cancelled. Closed and Cancelled are terminal.

Archiving is a technical soft delete and does not alter the last business status. Every successful edit, transition, or archive advances the optimistic concurrency version and records UTC audit data.

## Persistence and Companies integration

Projects use the shared BusinessOS SQLite file in `BusinessOS:Persistence:DatabasePath`, table `business_projects`, and independent history `__EFMigrationsHistory_BusinessProjects`. Dates use ISO `yyyy-MM-dd`. Active names are unique per company without regard to case through the partial `ux_business_projects_company_name_active` index (`is_deleted = 0`); an archived name can be reused.

BusinessProjects verifies companies through an Application port implemented by AppHost. Companies uses a generic archive constraint, also composed in AppHost: any non-archived project—including Closed or Cancelled—blocks company archive until all projects are archived.

The database backup and recovery unit is the complete shared BusinessOS database. Compatibility validation treats a missing BusinessProjects history in an older backup as an empty prefix; unknown, duplicated, or out-of-order migrations in either module must block restore.

## Manual scenario

Create and activate a company, select it in Projects, create and edit a Draft project, advance it to Analysis, filter and refresh the list, verify company archive is blocked, archive the project, then archive the company. Restart and verify persistence. Recovery restores the shared company/project database.

## Current limits

This block deliberately excludes budgets, CAPEX/OPEX, task schedules, attachments, comments, audit logs, permissions, import, synchronization, notifications, background jobs, hard delete, individual-project restore, and GymOS analytics.

## Desktop workspace

The WinUI workspace provides Companies and Projects sections. Projects supports active-company selection, status filtering, create/edit, lifecycle transition using the server-provided `AllowedTransitions`, refresh, and confirmed archive. Navigation and recovery are disabled while either section is busy or has an editor or confirmation interaction open. The controls expose stable BusinessProjects automation identifiers used by the Windows `Ready` smoke scenario.

Startup inspects both EF contexts before changing the shared file. If either has pending migrations, the existing verified backup service creates one backup, then Companies migrations run before BusinessProjects migrations. Recovery validates the Companies history and the optional BusinessProjects history independently; an older backup without the latter remains compatible.

When the user enters Projects, the workspace reloads active companies, preserves a still-valid selection, auto-selects a sole company, and clears projects/editor state when the company disappears. The status filter has an explicit **Wszystkie** option. Status and archive dialogs capture project identity and version, lock selection/navigation/recovery, and clear captured state after success, cancellation, conflict, or failure. Windows UI Automation remains subject to the Windows-only gate.
