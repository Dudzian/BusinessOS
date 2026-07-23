# BusinessOS

BusinessOS uses a pinned, reproducible engineering environment. Start with the scripts below instead of guessing SDK, PowerShell, NuGet cache, or WinUI prerequisites.

## Linux/macOS

```bash
./eng/setup-environment.sh
source ./eng/activate-environment.sh
pwsh -NoProfile -File ./eng/doctor.ps1 -Mode CrossPlatform
pwsh -NoProfile -File ./eng/verify.ps1
```

## Windows

```powershell
./eng/setup-environment.ps1
. ./eng/activate-environment.ps1
./eng/doctor.ps1 -Mode Windows
./eng/verify.ps1
```

Pinned versions live in `eng/environment.lock.json`; `global.json` pins .NET SDK resolution to `.tools/dotnet` before the host SDK.
