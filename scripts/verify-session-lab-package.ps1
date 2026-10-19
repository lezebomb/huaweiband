param([string]$IsoPath)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
if(!$IsoPath){$IsoPath=(Get-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\latest-session-lab.txt') -Raw).Trim()+'.iso'}
$bandKeyIso=(Resolve-Path -LiteralPath $IsoPath).Path
$bandKeyMetadata=Get-Content -LiteralPath ($bandKeyIso+'.json') -Raw | ConvertFrom-Json
if($bandKeyMetadata.sha256 -ne (Get-FileHash -LiteralPath $bandKeyIso).Hash.ToLowerInvariant()){throw 'Session ISO hash mismatch'}
$bandKeyEntries=@(& tar.exe -tf $bandKeyIso)
if($LASTEXITCODE){throw 'Session ISO enumeration failed'}
foreach($bandKeyName in $bandKeyEntries){if([IO.Path]::IsPathRooted($bandKeyName) -or $bandKeyName -match '(^|[\\/])\.\.([\\/]|$)|:'){throw 'Unsafe ISO path'}}
$bandKeyExtracted=Join-Path $bandKeyRoot ('artifacts\session-iso-verification-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $bandKeyExtracted | Out-Null
& tar.exe -xf $bandKeyIso -C $bandKeyExtracted
if($LASTEXITCODE){throw 'Session ISO extraction failed'}
$bandKeyManifest=Get-Content -LiteralPath (Join-Path $bandKeyExtracted 'manifest.json') -Raw | ConvertFrom-Json
if($bandKeyManifest.schema -ne 'bandkey-session-lab-package-v1' -or $bandKeyManifest.guestTested -ne $false -or $bandKeyManifest.installsProvider -ne $false -or $bandKeyManifest.hostLoginConfigurationModified -ne $false -or $bandKeyManifest.realRssiHardwareTested -ne $false -or $bandKeyManifest.sessionObservationsHardwareVerified -ne $false){throw 'Unsafe or inaccurate session metadata'}
$bandKeyExpected=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($bandKeyFile in $bandKeyManifest.files){
 $bandKeyTarget=[IO.Path]::GetFullPath((Join-Path $bandKeyExtracted $bandKeyFile.name))
 if(!$bandKeyTarget.StartsWith($bandKeyExtracted+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or !$bandKeyExpected.Add($bandKeyFile.name)){throw 'Duplicate or unsafe payload path'}
 $bandKeyItem=Get-Item -LiteralPath $bandKeyTarget
 if($bandKeyItem.PSIsContainer -or ($bandKeyItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $bandKeyItem.Length -ne $bandKeyFile.bytes -or (Get-FileHash -LiteralPath $bandKeyTarget).Hash.ToLowerInvariant() -ne $bandKeyFile.sha256){throw 'Session payload mismatch'}
}
$bandKeyActual=@(Get-ChildItem -LiteralPath $bandKeyExtracted -Recurse -File | Where-Object {$_.FullName -ne (Join-Path $bandKeyExtracted 'manifest.json')})
if($bandKeyActual.Count -ne $bandKeyExpected.Count){throw 'Unlisted session payload'}
foreach($bandKeyFile in $bandKeyActual){
 $bandKeyRelative=[IO.Path]::GetRelativePath($bandKeyExtracted,$bandKeyFile.FullName) -replace '\\','/'
 if(!$bandKeyExpected.Contains($bandKeyRelative) -or $bandKeyRelative -match '(?i)(^|/)(runtime|Vault|Exchange)/|\.(dpapi|pfx|p12|key|keystore|jks|apk|pdb)$|(^|/)(autorun\.inf|BandKey\.exe|BandKey\.VmSetup\.exe|BandKey\.CredentialProvider\.dll)$'){throw 'Unexpected private/runtime/installer entry'}
}
foreach($bandKeyRequired in @('Start-VM-Session-Lab.cmd','README.txt','SessionLab/BandKey.SessionLab.exe','SessionLab/BandKey.SessionLab.dll','SessionLab/BandKey.dll','SessionLab/BandKey.Core.dll','SessionLab/coreclr.dll')){if(!$bandKeyExpected.Contains($bandKeyRequired)){throw 'Required session payload absent'}}
$bandKeyLauncher=[IO.File]::ReadAllText((Join-Path $bandKeyExtracted 'Start-VM-Session-Lab.cmd'))
if(!$bandKeyLauncher.Contains('start "" "%~dp0SessionLab\BandKey.SessionLab.exe"')){throw 'Unexpected session launcher'}
[ordered]@{iso=$bandKeyIso;sha256=$bandKeyMetadata.sha256;payloadHashesVerified=$bandKeyExpected.Count;privateRuntimeAndLoginInstallersAbsent=$true;hostGuestRejectionVerified=$bandKeyManifest.hostGuestRejectionVerified;guestTested=$false;extracted=$bandKeyExtracted} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\session-lab-package-verification.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\session-lab-package-verification.json') -Raw
