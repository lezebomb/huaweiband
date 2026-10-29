param([string]$PackageDirectory=$PSScriptRoot,[switch]$NoStart)
$ErrorActionPreference='Stop'
$bandKeyPackage=[IO.Path]::GetFullPath($PackageDirectory)
$bandKeyManifest=Get-Content -LiteralPath (Join-Path $bandKeyPackage 'manifest.json') -Raw|ConvertFrom-Json
if($bandKeyManifest.schema -ne 'bandkey.host-package.v1'){throw 'Invalid host package'}
foreach($bandKeyFile in $bandKeyManifest.files){
    if($bandKeyFile.path -notmatch '^[a-zA-Z0-9_. /-]+$' -or $bandKeyFile.path -match '(^|/)\.\.(/|$)' -or [IO.Path]::IsPathRooted($bandKeyFile.path)){throw 'Unsafe package entry'}
    $bandKeySource=Join-Path $bandKeyPackage $bandKeyFile.path
    if((Get-FileHash -LiteralPath $bandKeySource).Hash.ToLowerInvariant() -ne $bandKeyFile.sha256){throw ('Package hash mismatch: '+$bandKeyFile.path)}
}
$bandKeyConfig=Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\BandKey\HostPilot'
$bandKeySid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if($bandKeyConfig.Enabled -ne 1 -or $bandKeyConfig.TargetSid -ne $bandKeySid -or $bandKeyConfig.DeploymentProfile -ne 'host-pilot-v1'){throw 'Enroll this Windows account with the host installer first'}
$bandKeyDestination=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\BandKeyHostPilot'
function Assert-BandKeyOrdinaryPath([string]$Target){
    $bandKeyCursor=$Target
    while($bandKeyCursor -and $bandKeyCursor.StartsWith($bandKeyDestination,[StringComparison]::OrdinalIgnoreCase)){
        if((Test-Path -LiteralPath $bandKeyCursor) -and ((Get-Item -LiteralPath $bandKeyCursor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse payload path refused'}
        $bandKeyCursor=Split-Path -Parent $bandKeyCursor
    }
}
if(Test-Path -LiteralPath $bandKeyDestination){
    if((Get-Item -LiteralPath $bandKeyDestination).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse installation directory refused'}
    if(Get-Process -Name BandKey -ErrorAction SilentlyContinue|Where-Object{$_.Path -eq (Join-Path $bandKeyDestination 'BandKey.exe')}){throw 'Exit the current host agent before updating'}
}
New-Item -ItemType Directory -Force -Path $bandKeyDestination|Out-Null
foreach($bandKeyFile in $bandKeyManifest.files|Where-Object{$_.path.StartsWith('Agent/')} ){
    $bandKeyRelative=$bandKeyFile.path.Substring(6)
    $bandKeyTarget=Join-Path $bandKeyDestination $bandKeyRelative
    Assert-BandKeyOrdinaryPath $bandKeyTarget
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $bandKeyTarget)|Out-Null
    Copy-Item -LiteralPath (Join-Path $bandKeyPackage $bandKeyFile.path) -Destination $bandKeyTarget -Force
    if((Get-FileHash -LiteralPath $bandKeyTarget).Hash.ToLowerInvariant() -ne $bandKeyFile.sha256){throw 'Installed file readback mismatch'}
}
Copy-Item -LiteralPath (Join-Path $bandKeyPackage 'uninstall-host-agent.ps1') -Destination $bandKeyDestination -Force
Copy-Item -LiteralPath (Join-Path $bandKeyPackage 'host-resident-watchdog.ps1') -Destination $bandKeyDestination -Force
$bandKeyTaskName='BandKeyHostPilot-'+$bandKeySid
$bandKeyExecutable=Join-Path $bandKeyDestination 'BandKey.exe'
$bandKeyWatchShell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$bandKeyWatchArguments='-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "'+(Join-Path $bandKeyDestination 'host-resident-watchdog.ps1')+'"'
$bandKeyExisting=Get-ScheduledTask -TaskName $bandKeyTaskName -ErrorAction SilentlyContinue
$bandKeyTaskOwner=if($bandKeyExisting){$bandKeyExisting.Principal.UserId}else{$bandKeySid}
if($bandKeyTaskOwner -ne $bandKeySid){$bandKeyTaskOwner=([Security.Principal.NTAccount]::new($bandKeyTaskOwner)).Translate([Security.Principal.SecurityIdentifier]).Value}
$bandKeyOwnedAction=$false
if($bandKeyExisting -and @($bandKeyExisting.Actions).Count -eq 1){
    $bandKeyOwnedAction=([IO.Path]::GetFullPath($bandKeyExisting.Actions.Execute) -eq [IO.Path]::GetFullPath($bandKeyExecutable) -and $bandKeyExisting.Actions.Arguments -eq '--host-bridge --resident') -or
        ([IO.Path]::GetFullPath($bandKeyExisting.Actions.Execute) -eq [IO.Path]::GetFullPath($bandKeyWatchShell) -and $bandKeyExisting.Actions.Arguments -eq $bandKeyWatchArguments)
}
if($bandKeyExisting -and ($bandKeyTaskOwner -ne $bandKeySid -or !$bandKeyOwnedAction)){throw 'An unrelated startup task occupies this name'}
$bandKeyAction=New-ScheduledTaskAction -Execute $bandKeyWatchShell -Argument $bandKeyWatchArguments -WorkingDirectory $bandKeyDestination
$bandKeyPrincipal=New-ScheduledTaskPrincipal -UserId $bandKeySid -LogonType Interactive -RunLevel Limited
$bandKeyTrigger=New-ScheduledTaskTrigger -AtLogOn -User $bandKeySid
$bandKeySettings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $bandKeyTaskName -Action $bandKeyAction -Principal $bandKeyPrincipal -Trigger $bandKeyTrigger -Settings $bandKeySettings -Description 'BandKey current-user tray agent; preserves Windows sign-in; restarts on failure.' -Force|Out-Null
$bandKeyMenu=Join-Path ([Environment]::GetFolderPath('Programs')) 'BandKey'
New-Item -ItemType Directory -Force -Path $bandKeyMenu|Out-Null
$bandKeyShell=New-Object -ComObject WScript.Shell
$bandKeyStatusLink=$bandKeyShell.CreateShortcut((Join-Path $bandKeyMenu 'BandKey.lnk'));$bandKeyStatusLink.TargetPath=$bandKeyExecutable;$bandKeyStatusLink.Arguments='--host-bridge --resident';$bandKeyStatusLink.WorkingDirectory=$bandKeyDestination;$bandKeyStatusLink.Save()
$bandKeyRemoveLink=$bandKeyShell.CreateShortcut((Join-Path $bandKeyMenu 'Uninstall BandKey.lnk'));$bandKeyRemoveLink.TargetPath=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe';$bandKeyRemoveLink.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $bandKeyDestination 'uninstall-host-agent.ps1')+'"';$bandKeyRemoveLink.Save()
[ordered]@{schema='bandkey.host-agent-install.v1';version=$bandKeyManifest.version;task=$bandKeyTaskName;sessionZero=$false;files=@($bandKeyManifest.files|Where-Object{$_.path.StartsWith('Agent/')}|ForEach-Object{$_.path.Substring(6)})+@('host-resident-watchdog.ps1')}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $bandKeyDestination 'installation.json') -Encoding utf8
if(!$NoStart){Start-ScheduledTask -TaskName $bandKeyTaskName}
Write-Output 'BandKey current-user residency installed; no system sign-in providers were changed by this script.'
