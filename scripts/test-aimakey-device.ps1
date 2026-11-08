param([Parameter(Mandatory=$true)][string]$Serial,[string]$AdbPath='adb',[switch]$ScreenOff,[switch]$SkipInstall)
$ErrorActionPreference='Stop'
$aimaRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $aimaRoot
function Invoke-AimaAdb([string[]]$Arguments){
  & $AdbPath -s $Serial @Arguments
  if($LASTEXITCODE){throw ('ADB failed: '+$Arguments[0])}
}
# Only the separate simulator and its test APK; never the official app or BandKey package.
if(!$SkipInstall){
  Invoke-AimaAdb @('install','-r','android\aimakey\build\outputs\apk\debug\aimakey-debug.apk')
  Invoke-AimaAdb @('install','-r','android\aimakey\build\outputs\apk\androidTest\debug\aimakey-debug-androidTest.apk')
}
# ColorOS may prohibit shell grants. Use the app's normal runtime permission dialogs.
$aimaApi=[int](& $AdbPath -s $Serial shell getprop ro.build.version.sdk)
$aimaPackage=(& $AdbPath -s $Serial shell dumpsys package dev.bandkey.aimakey) -join "`n"
if(($aimaApi -ge 31 -and $aimaPackage -notmatch 'android.permission.BLUETOOTH_CONNECT: granted=true') -or
   ($aimaApi -ge 33 -and $aimaPackage -notmatch 'android.permission.POST_NOTIFICATIONS: granted=true')){
  throw 'Open AimaKey simulator, tap background simulation and allow Nearby devices and Notifications; then rerun with -SkipInstall.'
}
$aimaRun=@('shell','am','instrument','-w','-r')
if($ScreenOff){$aimaRun+=@('-e','screenOff','true')}
$aimaRun+='dev.bandkey.aimakey.test/dev.bandkey.aimakey.AimaInstrumentation'
$aimaOutput=& $AdbPath -s $Serial @aimaRun
New-Item -ItemType Directory -Force artifacts\aimakey | Out-Null
$aimaOutput | Set-Content -LiteralPath 'artifacts\aimakey\device-tests.txt'
$aimaOutput
if($LASTEXITCODE -or !($aimaOutput -match '^INSTRUMENTATION_CODE: -1$') -or ($aimaOutput -match 'failure=')){throw 'AimaKey mock device tests failed'}
