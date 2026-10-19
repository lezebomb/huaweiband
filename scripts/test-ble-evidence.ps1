param([Parameter(Mandatory=$true)][string]$SourceReport)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'phone-ble-evidence.ps1')
$bandKeySource=(Resolve-Path -LiteralPath $SourceReport).Path
$bandKeyOriginal=Get-Content -LiteralPath $bandKeySource -Raw | ConvertFrom-Json
$bandKeySourceDirectory=Split-Path -Parent $bandKeySource
$bandKeyOutput=Join-Path $bandKeyRoot ('artifacts\ble-evidence-tests-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $bandKeyOutput | Out-Null
$bandKeyTests=[Collections.Generic.List[object]]::new()
function Edit-BandKeyReport([string]$Root,[string]$Name,[scriptblock]$Edit){$bandKeyValue=Get-Content -LiteralPath (Join-Path $Root $Name) -Raw | ConvertFrom-Json;& $Edit $bandKeyValue;$bandKeyValue | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Root $Name) -Encoding utf8}
function Test-BandKeyEvidence([string]$Name,[bool]$Expected,[scriptblock]$Edit){
    $bandKeyCase=Join-Path $bandKeyOutput ([Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $bandKeyCase | Out-Null
    foreach($bandKeyFile in @('phone-result.json','ble-host-result.json','host-result.json','native-phone-result.json')){Copy-Item -LiteralPath (Join-Path $bandKeySourceDirectory $bandKeyFile) -Destination $bandKeyCase}
    & $Edit $bandKeyCase
    $bandKeyAccepted=$false
    try{$null=Get-BandKeyBleEvidence -Root $bandKeyCase -RunId $bandKeyOriginal.runId;$bandKeyAccepted=$true}catch{}
    $bandKeyTests.Add([pscustomobject]@{name=$Name;passed=($bandKeyAccepted -eq $Expected)})
    if($bandKeyAccepted -ne $Expected){throw ('Evidence validator regression: '+$Name)}
}
Test-BandKeyEvidence 'actual matched evidence including expected negative TLS teardown' $true {}
Test-BandKeyEvidence 'normal clean transport stop is accepted' $true {param($r) Edit-BandKeyReport $r 'ble-host-result.json' {param($v) $v.passed=$true;$v.stage='stopped';$v.error=$null}}
Test-BandKeyEvidence 'mismatched byte counters rejected' $false {param($r) Edit-BandKeyReport $r 'ble-host-result.json' {param($v) $v.sentBytes++}}
Test-BandKeyEvidence 'insufficient completed streams rejected' $false {param($r) Edit-BandKeyReport $r 'ble-host-result.json' {param($v) $v.streams=2}}
Test-BandKeyEvidence 'stale phone run rejected' $false {param($r) Edit-BandKeyReport $r 'phone-result.json' {param($v) $v.runId='stale'}}
Test-BandKeyEvidence 'failed phone check rejected' $false {param($r) Edit-BandKeyReport $r 'phone-result.json' {param($v) $v.checks[0].passed=$false}}
Test-BandKeyEvidence 'duplicate named phone check rejected' $false {param($r) Edit-BandKeyReport $r 'phone-result.json' {param($v) $v.checks+=@($v.checks[0])}}
Test-BandKeyEvidence 'native verification failure rejected' $false {param($r) Edit-BandKeyReport $r 'native-phone-result.json' {param($v) $v.failed=1}}
Test-BandKeyEvidence 'unknown host failure rejected' $false {param($r) Edit-BandKeyReport $r 'ble-host-result.json' {param($v) $v.passed=$false;$v.error='AccessViolationException'}}
Test-BandKeyEvidence 'duplicate proof delivery rejected' $false {param($r) Edit-BandKeyReport $r 'host-result.json' {param($v) $v.submissions=2}}
Test-BandKeyEvidence 'USB data mapping rejected' $false {param($r) Edit-BandKeyReport $r 'ble-host-result.json' {param($v) $v.usbDataMappingCreated=$true}}
Test-BandKeyEvidence 'missing no-authentication flag rejected' $false {param($r) Edit-BandKeyReport $r 'ble-host-result.json' {param($v) $v.PSObject.Properties.Remove('windowsAuthenticationSubmitted')}}
Test-BandKeyEvidence 'unexpected extra failed check rejected' $false {param($r) Edit-BandKeyReport $r 'phone-result.json' {param($v) $v.checks+=@([pscustomobject]@{name='extra';passed=$false})}}
Test-BandKeyEvidence 'TLS termination at relay rejected' $false {param($r) Edit-BandKeyReport $r 'host-result.json' {param($v) $v.tlsTerminated=$true}}
[ordered]@{passed=$bandKeyTests.Count;failed=0;tests=@($bandKeyTests);reportMutationTestsOnly=$true;newPhysicalBleRun=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\ble-evidence-tests.json') -Encoding utf8
Write-Output ('BLE evidence validator: '+$bandKeyTests.Count+' checks passed (report replay/mutations, no new hardware run).')
