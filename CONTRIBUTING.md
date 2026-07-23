# Contributing

Before changing code, prepare and activate the BusinessOS environment. The required .NET SDK, PowerShell minimum, NuGet cache, target framework, runtime identifier, and Windows SDK baseline are versioned in `eng/environment.lock.json`.

Run `./eng/setup-environment.sh` and `source ./eng/activate-environment.sh` on Linux/macOS, or `./eng/setup-environment.ps1` and `. ./eng/activate-environment.ps1` on Windows. Then run `pwsh -NoProfile -File ./eng/doctor.ps1 -Mode CrossPlatform` or `./eng/doctor.ps1 -Mode Windows`.

Use `eng/verify-cross-platform.ps1` for non-Windows checks and `eng/verify-windows.ps1` for the full Windows/WinUI gate. Do not commit `.tools/`, `.cache/`, SDK archives, PowerShell binaries, package caches, certificates, tokens, or logs with credentials.
