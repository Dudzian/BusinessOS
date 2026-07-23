Set-StrictMode -Version Latest
function Assert-FileHash {
  param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$Expected,[ValidateSet('SHA256','SHA512')][string]$Algorithm='SHA256')
  $len = if($Algorithm -eq 'SHA512'){128}else{64}
  if([string]::IsNullOrWhiteSpace($Expected) -or $Expected -notmatch "^[A-Fa-f0-9]{$len}$"){throw "Missing or invalid $Algorithm checksum for $Path"}
  $upper=$Expected.ToUpperInvariant()
  if($upper -match '^(.)\1+$' -or $upper -in @('TODO','TBD','PLACEHOLDER')){throw "Placeholder-like $Algorithm checksum is not allowed for $Path"}
  if(-not(Test-Path -LiteralPath $Path)){throw "File not found for checksum: $Path"}
  $actual=(Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash.ToUpperInvariant()
  if($actual -ne $upper){throw "$Algorithm checksum mismatch for $Path. Expected $Expected, got $actual"}
  $actual
}
function Invoke-DownloadWithFallback {
  param([Parameter(Mandatory)][object[]]$Sources,[Parameter(Mandatory)][string]$OutFile,[Parameter(Mandatory)][string]$Checksum,[ValidateSet('SHA256','SHA512')][string]$Algorithm='SHA256',[scriptblock]$DownloadAdapter)
  $results=@()
  foreach($source in $Sources){
    $url = if($source -is [string]){$source}else{$source.Url}
    try{
      Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
      if($DownloadAdapter){$r=& $DownloadAdapter $url $OutFile}else{$r=Invoke-WebRequest -Uri $url -OutFile $OutFile -UseBasicParsing -PassThru; $r=[pscustomobject]@{StatusCode=$r.StatusCode;Bytes=(Get-Item $OutFile).Length}}
      $bytes=if(Test-Path $OutFile){(Get-Item $OutFile).Length}else{0}
      $status=$r.StatusCode
      if($status -ne 200 -or $bytes -le 0){$results += "URL=$url HTTP=$status bytes=$bytes checksum=not checked"; continue}
      try{$hash=Assert-FileHash -Path $OutFile -Expected $Checksum -Algorithm $Algorithm; $results += "URL=$url HTTP=$status bytes=$bytes checksum=$Algorithm OK $hash"; return [pscustomobject]@{Url=$url;Path=$OutFile;Bytes=$bytes;Hash=$hash;Attempts=$results}}
      catch{$results += "URL=$url HTTP=$status bytes=$bytes checksum=$($_.Exception.Message)"; continue}
    } catch { $results += "URL=$url ERROR=$($_.Exception.Message) bytes=0 checksum=not checked" }
  }
  throw "All downloads failed: $($results -join ' | '). Global network block possible."
}
function Expand-VerifiedArchive {
  param([Parameter(Mandatory)][string]$Archive,[Parameter(Mandatory)][string]$Destination,[ValidateSet('zip','tar.gz')][string]$Kind,[Parameter(Mandatory)][string]$ExpectedExecutable)
  if((Get-Item -LiteralPath $Archive).Length -le 0){throw "Archive is empty: $Archive"}
  New-Item -ItemType Directory -Force -Path $Destination | Out-Null
  if($Kind -eq 'zip'){
    try{Add-Type -AssemblyName System.IO.Compression.FileSystem; $zip=[IO.Compression.ZipFile]::OpenRead($Archive); $zip.Dispose()}catch{throw "Invalid ZIP archive: $Archive. $($_.Exception.Message)"}
    Expand-Archive -LiteralPath $Archive -DestinationPath $Destination -Force
  } else {
    & tar -tzf $Archive *> $null; if($LASTEXITCODE -ne 0){throw "Invalid TAR.GZ archive: $Archive"}
    & tar -xzf $Archive -C $Destination; if($LASTEXITCODE -ne 0){throw "TAR.GZ extraction failed: $Archive"}
  }
  $exe=Join-Path $Destination $ExpectedExecutable
  if(-not(Test-Path -LiteralPath $exe)){throw "Expected executable missing after extraction: $exe"}
  $exe
}
function Resolve-BusinessOSTool {
  param([Parameter(Mandatory)][string]$Name,[Parameter(Mandatory)][string]$LocalRoot,[Parameter(Mandatory)][string]$ExpectedVersion,[Parameter(Mandatory)][scriptblock]$VersionCommand)
  $exeName=if($IsWindows){"$Name.exe"}else{$Name}; $local=Join-Path $LocalRoot $exeName
  if(Test-Path -LiteralPath $local){$v=& $VersionCommand $local; if($v -eq $ExpectedVersion){return [pscustomobject]@{Executable=$local;Root=$LocalRoot;Source='local';Version=$v}}}
  $cmd=Get-Command $Name -ErrorAction SilentlyContinue
  if($cmd){$v=& $VersionCommand $cmd.Source; if($v -eq $ExpectedVersion){return [pscustomobject]@{Executable=$cmd.Source;Root=(Split-Path -Parent $cmd.Source);Source='host';Version=$v}}}
  $null
}
Export-ModuleMember -Function Assert-FileHash,Invoke-DownloadWithFallback,Expand-VerifiedArchive,Resolve-BusinessOSTool
