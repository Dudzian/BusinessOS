$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Missing dotnet CLI; cannot verify solution membership.'
}
$projectsOnDisk = Get-ChildItem -Path src, tests -Recurse -Filter *.csproj | ForEach-Object { $_.FullName.Substring($repoRoot.Path.Length + 1).Replace('\', '/') } | Sort-Object
$solutionOutput = dotnet sln BusinessOS.sln list
if ($LASTEXITCODE -ne 0) { throw "dotnet sln list failed with exit code $LASTEXITCODE." }
$solutionProjects = $solutionOutput | Select-Object -Skip 2 | ForEach-Object { $_.Trim().Replace('\', '/') } | Where-Object { $_ } | Sort-Object
if ($solutionProjects.Count -eq 0) { throw 'BusinessOS.sln does not contain any projects.' }
$missing = Compare-Object -ReferenceObject $projectsOnDisk -DifferenceObject $solutionProjects | Where-Object { $_.SideIndicator -eq '<=' } | ForEach-Object { $_.InputObject }
$extra = Compare-Object -ReferenceObject $projectsOnDisk -DifferenceObject $solutionProjects | Where-Object { $_.SideIndicator -eq '=>' } | ForEach-Object { $_.InputObject }
if ($missing -or $extra) {
    if ($missing) { Write-Error "Projects missing from solution: $($missing -join ', ')" }
    if ($extra) { Write-Error "Projects listed in solution but not on disk: $($extra -join ', ')" }
    exit 1
}
Write-Host "Solution project membership is complete: $($solutionProjects.Count) projects."
