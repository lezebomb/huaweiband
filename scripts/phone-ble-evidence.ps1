function Get-BandKeyBleEvidence {
    param([Parameter(Mandatory=$true)][string]$Root,[Parameter(Mandatory=$true)][string]$RunId)
    $bandKeyPhone=Get-Content -LiteralPath (Join-Path $Root 'phone-result.json') -Raw | ConvertFrom-Json
    $bandKeyHost=Get-Content -LiteralPath (Join-Path $Root 'ble-host-result.json') -Raw | ConvertFrom-Json
    $bandKeyTls=Get-Content -LiteralPath (Join-Path $Root 'host-result.json') -Raw | ConvertFrom-Json
    $bandKeyCng=Get-Content -LiteralPath (Join-Path $Root 'native-phone-result.json') -Raw | ConvertFrom-Json
    if(!$bandKeyPhone.passed -or $bandKeyPhone.mode -ne 'ble' -or $bandKeyPhone.runId -ne $RunId -or !$bandKeyPhone.normalIdentityPreserved -or !$bandKeyPhone.syntheticAccount -or !$bandKeyPhone.automaticTestConfirmation -or $bandKeyPhone.windowsAuthenticationSubmitted -ne $false -or $bandKeyPhone.realWearableTested -ne $false){throw 'Invalid or stale phone BLE evidence'}
    if(@($bandKeyPhone.checks | Where-Object {!$_.passed}).Count){throw 'Phone has a failing check'}
    $bandKeyRequired=@('phone lab key differs from normal bridge identity','BLE hardware test accepts only its loopback synthetic fixture','phone BLE advertisement started','PC subscribed and controller supplied this run ID','PC-signed prompt crossed BLE TLS','only fixed synthetic account/session may auto-sign','BLE authorization submitted with no Windows unlock claim','BLE approval replay rejected','both GATT directions carried bytes','wrong TLS certificate refused over BLE','normal bridge identity remains unchanged')
    foreach($bandKeyName in $bandKeyRequired){
        $bandKeyCheck=@($bandKeyPhone.checks | Where-Object {$_.name -eq $bandKeyName})
        if($bandKeyCheck.Count -ne 1 -or !$bandKeyCheck[0].passed){throw 'Missing/failed BLE hardware check'}
    }
    if(!$bandKeyPhone.ble.usbForControlOnly -or $bandKeyPhone.ble.streams -lt 3 -or $bandKeyHost.streams -lt 3 -or $bandKeyHost.usbDataMappingCreated -ne $false -or $bandKeyHost.windowsAuthenticationSubmitted -ne $false -or $bandKeyHost.mtu -lt 23 -or $bandKeyHost.passed -isnot [bool]){throw 'Invalid BLE stream/control evidence'}
    if($bandKeyHost.sentBytes -le 0 -or $bandKeyHost.receivedBytes -le 0 -or $bandKeyHost.sentBytes -ne $bandKeyPhone.ble.receivedBytes -or $bandKeyHost.receivedBytes -ne $bandKeyPhone.ble.sentBytes){throw 'BLE byte counters do not match across the physical link'}
    if($bandKeyTls.submissions -ne 1 -or !$bandKeyTls.bleFixture -or $bandKeyTls.tunnelAccepted -ne 0 -or !$bandKeyTls.loopbackOnly -or $bandKeyTls.tlsTerminated -ne $false -or !$bandKeyTls.syntheticAccount -or $bandKeyTls.windowsAuthenticationSubmitted -ne $false -or $bandKeyTls.realWearableTested -ne $false){throw 'Invalid TLS fixture delivery evidence'}
    if($bandKeyCng.passed -ne 2 -or $bandKeyCng.failed -ne 0 -or $bandKeyCng.windowsAuthenticationSubmitted -ne $false){throw 'Native signature verification did not pass'}
    # The final wrong-certificate test deliberately tears down its TLS stream.
    # Accept that termination only after all positive/negative checks and all
    # cross-device counters match. Never silently ignore an arbitrary host error.
    $bandKeyExpectedNegativeEnd=(!$bandKeyHost.passed -and $bandKeyHost.stage -in @('stream-failed','link-failed') -and $bandKeyHost.error -in @('OperationCanceledException','IOException','ChannelClosedException'))
    if(!$bandKeyHost.passed -and !$bandKeyExpectedNegativeEnd){throw 'Unexpected BLE host failure'}
    [pscustomobject][ordered]@{
        consistent=$true;checks=$bandKeyRequired.Count;mtu=$bandKeyHost.mtu;completedStreams=$bandKeyHost.streams
        pcToPhoneBytes=$bandKeyHost.sentBytes;phoneToPcBytes=$bandKeyHost.receivedBytes
        transportStoppedCleanly=[bool]$bandKeyHost.passed;expectedNegativeTlsTermination=[bool]$bandKeyExpectedNegativeEnd
        keyInsideSecureHardwareReported=[bool]$bandKeyPhone.keyInsideSecureHardware
    }
}
