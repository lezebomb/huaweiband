param([Parameter(Mandatory=$true)][string]$NativeDirectory,[switch]$Rollback)
$ErrorActionPreference='Stop'
$bandKeyNative=(Resolve-Path -LiteralPath $NativeDirectory).Path
$bandKeySetup=Join-Path $bandKeyNative 'BandKey.HostSetup.exe'
if(!(Test-Path -LiteralPath $bandKeySetup -PathType Leaf)){throw 'Host setup missing'}
$bandKeyOperation=if($Rollback){'--rollback-update'}else{'--update'}
$bandKeyUpdate=Start-Process -FilePath $bandKeySetup -ArgumentList $bandKeyOperation -Verb RunAs -WindowStyle Hidden -Wait -PassThru
if($bandKeyUpdate.ExitCode){throw ('Native operation did not complete: '+$bandKeyUpdate.ExitCode)}
Write-Output 'Native operation completed; no automatic lock, sign-out or reboot was performed.'
