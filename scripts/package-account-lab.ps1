$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
$bandKeyInput=Join-Path $bandKeyRoot 'artifacts\bandkey-provider-lab'
$bandKeyTests=Get-Content -LiteralPath (Join-Path $bandKeyInput 'tests.json') -Raw | ConvertFrom-Json
$bandKeyGuard=Get-Content -LiteralPath (Join-Path $bandKeyInput 'setup-host-check.json') -Raw | ConvertFrom-Json
if($bandKeyTests.failed -ne 0 -or $bandKeyTests.passed -lt 208 -or $bandKeyTests.registered -or $bandKeyTests.windowsAuthenticationSubmitted -or $bandKeyGuard.vmGuardAccepted -or $bandKeyGuard.mutationPerformed -or $bandKeyGuard.providerRegistered){throw 'Native checks or host refusal missing'}
$bandKeyOutput=Join-Path $bandKeyRoot ('virtualmachine\BandKey-Account-Backend-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $bandKeyOutput | Out-Null
foreach($bandKeyName in @('BandKey.CredentialProvider.dll','BandKey.VmSetup.exe','BandKey.VmConfirmation.exe')){
    Copy-Item -LiteralPath (Join-Path $bandKeyInput $bandKeyName) -Destination $bandKeyOutput
}
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'docs\vm-account-backend-20261025.md') -Destination (Join-Path $bandKeyOutput 'READ-ME-FIRST.txt')
$bandKeyDllHash=(Get-FileHash -LiteralPath (Join-Path $bandKeyOutput 'BandKey.CredentialProvider.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
if(![Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $bandKeyOutput 'BandKey.VmSetup.exe'))).Contains($bandKeyDllHash)){throw 'Installer DLL hash mismatch'}
$bandKeyFiles=@(Get-ChildItem -LiteralPath $bandKeyOutput -File | Sort-Object Name | ForEach-Object {
    [ordered]@{name=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
[ordered]@{schema='bandkey.vm-account-backend.v1';supportedGuest='VMware Windows 11 build 26300';nativeChecksPassed=$bandKeyTests.passed;guestTested=$false;realMicrosoftAccountTested=$false;hostRegistered=$false;phoneKeyIncluded=$false;files=$bandKeyFiles} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bandKeyOutput 'manifest.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'new-lab-data-iso.ps1') -SourceDirectory $bandKeyOutput
$bandKeyVerified=Join-Path $bandKeyRoot ('artifacts\account-iso-verification-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $bandKeyVerified | Out-Null
$bandKeyEntries=@(& tar.exe -tf ($bandKeyOutput+'.iso'))
if($LASTEXITCODE -ne 0 -or ($bandKeyEntries | Where-Object {[IO.Path]::IsPathRooted($_) -or $_ -match '(^|[\\/])\.\.([\\/]|$)|:'})){throw 'Unsafe ISO path'}
& tar.exe -xf ($bandKeyOutput+'.iso') -C $bandKeyVerified
if($LASTEXITCODE -ne 0 -or @(Get-ChildItem -LiteralPath $bandKeyVerified -Recurse -File).Count -ne $bandKeyFiles.Count+1){throw 'ISO readback mismatch'}
foreach($bandKeyFile in $bandKeyFiles){
    $bandKeyItem=Get-Item -LiteralPath (Join-Path $bandKeyVerified $bandKeyFile.name)
    if($bandKeyItem.Length -ne $bandKeyFile.bytes -or (Get-FileHash -LiteralPath $bandKeyItem.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $bandKeyFile.sha256){throw 'ISO payload mismatch'}
}
[ordered]@{iso=($bandKeyOutput+'.iso');sha256=(Get-FileHash -LiteralPath ($bandKeyOutput+'.iso') -Algorithm SHA256).Hash.ToLowerInvariant();payloadHashesVerified=$bandKeyFiles.Count;guestTested=$false;realMicrosoftAccountTested=$false;hostRegistered=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\latest-account-backend.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\latest-account-backend.json') -Raw
