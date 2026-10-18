param([string]$JavaHome=$env:JAVA_HOME)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $bandKeyRoot
$bandKeyOutput=Join-Path $bandKeyRoot 'artifacts\ble-codec-interop'
$bandKeyClasses=Join-Path $bandKeyOutput 'classes'
New-Item -ItemType Directory -Force -Path $bandKeyClasses | Out-Null
$bandKeyDotnet=Join-Path $bandKeyRoot '.tooling\dotnet\dotnet.exe'
$bandKeyTest=Join-Path $bandKeyRoot 'tests\BandKey.Tests\bin\Release\net10.0\BandKey.Tests.dll'
$bandKeyJavac=if($JavaHome){Join-Path $JavaHome 'bin\javac.exe'}else{'javac'}
$bandKeyJava=if($JavaHome){Join-Path $JavaHome 'bin\java.exe'}else{'java'}
& $bandKeyDotnet $bandKeyTest --ble-vectors $bandKeyOutput
if($LASTEXITCODE){throw 'CSharp BLE vector generation failed'}
& $bandKeyJavac -encoding UTF-8 -d $bandKeyClasses 'android/app/src/main/java/dev/bandkey/bridge/BlePipeFrame.java' 'tests/interop/BlePipeCodecTests.java'
if($LASTEXITCODE){throw 'Java BLE codec compilation failed'}
& $bandKeyJava -cp $bandKeyClasses dev.bandkey.bridge.BlePipeCodecTests (Join-Path $bandKeyOutput 'java.json') $bandKeyOutput
if($LASTEXITCODE){throw 'Java BLE vector verification failed'}
& $bandKeyDotnet $bandKeyTest --verify-ble-java $bandKeyOutput
if($LASTEXITCODE){throw 'CSharp verification of Java BLE frames failed'}
