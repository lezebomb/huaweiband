param([switch]$PolicyOnly)
$ErrorActionPreference='Stop'
function Get-BandKeyHostWatchAction([bool]$Paused,[bool]$StopMarker,[double]$Age,[double]$Uptime){
    if($StopMarker){return 'stop'}
    if($Paused){return 'paused'}
    if($Uptime -ge 90 -and $Age -ge 90){return 'restart'}
    return 'wait'
}
if($PolicyOnly){return}
$bandKeyExecutable=Join-Path $PSScriptRoot 'BandKey.exe'
$bandKeyData=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BandKeyHostBridge'
$bandKeySettings=Join-Path $bandKeyData 'resident-settings.json'
$bandKeyStop=Join-Path $bandKeyData 'stop-resident.request'
$bandKeyReports=Join-Path $bandKeyData 'reports'
$bandKeyLog=Join-Path $bandKeyData 'resident-watchdog.log'
if([IO.Path]::GetFullPath($PSScriptRoot) -ne [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/BandKeyHostPilot'))){throw 'Run the installed current-user watchdog'}
if(!(Test-Path -LiteralPath $bandKeyExecutable)){throw 'Installed agent missing'}
$bandKeyMutex=[Threading.Mutex]::new($false,('Local\BandKeyHostWatchdog-'+[Security.Principal.WindowsIdentity]::GetCurrent().User.Value))
$bandKeyOwned=$false
function Write-BandKeyWatchLog([string]$Code){
    New-Item -ItemType Directory -Force -Path $bandKeyData|Out-Null
    if((Test-Path -LiteralPath $bandKeyLog) -and (Get-Item -LiteralPath $bandKeyLog).Length -gt 65536){Move-Item -LiteralPath $bandKeyLog -Destination ($bandKeyLog+'.old') -Force}
    Add-Content -LiteralPath $bandKeyLog -Value ([DateTime]::UtcNow.ToString('o')+' '+$Code)
}
try{
    try{$bandKeyOwned=$bandKeyMutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$bandKeyOwned=$true}
    if(!$bandKeyOwned){return}
    $bandKeyFailures=0
    while(!(Test-Path -LiteralPath $bandKeyStop)){
        $bandKeyProcesses=@(Get-Process -Name BandKey -ErrorAction SilentlyContinue|Where-Object {$_.Path -eq $bandKeyExecutable})
        if($bandKeyProcesses.Count -gt 1){throw 'Unexpected duplicate agent processes'}
        $bandKeyProcess=if($bandKeyProcesses.Count){$bandKeyProcesses[0]}else{Start-Process -FilePath $bandKeyExecutable -ArgumentList '--host-bridge --resident' -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru}
        [void]$bandKeyProcess.Handle # Keep a handle to this process, not a later recycled PID.
        $bandKeyClock=[Diagnostics.Stopwatch]::StartNew();$bandKeyLastProgress=0.0;$bandKeyForced=$false
        $bandKeySeen=@{}
        Get-ChildItem -LiteralPath $bandKeyReports -Filter 'relay-*.json' -File -ErrorAction SilentlyContinue|ForEach-Object {$bandKeySeen[$_.Name]=($_.LastWriteTimeUtc.Ticks.ToString()+':'+$_.Length)}
        Write-BandKeyWatchLog 'agent-watched'
        while(!$bandKeyProcess.HasExited){
            if(Test-Path -LiteralPath $bandKeyStop){return} # Explicit update/uninstall must not relaunch.
            $bandKeyPaused=$true
            try{if(Test-Path -LiteralPath $bandKeySettings){$bandKeyPaused=[bool](Get-Content -LiteralPath $bandKeySettings -Raw|ConvertFrom-Json).paused}else{$bandKeyPaused=$false}}catch{$bandKeyPaused=$true}
            Get-ChildItem -LiteralPath $bandKeyReports -Filter 'relay-*.json' -File -ErrorAction SilentlyContinue|ForEach-Object {
                $stamp=$_.LastWriteTimeUtc.Ticks.ToString()+':'+$_.Length
                if(!$bandKeySeen.ContainsKey($_.Name) -or $bandKeySeen[$_.Name] -ne $stamp){$bandKeyLastProgress=$bandKeyClock.Elapsed.TotalSeconds;$bandKeySeen[$_.Name]=$stamp}
            }
            $bandKeyAction=Get-BandKeyHostWatchAction $bandKeyPaused $false ($bandKeyClock.Elapsed.TotalSeconds-$bandKeyLastProgress) $bandKeyClock.Elapsed.TotalSeconds
            if($bandKeyAction -eq 'paused'){$bandKeyLastProgress=$bandKeyClock.Elapsed.TotalSeconds}
            if($bandKeyAction -eq 'restart'){
                Write-BandKeyWatchLog 'report-stale-restart'
                $bandKeyProcess.Kill();$bandKeyProcess.WaitForExit();$bandKeyForced=$true;break
            }
            Start-Sleep -Seconds 5
            $bandKeyProcess.Refresh()
        }
        if(Test-Path -LiteralPath $bandKeyStop){return}
        $bandKeyProcess.WaitForExit()
        if(!$bandKeyForced -and $bandKeyProcess.ExitCode -eq 0){Write-BandKeyWatchLog 'explicit-agent-exit';return}
        if($bandKeyClock.Elapsed.TotalMinutes -ge 5){$bandKeyFailures=0}
        $bandKeyFailures++;Write-BandKeyWatchLog 'agent-recovery'
        Start-Sleep -Seconds ([Math]::Min(60,[Math]::Pow(2,[Math]::Min(6,$bandKeyFailures))))
        $bandKeyProcess.Dispose()
    }
}finally{if($bandKeyOwned){$bandKeyMutex.ReleaseMutex()};$bandKeyMutex.Dispose()}
