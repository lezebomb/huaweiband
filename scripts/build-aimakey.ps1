param([string]$SdkPath=$env:ANDROID_HOME,[string]$JavaHome=$env:JAVA_HOME,[string]$WearEngineAppId,[switch]$Lint,[switch]$DeviceTests)
$ErrorActionPreference='Stop'
$aimaRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $aimaRoot
if(!$SdkPath -or !(Test-Path -LiteralPath $SdkPath)){throw 'Pass -SdkPath for Android SDK API 36 and Build Tools 36.0.0.'}
$env:JAVA_HOME=$JavaHome
$env:ANDROID_HOME=(Resolve-Path -LiteralPath $SdkPath).Path
$env:GRADLE_USER_HOME=Join-Path $aimaRoot '.tooling\gradle'
$env:ANDROID_USER_HOME=Join-Path $aimaRoot '.tooling\android-prefs\.android'
New-Item -ItemType Directory -Force -Path $env:ANDROID_USER_HOME,'artifacts\aimakey' | Out-Null
& (Join-Path $PSScriptRoot 'test-aimakey.ps1') -JavaHome $JavaHome
$aimaArguments=@(':aimakey:assembleDebug','--console=plain')
if($Lint){$aimaArguments+=':aimakey:lintDebug'}
if($DeviceTests){$aimaArguments+=':aimakey:assembleDebugAndroidTest'}
if($WearEngineAppId){$aimaArguments+="-PaimaWearEngineAppId=$WearEngineAppId"}
$aimaMirror=Join-Path $aimaRoot '.tooling\wearengine-maven'
if(Test-Path -LiteralPath $aimaMirror){$aimaArguments+="-PbandKeyWearEngineMirror=$aimaMirror"}
Push-Location android
try{& .\gradlew.bat @aimaArguments;if($LASTEXITCODE){throw 'AimaKey Android build failed'}}finally{Pop-Location}
$aimaVersion=(Get-Content -LiteralPath 'android\aimakey\build\outputs\apk\debug\output-metadata.json' -Raw | ConvertFrom-Json).elements[0].versionName
$aimaApk=Join-Path 'artifacts\aimakey' ('AimaKey-'+$aimaVersion+'.apk')
Copy-Item -LiteralPath 'android\aimakey\build\outputs\apk\debug\aimakey-debug.apk' -Destination $aimaApk -Force
& (Join-Path $env:ANDROID_HOME 'build-tools\36.0.0\apksigner.bat') verify --verbose $aimaApk
if($LASTEXITCODE){throw 'AimaKey signature check failed'}
Get-FileHash -LiteralPath $aimaApk -Algorithm SHA256
