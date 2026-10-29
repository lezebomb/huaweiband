$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../scripts/host-resident-watchdog.ps1') -PolicyOnly
$cases=@(
    @('fresh report', $false,$false,5,600,'wait'),
    @('startup grace', $false,$false,89,89,'wait'),
    @('live process without progress', $false,$false,90,600,'restart'),
    @('user pause', $true,$false,3600,7200,'paused'),
    @('explicit update marker', $false,$true,3600,7200,'stop'),
    @('stop wins over pause', $true,$true,3600,7200,'stop')
)
foreach($case in $cases){if((Get-BandKeyHostWatchAction $case[1] $case[2] $case[3] $case[4]) -ne $case[5]){throw ('Watchdog policy failed: '+$case[0])}}
Write-Output 'Host watchdog policy: 6/6 passed; no process terminated by these checks.'
