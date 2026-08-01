# Companies application CRUD — Block 2C

Block 2C delivers the first end-to-end BusinessOS business workflow: list active companies, view details, create, edit all company fields and status, refresh, and archive using soft delete. The fields are legal and display names, optional tax identification number, two-letter country code, ISO-4217-format base currency, time-zone identifier, and status.

## Architecture and safety

The domain entity validates and normalizes every mutation. Polish NIP values have spaces and hyphens removed and must pass the checksum. Time-zone values are portable strings rather than OS lookups. Application exposes request/DTO/result contracts, injects `TimeProvider`, and maps expected validation, duplicate, cancellation, persistence, not-found, and concurrency outcomes to safe Polish messages. It never exposes EF entities to Desktop.

AppHost composes the Application service and Infrastructure store. Its deterministic local organization and user IDs keep data accessible across restarts; a future identity/workspace module will replace this single-user execution context. Technical IDs are not displayed.

SQLite uses the existing query filter for soft delete and `EntityVersion` concurrency. A partial unique index over `(organization_id, tax_identification_number)` where the row is not deleted and tax ID is non-null guarantees uniqueness within an organization. The same tax ID is allowed in another organization or after archive, and multiple null values are allowed. Pre-checks improve feedback, while the database constraint protects races. There is no hard delete or individual restore.

## Manual scenario

1. Start BusinessOS and select **Dodaj** in **Firmy**.
2. Enter valid data (defaults are `PL`, `PLN`, `Europe/Warsaw`, and `Active`) and save.
3. Select the company, choose **Edytuj**, change its display name or status, and save.
4. Select **Archiwizuj**, confirm the named company, and verify it disappears.
5. Use **Kopie zapasowe** to confirm the existing recovery workflow remains available.

The editor retains fields after validation errors, prevents repeated saves while busy, reloads the captured company identity after a concurrency conflict, and never presents raw EF/SQLite errors. `Archived` is deliberately excluded from the ordinary status selector; only the dedicated confirmed archive operation performs soft delete. The Windows `Ready` smoke automates create, edit, archive, empty-state restoration, and recovery-button availability. Current limitations include no login, synchronization, import, projects, budgeting, branches, attachments, audit log, server pagination, full-text search, hard delete, or per-company restore.
