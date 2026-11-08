param([Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9_.:-]+$')][string]$Serial,[string]$AdbPath='adb',[ValidateRange(10,60)][int]$Seconds=45)
$ErrorActionPreference='Stop'
$aimaRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $aimaRoot
# Passive observation only. User must already have agreed to any real vehicle action.
# This script never launches/clicks the official app, writes vehicle packets, or clears logs.
$aimaPidText=(& $AdbPath -s $Serial shell pidof -s com.adb.useroperation) -join ''
if($LASTEXITCODE -or $aimaPidText -notmatch '^\d+$'){throw 'Open the official app manually first; no process was started by this script.'}
$aimaOutput=Join-Path $aimaRoot ('.research\aimakey\observations\'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $aimaOutput | Out-Null
# Runtime output may include sensitive app data. Restrict the new directory to this user and SYSTEM.
$aimaAcl=[Security.AccessControl.DirectorySecurity]::new()
$aimaAcl.SetAccessRuleProtection($true,$false)
$aimaInherit=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
foreach($aimaSid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))){
    $aimaAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($aimaSid,[Security.AccessControl.FileSystemRights]::FullControl,$aimaInherit,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
}
Set-Acl -LiteralPath $aimaOutput -AclObject $aimaAcl
$aimaAdb=(Get-Command $AdbPath -ErrorAction Stop).Source
$aimaLogProcess=Start-Process -FilePath $aimaAdb -ArgumentList @('-s',$Serial,'logcat',"--pid=$aimaPidText",'-v','epoch','-T','1') -WindowStyle Hidden -RedirectStandardOutput (Join-Path $aimaOutput 'logcat.txt') -RedirectStandardError (Join-Path $aimaOutput 'adb-error.txt') -PassThru
$aimaStarted=[DateTimeOffset]::UtcNow
try{
    Write-Output "READY: passive capture for $Seconds seconds; only the user may operate the official app."
    if(!$aimaLogProcess.WaitForExit($Seconds*1000)){Stop-Process -Id $aimaLogProcess.Id; $aimaLogProcess.WaitForExit()}
}finally{
    if(!$aimaLogProcess.HasExited){Stop-Process -Id $aimaLogProcess.Id}
    $aimaLogProcess.Dispose()
}
@{scope='passive official-app logcat; no commands sent';startedAt=$aimaStarted.ToString('O');endedAt=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $aimaOutput 'capture.json')
Write-Output ('Local private output: '+$aimaOutput)
