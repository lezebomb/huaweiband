param([string]$SdkPath=$env:ANDROID_HOME, [string]$WearEngineAppId, [switch]$Lint)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $bandKeyRoot
if(!$SdkPath -or !(Test-Path -LiteralPath $SdkPath)){throw 'Set ANDROID_HOME or pass -SdkPath to an installed Android SDK (API 36).'}
$env:ANDROID_HOME=(Resolve-Path -LiteralPath $SdkPath).Path
$env:GRADLE_USER_HOME=Join-Path $bandKeyRoot '.tooling\gradle'
$env:ANDROID_PREFS_ROOT=Join-Path $bandKeyRoot '.tooling\android-prefs'
$env:ANDROID_USER_HOME=Join-Path $env:ANDROID_PREFS_ROOT '.android'
# Keep the debug signing identity inside this checkout, never rewrite another project's key.
New-Item -ItemType Directory -Force -Path $env:ANDROID_USER_HOME,'artifacts' | Out-Null
& (Join-Path $PSScriptRoot 'test-android-runtime.ps1')
$bandKeyGradleArgs=@('assembleDebug','--console=plain')
if($Lint){$bandKeyGradleArgs+= 'lintDebug'}
if($WearEngineAppId){$bandKeyGradleArgs+= "-PwearEngineAppId=$WearEngineAppId"}
Push-Location -LiteralPath android
try { & '.\gradlew.bat' @bandKeyGradleArgs; if($LASTEXITCODE -ne 0){throw 'Android build failed'} }
finally{Pop-Location}
Copy-Item -LiteralPath 'android/app/build/outputs/apk/debug/app-debug.apk' -Destination 'artifacts/BandKey-bridge-debug.apk' -Force
& (Join-Path $env:ANDROID_HOME 'build-tools\36.0.0\apksigner.bat') verify --verbose --print-certs 'artifacts/BandKey-bridge-debug.apk' | Set-Content -LiteralPath 'artifacts/android-signing.txt'
if($LASTEXITCODE -ne 0){throw 'APK signature verification failed'}
Get-FileHash -LiteralPath 'artifacts/BandKey-bridge-debug.apk' -Algorithm SHA256
