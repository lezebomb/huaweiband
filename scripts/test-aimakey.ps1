param([string]$JavaHome=$env:JAVA_HOME)
$ErrorActionPreference='Stop'
$aimaRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $aimaRoot
if(!$JavaHome -or !(Test-Path -LiteralPath (Join-Path $JavaHome 'bin\javac.exe'))){throw 'Pass -JavaHome for JDK 21 or newer.'}
$aimaClasses=Join-Path $aimaRoot 'artifacts\aimakey\classes'
New-Item -ItemType Directory -Force -Path $aimaClasses | Out-Null
$aimaSources=@(Get-ChildItem -LiteralPath 'android\aimakey-core\src\main\java\dev\bandkey\aimakey\core' -Filter '*.java' | ForEach-Object FullName)
& (Join-Path $JavaHome 'bin\javac.exe') -encoding UTF-8 -d $aimaClasses @aimaSources 'tests\interop\AimaKeyTests.java' 'tests\interop\AimaFieldTests.java'
if($LASTEXITCODE -ne 0){throw 'AimaKey core compilation failed'}
& (Join-Path $JavaHome 'bin\java.exe') -cp $aimaClasses dev.bandkey.aimakey.core.AimaKeyTests 'artifacts\aimakey\core-tests.json'
if($LASTEXITCODE -ne 0){throw 'AimaKey core tests failed'}
& (Join-Path $JavaHome 'bin\java.exe') -cp $aimaClasses dev.bandkey.aimakey.core.AimaFieldTests 'artifacts\aimakey\field-tests.json'
if($LASTEXITCODE -ne 0){throw 'AimaKey field diagnostic tests failed'}
