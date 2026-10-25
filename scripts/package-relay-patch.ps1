param([Parameter(Mandatory=$true)][string]$BaselineDirectory)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
$bandKeyPublished=Join-Path $bandKeyRoot 'artifacts/BandKey-win-x64'
$bandKeyBaseline=(Resolve-Path -LiteralPath $BaselineDirectory).Path
foreach($bandKeyRuntime in @('BandKey.runtimeconfig.json','coreclr.dll','System.Private.CoreLib.dll','Microsoft.AspNetCore.Server.Kestrel.Core.dll')){
    if((Get-FileHash -LiteralPath (Join-Path $bandKeyPublished $bandKeyRuntime)).Hash -ne
       (Get-FileHash -LiteralPath (Join-Path $bandKeyBaseline $bandKeyRuntime)).Hash){throw ('Runtime differs; use a full relay package: '+$bandKeyRuntime)}
}
foreach($bandKeyCheck in @(@{name='core-tests.json';count=151},@{name='windows-tests.json';count=88})){
    $bandKeyReport=Get-Content -LiteralPath (Join-Path $bandKeyRoot ('artifacts/'+$bandKeyCheck.name)) -Raw|ConvertFrom-Json
    if($bandKeyReport.failed -ne 0 -or $bandKeyReport.passed -lt $bandKeyCheck.count){throw 'Required software checks missing'}
}
$bandKeyOutput=Join-Path $bandKeyRoot ('virtualmachine/BandKey-Relay-Patch-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
$bandKeyPatch=Join-Path $bandKeyOutput 'Patch'
New-Item -ItemType Directory -Path $bandKeyPatch | Out-Null
foreach($bandKeyName in @('BandKey.exe','BandKey.dll','BandKey.Core.dll','BandKey.deps.json','BandKey.runtimeconfig.json')){
    Copy-Item -LiteralPath (Join-Path $bandKeyPublished $bandKeyName) -Destination $bandKeyPatch
}
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'docs/fixed-phone-fast-0.8.0.md') -Destination (Join-Path $bandKeyOutput 'READ-ME-FIRST.txt')
$bandKeyFiles=@(Get-ChildItem -LiteralPath $bandKeyOutput -Recurse -File|Sort-Object FullName|ForEach-Object{
    [ordered]@{name=([IO.Path]::GetRelativePath($bandKeyOutput,$_.FullName)-replace '\\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
})
[ordered]@{schema='bandkey.relay-patch.v1';version='0.8.0';baseline='complete 0.7 relay directory';runtimeHashesMatched=4;loginInstallerIncluded=$false;hostRegistered=$false;guestTested=$false;files=$bandKeyFiles}|
    ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $bandKeyOutput 'manifest.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'new-lab-data-iso.ps1') -SourceDirectory $bandKeyOutput
$bandKeyIso=$bandKeyOutput+'.iso'
$bandKeyVerified=Join-Path $bandKeyRoot ('artifacts/relay-patch-readback-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $bandKeyVerified|Out-Null
$bandKeyEntries=@(& tar.exe -tf $bandKeyIso)
if($LASTEXITCODE -ne 0 -or ($bandKeyEntries|Where-Object {[IO.Path]::IsPathRooted($_) -or $_ -match '(^|[\\/])\.\.([\\/]|$)|:'})){throw 'Unsafe ISO entry'}
& tar.exe -xf $bandKeyIso -C $bandKeyVerified
if($LASTEXITCODE -ne 0 -or @(Get-ChildItem -LiteralPath $bandKeyVerified -Recurse -File).Count -ne $bandKeyFiles.Count+1){throw 'ISO file count mismatch'}
foreach($bandKeyFile in $bandKeyFiles){
    $bandKeyPath=Join-Path $bandKeyVerified $bandKeyFile.name
    if((Get-Item -LiteralPath $bandKeyPath).Length -ne $bandKeyFile.bytes -or (Get-FileHash -LiteralPath $bandKeyPath).Hash.ToLowerInvariant() -ne $bandKeyFile.sha256){throw 'ISO hash mismatch'}
}
[ordered]@{iso=$bandKeyIso;sha256=(Get-FileHash -LiteralPath $bandKeyIso).Hash.ToLowerInvariant();patchFiles=5;payloadHashesVerified=$bandKeyFiles.Count;patchBytes=(@(Get-ChildItem -LiteralPath $bandKeyPatch -File)|Measure-Object Length -Sum).Sum;guestTested=$false;hostRegistered=$false;loginInstallerIncluded=$false}|
    ConvertTo-Json|Set-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts/latest-relay-patch.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts/latest-relay-patch.json') -Raw