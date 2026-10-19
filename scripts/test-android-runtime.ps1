param([string]$JavaHome=$env:JAVA_HOME)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $bandKeyRoot
$bandKeyJavac=if($JavaHome){Join-Path $JavaHome 'bin\javac.exe'}else{'javac'}
$bandKeyJava=if($JavaHome){Join-Path $JavaHome 'bin\java.exe'}else{'java'}
$bandKeyClasses=Join-Path $bandKeyRoot 'artifacts\android-runtime-tests\classes'
New-Item -ItemType Directory -Force -Path $bandKeyClasses | Out-Null
& $bandKeyJavac -encoding UTF-8 -d $bandKeyClasses 'android/app/src/main/java/dev/bandkey/bridge/PromptGate.java' 'android/app/src/main/java/dev/bandkey/bridge/RetrySchedule.java' 'tests/interop/BridgeRuntimeTests.java'
if($LASTEXITCODE -ne 0){throw 'Android runtime test compilation failed'}
& $bandKeyJava -cp $bandKeyClasses dev.bandkey.bridge.BridgeRuntimeTests 'artifacts/android-runtime-tests.json'
if($LASTEXITCODE -ne 0){throw 'Android runtime lifecycle tests failed'}
& $bandKeyJavac -encoding UTF-8 -d $bandKeyClasses 'android/app/src/main/java/dev/bandkey/bridge/BleBackgroundLease.java' 'tests/interop/BleBackgroundLeaseTests.java'
if($LASTEXITCODE -ne 0){throw 'BLE background lease compilation failed'}
& $bandKeyJava -cp $bandKeyClasses dev.bandkey.bridge.BleBackgroundLeaseTests 'artifacts/android-ble-background-tests.json'
if($LASTEXITCODE -ne 0){throw 'BLE background lease tests failed'}
& $bandKeyJavac -encoding UTF-8 -d $bandKeyClasses 'android/app/src/main/java/dev/bandkey/bridge/BlePipeFrame.java' 'tests/interop/BlePipeCodecTests.java'
if($LASTEXITCODE -ne 0){throw 'BLE production codec compilation failed'}
& $bandKeyJava -cp $bandKeyClasses dev.bandkey.bridge.BlePipeCodecTests 'artifacts/android-ble-codec-tests.json'
if($LASTEXITCODE -ne 0){throw 'BLE production codec tests failed'}
& $bandKeyJavac -encoding UTF-8 -d $bandKeyClasses 'android/app/src/androidTest/java/dev/bandkey/bridge/TlsFailureClassifier.java' 'tests/interop/TlsFailureClassifierTests.java'
if($LASTEXITCODE -ne 0){throw 'TLS error classification compilation failed'}
& $bandKeyJava -cp $bandKeyClasses dev.bandkey.bridge.TlsFailureClassifierTests 'artifacts/android-tls-error-tests.json'
if($LASTEXITCODE -ne 0){throw 'TLS error classification tests failed'}
