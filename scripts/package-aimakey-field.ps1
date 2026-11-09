param([string]$SdkPath=$env:ANDROID_HOME,[string]$JavaHome=$env:JAVA_HOME,
 [Parameter(Mandatory=$true)][string]$BandKeySigningKey,[string]$KeyAlias='androiddebugkey',
 [string]$RollbackApk='maintenance/Current/BandKey-bridge-0.9.5.apk')
$ErrorActionPreference='Stop'
$aimaRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $aimaRoot
if(!$env:BANDKEY_ANDROID_STORE_PASSWORD -or !$env:BANDKEY_ANDROID_KEY_PASSWORD){throw 'Set BANDKEY_ANDROID_STORE_PASSWORD and BANDKEY_ANDROID_KEY_PASSWORD in this process; do not store passwords in source.'}
$env:JAVA_HOME=$JavaHome; $env:ANDROID_HOME=$SdkPath
$env:GRADLE_USER_HOME=Join-Path $aimaRoot '.tooling/gradle'
$env:ANDROID_USER_HOME=Join-Path $aimaRoot '.tooling/android-prefs/.android'
$aimaOutput=Join-Path $aimaRoot 'artifacts/aimakey-field'
New-Item -ItemType Directory -Force $aimaOutput,$env:ANDROID_USER_HOME | Out-Null
& (Join-Path $PSScriptRoot 'test-aimakey.ps1') -JavaHome $JavaHome
$aimaArgs=@(':app:assembleDebug',':app:lintDebug',':aimakey:assembleDebug',':aimakey:assembleDebugAndroidTest',':aimakey:lintDebug',':aimakey-field:lintDebug','--console=plain')
$aimaMirror=Join-Path $aimaRoot '.tooling/wearengine-maven'
if(Test-Path -LiteralPath $aimaMirror){$aimaArgs+="-PbandKeyWearEngineMirror=$aimaMirror"}
Push-Location android
try{& .\gradlew.bat @aimaArgs; if($LASTEXITCODE){throw 'Field Android build failed'}}finally{Pop-Location}
$aimaSigner=Join-Path $SdkPath 'build-tools/36.0.0/apksigner.bat'
function Get-AimaCertificate([string]$Apk){
 $aimaVerification=& $aimaSigner verify --print-certs $Apk
 if($LASTEXITCODE){throw 'APK verification failed'}
 $aimaLine=@($aimaVerification | Where-Object {$_ -match '^Signer #1 certificate SHA-256 digest: ([a-fA-F0-9]{64})$'})
 if($aimaLine.Count -ne 1){throw 'Unexpected signer identity'}
 return ($aimaLine[0] -split ': ',2)[1].ToLowerInvariant()
}
$aimaExpected=Get-AimaCertificate $RollbackApk
$aimaHostVersion=(Get-Content android/app/build/outputs/apk/debug/output-metadata.json -Raw | ConvertFrom-Json).elements[0].versionName
$aimaPhoneVersion=(Get-Content android/aimakey/build/outputs/apk/debug/output-metadata.json -Raw | ConvertFrom-Json).elements[0].versionName
$aimaHostApk=Join-Path $aimaOutput ('BandKey-'+$aimaHostVersion+'.apk')
$aimaPhoneApk=Join-Path $aimaOutput ('AimaKey-'+$aimaPhoneVersion+'.apk')
& $aimaSigner sign --ks $BandKeySigningKey --ks-key-alias $KeyAlias --ks-pass env:BANDKEY_ANDROID_STORE_PASSWORD --key-pass env:BANDKEY_ANDROID_KEY_PASSWORD --out $aimaHostApk 'android/app/build/outputs/apk/debug/app-debug.apk'
if($LASTEXITCODE){throw 'Original BandKey signing failed'}
if((Get-AimaCertificate $aimaHostApk) -cne $aimaExpected){throw 'Refusing a BandKey package signed by a different identity'}
Copy-Item -LiteralPath 'android/aimakey/build/outputs/apk/debug/aimakey-debug.apk' -Destination $aimaPhoneApk -Force
$null=Get-AimaCertificate $aimaPhoneApk
Copy-Item -LiteralPath $RollbackApk -Destination (Join-Path $aimaOutput 'BandKey-rollback-0.9.5.apk') -Force
@{scope='wrist notification diagnostics and phone mock only';bandKey=$aimaHostVersion;aimaKey=$aimaPhoneVersion;bandKeyCertificateMatchesRollback=$true;
 apks=@(@{file=[IO.Path]::GetFileName($aimaHostApk);sha256=(Get-FileHash $aimaHostApk).Hash.ToLowerInvariant()},@{file=[IO.Path]::GetFileName($aimaPhoneApk);sha256=(Get-FileHash $aimaPhoneApk).Hash.ToLowerInvariant()})} |
 ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $aimaOutput 'release.json')
Write-Output ('Verified field packages: '+$aimaOutput)
