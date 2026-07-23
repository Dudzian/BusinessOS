$ErrorActionPreference='Stop'
Describe 'BusinessOS environment scripts' {
  It 'has a non-empty manifest matching global.json' { $m=Get-Content eng/environment.lock.json -Raw|ConvertFrom-Json; $g=Get-Content global.json -Raw|ConvertFrom-Json; $m.dotnetSdk | Should -Be $g.sdk.version }
  It 'activation is idempotent without PATH duplicates' { . ./eng/activate-environment.ps1; $p1=$env:PATH; . ./eng/activate-environment.ps1; $env:PATH | Should -Be $p1 }
  It 'distinguishes CrossPlatform and Windows doctor modes' { (Get-Content eng/doctor.ps1 -Raw) | Should -Match "ValidateSet\('CrossPlatform','Windows'\)" }
  It 'checked command fails on non-zero exit' { Import-Module ./eng/BusinessOS.Engineering.psm1 -Force; { Invoke-CheckedCommand pwsh @('-NoProfile','-Command','exit 9') } | Should -Throw }
  It 'setup scripts validate empty manifests, SDK, PowerShell, and cache writability in source' { $s=Get-Content eng/setup-environment.ps1 -Raw; $d=Get-Content eng/doctor.ps1 -Raw; $s | Should -Match 'Empty environment manifest'; $d | Should -Match '.NET SDK'; $d | Should -Match 'PowerShell'; $d | Should -Match 'write-test' }
}
