$ErrorActionPreference='Stop'
$bandKeyDestination=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\BandKeyHostPilot'
function Assert-BandKeyOrdinaryPath([string]$Target){
    $bandKeyCursor=$Target
    while($bandKeyCursor -and $bandKeyCursor.StartsWith($bandKeyDestination,[StringComparison]::OrdinalIgnoreCase)){
        if((Test-Path -LiteralPath $bandKeyCursor) -and ((Get-Item -LiteralPath $bandKeyCursor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse payload path refused'}
        $bandKeyCursor=Split-Path -Parent $bandKeyCursor
    }
}
if([IO.Path]::GetFullPath($PSScriptRoot) -ne [IO.Path]::GetFullPath($bandKeyDestination)){throw 'Run the installed BandKey uninstall entry'}
if((Get-Item -LiteralPath $bandKeyDestination).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse installation directory refused'}
$bandKeySid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$bandKeyTaskName='BandKeyHostPilot-'+$bandKeySid
$bandKeyExecutable=Join-Path $bandKeyDestination 'BandKey.exe'
$bandKeyTask=Get-ScheduledTask -TaskName $bandKeyTaskName -ErrorAction SilentlyContinue
if($bandKeyTask){
    $bandKeyTaskOwner=$bandKeyTask.Principal.UserId
    if($bandKeyTaskOwner -ne $bandKeySid){$bandKeyTaskOwner=([Security.Principal.NTAccount]::new($bandKeyTaskOwner)).Translate([Security.Principal.SecurityIdentifier]).Value}
    if($bandKeyTaskOwner -ne $bandKeySid -or @($bandKeyTask.Actions).Count -ne 1 -or [IO.Path]::GetFullPath($bandKeyTask.Actions.Execute) -ne [IO.Path]::GetFullPath($bandKeyExecutable) -or $bandKeyTask.Actions.Arguments -ne '--host-bridge --resident'){throw 'Unexpected startup task ownership'}
    Disable-ScheduledTask -TaskName $bandKeyTaskName|Out-Null
}
$bandKeyData=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BandKeyHostBridge'
New-Item -ItemType Directory -Force -Path $bandKeyData|Out-Null
Set-Content -LiteralPath (Join-Path $bandKeyData 'stop-resident.request') -Value 'uninstall'
Start-Sleep -Seconds 3
Get-Process -Name BandKey -ErrorAction SilentlyContinue|Where-Object{$_.Path -eq $bandKeyExecutable}|Stop-Process
$bandKeyRecovery=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'BandKeyHostPilotRecovery\BandKey.HostSetup.exe'
if(!(Test-Path -LiteralPath $bandKeyRecovery)){throw 'Recovery installer missing; startup disabled, original Windows sign-in preserved'}
$bandKeyRemove=Start-Process -FilePath $bandKeyRecovery -ArgumentList '--remove' -Verb RunAs -Wait -PassThru
if($bandKeyRemove.ExitCode){throw 'Native removal not completed; use the protected recovery installer to retry'}
if(Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{1DF46BC8-73B9-48B4-8E5A-55EC21143C4E}'){throw 'BandKey provider registration remains; cleanup stopped'}
if($bandKeyTask){Unregister-ScheduledTask -TaskName $bandKeyTaskName -Confirm:$false}
$bandKeyMenu=Join-Path ([Environment]::GetFolderPath('Programs')) 'BandKey'
foreach($bandKeyLink in @('BandKey.lnk','Uninstall BandKey.lnk')){Remove-Item -LiteralPath (Join-Path $bandKeyMenu $bandKeyLink) -Force -ErrorAction SilentlyContinue}
$bandKeyInstall=Get-Content -LiteralPath (Join-Path $bandKeyDestination 'installation.json') -Raw|ConvertFrom-Json
foreach($bandKeyRelative in $bandKeyInstall.files){
    if($bandKeyRelative -notmatch '^[a-zA-Z0-9_. /-]+$' -or $bandKeyRelative -match '(^|/)\.\.(/|$)' -or [IO.Path]::IsPathRooted($bandKeyRelative)){throw 'Unsafe installed manifest entry'}
    $bandKeyTarget=Join-Path $bandKeyDestination $bandKeyRelative;Assert-BandKeyOrdinaryPath $bandKeyTarget
    if(Test-Path -LiteralPath $bandKeyTarget){Remove-Item -LiteralPath $bandKeyTarget -Force -ErrorAction Stop}
}
Write-Output 'BandKey host registration, protected credential vault, startup and agent payload removed. Original Windows sign-in preserved. User logs and this uninstall receipt remain.'
