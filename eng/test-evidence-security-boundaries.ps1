$ErrorActionPreference='Stop'
$root=(Resolve-Path "$PSScriptRoot/..").Path
$auditor="$PSScriptRoot/audit-github-ci.ps1"
$failed=0
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-Zip([string]$Path,[object[]]$Entries){
 if(Test-Path $Path){Remove-Item $Path -Force}
 $archive=[IO.Compression.ZipFile]::Open($Path,[IO.Compression.ZipArchiveMode]::Create)
 try{foreach($spec in $Entries){$entry=$archive.CreateEntry($spec.Name,[IO.Compression.CompressionLevel]::Fastest);if($spec.Directory){continue};$stream=$entry.Open();try{if($spec.Size){$buffer=[byte[]]::new(1048576);[long]$remaining=$spec.Size;while($remaining-gt 0){$count=[int][Math]::Min($buffer.Length,$remaining);$stream.Write($buffer,0,$count);$remaining-=$count}}else{$bytes=[Text.Encoding]::UTF8.GetBytes($(if($null-ne$spec.Content){$spec.Content}else{'x'}));$stream.Write($bytes,0,$bytes.Length)}}finally{$stream.Dispose()}}}finally{$archive.Dispose()}
}
function Invoke-ZipBoundary([string]$Name,[object[]]$Entries,[switch]$ExistingDestination){
 $fixture="$root/artifacts/generated-security-fixtures/$Name";if(Test-Path $fixture){Remove-Item $fixture -Recurse -Force};Copy-Item "$root/tests/fixtures/github-api/green-pr" $fixture -Recurse
 $zip="$fixture/boundary.zip";New-Zip $zip $Entries
 $input=Get-Content "$fixture/audit-input.json" -Raw|ConvertFrom-Json;$input.artifacts[0]|Add-Member zipPath 'boundary.zip';$input|ConvertTo-Json -Depth 30|Set-Content "$fixture/audit-input.json" -Encoding utf8NoBOM
 $output="artifacts/security-boundary-tests/$Name";$destination="$root/$output/extracted/businessos-cross-platform-100-1";if(Test-Path "$root/$output"){Remove-Item "$root/$output" -Recurse -Force};if($ExistingDestination){New-Item $destination -ItemType Directory -Force|Out-Null;Set-Content "$destination/sentinel.txt" 'preserve'}
 & pwsh -NoProfile -File $auditor -Repository Dudzian/BusinessOS -PullRequest 20 -FixtureRoot $fixture -OutputDirectory $output
 if($LASTEXITCODE-ne 35){Write-Error "$Name expected exit 35, got $LASTEXITCODE";$script:failed++}
 if($ExistingDestination){if((Get-Content "$destination/sentinel.txt" -Raw).Trim()-ne'preserve'-or@(Get-ChildItem $destination -Recurse -File).Count-ne 1){Write-Error "$Name modified an existing destination";$script:failed++}}elseif(Test-Path $destination){Write-Error "$Name created destination before preflight completed";$script:failed++}
 Write-Host "ZIP boundary $Name PASS"
}
$small=@{Content='x'}
Invoke-ZipBoundary duplicate-path @(@{Name='summary.json';Content='a'},@{Name='summary.json';Content='b'})
Invoke-ZipBoundary case-duplicate @(@{Name='summary.json';Content='a'},@{Name='SUMMARY.JSON';Content='b'})
Invoke-ZipBoundary absolute-posix @(@{Name='/outside.txt';Content='x'})
Invoke-ZipBoundary absolute-windows-drive @(@{Name='C:\outside.txt';Content='x'})
Invoke-ZipBoundary absolute-windows-unc @(@{Name='\\server\share\outside.txt';Content='x'})
Invoke-ZipBoundary traversal-forward @(@{Name='../outside.txt';Content='x'})
Invoke-ZipBoundary traversal-backslash @(@{Name='..\outside.txt';Content='x'})
Invoke-ZipBoundary file-directory @(@{Name='node';Content='x'},@{Name='node/child.txt';Content='x'}) -ExistingDestination
Invoke-ZipBoundary directory-file @(@{Name='node/';Directory=$true},@{Name='node';Content='x'}) -ExistingDestination
$countEntries=for($i=0;$i-lt 2049;$i++){@{Name="entries/$i.txt";Content=''}};Invoke-ZipBoundary entry-count $countEntries
Invoke-ZipBoundary single-size @(@{Name='large.bin';Size=67108865})
Invoke-ZipBoundary total-size @(@{Name='a.bin';Size=62914560},@{Name='b.bin';Size=62914560},@{Name='c.bin';Size=62914560},@{Name='d.bin';Size=62914560},@{Name='e.bin';Size=62914560})

