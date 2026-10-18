param([Parameter(Mandatory=$true)][string]$AdbPath,[string]$Serial,[switch]$GrantNearbyPermissions)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
$bandKeyAdb=(Resolve-Path -LiteralPath $AdbPath).Path
if(!$Serial){$bandKeyDevices=@(& $bandKeyAdb devices | Where-Object {$_ -match '^\S+\s+device$'} | ForEach-Object {($_ -split '\s+')[0]});if($bandKeyDevices.Count -ne 1){throw 'Exactly one authorized phone is required.'};$Serial=$bandKeyDevices[0]}
function Start-BandKeyProcess([string]$File,[string[]]$Arguments){
    $bandKeyInfo=[Diagnostics.ProcessStartInfo]::new($File);$bandKeyInfo.UseShellExecute=$false;$bandKeyInfo.CreateNoWindow=$true;$bandKeyInfo.RedirectStandardOutput=$true;$bandKeyInfo.RedirectStandardError=$true
    $bandKeyInfo.Environment['DOTNET_ROOT']=Join-Path $bandKeyRoot '.tooling\dotnet'
    foreach($bandKeyArg in $Arguments){[void]$bandKeyInfo.ArgumentList.Add($bandKeyArg)}
    $bandKeyProcess=[Diagnostics.Process]::Start($bandKeyInfo)
    return @{Process=$bandKeyProcess;Out=$bandKeyProcess.StandardOutput.ReadToEndAsync();Err=$bandKeyProcess.StandardError.ReadToEndAsync()}
}
function Invoke-BandKeyAdb([string[]]$Arguments,[int]$Timeout=15000,[switch]$AllowAbsent){
    $bandKeyCall=Start-BandKeyProcess $bandKeyAdb (@('-s',$Serial)+$Arguments)
    try{
        if(!$bandKeyCall.Process.WaitForExit($Timeout)){$bandKeyCall.Process.Kill();throw ('ADB timeout: '+$Arguments[0])}
        $bandKeyText=$bandKeyCall.Out.GetAwaiter().GetResult()
        if($bandKeyCall.Process.ExitCode -and !$AllowAbsent){throw ('ADB failed: '+$Arguments[0])}
        return $bandKeyText
    }finally{$bandKeyCall.Process.Dispose()}
}
function Copy-BandKeyPublicKey([string]$Destination){
    # Stream public bytes without PowerShell text decoding. No private export API.
    $bandKeyInfo=[Diagnostics.ProcessStartInfo]::new($bandKeyAdb);$bandKeyInfo.UseShellExecute=$false;$bandKeyInfo.CreateNoWindow=$true;$bandKeyInfo.RedirectStandardOutput=$true
    foreach($bandKeyArg in @('-s',$Serial,'exec-out','run-as','dev.bandkey.bridge','cat','files/usb-test/phone-public.bin')){[void]$bandKeyInfo.ArgumentList.Add($bandKeyArg)}
    $bandKeyProcess=[Diagnostics.Process]::Start($bandKeyInfo);$bandKeyFile=[IO.File]::Create($Destination)
    try{
        $bandKeyCopy=$bandKeyProcess.StandardOutput.BaseStream.CopyToAsync($bandKeyFile)
        if(!$bandKeyCopy.Wait(10000)){$bandKeyProcess.Kill();throw 'Public export timeout'}
        if(!$bandKeyProcess.WaitForExit(10000) -or $bandKeyProcess.ExitCode){throw 'Public export failed'}
        $bandKeyCopy.GetAwaiter().GetResult()
    }finally{$bandKeyFile.Dispose();$bandKeyProcess.Dispose()}
}
$bandKeyOutput=Join-Path $bandKeyRoot ('artifacts\phone-ble-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $bandKeyOutput | Out-Null
$bandKeyExecutable=Join-Path $bandKeyRoot 'tests\BandKey.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\BandKey.Windows.Tests.exe'
$bandKeyDotnet=Join-Path $bandKeyRoot '.tooling\dotnet\dotnet.exe'
$bandKeyMain=Join-Path $bandKeyRoot 'android\app\build\outputs\apk\debug\app-debug.apk'
$bandKeyTest=Join-Path $bandKeyRoot 'android\app\build\outputs\apk\androidTest\debug\app-debug-androidTest.apk'
$bandKeyHost=$null;$bandKeyTunnel=$null;$bandKeyPhone=$null;$bandKeyInstalled=$false;$bandKeyPassed=$false
$bandKeyCleanupErrors=[Collections.Generic.List[string]]::new()
$bandKeyRunId=[Guid]::NewGuid().ToString('N')
try{
    if((Invoke-BandKeyAdb @('reverse','--list')) -match '\btcp:1944[34]\b'){throw 'Existing USB data mapping preserved; remove it explicitly before a BLE-only test.'}
    Invoke-BandKeyAdb @('install','-r',$bandKeyMain) 120000 | Out-Null
    Invoke-BandKeyAdb @('install','-r',$bandKeyTest) 120000 | Out-Null;$bandKeyInstalled=$true
    if($GrantNearbyPermissions){foreach($bandKeyPermission in @('android.permission.BLUETOOTH_CONNECT','android.permission.BLUETOOTH_ADVERTISE')){Invoke-BandKeyAdb @('shell','pm','grant','dev.bandkey.bridge',$bandKeyPermission) | Out-Null}}
    Invoke-BandKeyAdb @('shell','am','instrument','-w','-e','mode','export','-e','runId',$bandKeyRunId,'dev.bandkey.bridge.test/dev.bandkey.bridge.VmUsbInstrumentation') 90000 | Out-Null
    $bandKeyExport=(Invoke-BandKeyAdb @('exec-out','run-as','dev.bandkey.bridge','cat','files/usb-test/result.json')) | ConvertFrom-Json
    if(!$bandKeyExport.passed -or $bandKeyExport.mode -ne 'export' -or $bandKeyExport.runId -ne $bandKeyRunId){throw 'Stale or failed export'}
    Copy-BandKeyPublicKey (Join-Path $bandKeyOutput 'phone-public.bin')
    $bandKeyHost=Start-BandKeyProcess $bandKeyExecutable @('--phone-usb-lab','--ble','--root',$bandKeyOutput)
    for($bandKeyWait=0;$bandKeyWait -lt 30 -and !(Test-Path -LiteralPath (Join-Path $bandKeyOutput 'ready'));$bandKeyWait++){if($bandKeyHost.Process.HasExited){throw 'BLE TLS fixture failed to start'};Start-Sleep -Seconds 1}
    if(!(Test-Path -LiteralPath (Join-Path $bandKeyOutput 'ready'))){throw 'BLE TLS fixture readiness timeout'}
    Invoke-BandKeyAdb @('push',(Join-Path $bandKeyOutput 'pc.json'),'/data/local/tmp/bandkey-ble-pc.json') | Out-Null
    Invoke-BandKeyAdb @('shell','run-as','dev.bandkey.bridge','cp','/data/local/tmp/bandkey-ble-pc.json','files/usb-test/pc.json') | Out-Null
    Invoke-BandKeyAdb @('shell','run-as','dev.bandkey.bridge','rm','-f','files/usb-test/ble-go','files/usb-test/ble-ready') | Out-Null
    $bandKeyPhone=Start-BandKeyProcess $bandKeyAdb @('-s',$Serial,'shell','am','instrument','-w','-e','mode','ble','-e','runId',$bandKeyRunId,'dev.bandkey.bridge.test/dev.bandkey.bridge.VmUsbInstrumentation')
    $bandKeyAdvertising=$false
    for($bandKeyWait=0;$bandKeyWait -lt 20;$bandKeyWait++){
        if($bandKeyPhone.Process.HasExited){throw 'Phone BLE instrumentation failed before advertising'}
        if((Invoke-BandKeyAdb @('exec-out','run-as','dev.bandkey.bridge','cat','files/usb-test/ble-ready') -AllowAbsent) -eq $bandKeyRunId){$bandKeyAdvertising=$true;break};Start-Sleep -Seconds 1
    }
    if(!$bandKeyAdvertising){throw 'Phone BLE advertising timeout; check nearby-device permission'}
    $bandKeyTunnel=Start-BandKeyProcess $bandKeyExecutable @('--phone-ble-tunnel',$bandKeyOutput)
    for($bandKeyWait=0;$bandKeyWait -lt 45 -and !(Test-Path -LiteralPath (Join-Path $bandKeyOutput 'ble-pc-ready'));$bandKeyWait++){if($bandKeyTunnel.Process.HasExited){throw 'Windows BLE connection failed'};Start-Sleep -Seconds 1}
    if(!(Test-Path -LiteralPath (Join-Path $bandKeyOutput 'ble-pc-ready'))){throw 'Windows BLE readiness timeout'}
    & $bandKeyDotnet ([IO.Path]::ChangeExtension($bandKeyExecutable,'.dll')) --phone-usb-lab --root $bandKeyOutput --new-request
    if($LASTEXITCODE){throw 'Synthetic native request generation failed'}
    [IO.File]::WriteAllText((Join-Path $bandKeyOutput 'ble-go'),$bandKeyRunId)
    Invoke-BandKeyAdb @('push',(Join-Path $bandKeyOutput 'ble-go'),'/data/local/tmp/bandkey-ble-go') | Out-Null
    Invoke-BandKeyAdb @('shell','run-as','dev.bandkey.bridge','cp','/data/local/tmp/bandkey-ble-go','files/usb-test/ble-go') | Out-Null
    if(!$bandKeyPhone.Process.WaitForExit(90000)){throw 'Phone BLE test timeout'}
    $bandKeyReport=(Invoke-BandKeyAdb @('exec-out','run-as','dev.bandkey.bridge','cat','files/usb-test/result.json')) | ConvertFrom-Json
    $bandKeyReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $bandKeyOutput 'phone-result.json') -Encoding utf8
    if(!$bandKeyReport.passed -or $bandKeyReport.mode -ne 'ble' -or $bandKeyReport.runId -ne $bandKeyRunId -or $bandKeyReport.windowsAuthenticationSubmitted -or !$bandKeyReport.ble.usbForControlOnly -or $bandKeyReport.ble.sentBytes -le 0 -or $bandKeyReport.ble.receivedBytes -le 0){throw 'BLE phone result failed or stale'}
    if((Invoke-BandKeyAdb @('reverse','--list')) -match '\btcp:1944[34]\b'){throw 'USB data mapping appeared during BLE test'}
    & (Join-Path $bandKeyRoot 'artifacts\bandkey-provider-lab\BandKey.CredentialProvider.Tests.exe') --verify-phone $bandKeyOutput
    if($LASTEXITCODE){throw 'CNG rejected captured BLE phone signature'}
    $bandKeyPassed=$true
}finally{
    if($bandKeyPhone){
        try{if(!$bandKeyPhone.Process.HasExited){Invoke-BandKeyAdb @('shell','am','force-stop','dev.bandkey.bridge') | Out-Null;if(!$bandKeyPhone.Process.WaitForExit(5000)){$bandKeyPhone.Process.Kill()}}}
        catch{$bandKeyPassed=$false;$bandKeyCleanupErrors.Add('phone-stop-'+$_.Exception.GetType().Name)}
        finally{if(!$bandKeyPhone.Process.HasExited){$bandKeyPhone.Process.Kill()};$bandKeyPhone.Process.Dispose()}
    }
    foreach($bandKeyChild in @(@{Item=$bandKeyTunnel;Stop='ble-stop';Log='ble'},@{Item=$bandKeyHost;Stop='stop';Log='tls'})){
        if($bandKeyChild.Item){
            try{[IO.File]::WriteAllText((Join-Path $bandKeyOutput $bandKeyChild.Stop),'stop');if(!$bandKeyChild.Item.Process.WaitForExit(15000)){$bandKeyChild.Item.Process.Kill();$bandKeyPassed=$false};[IO.File]::WriteAllText((Join-Path $bandKeyOutput ($bandKeyChild.Log+'-stderr.txt')),$bandKeyChild.Item.Err.GetAwaiter().GetResult())}
            catch{$bandKeyPassed=$false;$bandKeyCleanupErrors.Add($bandKeyChild.Log+'-stop-'+$_.Exception.GetType().Name)}
            finally{if(!$bandKeyChild.Item.Process.HasExited){$bandKeyChild.Item.Process.Kill()};$bandKeyChild.Item.Process.Dispose()}
        }
    }
    if($bandKeyPassed){
        try{
            $bandKeyTls=Get-Content -LiteralPath (Join-Path $bandKeyOutput 'host-result.json') -Raw | ConvertFrom-Json
            $bandKeyBle=Get-Content -LiteralPath (Join-Path $bandKeyOutput 'ble-host-result.json') -Raw | ConvertFrom-Json
            if($bandKeyTls.submissions -ne 1 -or !$bandKeyTls.bleFixture -or $bandKeyTls.tunnelAccepted -ne 0 -or $bandKeyBle.SentBytes -le 0 -or $bandKeyBle.ReceivedBytes -le 0 -or $bandKeyBle.windowsAuthenticationSubmitted){$bandKeyPassed=$false}
        }catch{$bandKeyPassed=$false;$bandKeyCleanupErrors.Add('host-report-'+$_.Exception.GetType().Name)}
    }
    if($bandKeyInstalled){try{Invoke-BandKeyAdb @('uninstall','dev.bandkey.bridge.test') | Out-Null}catch{$bandKeyPassed=$false;$bandKeyCleanupErrors.Add('test-package-remove-'+$_.Exception.GetType().Name)}}
    [ordered]@{passed=$bandKeyPassed;runId=$bandKeyRunId;mainApkSha256=(Get-FileHash -LiteralPath $bandKeyMain -Algorithm SHA256).Hash.ToLowerInvariant();transport='physical BLE GATT carrying pinned TLS';physicalBleEvidenceVerified=$bandKeyPassed;usbForControlOnly=$true;syntheticAccount=$true;automaticTestConfirmation=$true;windowsAuthenticationSubmitted=$false;realWearableTested=$false;vmOperated=$false;cleanupErrors=@($bandKeyCleanupErrors)} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bandKeyOutput 'summary.json') -Encoding utf8
}
Get-Content -LiteralPath (Join-Path $bandKeyOutput 'summary.json') -Raw
if(!$bandKeyPassed){throw 'BLE physical test verification failed'}
