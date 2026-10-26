param([switch]$IncludeRelay,[string]$PhonePublicKey,[string]$ExpectedPhoneSha256)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
$bandKeyInput=Join-Path $bandKeyRoot 'artifacts\bandkey-provider-lab'
$bandKeyTests=Get-Content -LiteralPath (Join-Path $bandKeyInput 'tests.json') -Raw | ConvertFrom-Json
$bandKeyGuard=Get-Content -LiteralPath (Join-Path $bandKeyInput 'setup-host-check.json') -Raw | ConvertFrom-Json
if($bandKeyTests.failed -ne 0 -or $bandKeyTests.passed -lt 236 -or $bandKeyTests.registered -or $bandKeyTests.windowsAuthenticationSubmitted -or $bandKeyGuard.vmGuardAccepted -or $bandKeyGuard.mutationPerformed -or $bandKeyGuard.providerRegistered -or $bandKeyGuard.deploymentRecoveryVersion -ne 1){throw 'Native checks or host refusal missing'}
$bandKeyOutput=Join-Path $bandKeyRoot ('virtualmachine\BandKey-MSA-VM-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $bandKeyOutput | Out-Null
$bandKeyNativeFiles=@('BandKey.CredentialProvider.dll','BandKey.VmSetup.exe')
if(!$IncludeRelay){$bandKeyNativeFiles+='BandKey.VmConfirmation.exe'}
foreach($bandKeyName in $bandKeyNativeFiles){
    Copy-Item -LiteralPath (Join-Path $bandKeyInput $bandKeyName) -Destination $bandKeyOutput
}
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'docs\host-account-rollout-20261025.md') -Destination (Join-Path $bandKeyOutput 'READ-ME-FIRST.txt')
$bandKeyDllHash=(Get-FileHash -LiteralPath (Join-Path $bandKeyOutput 'BandKey.CredentialProvider.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
if(![Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $bandKeyOutput 'BandKey.VmSetup.exe'))).Contains($bandKeyDllHash)){throw 'Installer DLL hash mismatch'}
if($IncludeRelay){
    foreach($bandKeyCheck in @(@{file='core-tests.json';minimum=151},@{file='windows-tests.json';minimum=88})){
        $bandKeyReport=Get-Content -LiteralPath (Join-Path $bandKeyRoot ('artifacts/'+$bandKeyCheck.file)) -Raw|ConvertFrom-Json
        if($bandKeyReport.failed -ne 0 -or $bandKeyReport.passed -lt $bandKeyCheck.minimum){throw 'Current relay verification missing'}
    }
    $bandKeyBridge=Join-Path $bandKeyOutput 'VmBridge'
    New-Item -ItemType Directory -Path $bandKeyBridge|Out-Null
    $bandKeyZip=[IO.Compression.ZipFile]::OpenRead((Join-Path $bandKeyRoot 'artifacts/BandKey-win-x64.zip'))
    try{
        foreach($bandKeyEntry in $bandKeyZip.Entries){
            if(!$bandKeyEntry.Name -or $bandKeyEntry.FullName -match '^docs/'){continue}
            if([IO.Path]::IsPathRooted($bandKeyEntry.FullName) -or $bandKeyEntry.FullName -match '(^|[\\/])\.\.([\\/]|$)|:'){throw 'Unsafe archive path'}
            $bandKeyTarget=[IO.Path]::GetFullPath((Join-Path $bandKeyBridge $bandKeyEntry.FullName))
            if(!$bandKeyTarget.StartsWith($bandKeyBridge+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $bandKeyEntry.FullName -match '(?i)(^|/)(runtime|Vault|Exchange)/|\.(dpapi|pfx|p12|key|keystore|jks|apk)$'){throw 'Unsafe/private archive entry'}
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($bandKeyTarget))|Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($bandKeyEntry,$bandKeyTarget,$false)
        }
    }finally{$bandKeyZip.Dispose()}
    foreach($bandKeyFile in @('BandKey.exe','BandKey.dll','BandKey.Core.dll','BandKey.runtimeconfig.json','coreclr.dll')){
        if((Get-FileHash -LiteralPath (Join-Path $bandKeyBridge $bandKeyFile)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $bandKeyRoot ('artifacts/BandKey-win-x64/'+$bandKeyFile))).Hash){throw 'Screened relay archive differs from current publish'}
    }
    $bandKeyLaunch='@echo off'+[Environment]::NewLine+'start "" "%~dp0BandKey.exe" --vm-bridge'+[Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $bandKeyBridge 'Start-VM-Bridge.cmd'),$bandKeyLaunch,[Text.Encoding]::ASCII)
}
if($PhonePublicKey){
    $bandKeyPublic=(Resolve-Path -LiteralPath $PhonePublicKey).Path
    if((Get-Item -LiteralPath $bandKeyPublic).Length -ne 72 -or $ExpectedPhoneSha256 -cnotmatch '^[a-f0-9]{64}$' -or (Get-FileHash -LiteralPath $bandKeyPublic).Hash.ToLowerInvariant() -cne $ExpectedPhoneSha256){throw 'Explicit public key fingerprint mismatch'}
    Copy-Item -LiteralPath $bandKeyPublic -Destination (Join-Path $bandKeyOutput 'Phone-VM-public.bandkeypub')
}
$bandKeyFiles=@(Get-ChildItem -LiteralPath $bandKeyOutput -Recurse -File | Sort-Object Name | ForEach-Object {
    [ordered]@{name=([IO.Path]::GetRelativePath($bandKeyOutput,$_.FullName)-replace '\\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
[ordered]@{schema='bandkey.vm-account-backend.v2';supportedGuest='VMware Windows 11 build 26300';nativeChecksPassed=$bandKeyTests.passed;guestTested=$false;realMicrosoftAccountTested=$false;hostRegistered=$false;phoneKeyIncluded=([bool]$PhonePublicKey);relayIncluded=[bool]$IncludeRelay;androidApkIncluded=$false;nativeProtocolVersion=3;deploymentRecoveryVersion=1;files=$bandKeyFiles} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bandKeyOutput 'manifest.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'new-lab-data-iso.ps1') -SourceDirectory $bandKeyOutput
$bandKeyVerified=Join-Path $bandKeyRoot ('artifacts\account-iso-verification-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $bandKeyVerified | Out-Null
$bandKeyEntries=@(& tar.exe -tf ($bandKeyOutput+'.iso'))
if($LASTEXITCODE -ne 0 -or ($bandKeyEntries | Where-Object {[IO.Path]::IsPathRooted($_) -or $_ -match '(^|[\\/])\.\.([\\/]|$)|:'})){throw 'Unsafe ISO path'}
& tar.exe -xf ($bandKeyOutput+'.iso') -C $bandKeyVerified
if($LASTEXITCODE -ne 0 -or @(Get-ChildItem -LiteralPath $bandKeyVerified -Recurse -File).Count -ne $bandKeyFiles.Count+1){throw 'ISO readback mismatch'}
foreach($bandKeyFile in $bandKeyFiles){
    $bandKeyItem=Get-Item -LiteralPath (Join-Path $bandKeyVerified $bandKeyFile.name)
    if($bandKeyItem.Length -ne $bandKeyFile.bytes -or (Get-FileHash -LiteralPath $bandKeyItem.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $bandKeyFile.sha256){throw 'ISO payload mismatch'}
}
[ordered]@{iso=($bandKeyOutput+'.iso');sha256=(Get-FileHash -LiteralPath ($bandKeyOutput+'.iso') -Algorithm SHA256).Hash.ToLowerInvariant();payloadHashesVerified=$bandKeyFiles.Count;guestTested=$false;realMicrosoftAccountTested=$false;hostRegistered=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\latest-account-backend.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\latest-account-backend.json') -Raw
