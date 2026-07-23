# Testing

Block 1 currently contains only:

- unit tests for domain primitives and minimal domain entities;
- architecture tests for project boundaries and forbidden production dependencies;
- a Windows desktop smoke test that launches the unpackaged WinUI app and verifies the `BusinessOS` window through UI Automation.

The full Block 1 gate on Windows is:

```powershell
./eng/verify.ps1
```

Diagnostic commands for individual stages:

```powershell
./eng/bootstrap.ps1
./eng/build.ps1 -Configuration Release
./eng/smoke-test-desktop.ps1 -Configuration Release
./eng/test.ps1 -Configuration Release
./eng/verify-test-results.ps1
```

Integration tests, migration tests, SQLite tests and backup tests do not exist in Block 1. They will be added only when the persistence block starts.
