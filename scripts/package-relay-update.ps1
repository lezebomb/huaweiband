param([Parameter(Mandatory=$true)][string]$SourceZip,[string]$ReadMeDocument='docs/vm-relay-observation-20261023.md',[string]$PhonePublicKey,[string]$ExpectedPhoneSha256)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
$bandKeyZipPath=(Resolve-Path -LiteralPath $SourceZip).Path
$bandKeyUpdate=Join-Path $bandKeyRoot ('virtualmachine\BandKey-Relay-Update-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
$bandKeyPayload=Join-Path $bandKeyUpdate 'VmBridge'
New-Item -ItemType Directory -Path $bandKeyPayload | Out-Null
$bandKeyZip=[IO.Compression.ZipFile]::OpenRead($bandKeyZipPath)
try {
    foreach($bandKeyEntry in $bandKeyZip.Entries){
        if(!$bandKeyEntry.Name -or $bandKeyEntry.FullName -match '^docs/'){continue}
        if($bandKeyEntry.FullName -match '(?i)(^|/)(runtime|Vault|Exchange)/|\.(dpapi|pfx|p12|key|keystore|jks)$|(^|/)autorun\.inf$|CredentialProvider|VmSetup|VmConfirmation'){throw 'Private or installer material is not permitted in a relay-only update.'}
        $bandKeyTarget=[IO.Path]::GetFullPath((Join-Path $bandKeyPayload $bandKeyEntry.FullName))
        if(!$bandKeyTarget.StartsWith($bandKeyPayload+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe ZIP entry'}
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($bandKeyTarget)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($bandKeyEntry,$bandKeyTarget,$false)
    }
} finally {$bandKeyZip.Dispose()}
foreach($bandKeyRequired in @('BandKey.exe','BandKey.dll','BandKey.Core.dll','coreclr.dll')){
    if(!(Test-Path -LiteralPath (Join-Path $bandKeyPayload $bandKeyRequired))){throw 'Incomplete self-contained relay'}
}
'@echo off'+"`r`n"+'start "" "%~dp0BandKey.exe" --vm-bridge'+"`r`n" | Set-Content -LiteralPath (Join-Path $bandKeyPayload 'Start-VM-Bridge.cmd') -Encoding ascii -NoNewline
Copy-Item -LiteralPath (Join-Path $bandKeyRoot $ReadMeDocument) -Destination (Join-Path $bandKeyUpdate 'READ-ME-FIRST.txt')
if($PhonePublicKey){
    $bandKeyPublic=(Resolve-Path -LiteralPath $PhonePublicKey).Path
    if((Get-Item -LiteralPath $bandKeyPublic).Length -ne 72 -or !$ExpectedPhoneSha256 -or
        (Get-FileHash -LiteralPath $bandKeyPublic -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedPhoneSha256.ToLowerInvariant()){throw 'Public key fingerprint mismatch'}
    Copy-Item -LiteralPath $bandKeyPublic -Destination (Join-Path $bandKeyUpdate 'Phone-VM-public.bandkeypub')
}
$bandKeyFiles=@(Get-ChildItem -LiteralPath $bandKeyUpdate -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{name=([IO.Path]::GetRelativePath($bandKeyUpdate,$_.FullName) -replace '\\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
[ordered]@{schema='bandkey.relay-update.v1';relayOnly=$true;loginInstallerIncluded=$false;phoneKeyIncluded=[bool]$PhonePublicKey;guestTested=$false;hostInstalled=$false;sourceZipSha256=(Get-FileHash -LiteralPath $bandKeyZipPath -Algorithm SHA256).Hash.ToLowerInvariant();files=$bandKeyFiles} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bandKeyUpdate 'manifest.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'new-lab-data-iso.ps1') -SourceDirectory $bandKeyUpdate
# Verify the actual image bytes and payload, without mounting or operating a VM.
$bandKeyVerified=Join-Path $bandKeyRoot ('artifacts\relay-update-verification-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $bandKeyVerified | Out-Null
$bandKeyImagePaths=@(& tar.exe -tf ($bandKeyUpdate+'.iso'))
if($LASTEXITCODE -ne 0 -or ($bandKeyImagePaths | Where-Object {[IO.Path]::IsPathRooted($_) -or $_ -match '(^|[\\/])\.\.([\\/]|$)|:'})){throw 'Unsafe or unreadable ISO'}
& tar.exe -xf ($bandKeyUpdate+'.iso') -C $bandKeyVerified
if($LASTEXITCODE -ne 0){throw 'ISO readback failed'}
$bandKeyManifest=Get-Content -LiteralPath (Join-Path $bandKeyVerified 'manifest.json') -Raw | ConvertFrom-Json
$bandKeyActual=@(Get-ChildItem -LiteralPath $bandKeyVerified -Recurse -File)
if(!$bandKeyManifest.relayOnly -or $bandKeyActual.Count -ne $bandKeyFiles.Count+1){throw 'Unexpected relay payload count'}
foreach($bandKeyFile in $bandKeyFiles){
    $bandKeyItem=Get-Item -LiteralPath (Join-Path $bandKeyVerified $bandKeyFile.name)
    if($bandKeyItem.Length -ne $bandKeyFile.bytes -or (Get-FileHash -LiteralPath $bandKeyItem.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $bandKeyFile.sha256){throw 'ISO payload hash mismatch'}
}
[ordered]@{iso=($bandKeyUpdate+'.iso');sha256=(Get-FileHash -LiteralPath ($bandKeyUpdate+'.iso') -Algorithm SHA256).Hash.ToLowerInvariant();payloadHashesVerified=$bandKeyFiles.Count;guestTested=$false;hostInstalled=$false;loginInstallerIncluded=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\latest-relay-update.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\latest-relay-update.json') -Raw
