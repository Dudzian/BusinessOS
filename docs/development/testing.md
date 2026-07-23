# Testing and verification

Use two verification levels:

- `pwsh -NoProfile -File ./eng/verify-cross-platform.ps1` on Linux/macOS. WinUI build and smoke tests are reported as `SKIPPED — requires Windows verification`.
- `./eng/verify-windows.ps1` on Windows for restore, format, Release build, tests, architecture checks, TRX verification, vulnerable package scan, WinUI build, EXE launch, MainWindowHandle, title check, UI Automation, and safe shutdown.

`eng/verify.ps1` delegates to the correct verifier for the operating system.
