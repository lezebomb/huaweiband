param([switch]$SkipBuild)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $bandKeyRoot
if(!$SkipBuild){
    & (Join-Path $PSScriptRoot 'build.ps1') -Publish
    & (Join-Path $PSScriptRoot 'test-bandkey-provider.ps1')
    & (Join-Path $PSScriptRoot 'build-android.ps1') -SdkPath $env:ANDROID_HOME -Lint
}
$bandKeyInput=Join-Path $bandKeyRoot 'artifacts\bandkey-provider-lab'
$bandKeyPublished=Join-Path $bandKeyRoot 'artifacts\BandKey-win-x64'
$bandKeyReports=@('artifacts/core-tests.json','artifacts/windows-tests.json','artifacts/bandkey-provider-lab/tests.json','artifacts/bandkey-provider-lab/grant-interop.json','artifacts/bandkey-provider-lab/java-grant-interop.json','artifacts/android-runtime-tests.json','artifacts/android-ble-codec-tests.json','artifacts/ble-codec-interop/interop.json')
foreach($bandKeyReportPath in $bandKeyReports){
    $bandKeyReport=Get-Content -LiteralPath $bandKeyReportPath -Raw | ConvertFrom-Json
    if($bandKeyReport.failed -ne 0 -or $bandKeyReport.passed -le 0){throw "Failed/missing verification: $bandKeyReportPath"}
}
[xml]$bandKeyLint=Get-Content -LiteralPath 'android/app/build/reports/lint-results-debug.xml' -Raw
if(@($bandKeyLint.issues.issue | Where-Object {$_.severity -in @('Error','Fatal')}).Count){throw 'Android Lint errors'}
$bandKeyOutput=Join-Path $bandKeyRoot ('virtualmachine\BandKey-VM-Phone-Lab-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $bandKeyOutput | Out-Null
foreach($bandKeyName in @('BandKey.CredentialProvider.dll','BandKey.VmSetup.exe','BandKey.VmConfirmation.exe')){
    Copy-Item -LiteralPath (Join-Path $bandKeyInput $bandKeyName) -Destination $bandKeyOutput
}
$bandKeyBridge=Join-Path $bandKeyOutput 'VmBridge'
New-Item -ItemType Directory -Path $bandKeyBridge | Out-Null
# Read the explicitly screened ZIP instead of inheriting a potentially dirty publish tree.
$bandKeyZip=[IO.Compression.ZipFile]::OpenRead((Join-Path $bandKeyRoot 'artifacts\BandKey-win-x64.zip'))
try {
    foreach($bandKeyEntry in $bandKeyZip.Entries){
        if(!$bandKeyEntry.Name -or $bandKeyEntry.FullName -match '^docs/'){continue}
        $bandKeyTarget=[IO.Path]::GetFullPath((Join-Path $bandKeyBridge $bandKeyEntry.FullName))
        if(!$bandKeyTarget.StartsWith($bandKeyBridge+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe ZIP entry'}
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($bandKeyTarget)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($bandKeyEntry,$bandKeyTarget,$false)
    }
} finally {$bandKeyZip.Dispose()}
if(!(Test-Path -LiteralPath (Join-Path $bandKeyBridge 'BandKey.exe')) -or !(Test-Path -LiteralPath (Join-Path $bandKeyBridge 'coreclr.dll'))){throw 'Self-contained relay missing'}
'@echo off'+"`r`n"+'start "" "%~dp0BandKey.exe" --vm-bridge'+"`r`n" | Set-Content -LiteralPath (Join-Path $bandKeyBridge 'Start-VM-Bridge.cmd') -Encoding ascii -NoNewline
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'artifacts\BandKey-bridge-debug.apk') -Destination $bandKeyOutput
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'docs\vm-phone-bridge-test.md') -Destination (Join-Path $bandKeyOutput 'READ-ME-FIRST.txt')
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'docs\vm-phone-relay-protocol.md') -Destination $bandKeyOutput
foreach($bandKeyTransportDoc in @('usb-transport-test.md','ble-phone-transport.md')){Copy-Item -LiteralPath (Join-Path $bandKeyRoot ('docs\'+$bandKeyTransportDoc)) -Destination $bandKeyOutput}
$bandKeyFiles=@(Get-ChildItem -LiteralPath $bandKeyOutput -Recurse -File | Sort-Object FullName | ForEach-Object {
    $bandKeyName=[IO.Path]::GetRelativePath($bandKeyOutput,$_.FullName) -replace '\\','/'
    if($bandKeyName -match '(?i)(^|/)(runtime|Vault|Exchange)/|\.(dpapi|pfx|p12|key|keystore|jks)$'){throw "Unexpected runtime/private material: $bandKeyName"}
    [ordered]@{name=$bandKeyName;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
[ordered]@{
    purpose='VM-only foreground phone test confirmation; no real wearable or proximity claim'
    supportedGuest='VMware Windows 11 x64 build 26300'
    clsid='{76B424B6-A72D-4E98-9DC1-485E37A5429C}'
    signerKind=1;phoneKeyIncluded=$false;localSimulationSignerCreatedInPhoneMode=$false
    nativeStaticAnalysisPassed=$true;guestTested=$false;androidKeystoreHardwareTested=$false
    phoneToGuestUnlockTested=$false;hostRegistrationPerformed=$false;productionBackendEnabled=$false
    blePhoneTransportImplemented=$true;blePhysicalHardwareVerified=$false;currentApkInstalledOnPhone=$false
    previousLocalSimulationGuestResult='user reported valid automatic unlock, no-confirm/replay refusal, invalid signature refusal, client Arm expiry refusal; not this phone mode'
    tests=@($bandKeyReports | ForEach-Object { $bandKeyReport=Get-Content -LiteralPath $_ -Raw | ConvertFrom-Json; [ordered]@{report=$_;passed=$bandKeyReport.passed;failed=$bandKeyReport.failed} })
    files=$bandKeyFiles
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $bandKeyOutput 'manifest.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'new-lab-data-iso.ps1') -SourceDirectory $bandKeyOutput
$bandKeyOutput | Set-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\latest-vm-phone-lab.txt') -Encoding utf8
