# Windows-first CI policy

BusinessOS is a **Windows-first** application. The cross-platform gate verifies logic that is independent of WinUI; a cross-platform PASS never substitutes for a Windows PASS. Any change to Desktop, AppHost, persistence, or startup is not merge-ready without a successful Windows gate.

## Stable checks and merge rule

The required jobs are `ci / cross-platform`, `ci / windows`, and the stable aggregate `ci / required-gates`. The aggregate succeeds only when both platform jobs succeed. After this change is merged, the repository owner must configure `ci / required-gates` as a required check for `main`; this repository change does not alter branch protection.

`FAIL`, `cancelled`, `skipped`, `UNKNOWN`, and `NOT RUN` are never evidence of success. A Windows check that was not run must be reported exactly as `NOT RUN`, and merging must wait for a real Windows PASS.

## Evidence and audit

Each gate stages only an allow-listed evidence set under `artifacts/ci-evidence/<gate>`. `summary.json` follows `eng/schemas/ci-evidence.schema.json`; `manifest.json` records size, media type, and SHA-256. Databases, keys, temporary files, and contextual secret patterns are rejected before upload. Evidence is retained for 14 days and artifact names include run ID and attempt.

Audit a PR without GitHub CLI using `eng/audit-github-ci.ps1 -Repository OWNER/REPO -PullRequest NUMBER`. The auditor checks all jobs, both evidence archives, manifests, summaries, smoke completeness, and commit/tree identity. Different commit IDs are acceptable only when the Git tree IDs match exactly.

## Red Windows CI

1. Open the newest run attempt and confirm `windows` actually ran.
2. Download the Windows evidence artifact and validate `manifest.json` hashes.
3. Read `summary.json` failure stage/message and the last completed stage.
4. Inspect the smoke diagnostics, sanitized UI Automation tree/top-level window list, and failure-only screenshot.
5. Reproduce with `eng/verify-windows.ps1`; never waive the Windows gate based on cross-platform results.
