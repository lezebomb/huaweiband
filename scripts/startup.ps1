param([switch]$Remove,[string]$Executable=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\BandKey-win-x64\BandKey.exe'))
$ErrorActionPreference='Stop'
$bandKeyTaskName='BandKey-CurrentUser'
if($Remove){Unregister-ScheduledTask -TaskName $bandKeyTaskName -Confirm:$false -ErrorAction SilentlyContinue;return}
$bandKeyExe=(Resolve-Path -LiteralPath $Executable).Path
$bandKeyData=Join-Path (Split-Path -Parent $bandKeyExe) 'runtime'
$bandKeyWatchdog=Join-Path $PSScriptRoot 'watchdog.ps1'
$bandKeyWho=[Security.Principal.WindowsIdentity]::GetCurrent().Name
$bandKeyArguments='-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "'+$bandKeyWatchdog+'" -Executable "'+$bandKeyExe+'" -DataDirectory "'+$bandKeyData+'"'
$bandKeyAction=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $bandKeyArguments
$bandKeyTrigger=New-ScheduledTaskTrigger -AtLogOn -User $bandKeyWho
$bandKeyPrincipal=New-ScheduledTaskPrincipal -UserId $bandKeyWho -LogonType Interactive -RunLevel Limited
$bandKeySettings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $bandKeyTaskName -Action $bandKeyAction -Trigger $bandKeyTrigger -Principal $bandKeyPrincipal -Settings $bandKeySettings -Description 'BandKey per-user BLE agent; no Credential Provider registration' -Force
