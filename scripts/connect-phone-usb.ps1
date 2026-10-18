param([Parameter(Mandatory=$true)][string]$GuestAddress,[Parameter(Mandatory=$true)][string]$AdbPath,[string]$Serial,[ValidateRange(1,7200)][int]$Seconds=1800)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
$bandKeyAddress=[Net.IPAddress]::Parse($GuestAddress)
$bandKeyBytes=$bandKeyAddress.GetAddressBytes()
if($bandKeyAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or !($bandKeyBytes[0] -eq 10 -or ($bandKeyBytes[0] -eq 172 -and $bandKeyBytes[1] -ge 16 -and $bandKeyBytes[1] -le 31) -or ($bandKeyBytes[0] -eq 192 -and $bandKeyBytes[1] -eq 168))){throw 'Supply the private IPv4 of the BandKey guest.'}
$bandKeyAdb=(Resolve-Path -LiteralPath $AdbPath).Path
if(!$Serial){$bandKeyDevices=@(& $bandKeyAdb devices | Where-Object {$_ -match '^\S+\s+device$'} | ForEach-Object {($_ -split '\s+')[0]});if($bandKeyDevices.Count -ne 1){throw 'Exactly one authorized USB device is required.'};$Serial=$bandKeyDevices[0]}
function Invoke-BandKeyAdb([string[]]$Arguments){$bandKeyText=& $bandKeyAdb -s $Serial @Arguments;if($LASTEXITCODE){throw ('ADB failed: '+$Arguments[0])};return $bandKeyText}
if(@(Invoke-BandKeyAdb @('reverse','--list')) -match '\btcp:19443\b'){throw 'Phone port 19443 already mapped; existing mapping was preserved.'}
$bandKeyProbe=[Net.Sockets.TcpClient]::new()
try{
    $bandKeyConnecting=$bandKeyProbe.ConnectAsync($bandKeyAddress,19443)
    if(!$bandKeyConnecting.Wait(3000)){throw 'VM relay not reachable. Start its private-IP listener first.'}
    $bandKeyConnecting.GetAwaiter().GetResult()
}finally{$bandKeyProbe.Dispose()}
$bandKeyOutput=Join-Path $bandKeyRoot ('artifacts\usb-vm-tunnel-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $bandKeyOutput | Out-Null
$bandKeyReady=Join-Path $bandKeyOutput 'ready';$bandKeyStop=Join-Path $bandKeyOutput 'stop'
$bandKeyInfo=[Diagnostics.ProcessStartInfo]::new((Join-Path $bandKeyRoot 'artifacts\BandKey-win-x64\BandKey.exe'));$bandKeyInfo.UseShellExecute=$false;$bandKeyInfo.CreateNoWindow=$true
foreach($bandKeyArg in @('--usb-tunnel','--target',$GuestAddress,'--seconds',$Seconds.ToString(),'--ready',$bandKeyReady,'--stop-file',$bandKeyStop,'--output',(Join-Path $bandKeyOutput 'result.json'))){[void]$bandKeyInfo.ArgumentList.Add($bandKeyArg)}
$bandKeyProcess=[Diagnostics.Process]::Start($bandKeyInfo);$bandKeyOwned=$false
try{
    for($bandKeyWait=0;$bandKeyWait -lt 15 -and !(Test-Path -LiteralPath $bandKeyReady);$bandKeyWait++){if($bandKeyProcess.HasExited){throw 'USB tunnel failed to start.'};Start-Sleep -Seconds 1}
    if(!(Test-Path -LiteralPath $bandKeyReady)){throw 'USB tunnel readiness timeout.'}
    Invoke-BandKeyAdb @('reverse','--no-rebind','tcp:19443','tcp:19443') | Out-Null;$bandKeyOwned=$true
    Write-Output 'USB tunnel ready. Phone endpoint: https://127.0.0.1:19443 ; TLS still terminates at the VM. No shared Wi-Fi or VMware NAT port forwarding is required.'
    while(!$bandKeyProcess.WaitForExit(1000)){
        & $bandKeyAdb -s $Serial get-state *> $null
        if($LASTEXITCODE){break}
    }
}finally{
    if($bandKeyOwned){& $bandKeyAdb -s $Serial reverse --remove tcp:19443 *> $null}
    [IO.File]::WriteAllText($bandKeyStop,'stop');if(!$bandKeyProcess.WaitForExit(10000)){$bandKeyProcess.Kill()};$bandKeyProcess.Dispose()
}
