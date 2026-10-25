param([string]$PhonePublicKey,[string]$ExpectedPhoneSha256)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $bandKeyRoot
$bandKeyReports=@(
    @{path='artifacts/core-tests.json';minimum=142},
    @{path='artifacts/windows-tests.json';minimum=83},
    @{path='artifacts/android-vm-wear-session-tests.json';minimum=23},
    @{path='artifacts/bandkey-provider-lab/tests.json';minimum=221},
    @{path='artifacts/bandkey-provider-lab/renewal-interop.json';minimum=4}
)
foreach($bandKeyReport in $bandKeyReports){
    $bandKeyData=Get-Content -LiteralPath $bandKeyReport.path -Raw | ConvertFrom-Json
    if($bandKeyData.failed -ne 0 -or $bandKeyData.passed -lt $bandKeyReport.minimum){throw ('Missing checks: '+$bandKeyReport.path)}
}
$bandKeyGuard=Get-Content -LiteralPath 'artifacts/bandkey-provider-lab/setup-host-check.json' -Raw | ConvertFrom-Json
if($bandKeyGuard.vmGuardAccepted -or $bandKeyGuard.mutationPerformed -or $bandKeyGuard.providerRegistered){throw 'Physical host refusal missing'}
[xml]$bandKeyLint=Get-Content -LiteralPath 'android/app/build/reports/lint-results-debug.xml' -Raw
if(@($bandKeyLint.issues.issue | Where-Object {$_.severity -in @('Error','Fatal')}).Count){throw 'Android lint failed'}
$bandKeyApk=Join-Path $bandKeyRoot 'artifacts/BandKey-bridge-0.5.0-debug.apk'
if((Get-FileHash -LiteralPath $bandKeyApk).Hash -ne (Get-FileHash -LiteralPath 'artifacts/BandKey-bridge-debug.apk').Hash){throw 'Versioned APK does not match current build'}
$bandKeyOutput=Join-Path $bandKeyRoot ('virtualmachine/BandKey-Fixed-Phone-Lab-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $bandKeyOutput | Out-Null
foreach($bandKeyName in @('BandKey.CredentialProvider.dll','BandKey.VmSetup.exe','BandKey.VmConfirmation.exe')){
    Copy-Item -LiteralPath (Join-Path $bandKeyRoot ('artifacts/bandkey-provider-lab/'+$bandKeyName)) -Destination $bandKeyOutput
}
$bandKeyDllHash=(Get-FileHash -LiteralPath (Join-Path $bandKeyOutput 'BandKey.CredentialProvider.dll')).Hash.ToLowerInvariant()
if(![Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $bandKeyOutput 'BandKey.VmSetup.exe'))).Contains($bandKeyDllHash)){throw 'Installer provider hash mismatch'}
$bandKeyBridge=Join-Path $bandKeyOutput 'VmBridge'
New-Item -ItemType Directory -Path $bandKeyBridge | Out-Null
$bandKeyZip=[IO.Compression.ZipFile]::OpenRead((Join-Path $bandKeyRoot 'artifacts/BandKey-win-x64.zip'))
try{
    foreach($bandKeyEntry in $bandKeyZip.Entries){
        if(!$bandKeyEntry.Name -or $bandKeyEntry.FullName -match '^docs/'){continue}
        $bandKeyTarget=[IO.Path]::GetFullPath((Join-Path $bandKeyBridge $bandKeyEntry.FullName))
        if(!$bandKeyTarget.StartsWith($bandKeyBridge+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe ZIP entry'}
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($bandKeyTarget)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($bandKeyEntry,$bandKeyTarget,$false)
    }
}finally{$bandKeyZip.Dispose()}
if(!(Test-Path -LiteralPath (Join-Path $bandKeyBridge 'BandKey.exe')) -or !(Test-Path -LiteralPath (Join-Path $bandKeyBridge 'coreclr.dll'))){throw 'Self-contained bridge missing'}
$bandKeyStart='@echo off'+[Environment]::NewLine+'start "" "%~dp0BandKey.exe" --vm-bridge'+[Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $bandKeyBridge 'Start-VM-Bridge.cmd'),$bandKeyStart,[Text.Encoding]::ASCII)
Copy-Item -LiteralPath $bandKeyApk -Destination $bandKeyOutput
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'docs/fixed-phone-vm-20261025.md') -Destination (Join-Path $bandKeyOutput 'READ-ME-FIRST.txt')
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'docs/fixed-phone-protocol.md') -Destination $bandKeyOutput
$bandKeyPhoneHash=$null
if($PhonePublicKey){
    $bandKeyPublic=(Resolve-Path -LiteralPath $PhonePublicKey).Path
    $bandKeyPhoneHash=(Get-FileHash -LiteralPath $bandKeyPublic).Hash.ToLowerInvariant()
    if((Get-Item -LiteralPath $bandKeyPublic).Length -ne 72 -or !$ExpectedPhoneSha256 -or $bandKeyPhoneHash -cne $ExpectedPhoneSha256.ToLowerInvariant()){throw 'Explicit public-key fingerprint mismatch'}
    Copy-Item -LiteralPath $bandKeyPublic -Destination (Join-Path $bandKeyOutput 'Phone-VM-public.bandkeypub')
}
$bandKeyFiles=@(Get-ChildItem -LiteralPath $bandKeyOutput -Recurse -File | Sort-Object FullName | ForEach-Object{
    $bandKeyName=[IO.Path]::GetRelativePath($bandKeyOutput,$_.FullName) -replace '\\','/'
    if($bandKeyName -match '(?i)(^|/)(runtime|Vault|Exchange)/|\.(dpapi|pfx|p12|key|keystore|jks)$'){throw ('Private/runtime material: '+$bandKeyName)}
    [ordered]@{name=$bandKeyName;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
})
[ordered]@{
    schema='bandkey.fixed-phone-lab.v1';androidVersion='0.5.0';nativeProtocolVersion=3
    guest='VMware Windows 11 build26300';fixedPhoneImplemented=$true
    fixedPhoneEndToEndHardwareTested=$false;mobilePhoneRssiIntegrated=$false
    hostRegistered=$false;realMicrosoftAccountTested=$false;automaticLockDefault=$false
    phonePublicKeyIncluded=($null -ne $bandKeyPhoneHash);phonePublicKeySha256=$bandKeyPhoneHash
    tests=$bandKeyReports;files=$bandKeyFiles
} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $bandKeyOutput 'manifest.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'new-lab-data-iso.ps1') -SourceDirectory $bandKeyOutput
$bandKeyIso=$bandKeyOutput+'.iso'
$bandKeyVerified=Join-Path $bandKeyRoot ('artifacts/fixed-phone-iso-readback-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $bandKeyVerified | Out-Null
$bandKeyEntries=@(& tar.exe -tf $bandKeyIso)
if($LASTEXITCODE -ne 0 -or ($bandKeyEntries | Where-Object {[IO.Path]::IsPathRooted($_) -or $_ -match '(^|[\\/])\.\.([\\/]|$)|:'})){throw 'Unsafe ISO entry'}
& tar.exe -xf $bandKeyIso -C $bandKeyVerified
if($LASTEXITCODE -ne 0 -or @(Get-ChildItem -LiteralPath $bandKeyVerified -Recurse -File).Count -ne $bandKeyFiles.Count+1){throw 'ISO extraction mismatch'}
foreach($bandKeyFile in $bandKeyFiles){
    $bandKeyPath=Join-Path $bandKeyVerified $bandKeyFile.name
    if((Get-Item -LiteralPath $bandKeyPath).Length -ne $bandKeyFile.bytes -or (Get-FileHash -LiteralPath $bandKeyPath).Hash.ToLowerInvariant() -ne $bandKeyFile.sha256){throw ('ISO hash mismatch: '+$bandKeyFile.name)}
}
[ordered]@{iso=$bandKeyIso;sha256=(Get-FileHash -LiteralPath $bandKeyIso).Hash.ToLowerInvariant();payloadHashesVerified=$bandKeyFiles.Count;androidVersion='0.5.0';guestTested=$false;hostRegistered=$false} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts/latest-fixed-phone-lab.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts/latest-fixed-phone-lab.json') -Raw