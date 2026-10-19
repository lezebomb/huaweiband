param([switch]$PublishedProbeSucceeded,[string]$PhoneUsbReport,[string]$PhoneBleReport,[string]$BleRecoveryReport)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $bandKeyRoot
$bandKeyCore=Get-Content artifacts/core-tests.json -Raw | ConvertFrom-Json
$bandKeyWindows=Get-Content artifacts/windows-tests.json -Raw | ConvertFrom-Json
$bandKeyAndroidRuntime=Get-Content artifacts/android-runtime-tests.json -Raw | ConvertFrom-Json
$bandKeyTlsErrors=Get-Content artifacts/android-tls-error-tests.json -Raw | ConvertFrom-Json
$bandKeyBleReportTests=Get-Content artifacts/ble-evidence-tests.json -Raw | ConvertFrom-Json
$bandKeyBleCodec=Get-Content artifacts/android-ble-codec-tests.json -Raw | ConvertFrom-Json
$bandKeyBleInterop=Get-Content artifacts/ble-codec-interop/interop.json -Raw | ConvertFrom-Json
$bandKeyFlow=Get-Content artifacts/flow-lab.json -Raw | ConvertFrom-Json
$bandKeyProvider=Get-Content artifacts/credential-provider-lab/tests.json -Raw | ConvertFrom-Json
if($bandKeyCore.failed -ne 0 -or $bandKeyWindows.failed -ne 0 -or $bandKeyAndroidRuntime.failed -ne 0){throw 'Cannot record a release with failing tests.'}
if($bandKeyTlsErrors.failed -ne 0 -or $bandKeyTlsErrors.passed -ne 5 -or $bandKeyBleReportTests.failed -ne 0 -or $bandKeyBleReportTests.passed -ne 14 -or !$bandKeyBleReportTests.reportMutationTestsOnly -or $bandKeyBleReportTests.newPhysicalBleRun -ne $false){throw 'Missing TLS failure/report validator verification.'}
if($bandKeyBleCodec.failed -ne 0 -or $bandKeyBleCodec.passed -ne 22 -or $bandKeyBleInterop.failed -ne 0 -or $bandKeyBleInterop.passed -ne 1001 -or $bandKeyBleInterop.physicalBleTested){throw 'Missing or inaccurate BLE software verification.'}
if($bandKeyFlow.failed -ne 0 -or !$bandKeyFlow.simulated -or $bandKeyFlow.realWindowsAuthenticationTested -or $bandKeyProvider.failed -ne 0 -or $bandKeyProvider.registered -or $bandKeyProvider.windowsAuthenticationSubmitted){throw 'Unsafe or failing lab report.'}
[xml]$bandKeyLint=Get-Content android/app/build/reports/lint-results-debug.xml -Raw
$bandKeyErrors=@($bandKeyLint.issues.issue | Where-Object {$_.severity -in @('Error','Fatal')}).Count
$bandKeyWarnings=@($bandKeyLint.issues.issue | Where-Object {$_.severity -eq 'Warning'}).Count
if($bandKeyErrors){throw 'Cannot record a release with Android Lint errors.'}
$bandKeyRecovery=$null
if($BleRecoveryReport){
  $bandKeyRecovery=Get-Content -LiteralPath $BleRecoveryReport -Raw | ConvertFrom-Json
  if(!$bandKeyRecovery.passed -or !$bandKeyRecovery.physicalBle -or !$bandKeyRecovery.controlledTcpInterruption -or $bandKeyRecovery.targetKind -notin @('synthetic-host-fixture','isolated-vm') -or $bandKeyRecovery.syntheticAccount -isnot [bool] -or ($bandKeyRecovery.targetKind -eq 'synthetic-host-fixture') -ne $bandKeyRecovery.syntheticAccount -or $bandKeyRecovery.beforeStreams -lt 6 -or $bandKeyRecovery.beforeFailures -ne 0 -or $bandKeyRecovery.postRecoveryProxyAccepted -lt 8 -or $bandKeyRecovery.streams -lt ($bandKeyRecovery.beforeStreams+8) -or $bandKeyRecovery.sentBytes -le 0 -or $bandKeyRecovery.receivedBytes -le 0 -or $bandKeyRecovery.mtu -lt 23 -or $bandKeyRecovery.attempts -ne 2 -or @($bandKeyRecovery.failures).Count -ne 1 -or $bandKeyRecovery.cleanupErrors.Count -ne 0 -or $bandKeyRecovery.tlsTerminated -ne $false -or $bandKeyRecovery.automaticApproval -ne $false -or $bandKeyRecovery.windowsAuthenticationSubmitted -ne $false -or $bandKeyRecovery.realWearableTested -ne $false -or $bandKeyRecovery.applicationResponseAfterRecoveryIndependentlyObserved -ne $false -or $bandKeyRecovery.windowsAssemblySha256 -ne (Get-FileHash -LiteralPath artifacts/BandKey-win-x64/BandKey.dll -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'BLE recovery evidence failed or belongs to another Windows assembly.'}
}
$bandKeyUsb=$null
$bandKeyBle=$null;$bandKeyBleEvidence=$null
if($PhoneUsbReport){
  $bandKeyUsb=Get-Content -LiteralPath $PhoneUsbReport -Raw | ConvertFrom-Json
  if(!$bandKeyUsb.passed -or $bandKeyUsb.phoneRuns -ne 7 -or $bandKeyUsb.checks -ne 36 -or !$bandKeyUsb.syntheticAccount -or $bandKeyUsb.windowsAuthenticationSubmitted -or $bandKeyUsb.realWearableTested -or $bandKeyUsb.mainApkSha256 -ne (Get-FileHash -LiteralPath artifacts/BandKey-bridge-debug.apk -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'USB evidence failed or belongs to another APK.'}
}
if($PhoneBleReport){
  . (Join-Path $PSScriptRoot 'phone-ble-evidence.ps1')
  $bandKeyBlePath=(Resolve-Path -LiteralPath $PhoneBleReport).Path
  $bandKeyBle=Get-Content -LiteralPath $bandKeyBlePath -Raw | ConvertFrom-Json
  if(!$bandKeyBle.passed -or !$bandKeyBle.physicalBleEvidenceVerified -or !$bandKeyBle.usbForControlOnly -or !$bandKeyBle.syntheticAccount -or $bandKeyBle.windowsAuthenticationSubmitted -ne $false -or $bandKeyBle.realWearableTested -ne $false -or $bandKeyBle.vmOperated -ne $false -or $bandKeyBle.cleanupErrors.Count -ne 0 -or $bandKeyBle.mainApkSha256 -ne (Get-FileHash -LiteralPath artifacts/BandKey-bridge-debug.apk -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'BLE evidence failed or belongs to another APK.'}
  $bandKeyBleEvidence=Get-BandKeyBleEvidence -Root (Split-Path -Parent $bandKeyBlePath) -RunId $bandKeyBle.runId
}
$bandKeyFiles=foreach($bandKeyRelative in @('artifacts/BandKey-win-x64.zip','artifacts/BandKey-bridge-debug.apk',
  'artifacts/BandKey-win-x64/BandKey.exe','artifacts/BandKey-win-x64/BandKey.dll','artifacts/BandKey-win-x64/BandKey.Core.dll')){
  $bandKeyItem=Get-Item -LiteralPath $bandKeyRelative
  [ordered]@{path=$bandKeyRelative;bytes=$bandKeyItem.Length;sha256=(Get-FileHash -LiteralPath $bandKeyItem.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
[ordered]@{
  generatedAt=[DateTimeOffset]::Now.ToString('O');version='0.1.0'
  coreTestsPassed=$bandKeyCore.passed;windowsTestsPassed=$bandKeyWindows.passed
  androidRuntimeTestsPassed=$bandKeyAndroidRuntime.passed
  androidTlsFailureChecksPassed=$bandKeyTlsErrors.passed;bleReportMutationChecksPassed=$bandKeyBleReportTests.passed
  androidBleCodecChecksPassed=$bandKeyBleCodec.passed;bleCrossLanguageChecksPassed=$bandKeyBleInterop.passed
  blePhysicalHardwareVerified=($null -ne $bandKeyBleEvidence)
  blePhysicalDeviceModel=$(if($bandKeyBle){$bandKeyBle.device.model}else{$null})
  blePhysicalEvidence=$bandKeyBleEvidence
  bleFaultRecoveryHardwareVerified=($null -ne $bandKeyRecovery)
  bleFaultRecoveryScope='Physical transport recovery only; no independent application result or Windows authentication'
  simulatedFlowTestsPassed=$bandKeyFlow.passed;referenceProviderInterfaceChecksPassed=$bandKeyProvider.passed
  referenceProviderRegistered=$false;windowsAuthenticationSubmitted=$false
  windowsSelfContainedProbeSucceeded=[bool]$PublishedProbeSucceeded
  windowsUnlockImplemented=$false;wearableButtonHardwareVerified=$false
  androidPhonePreviouslyInstalled=$true;androidPublishedApkInstalled=$(if($null -ne $bandKeyUsb -or $null -ne $bandKeyBle){$true}else{$null})
  phoneUsbSyntheticChecksPassed=$(if($bandKeyUsb){$bandKeyUsb.checks}else{0})
  androidKeystoreReportedSecureHardware=$(if($bandKeyUsb){$bandKeyUsb.keyInsideSecureHardware}else{$null})
  phoneToVmUnlockTested=$false
  wearEngineAppId='119393073';wearEngineSdk='5.0.3.304'
  huaweiAccountServiceEnabled=$true;wearEngineServiceStatus='application-pending-user-reported-2026-10-19'
  wearEngineServiceApproved=$false;wearEngineHardwareDiagnosticError=8
  androidLint=[ordered]@{errors=$bandKeyErrors;warnings=$bandKeyWarnings}
  files=@($bandKeyFiles)
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath artifacts/release-manifest.json -Encoding utf8
Get-Content artifacts/release-manifest.json -Raw
