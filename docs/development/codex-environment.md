# Codex environment start

Every future Codex session must prepare and activate the environment before code changes.

## Linux/macOS

```bash
./eng/setup-environment.sh
source ./eng/activate-environment.sh
pwsh -NoProfile -File ./eng/doctor.ps1 -Mode CrossPlatform
pwsh -NoProfile -File ./eng/verify-cross-platform.ps1
```

## Windows

```powershell
./eng/setup-environment.ps1
. ./eng/activate-environment.ps1
./eng/doctor.ps1 -Mode Windows
./eng/verify-windows.ps1
```

If installation is blocked by network or container policy, the report must include the attempted source URL, error code, alternative sources checked, and all checks that could still be completed. `dotnet: command not found` alone is not a sufficient environment preparation attempt.
