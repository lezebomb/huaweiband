param([string]$IsoPath)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
if(!$IsoPath){$IsoPath=(Get-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\latest-vm-phone-lab.txt') -Raw).Trim()+'.iso'}
$bandKeyIso=(Resolve-Path -LiteralPath $IsoPath).Path
$bandKeyMetadata=Get-Content -LiteralPath ($bandKeyIso+'.json') -Raw | ConvertFrom-Json
if($bandKeyMetadata.sha256 -ne (Get-FileHash -LiteralPath $bandKeyIso -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'ISO SHA-256 mismatch'}
$bandKeyEntries=@(& tar.exe -tf $bandKeyIso)
if($LASTEXITCODE){throw 'Cannot enumerate ISO'}
foreach($bandKeyName in $bandKeyEntries){
    if([IO.Path]::IsPathRooted($bandKeyName) -or $bandKeyName -match '(^|[\\/])\.\.([\\/]|$)|:'){throw 'Unsafe ISO path'}
}
$bandKeyExtracted=Join-Path $bandKeyRoot ('artifacts\vm-phone-iso-verification-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $bandKeyExtracted | Out-Null
& tar.exe -xf $bandKeyIso -C $bandKeyExtracted
if($LASTEXITCODE){throw 'Cannot extract ISO'}
$bandKeyManifest=Get-Content -LiteralPath (Join-Path $bandKeyExtracted 'manifest.json') -Raw | ConvertFrom-Json
if($bandKeyManifest.signerKind -ne 1 -or $bandKeyManifest.guestTested -or $bandKeyManifest.phoneToGuestUnlockTested -or $bandKeyManifest.hostRegistrationPerformed -or $bandKeyManifest.productionBackendEnabled -or $bandKeyManifest.phoneKeyIncluded){throw 'Unsafe/inaccurate package metadata'}
$bandKeyExpected=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($bandKeyFile in $bandKeyManifest.files){
    $bandKeyTarget=[IO.Path]::GetFullPath((Join-Path $bandKeyExtracted $bandKeyFile.name))
    if(!$bandKeyTarget.StartsWith($bandKeyExtracted+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or !$bandKeyExpected.Add($bandKeyFile.name)){throw 'Invalid/duplicate manifest path'}
    $bandKeyItem=Get-Item -LiteralPath $bandKeyTarget
    if($bandKeyItem.PSIsContainer -or ($bandKeyItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $bandKeyItem.Length -ne $bandKeyFile.bytes -or (Get-FileHash -LiteralPath $bandKeyTarget -Algorithm SHA256).Hash.ToLowerInvariant() -ne $bandKeyFile.sha256){throw "Payload mismatch: $($bandKeyFile.name)"}
}
$bandKeyActual=@(Get-ChildItem -LiteralPath $bandKeyExtracted -Recurse -File | Where-Object {$_.FullName -ne (Join-Path $bandKeyExtracted 'manifest.json')})
if($bandKeyActual.Count -ne $bandKeyExpected.Count){throw 'Unlisted payload'}
foreach($bandKeyFile in $bandKeyActual){
    $bandKeyRelative=[IO.Path]::GetRelativePath($bandKeyExtracted,$bandKeyFile.FullName) -replace '\\','/'
    if(!$bandKeyExpected.Contains($bandKeyRelative) -or $bandKeyRelative -match '(?i)(^|/)(runtime|Vault|Exchange)/|\.(dpapi|pfx|p12|key|keystore|jks)$|(^|/)autorun\.inf$'){throw 'Unexpected private/runtime/autorun entry'}
}
$bandKeyDllHash=(Get-FileHash -LiteralPath (Join-Path $bandKeyExtracted 'BandKey.CredentialProvider.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
if(![Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $bandKeyExtracted 'BandKey.VmSetup.exe'))).Contains($bandKeyDllHash)){throw 'Installer DLL hash binding missing'}
foreach($bandKeyName in @('BandKey.exe','BandKey.dll','BandKey.Core.dll','coreclr.dll')){
    if((Get-FileHash -LiteralPath (Join-Path $bandKeyExtracted ('VmBridge\'+$bandKeyName))).Hash -ne (Get-FileHash -LiteralPath (Join-Path $bandKeyRoot ('artifacts\BandKey-win-x64\'+$bandKeyName))).Hash){throw 'Stale published relay'}
}
if((Get-FileHash -LiteralPath (Join-Path $bandKeyExtracted 'BandKey-bridge-debug.apk')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $bandKeyRoot 'artifacts\BandKey-bridge-debug.apk')).Hash){throw 'Stale Android payload'}
$bandKeyLauncher=[IO.File]::ReadAllText((Join-Path $bandKeyExtracted 'VmBridge\Start-VM-Bridge.cmd'))
if(!$bandKeyLauncher.Contains('start "" "%~dp0BandKey.exe" --vm-bridge')){throw 'Incorrect relay launcher'}
[ordered]@{iso=$bandKeyIso;sha256=$bandKeyMetadata.sha256;payloadHashesVerified=$bandKeyExpected.Count;installerHashBindingVerified=$true;publishedRelayMatched=$true;apkMatched=$true;knownPrivateRuntimePathsAbsent=$true;guestTested=$false;extracted=$bandKeyExtracted} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\vm-phone-package-verification.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\vm-phone-package-verification.json') -Raw
