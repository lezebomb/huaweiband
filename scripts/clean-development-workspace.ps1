param([switch]$Apply,[switch]$RemoveVm,[switch]$RemoveToolchain)
$ErrorActionPreference='Stop'
$bandKeyRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$bandKeyMaintenance=Join-Path $bandKeyRoot 'maintenance'
function Assert-BandKeyCleanupPath([string]$Path){
    $full=[IO.Path]::GetFullPath($Path)
    if(!$full.StartsWith($bandKeyRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup target escapes checkout'}
    $cursor=$full
    while($cursor -and $cursor.Length -ge $bandKeyRoot.Length){
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Reparse cleanup path refused: '+$cursor)}
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
    if(Test-Path -LiteralPath $full -PathType Container){
        if(Get-ChildItem -LiteralPath $full -Recurse -Force -Attributes ReparsePoint -ErrorAction Stop|Select-Object -First 1){throw 'Cleanup does not follow nested reparse points'}
    }
    return $full
}
$bandKeyTargets=@('artifacts','outputs','.research','offline-archive','android/.gradle','android/build','android/app/build','开源软件.jpg')
foreach($bandKeyTree in @('src','tests')){
    Get-ChildItem -LiteralPath (Join-Path $bandKeyRoot $bandKeyTree) -Directory -Recurse | Where-Object {$_.Name -in @('bin','obj')} | ForEach-Object {
        $bandKeyTargets+=[IO.Path]::GetRelativePath($bandKeyRoot,$_.FullName)
    }
}
if($RemoveVm){
    $bandKeyVm=Join-Path $bandKeyRoot 'virtualmachine/BandKey-Win11/BandKey-Win11.vmx'
    if(Test-Path -LiteralPath $bandKeyVm){
        if(!(Select-String -LiteralPath $bandKeyVm -Pattern '^displayName = "BandKey-Win11"$' -Quiet)){throw 'Unexpected VM identity'}
        if(Get-CimInstance Win32_Process -Filter "Name='vmware-vmx.exe'"|Where-Object {$_.CommandLine -and $_.CommandLine.Contains($bandKeyVm,[StringComparison]::OrdinalIgnoreCase)}){throw 'BandKey VM is running; no VM files were removed'}
    }
    $bandKeyTargets+='virtualmachine'
}
if($RemoveToolchain){
    $bandKeyVswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if(Test-Path -LiteralPath (Join-Path $bandKeyRoot '.tooling/vs-buildtools')){
        if(!(Test-Path -LiteralPath $bandKeyVswhere)){throw 'Cannot verify project-local Build Tools uninstall'}
        $bandKeyInstances=@((& $bandKeyVswhere -all -products '*' -format json)|ConvertFrom-Json)
        if($LASTEXITCODE -or ($bandKeyInstances|Where-Object {$_.installationPath -eq (Join-Path $bandKeyRoot '.tooling/vs-buildtools')})){throw 'Uninstall the registered project-local Build Tools before cleanup'}
    }
    $bandKeyTargets+='.tooling'
}
$bandKeyTracked=@(& git -C $bandKeyRoot ls-files)
if($LASTEXITCODE){throw 'Cannot inspect tracked source'}
$bandKeyPlan=@(foreach($bandKeyRelative in ($bandKeyTargets|Sort-Object -Unique)){
    $bandKeyTarget=Assert-BandKeyCleanupPath (Join-Path $bandKeyRoot $bandKeyRelative)
    if(!(Test-Path -LiteralPath $bandKeyTarget)){continue}
    $bandKeyGitPath=$bandKeyRelative.Replace('\','/')
    if($bandKeyTracked|Where-Object {$_ -eq $bandKeyGitPath -or $_.StartsWith($bandKeyGitPath+'/',[StringComparison]::Ordinal)}){throw ('Tracked source would be removed: '+$bandKeyGitPath)}
    $bandKeyBytes=if(Test-Path -LiteralPath $bandKeyTarget -PathType Container){(Get-ChildItem -LiteralPath $bandKeyTarget -Recurse -File -Force|Measure-Object Length -Sum).Sum}else{(Get-Item -LiteralPath $bandKeyTarget).Length}
    [pscustomobject]@{path=$bandKeyTarget;bytes=[long]$bandKeyBytes}
})
if(!$Apply){$bandKeyPlan;return}
$bandKeyBackup=Get-Content -LiteralPath (Join-Path $bandKeyMaintenance 'backup.json') -Raw|ConvertFrom-Json
if(!$bandKeyBackup.signingIdentityBackedUp -or !(Test-Path -LiteralPath (Join-Path $bandKeyMaintenance 'Private/debug.keystore'))){throw 'Private signing identity backup is required'}
if($RemoveToolchain -and (Test-Path -LiteralPath (Join-Path $bandKeyRoot '.tooling/android-prefs/.android/debug.keystore'))){
    if((Get-FileHash (Join-Path $bandKeyRoot '.tooling/android-prefs/.android/debug.keystore')).Hash -ne (Get-FileHash (Join-Path $bandKeyMaintenance 'Private/debug.keystore')).Hash){throw 'Signing backup differs'}
}
$bandKeyHead=(& git -C $bandKeyRoot rev-parse HEAD).Trim();if($LASTEXITCODE){throw 'Cannot read HEAD'}
$bandKeyRemote=(& git -C $bandKeyRoot ls-remote origin refs/heads/main);if($LASTEXITCODE -or !$bandKeyRemote -or ($bandKeyRemote -split '\s+')[0] -ne $bandKeyHead){throw 'Push the completed source phase before cleanup'}
if(@(& git -C $bandKeyRoot status --porcelain).Count){throw 'Commit all source changes before cleanup'}
foreach($bandKeyEntry in $bandKeyPlan){
    $bandKeyTarget=Assert-BandKeyCleanupPath $bandKeyEntry.path
    Remove-Item -LiteralPath $bandKeyTarget -Recurse -Force -ErrorAction Stop
}
[ordered]@{schema='bandkey.cleanup.v1';sourceCommit=$bandKeyHead;finishedUtc=[DateTime]::UtcNow.ToString('o');removed=$bandKeyPlan;logicalBytesRemoved=($bandKeyPlan|Measure-Object bytes -Sum).Sum;runtimeDataRemoved=$false;ubuntuTouched=$false}|
    ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $bandKeyMaintenance 'cleanup.json') -Encoding utf8
Write-Output ('Cleanup complete: '+[math]::Round(($bandKeyPlan|Measure-Object bytes -Sum).Sum/1GB,2)+' GiB logical files removed')
