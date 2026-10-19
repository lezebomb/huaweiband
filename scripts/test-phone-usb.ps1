param([Parameter(Mandatory=$true)][string]$AdbPath,[string]$Serial,[switch]$RemoveTestPackage)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
$bandKeyAdb=(Resolve-Path -LiteralPath $AdbPath).Path
if(!$Serial){
    $bandKeyDevices=@(& $bandKeyAdb devices | Where-Object {$_ -match '^\S+\s+device$'} | ForEach-Object {($_ -split '\s+')[0]})
    if($bandKeyDevices.Count -ne 1){throw 'Exactly one authorized USB phone is required, or supply -Serial.'}
    $Serial=$bandKeyDevices[0]
}
function Invoke-BandKeyAdb([string[]]$Arguments){
    $bandKeyText=& $bandKeyAdb -s $Serial @Arguments
    if($LASTEXITCODE){throw ('ADB failed: '+$Arguments[0])}
    return $bandKeyText
}
function Copy-BandKeyPhoneFile([string]$Name,[string]$Destination){
    $bandKeyInfo=[Diagnostics.ProcessStartInfo]::new($bandKeyAdb);$bandKeyInfo.UseShellExecute=$false;$bandKeyInfo.RedirectStandardOutput=$true;$bandKeyInfo.RedirectStandardError=$true;$bandKeyInfo.CreateNoWindow=$true
    foreach($bandKeyArg in @('-s',$Serial,'exec-out','run-as','dev.bandkey.bridge','cat',('files/usb-test/'+$Name))){[void]$bandKeyInfo.ArgumentList.Add($bandKeyArg)}
    $bandKeyProcess=[Diagnostics.Process]::Start($bandKeyInfo);$bandKeyFile=[IO.File]::Create($Destination)
    try{$bandKeyProcess.StandardOutput.BaseStream.CopyTo($bandKeyFile)}finally{$bandKeyFile.Dispose()}
    $bandKeyProcess.WaitForExit();if($bandKeyProcess.ExitCode){throw 'Cannot retrieve app-owned USB test result'};$bandKeyProcess.Dispose()
}
function Install-BandKeyTestApk([string]$Package,[string]$Path){
    # pm path returns 1 for an absent package. Query the exact package first so
    # first-time test installation does not turn expected absence into an ADB error.
    $bandKeyPresent=@(Invoke-BandKeyAdb @('shell','pm','list','packages',$Package)) -contains ('package:'+$Package)
    $bandKeyInstalled=$null
    if($bandKeyPresent){$bandKeyInstalled=@(Invoke-BandKeyAdb @('shell','pm','path',$Package)) | Where-Object {$_ -match '^package:/data/app/.+/base\.apk$'} | Select-Object -First 1}
    if($bandKeyInstalled){
        $bandKeyBase=$bandKeyInstalled.Substring(8).Trim()
        if($bandKeyBase -match '^/data/app/[A-Za-z0-9_~./=+-]+/base\.apk$'){
            $bandKeyHash=(@(Invoke-BandKeyAdb @('shell','sha256sum',$bandKeyBase)) -join '')
            if($bandKeyHash -match '^([a-f0-9]{64})\s' -and $Matches[1] -eq (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()){return}
        }
    }
    Invoke-BandKeyAdb @('install','-r',$Path) | Out-Null
}
$bandKeyOutput=Join-Path $bandKeyRoot ('artifacts\phone-usb-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $bandKeyOutput | Out-Null
$bandKeyResults=[Collections.Generic.List[object]]::new()
function Invoke-BandKeyMode([string]$Mode){
    $bandKeyRunId=[Guid]::NewGuid().ToString('N')
    $bandKeyModeInfo=[Diagnostics.ProcessStartInfo]::new($bandKeyAdb);$bandKeyModeInfo.UseShellExecute=$false;$bandKeyModeInfo.CreateNoWindow=$true;$bandKeyModeInfo.RedirectStandardOutput=$true;$bandKeyModeInfo.RedirectStandardError=$true
    foreach($bandKeyArg in @('-s',$Serial,'shell','am','instrument','-w','-e','mode',$Mode,'-e','runId',$bandKeyRunId,'dev.bandkey.bridge.test/dev.bandkey.bridge.VmUsbInstrumentation')){[void]$bandKeyModeInfo.ArgumentList.Add($bandKeyArg)}
    $bandKeyModeProcess=[Diagnostics.Process]::Start($bandKeyModeInfo);$bandKeyModeOut=$bandKeyModeProcess.StandardOutput.ReadToEndAsync();$bandKeyModeErr=$bandKeyModeProcess.StandardError.ReadToEndAsync()
    try{
        if(!$bandKeyModeProcess.WaitForExit(90000)){
            # Only stop our instrumentation target and this command, never the ADB server.
            Invoke-BandKeyAdb @('shell','am','force-stop','dev.bandkey.bridge') | Out-Null
            $bandKeyModeProcess.Kill();throw "Phone instrumentation timeout: $Mode"
        }
        if($bandKeyModeProcess.ExitCode){throw "Phone instrumentation failed: $Mode"}
    }finally{$bandKeyModeProcess.Dispose()}
    $bandKeyResult=Join-Path $bandKeyOutput ($Mode+'.json');Copy-BandKeyPhoneFile 'result.json' $bandKeyResult
    $bandKeyReport=Get-Content -LiteralPath $bandKeyResult -Raw | ConvertFrom-Json
    $bandKeyResults.Add($bandKeyReport)
    if(!$bandKeyReport.passed -or $bandKeyReport.windowsAuthenticationSubmitted -or $bandKeyReport.mode -ne $Mode -or $bandKeyReport.runId -ne $bandKeyRunId){throw "Phone test failed or stale result: $Mode"}
    Write-Output "Physical Android USB test passed: $Mode"
}
$bandKeyHost=Join-Path $bandKeyRoot 'tests\BandKey.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\BandKey.Windows.Tests.exe'
$bandKeyDotnet=Join-Path $bandKeyRoot '.tooling\dotnet\dotnet.exe'
$bandKeyServer=$null;$bandKeyOwnedReverse=$false;$bandKeyPassed=$false;$bandKeyTestInstalled=$false
try{
    Install-BandKeyTestApk 'dev.bandkey.bridge' (Join-Path $bandKeyRoot 'android\app\build\outputs\apk\debug\app-debug.apk')
    Install-BandKeyTestApk 'dev.bandkey.bridge.test' (Join-Path $bandKeyRoot 'android\app\build\outputs\apk\androidTest\debug\app-debug-androidTest.apk')
    $bandKeyTestInstalled=$true
    Invoke-BandKeyMode 'export';Copy-BandKeyPhoneFile 'phone-public.bin' (Join-Path $bandKeyOutput 'phone-public.bin')
    $bandKeyInfo=[Diagnostics.ProcessStartInfo]::new($bandKeyHost);$bandKeyInfo.UseShellExecute=$false;$bandKeyInfo.CreateNoWindow=$true;$bandKeyInfo.RedirectStandardOutput=$true;$bandKeyInfo.RedirectStandardError=$true
    $bandKeyInfo.Environment['DOTNET_ROOT']=Join-Path $bandKeyRoot '.tooling\dotnet'
    foreach($bandKeyArg in @('--phone-usb-lab','--root',$bandKeyOutput)){[void]$bandKeyInfo.ArgumentList.Add($bandKeyArg)}
    $bandKeyServer=[Diagnostics.Process]::Start($bandKeyInfo);$bandKeyStdout=$bandKeyServer.StandardOutput.ReadToEndAsync();$bandKeyStderr=$bandKeyServer.StandardError.ReadToEndAsync()
    for($bandKeyWait=0;$bandKeyWait -lt 30 -and !(Test-Path -LiteralPath (Join-Path $bandKeyOutput 'ready'));$bandKeyWait++){
        if($bandKeyServer.HasExited){throw 'USB host fixture exited before readiness'}
        Start-Sleep -Seconds 1
    }
    if(!(Test-Path -LiteralPath (Join-Path $bandKeyOutput 'ready'))){throw 'USB host fixture readiness timeout'}
    if(@(Invoke-BandKeyAdb @('reverse','--list')) -match '\btcp:19444\b'){throw 'Phone port 19444 already mapped; existing mapping was preserved.'}
    Invoke-BandKeyAdb @('reverse','--no-rebind','tcp:19444','tcp:19444') | Out-Null;$bandKeyOwnedReverse=$true
    Invoke-BandKeyAdb @('push',(Join-Path $bandKeyOutput 'pc.json'),'/data/local/tmp/bandkey-usb-pc.json') | Out-Null
    Invoke-BandKeyAdb @('shell','run-as','dev.bandkey.bridge','cp','/data/local/tmp/bandkey-usb-pc.json','files/usb-test/pc.json') | Out-Null
    Invoke-BandKeyMode 'approve'
    Invoke-BandKeyMode 'wrong-pin'
    & $bandKeyDotnet ([IO.Path]::ChangeExtension($bandKeyHost,'.dll')) --phone-usb-lab --root $bandKeyOutput --new-request;if($LASTEXITCODE){throw 'New fixture request failed'}
    Invoke-BandKeyMode 'deny'
    & $bandKeyDotnet ([IO.Path]::ChangeExtension($bandKeyHost,'.dll')) --phone-usb-lab --root $bandKeyOutput --new-request;if($LASTEXITCODE){throw 'New fixture request failed'}
    Invoke-BandKeyMode 'expired'
    Invoke-BandKeyAdb @('reverse','--remove','tcp:19444') | Out-Null;$bandKeyOwnedReverse=$false
    Invoke-BandKeyMode 'link-down'
    Invoke-BandKeyAdb @('reverse','--no-rebind','tcp:19444','tcp:19444') | Out-Null;$bandKeyOwnedReverse=$true
    [IO.File]::WriteAllText((Join-Path $bandKeyOutput 'revoked'),'revoked')
    Invoke-BandKeyMode 'revoked'
    & (Join-Path $bandKeyRoot 'artifacts\bandkey-provider-lab\BandKey.CredentialProvider.Tests.exe') --verify-phone $bandKeyOutput
    if($LASTEXITCODE){throw 'Native CNG could not verify captured physical phone signature'}
    $bandKeyPassed=$true
}finally{
    if($bandKeyOwnedReverse){Invoke-BandKeyAdb @('reverse','--remove','tcp:19444') | Out-Null}
    if($bandKeyServer){
        [IO.File]::WriteAllText((Join-Path $bandKeyOutput 'stop'),'stop')
        if(!$bandKeyServer.WaitForExit(15000)){$bandKeyServer.Kill();$bandKeyPassed=$false}
        [IO.File]::WriteAllText((Join-Path $bandKeyOutput 'host-stdout.txt'),$bandKeyStdout.GetAwaiter().GetResult());[IO.File]::WriteAllText((Join-Path $bandKeyOutput 'host-stderr.txt'),$bandKeyStderr.GetAwaiter().GetResult());$bandKeyServer.Dispose()
        if(Test-Path -LiteralPath (Join-Path $bandKeyOutput 'host-result.json')){
            $bandKeyHostResult=Get-Content -LiteralPath (Join-Path $bandKeyOutput 'host-result.json') -Raw | ConvertFrom-Json
            if($bandKeyHostResult.submissions -ne 1 -or $bandKeyHostResult.windowsAuthenticationSubmitted -or $bandKeyHostResult.tlsTerminated -or !$bandKeyHostResult.loopbackOnly){$bandKeyPassed=$false}
        }else{$bandKeyPassed=$false}
    }
    # Retain the same-hash test APK for repeated local runs. Removal is explicit
    # so switching USB/BLE does not repeatedly invoke the phone's installer.
    if($bandKeyTestInstalled -and $RemoveTestPackage){Invoke-BandKeyAdb @('uninstall','dev.bandkey.bridge.test') | Out-Null}
    [ordered]@{passed=$bandKeyPassed;mainApkSha256=(Get-FileHash -LiteralPath (Join-Path $bandKeyRoot 'android\app\build\outputs\apk\debug\app-debug.apk') -Algorithm SHA256).Hash.ToLowerInvariant();phoneRuns=$bandKeyResults.Count;checks=@($bandKeyResults | ForEach-Object {$_.checks}).Count;keyInsideSecureHardware=@($bandKeyResults | Where-Object {$_.keyInsideSecureHardware -eq $true}).Count -eq $bandKeyResults.Count;transport='USB ADB reverse + opaque TCP tunnel + pinned loopback HTTPS';syntheticAccount=$true;automaticTestConfirmation=$true;windowsAuthenticationSubmitted=$false;vmOperated=$false;realWearableTested=$false;results=$bandKeyResults} |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $bandKeyOutput 'summary.json') -Encoding utf8
    $bandKeyOutput | Set-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\latest-phone-usb-test.txt') -Encoding utf8
}
Get-Content -LiteralPath (Join-Path $bandKeyOutput 'summary.json') -Raw
if(!$bandKeyPassed){throw 'Physical USB test verification failed'}
