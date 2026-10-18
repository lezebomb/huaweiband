$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'test-bandkey-provider.ps1')
$bandKeyInput=Join-Path $bandKeyRoot 'artifacts\bandkey-provider-lab'
$bandKeyOutput=Join-Path $bandKeyRoot ('virtualmachine\BandKey-VM-Signed-Lab-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $bandKeyOutput | Out-Null
foreach($bandKeyName in @('BandKey.CredentialProvider.dll','BandKey.VmSetup.exe','BandKey.VmConfirmation.exe')){
    Copy-Item -LiteralPath (Join-Path $bandKeyInput $bandKeyName) -Destination $bandKeyOutput
}
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'docs\vm-signed-confirmation-test.md') -Destination (Join-Path $bandKeyOutput 'READ-ME-FIRST.txt')
$bandKeyReport=Get-Content -LiteralPath (Join-Path $bandKeyInput 'tests.json') -Raw | ConvertFrom-Json
$bandKeyInteropReport=Get-Content -LiteralPath (Join-Path $bandKeyInput 'grant-interop.json') -Raw | ConvertFrom-Json
$bandKeyFiles=@(Get-ChildItem -LiteralPath $bandKeyOutput -File | Sort-Object Name | ForEach-Object {
    [ordered]@{name=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
[ordered]@{
    purpose='VM-only simulated signed approval with encrypted local-account credential; real wearable untested'
    clsid='{76B424B6-A72D-4E98-9DC1-485E37A5429C}'
    supportedGuest='VMware Windows 11 x64 build 26300'
    ordinaryProcessChecksPassed=$bandKeyReport.passed
    csharpGrantInteropChecksPassed=$bandKeyInteropReport.passed
    nativeReverseInteropChecksPassed=2
    nativeStaticAnalysisPassed=$true
    cleanupFixVersion=1
    cleanupFixGuestUserReported=$true
    providerLifecycleFixVersion=2
    providerDiagnosticsAvailable=$true
    signedPathOrdinaryProcessTest='synthetic SID/session and vault; real message pump, files, crypto and serialization; no Windows authentication submission'
    previousSignedGuestResult=[ordered]@{evidence='user screenshots/report';approvalDelivered=$true;bandKeyTileSelected=$true;automaticWindowsUnlock=$false;error='No current signed authorization'}
    credentialStorage='Machine DPAPI plus SYSTEM/administrator-only vault ACL; VM password entered locally by user'
    simulatedDeviceEvent=$true
    previousManualStage=[ordered]@{evidence='user-reported';install=$true;correctPasswordDesktop=$true;incorrectPasswordRejected=$true;systemSignInPreserved=$true;uninstall=$false;uninstallStatus='later screenshot: registration removed but file cleanup incomplete'}
    hostRegistrationPerformed=$false
    guestInstallationTested=$false
    windowsAuthenticationTested=$false
    wearableUnlockImplemented=$false
    files=$bandKeyFiles
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $bandKeyOutput 'manifest.json') -Encoding UTF8
& (Join-Path $PSScriptRoot 'new-lab-data-iso.ps1') -SourceDirectory $bandKeyOutput
