param([ValidatePattern('^[a-f0-9]{40,64}$')][string]$SourceTree,[switch]$VerifyBuild,
    [string]$JavaHome=$env:JAVA_HOME,[string]$SdkPath=$env:ANDROID_HOME)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $bandKeyRoot
if(!$SourceTree){$SourceTree=(& git rev-parse 'HEAD^{tree}').Trim();if($LASTEXITCODE -ne 0){throw 'Source tree unavailable'}}
$bandKeyType=(& git cat-file -t $SourceTree).Trim()
if($LASTEXITCODE -ne 0 -or $bandKeyType -ne 'tree'){throw 'Expected an existing Git source tree'}
$bandKeyTracked=@(& git ls-tree -r --name-only $SourceTree)
$bandKeyUnexpected=@($bandKeyTracked|Where-Object {$_ -match '(?i)(^|/)(runtime|artifacts|offline-archive|virtualmachine|\.research|\.tooling|bin|obj|build)/|\.(apk|jks|keystore|dpapi|pfx|p12|bandkeypub|key)$|codex-clipboard-'})
if($LASTEXITCODE -ne 0 -or $bandKeyUnexpected.Count -ne 0){
    throw 'Source snapshot includes unexpected runtime/private/build material'
}
$bandKeyPins=Get-Content -LiteralPath android/wearengine-artifacts.json -Raw|ConvertFrom-Json
if($bandKeyPins.schema -ne 'bandkey.wearengine-artifact-pins.v1' -or $bandKeyPins.artifacts.Count -ne 4){throw 'Invalid SDK pin catalog'}
$bandKeyArchive=Join-Path $bandKeyRoot ('offline-archive/WearEngine-5.0.3.304-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
foreach($bandKeyFolder in @('maven','source','app','reference','tools')){New-Item -ItemType Directory -Path (Join-Path $bandKeyArchive $bandKeyFolder)|Out-Null}
$bandKeyOrigins=@()
foreach($bandKeyPin in $bandKeyPins.artifacts){
    foreach($bandKeyPart in @($bandKeyPin.group,$bandKeyPin.module,$bandKeyPin.version,$bandKeyPin.extension)){
        if($bandKeyPart -cnotmatch '^[A-Za-z0-9_.-]+$'){throw 'Invalid artifact coordinate'}
    }
    if($bandKeyPin.sha256 -cnotmatch '^[a-f0-9]{64}$'){throw 'Invalid artifact digest'}
    $bandKeyName=$bandKeyPin.module+'-'+$bandKeyPin.version+'.'+$bandKeyPin.extension
    $bandKeyCache=Join-Path $bandKeyRoot ('.tooling/gradle/caches/modules-2/files-2.1/'+$bandKeyPin.group+'/'+$bandKeyPin.module+'/'+$bandKeyPin.version)
    $bandKeyCandidates=@(Get-ChildItem -LiteralPath $bandKeyCache -Recurse -File -Filter $bandKeyName|Where-Object {(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() -ceq $bandKeyPin.sha256})
    if($bandKeyCandidates.Count -ne 1){throw ('Verified cached SDK artifact unavailable: '+$bandKeyName)}
    $bandKeyRelative=$bandKeyPin.group.Replace('.','/')+'/'+$bandKeyPin.module+'/'+$bandKeyPin.version+'/'+$bandKeyName
    $bandKeyTarget=Join-Path $bandKeyArchive ('maven/'+$bandKeyRelative)
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($bandKeyTarget))|Out-Null
    Copy-Item -LiteralPath $bandKeyCandidates[0].FullName -Destination $bandKeyTarget
    $bandKeyOrigins+=[ordered]@{coordinate=($bandKeyPin.group+':'+$bandKeyPin.module+':'+$bandKeyPin.version);extension=$bandKeyPin.extension;originalUrl=($bandKeyPins.repository+$bandKeyRelative);sha256=$bandKeyPin.sha256;copiedFrom='resolved project Gradle cache'}
}
$bandKeySourceZip=Join-Path $bandKeyArchive 'source/BandKey-source.zip'
& git archive --format=zip ('--output='+$bandKeySourceZip) $SourceTree
if($LASTEXITCODE -ne 0){throw 'Source snapshot failed'}
$bandKeySample=Join-Path $bandKeyRoot '.research/WearEngine'
$bandKeySampleCommit='81a98055a479d9f9740374c0a26f27d56be19706'
$bandKeySampleLicense=(& git -c ('safe.directory='+$bandKeySample) -C $bandKeySample show ($bandKeySampleCommit+':LICENSE')) -join [Environment]::NewLine
if($LASTEXITCODE -ne 0 -or $bandKeySampleLicense -notmatch 'Apache License'){throw 'Official sample license unavailable'}
& git -c ('safe.directory='+$bandKeySample) -C $bandKeySample archive --format=zip ('--output='+(Join-Path $bandKeyArchive 'source/Huawei-official-sample.zip')) $bandKeySampleCommit LICENSE README.MD app/src build.gradle settings.gradle gradle.properties
if($LASTEXITCODE -ne 0){throw 'Official sample snapshot failed'}
[IO.File]::WriteAllText((Join-Path $bandKeyArchive 'reference/SAMPLE-LICENSE.txt'),$bandKeySampleLicense,[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath docs/wearengine-offline-archive-20261026.md -Destination (Join-Path $bandKeyArchive 'reference/API-NOTES.zh-CN.md')
Copy-Item -LiteralPath android/wearengine-artifacts.json -Destination (Join-Path $bandKeyArchive 'reference/wearengine-artifacts.json')
Copy-Item -LiteralPath tests/interop/WearEngineArchiveSmoke.java -Destination (Join-Path $bandKeyArchive 'tools/WearEngineArchiveSmoke.java')
Copy-Item -LiteralPath scripts/verify-wearengine-archive.ps1 -Destination (Join-Path $bandKeyArchive 'tools/verify-wearengine-archive.ps1')
Copy-Item -LiteralPath artifacts/BandKey-bridge-0.8.0-debug.apk -Destination (Join-Path $bandKeyArchive 'app/BandKey-bridge-0.8.0-debug.apk')
$bandKeySmokeCompiled=$false;$bandKeyOfflineBuilt=$false;$bandKeySignerHash=$null
if($VerifyBuild){
    if(!(Test-Path -LiteralPath (Join-Path $JavaHome 'bin/javac.exe')) -or !(Test-Path -LiteralPath (Join-Path $SdkPath 'platforms/android-36/android.jar'))){throw 'Existing JDK21 and Android SDK36 are required'}
    if(!(Test-Path -LiteralPath '.tooling/android-prefs/.android/debug.keystore')){throw 'Existing approved debug signing identity is missing; do not generate a replacement'}
    $bandKeyScratch=Join-Path $bandKeyRoot ('artifacts/wearengine-archive-build-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $bandKeyScratch|Out-Null
    $bandKeyAar=Join-Path $bandKeyArchive 'maven/com/huawei/hms/wearengine/5.0.3.304/wearengine-5.0.3.304.aar'
    $bandKeyZip=[IO.Compression.ZipFile]::OpenRead($bandKeyAar)
    try{
        $bandKeyEntry=$bandKeyZip.GetEntry('classes.jar')
        if(!$bandKeyEntry){throw 'SDK classes.jar unavailable'}
        [IO.Compression.ZipFileExtensions]::ExtractToFile($bandKeyEntry,(Join-Path $bandKeyScratch 'wearengine-classes.jar'),$false)
    }finally{$bandKeyZip.Dispose()}
    $bandKeyClasspath=(Join-Path $SdkPath 'platforms/android-36/android.jar')+';'+(Join-Path $bandKeyScratch 'wearengine-classes.jar')+';'+(Join-Path $bandKeyArchive 'maven/com/huawei/hmf/tasks/1.5.2.206/tasks-1.5.2.206.jar')
    & (Join-Path $JavaHome 'bin/javac.exe') --release 17 -encoding UTF-8 -classpath $bandKeyClasspath -d $bandKeyScratch (Join-Path $bandKeyArchive 'tools/WearEngineArchiveSmoke.java')
    if($LASTEXITCODE -ne 0){throw 'Archived SDK public API compile failed'}
    $bandKeySmokeCompiled=$true
    $env:JAVA_HOME=$JavaHome;$env:ANDROID_HOME=$SdkPath
    $env:GRADLE_USER_HOME=Join-Path $bandKeyRoot '.tooling/gradle'
    $env:ANDROID_PREFS_ROOT=Join-Path $bandKeyRoot '.tooling/android-prefs'
    $env:ANDROID_USER_HOME=Join-Path $env:ANDROID_PREFS_ROOT '.android'
    Push-Location -LiteralPath (Join-Path $bandKeyRoot 'android')
    try{
        & .\gradlew.bat --offline --console=plain --rerun-tasks ('-PbandKeyWearEngineMirror='+(Join-Path $bandKeyArchive 'maven')) assembleDebug *> (Join-Path $bandKeyScratch 'offline-build.log')
        if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $bandKeyScratch 'offline-build.log') -Tail 35;throw 'Offline Android build failed'}
    }finally{Pop-Location}
    $bandKeyOfflineBuilt=$true
    $bandKeySign=& (Join-Path $SdkPath 'build-tools/36.0.0/apksigner.bat') verify --verbose --print-certs (Join-Path $bandKeyArchive 'app/BandKey-bridge-0.8.0-debug.apk')
    if($LASTEXITCODE -ne 0){throw 'Archived own APK signature verification failed'}
    $bandKeyDigest=@($bandKeySign|Where-Object {$_ -match '^Signer #1 certificate SHA-256 digest: ([a-fA-F0-9]+)$'})
    if($bandKeyDigest.Count -ne 1){throw 'Expected exactly one own APK signer'}
    $null=$bandKeyDigest[0] -match 'digest: ([a-fA-F0-9]+)$';$bandKeySignerHash=$Matches[1].ToLowerInvariant()
}
$bandKeyBase=(& git rev-parse HEAD).Trim()
[ordered]@{schema='bandkey.wearengine-offline-verification.v1';archivedSdkApiCompiled=$bandKeySmokeCompiled;offlineAndroidBuildOnExistingToolchain=$bandKeyOfflineBuilt;ownApkSignerSha256=$bandKeySignerHash;entireBuildToolchainIncluded=$false;signingPrivateKeyIncluded=$false;serviceAuthorizationPreserved=$false}|
    ConvertTo-Json|Set-Content -LiteralPath (Join-Path $bandKeyArchive 'reference/verification.json') -Encoding utf8
$bandKeyFiles=@(Get-ChildItem -LiteralPath $bandKeyArchive -Recurse -File|Sort-Object FullName|ForEach-Object{
    [ordered]@{name=([IO.Path]::GetRelativePath($bandKeyArchive,$_.FullName)-replace '\\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
[ordered]@{schema='bandkey.wearengine-offline-archive.v1';sdkVersion='5.0.3.304';createdUtc=[DateTime]::UtcNow.ToString('o');sourceBaseCommit=$bandKeyBase;sourceTree=$SourceTree;officialSampleCommit=$bandKeySampleCommit;origins=$bandKeyOrigins;files=$bandKeyFiles}|
    ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $bandKeyArchive 'manifest.json') -Encoding utf8
& (Join-Path $bandKeyRoot 'scripts/verify-wearengine-archive.ps1') -Path $bandKeyArchive|Out-Null
$bandKeyZipPath=$bandKeyArchive+'.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($bandKeyArchive,$bandKeyZipPath,[IO.Compression.CompressionLevel]::Optimal,$false)
$bandKeyReadback=Join-Path $bandKeyRoot ('artifacts/wearengine-archive-readback-'+[Guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($bandKeyZipPath,$bandKeyReadback)
$bandKeyVerified=& (Join-Path $bandKeyRoot 'scripts/verify-wearengine-archive.ps1') -Path $bandKeyReadback
Add-Content -LiteralPath (Join-Path $bandKeyReadback 'reference/API-NOTES.zh-CN.md') -Value 'controlled checksum-failure check'
$bandKeyTamperDetected=$false
try{& (Join-Path $bandKeyRoot 'scripts/verify-wearengine-archive.ps1') -Path $bandKeyReadback|Out-Null}catch{$bandKeyTamperDetected=$true}
if(!$bandKeyTamperDetected){throw 'Archive verifier failed to detect changed content'}
$bandKeyZipHash=(Get-FileHash -LiteralPath $bandKeyZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
($bandKeyZipHash+'  '+[IO.Path]::GetFileName($bandKeyZipPath))|Set-Content -LiteralPath ($bandKeyZipPath+'.sha256') -Encoding ascii
[ordered]@{directory=$bandKeyArchive;zip=$bandKeyZipPath;sha256=$bandKeyZipHash;bytes=(Get-Item -LiteralPath $bandKeyZipPath).Length;filesVerified=$bandKeyVerified.filesVerified;sourceBaseCommit=$bandKeyBase;sourceTree=$SourceTree;archivedSdkApiCompiled=$bandKeySmokeCompiled;offlineAndroidBuildOnExistingToolchain=$bandKeyOfflineBuilt;changedFileDetected=$bandKeyTamperDetected;serviceAuthorizationBypassed=$false;signingPrivateKeyIncluded=$false}|
    ConvertTo-Json|Set-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts/latest-wearengine-archive.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts/latest-wearengine-archive.json') -Raw