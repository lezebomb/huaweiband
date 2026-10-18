param([switch]$PublishedProbeSucceeded,[string]$PhoneUsbReport)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $bandKeyRoot
$bandKeyCore=Get-Content artifacts/core-tests.json -Raw | ConvertFrom-Json
$bandKeyWindows=Get-Content artifacts/windows-tests.json -Raw | ConvertFrom-Json
$bandKeyAndroidRuntime=Get-Content artifacts/android-runtime-tests.json -Raw | ConvertFrom-Json
$bandKeyFlow=Get-Content artifacts/flow-lab.json -Raw | ConvertFrom-Json
$bandKeyProvider=Get-Content artifacts/credential-provider-lab/tests.json -Raw | ConvertFrom-Json
if($bandKeyCore.failed -ne 0 -or $bandKeyWindows.failed -ne 0 -or $bandKeyAndroidRuntime.failed -ne 0){throw 'Cannot record a release with failing tests.'}
if($bandKeyFlow.failed -ne 0 -or !$bandKeyFlow.simulated -or $bandKeyFlow.realWindowsAuthenticationTested -or $bandKeyProvider.failed -ne 0 -or $bandKeyProvider.registered -or $bandKeyProvider.windowsAuthenticationSubmitted){throw 'Unsafe or failing lab report.'}
[xml]$bandKeyLint=Get-Content android/app/build/reports/lint-results-debug.xml -Raw
$bandKeyErrors=@($bandKeyLint.issues.issue | Where-Object {$_.severity -in @('Error','Fatal')}).Count
$bandKeyWarnings=@($bandKeyLint.issues.issue | Where-Object {$_.severity -eq 'Warning'}).Count
if($bandKeyErrors){throw 'Cannot record a release with Android Lint errors.'}
$bandKeyUsb=$null
if($PhoneUsbReport){
  $bandKeyUsb=Get-Content -LiteralPath $PhoneUsbReport -Raw | ConvertFrom-Json
  if(!$bandKeyUsb.passed -or $bandKeyUsb.phoneRuns -ne 7 -or $bandKeyUsb.checks -ne 36 -or !$bandKeyUsb.syntheticAccount -or $bandKeyUsb.windowsAuthenticationSubmitted -or $bandKeyUsb.realWearableTested -or $bandKeyUsb.mainApkSha256 -ne (Get-FileHash -LiteralPath artifacts/BandKey-bridge-debug.apk -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'USB evidence failed or belongs to another APK.'}
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
  simulatedFlowTestsPassed=$bandKeyFlow.passed;referenceProviderInterfaceChecksPassed=$bandKeyProvider.passed
  referenceProviderRegistered=$false;windowsAuthenticationSubmitted=$false
  windowsSelfContainedProbeSucceeded=[bool]$PublishedProbeSucceeded
  windowsUnlockImplemented=$false;wearableButtonHardwareVerified=$false
  androidPhonePreviouslyInstalled=$true;androidPublishedApkInstalled=($null -ne $bandKeyUsb)
  phoneUsbSyntheticChecksPassed=$(if($bandKeyUsb){$bandKeyUsb.checks}else{0})
  androidKeystoreReportedSecureHardware=$(if($bandKeyUsb){$bandKeyUsb.keyInsideSecureHardware}else{$null})
  phoneToVmUnlockTested=$false
  wearEngineAppId='119393073';wearEngineSdk='5.0.3.304'
  huaweiAccountServiceEnabled=$true;wearEngineServiceStatus='under-review-user-reported-2026-10-17'
  wearEngineServiceApproved=$false;wearEngineHardwareDiagnosticError=8
  androidLint=[ordered]@{errors=$bandKeyErrors;warnings=$bandKeyWarnings}
  files=@($bandKeyFiles)
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath artifacts/release-manifest.json -Encoding utf8
Get-Content artifacts/release-manifest.json -Raw