Import-Module "$PSScriptRoot/BusinessOS.CiEvidence.psm1" -Force
$manifestRoot="$root/artifacts/manifest-boundary-tests";if(Test-Path $manifestRoot){Remove-Item $manifestRoot -Recurse -Force};New-Item $manifestRoot -ItemType Directory|Out-Null;Set-Content "$manifestRoot/data.txt" 'data' -NoNewline
function Entry([string]$Path='data.txt'){[pscustomobject]@{relativePath=$Path;sizeBytes=(Get-Item "$manifestRoot/data.txt").Length;sha256=(Get-FileHash "$manifestRoot/data.txt" -Algorithm SHA256).Hash.ToLowerInvariant();contentType='text/plain'}}
function Manifest([object]$Files){[pscustomobject]@{schemaVersion=1;generatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');files=$Files}}
function Reject-Manifest([string]$Name,[object]$Value){try{Test-BusinessOSManifest $manifestRoot $Value|Out-Null;Write-Error "$Name was accepted";$script:failed++}catch{Write-Host "Manifest boundary $Name PASS"}}
Test-BusinessOSManifest $manifestRoot (Manifest ([object[]]@((Entry))))|Out-Null;Write-Host 'Manifest boundary valid PASS'
Reject-Manifest files-scalar (Manifest (Entry));Reject-Manifest duplicate (Manifest ([object[]]@((Entry),(Entry))));Reject-Manifest case-duplicate (Manifest ([object[]]@((Entry),(Entry 'DATA.TXT'))));Reject-Manifest traversal (Manifest ([object[]]@((Entry '../data.txt'))));Reject-Manifest absolute-posix (Manifest ([object[]]@((Entry '/data.txt'))));Reject-Manifest absolute-windows (Manifest ([object[]]@((Entry 'C:\data.txt'))));Reject-Manifest missing-file (Manifest ([object[]]@((Entry 'missing.txt'))))
Set-Content "$manifestRoot/extra.txt" 'extra';Reject-Manifest unmanifested-file (Manifest ([object[]]@((Entry))));Remove-Item "$manifestRoot/extra.txt"
$bad=Entry;$bad.sizeBytes=-1;Reject-Manifest bad-size (Manifest ([object[]]@($bad)));$bad=Entry;$bad.sha256='A'*64;Reject-Manifest bad-sha (Manifest ([object[]]@($bad)));$bad=Entry;$bad|Add-Member unexpected $true;Reject-Manifest unknown-property (Manifest ([object[]]@($bad)));$bad=Entry;$bad.PSObject.Properties.Remove('contentType');Reject-Manifest missing-property (Manifest ([object[]]@($bad)));$bad=Entry;$bad.contentType='application/octet-stream';Reject-Manifest bad-content-type (Manifest ([object[]]@($bad)));Reject-Manifest file-directory-collision (Manifest ([object[]]@((Entry 'data.txt'),(Entry 'data.txt/child'))))
function Raw-Entry{[ordered]@{relativePath='data.txt';sizeBytes=(Get-Item "$manifestRoot/data.txt").Length;sha256=(Get-FileHash "$manifestRoot/data.txt" -Algorithm SHA256).Hash.ToLowerInvariant();contentType='text/plain'}}
function Raw-Envelope([object]$GeneratedAt='2026-01-01T00:00:00Z'){[ordered]@{schemaVersion=1;generatedAtUtc=$GeneratedAt;files=@(Raw-Entry)}}
function Invoke-RawManifestCase([string]$Name,[object]$Envelope,[bool]$Expected){$path="$manifestRoot/manifest.json";$Envelope|ConvertTo-Json -Depth 10|Set-Content $path -Encoding utf8NoBOM;try{$value=Read-BusinessOSManifest -Root $manifestRoot -Path $path;if(-not$Expected){Write-Error "$Name was accepted";$script:failed++}else{Write-Host "Raw manifest $Name PASS";return $value}}catch{if($Expected){Write-Error "$Name was rejected: $($_.Exception.Message)";$script:failed++}else{Write-Host "Raw manifest $Name PASS"}}finally{Remove-Item $path -ErrorAction SilentlyContinue}}
$value=Invoke-RawManifestCase utc-z (Raw-Envelope '2026-01-01T00:00:00Z') $true;if($value.generatedAtUtc-isnot[datetime]){Write-Error 'PowerShell 7.4 did not deserialize the validated timestamp as datetime';$failed++}else{Write-Host 'Raw manifest post-conversion datetime PASS'}
Invoke-RawManifestCase utc-zero-offset (Raw-Envelope '2026-01-01T00:00:00+00:00') $true|Out-Null
Invoke-RawManifestCase canonical-staging ([ordered]@{schemaVersion=1;generatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');files=@(Raw-Entry)}) $true|Out-Null
$greenRoot="$root/tests/fixtures/github-api/green-pr/cross-platform";Read-BusinessOSManifest -Root $greenRoot -Path "$greenRoot/manifest.json"|Out-Null;Write-Host 'Raw manifest canonical-green-pr PASS'
$case=Raw-Envelope;$case.Remove('schemaVersion');Invoke-RawManifestCase missing-schema-version $case $false
$case=Raw-Envelope;$case.schemaVersion='1';Invoke-RawManifestCase string-schema-version $case $false
$case=Raw-Envelope;$case.schemaVersion=1.5;Invoke-RawManifestCase fractional-schema-version $case $false
$case=Raw-Envelope;$case.schemaVersion=$null;Invoke-RawManifestCase null-schema-version $case $false
$case=Raw-Envelope;$case.Remove('generatedAtUtc');Invoke-RawManifestCase missing-generated-at $case $false
foreach($item in @(@{Name='null-generated-at';Value=$null},@{Name='number-generated-at';Value=123},@{Name='boolean-generated-at';Value=$true},@{Name='object-generated-at';Value=@{value='x'}},@{Name='array-generated-at';Value=@('x')},@{Name='empty-generated-at';Value=''},@{Name='invalid-generated-at';Value='not-a-date'},@{Name='non-utc-generated-at';Value='2026-01-01T01:00:00+01:00'})){$case=Raw-Envelope;$case.generatedAtUtc=$item.Value;Invoke-RawManifestCase $item.Name $case $false}
$case=Raw-Envelope;$case.Remove('files');Invoke-RawManifestCase missing-files $case $false
foreach($item in @(@{Name='object-files';Value=@{relativePath='data.txt'}},@{Name='string-files';Value='data.txt'},@{Name='null-files';Value=$null},@{Name='empty-files';Value=@()})){$case=Raw-Envelope;$case.files=$item.Value;Invoke-RawManifestCase $item.Name $case $false}
$case=Raw-Envelope;$case.extra=$true;Invoke-RawManifestCase unknown-root-property $case $false
Write-Host "Evidence security boundary failures: $failed";exit $(if($failed){1}else{0})
