$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'BusinessOS.Engineering.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'BusinessOS.Provisioning.psm1') -Force
$Root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path; $Lock=Read-BusinessOSEnvironmentLock; $Bootstrap=Read-BusinessOSBootstrapLock
if($Bootstrap.DOTNET_SDK_VERSION -ne $Lock.dotnetSdk){throw 'Bootstrap DOTNET_SDK_VERSION does not match environment.lock.json'}
foreach($p in '.tools','.cache',$Lock.dotnetRoot,$Lock.powershellRoot,$Lock.powershellModuleRoot,$Lock.nugetCache,$Lock.dotnetHome,$Lock.downloadCache){New-Item -ItemType Directory -Force -Path (Join-Path $Root $p)|Out-Null}
$dotnetRootLocal=Join-Path $Root $Lock.dotnetRoot; $pwshRootLocal=Join-Path $Root $Lock.powershellRoot
$dotnet=Resolve-BusinessOSTool -Name dotnet -LocalRoot $dotnetRootLocal -ExpectedVersion $Lock.dotnetSdk -VersionCommand { param($e) & $e --version }
if($null -eq $dotnet){
  $rid=if($IsWindows){'win-x64'}elseif($IsMacOS){'osx-x64'}else{'linux-x64'}; $asset=$Lock.dotnet.archives.$rid
  $metadataMatch=$false; foreach($murl in @($Lock.dotnet.metadata)){try{$m=Invoke-RestMethod -Uri $murl -UseBasicParsing; foreach($rel in @($m.releases)){foreach($sdk in @($rel.sdks)){if($sdk.version -eq $Lock.dotnetSdk){$f=@($sdk.files|Where-Object{$_.rid -eq $rid -and $_.url -eq $asset.url -and $_.hash -eq $asset.sha512})[0]; if($f){$metadataMatch=$true; break}}}; if($metadataMatch){break}}}catch{Write-Warning "metadata failed URL=$murl ERROR=$($_.Exception.Message)"}; if($metadataMatch){break}}
  if(-not $metadataMatch){throw "Pinned .NET archive URL/SHA512 does not match official metadata for $rid."}
  $ext=if($IsWindows){'zip'}else{'tar.gz'}; $archive=Join-Path $Root "$($Lock.downloadCache)/dotnet-sdk-$($Lock.dotnetSdk)-$rid.$ext"
  $dl=Invoke-DownloadWithFallback -Sources @($asset.url) -OutFile $archive -Checksum $asset.sha512 -Algorithm SHA512
  $expected=if($IsWindows){'dotnet.exe'}else{'dotnet'}
  Expand-VerifiedArchive -Archive $archive -Destination $dotnetRootLocal -Kind $ext -ExpectedExecutable $expected | Out-Null
  $dotnet=Resolve-BusinessOSTool -Name dotnet -LocalRoot $dotnetRootLocal -ExpectedVersion $Lock.dotnetSdk -VersionCommand { param($e) & $e --version }
}
if($null -eq $dotnet){throw 'dotnet provisioning failed'}
$pwsh=Resolve-BusinessOSTool -Name pwsh -LocalRoot $pwshRootLocal -ExpectedVersion $Lock.powershell.version -VersionCommand { param($e) & $e -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' }
if($null -eq $pwsh){
  $rid=if($IsWindows){'win-x64'}elseif($IsMacOS){'osx-x64'}else{'linux-x64'}; $a=$Lock.powershell.archives.$rid; $ext=if($IsWindows){'zip'}else{'tar.gz'}; $archive=Join-Path $Root "$($Lock.downloadCache)/powershell-$($Lock.powershell.version)-$rid.$ext"
  Invoke-DownloadWithFallback -Sources @($a.urls) -OutFile $archive -Checksum $a.sha256 -Algorithm SHA256 | Out-Host
  $expected=if($IsWindows){'pwsh.exe'}else{'pwsh'}
  Expand-VerifiedArchive -Archive $archive -Destination $pwshRootLocal -Kind $ext -ExpectedExecutable $expected | Out-Null
  if(-not $IsWindows){chmod +x (Join-Path $pwshRootLocal 'pwsh')}
  $pwsh=Resolve-BusinessOSTool -Name pwsh -LocalRoot $pwshRootLocal -ExpectedVersion $Lock.powershell.version -VersionCommand { param($e) & $e -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' }
}
if($null -eq $pwsh){throw 'PowerShell provisioning failed'}
$ghRoot=Join-Path $Root $Lock.githubCli.installRoot
$gh=Resolve-BusinessOSTool -Name gh -LocalRoot $ghRoot -ExpectedVersion $Lock.githubCli.version -VersionCommand {param($e) ((& $e --version)[0] -split ' ')[2]}
if($null -eq $gh){$rid=if($IsWindows){'win-x64'}elseif($IsMacOS){'osx-x64'}else{'linux-x64'};$a=$Lock.githubCli.archives.$rid;$ext=if($a.url -match '\.zip$'){'zip'}else{'tar.gz'};$archive=Join-Path $Root "$($Lock.downloadCache)/github-cli-$($Lock.githubCli.version)-$rid.$ext";Invoke-DownloadWithFallback -Sources @($a.url) -OutFile $archive -Checksum $a.sha256 -Algorithm SHA256|Out-Host;if(Test-Path $ghRoot){Remove-Item $ghRoot -Recurse -Force};New-Item -ItemType Directory -Force $ghRoot|Out-Null;if($ext-eq'zip'){Expand-Archive $archive $ghRoot -Force}else{& tar -xzf $archive -C $ghRoot --strip-components=1};$candidate=Get-ChildItem $ghRoot -Recurse -File|Where-Object Name -in @('gh','gh.exe')|Select-Object -First 1;if(-not $candidate){throw 'GitHub CLI executable missing'};$gh=[pscustomobject]@{Executable=$candidate.FullName;Root=(Split-Path $candidate.FullName -Parent);Source='local';Version=$Lock.githubCli.version}}
if($null -eq $gh){throw 'GitHub CLI provisioning failed'}
$resolved=[pscustomobject]@{dotnetExecutable=$dotnet.Executable;dotnetRoot=$dotnet.Root;dotnetSource=$dotnet.Source;powershellExecutable=$pwsh.Executable;powershellRoot=$pwsh.Root;powershellSource=$pwsh.Source;nugetPackages=(Join-Path $Root $Lock.nugetCache);dotnetCliHome=(Join-Path $Root $Lock.dotnetHome);powershellModuleRoot=(Join-Path $Root $Lock.powershellModuleRoot);githubCliExecutable=$gh.Executable;githubCliRoot=$gh.Root;githubCliSource=$gh.Source;githubCliVersion=$gh.Version}
$resolvedPath=Join-Path $Root '.cache/environment.resolved.json'; $resolved|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $resolvedPath
@("DOTNET_ROOT=$($resolved.dotnetRoot)","NUGET_PACKAGES=$($resolved.nugetPackages)","DOTNET_CLI_HOME=$($resolved.dotnetCliHome)","POWERSHELL_ROOT=$($resolved.powershellRoot)","PSMODULE_ROOT=$($resolved.powershellModuleRoot)","DOTNET_EXE=$($resolved.dotnetExecutable)","PWSH_EXE=$($resolved.powershellExecutable)","GITHUB_CLI_ROOT=$($resolved.githubCliRoot)","GH_EXE=$($resolved.githubCliExecutable)")|Set-Content -LiteralPath (Join-Path $Root '.cache/environment.resolved.env')
Write-Host "BusinessOS environment setup completed. Resolved state: $resolvedPath"
