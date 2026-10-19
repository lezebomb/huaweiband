$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $bandKeyRoot
$env:DOTNET_ROOT=Join-Path $bandKeyRoot '.tooling\dotnet'
$env:DOTNET_CLI_HOME=Join-Path $bandKeyRoot '.tooling\dotnet-home'
$env:NUGET_PACKAGES=Join-Path $bandKeyRoot '.tooling\nuget'
$bandKeyPublished=Join-Path $bandKeyRoot 'artifacts\BandKey-session-lab'
& (Join-Path $env:DOTNET_ROOT 'dotnet.exe') publish src/BandKey.SessionLab -c Release -r win-x64 --self-contained true -o $bandKeyPublished --configfile NuGet.Config ('-p:TargetPlatformSdkPath='+ (Join-Path $bandKeyRoot '.tooling\platform-sdk\')) -p:TargetPlatformDisplayName=Windows
if($LASTEXITCODE){throw 'Session lab publish failed'}
$bandKeyName='BandKey-VM-Session-Lab-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$bandKeyStage=[IO.Path]::GetFullPath((Join-Path $bandKeyRoot ('virtualmachine\'+$bandKeyName)))
if(!$bandKeyStage.StartsWith((Join-Path $bandKeyRoot 'virtualmachine\'),[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $bandKeyStage)){throw 'Unsafe or existing package stage'}
$bandKeyPayload=Join-Path $bandKeyStage 'SessionLab'
New-Item -ItemType Directory -Path $bandKeyPayload | Out-Null
foreach($bandKeyFile in Get-ChildItem -LiteralPath $bandKeyPublished -Recurse -File){
 $bandKeyRelative=[IO.Path]::GetRelativePath($bandKeyPublished,$bandKeyFile.FullName)
 if($bandKeyRelative -match '(?i)(^|[\\/])runtime[\\/]|\.(pdb|dpapi|pfx|p12|key|jks|keystore)$|^BandKey\.(exe|deps\.json|runtimeconfig\.json)$'){continue}
 $bandKeyDestination=Join-Path $bandKeyPayload $bandKeyRelative
 New-Item -ItemType Directory -Force -Path (Split-Path -Parent $bandKeyDestination) | Out-Null
 Copy-Item -LiteralPath $bandKeyFile.FullName -Destination $bandKeyDestination
}
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'THIRD_PARTY_NOTICES.md') -Destination $bandKeyStage
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'artifacts\BandKey-win-x64\licenses') -Destination $bandKeyStage -Recurse
$bandKeyGuide=Get-Content docs/vm-provider-test.md -Raw
$bandKeyMarker='## 独立会话与自动锁屏工具（2026-10-19）'
$bandKeyGuideStart=$bandKeyGuide.IndexOf($bandKeyMarker,[StringComparison]::Ordinal)
if($bandKeyGuideStart -lt 0){throw 'Session-only guide missing'}
[IO.File]::WriteAllText((Join-Path $bandKeyStage 'README.txt'),$bandKeyGuide.Substring($bandKeyGuideStart),[Text.UTF8Encoding]::new($true))
[IO.File]::WriteAllText((Join-Path $bandKeyStage 'Start-VM-Session-Lab.cmd'),"@echo off`r`nstart `"`" `"%~dp0SessionLab\BandKey.SessionLab.exe`"`r`n",[Text.Encoding]::ASCII)
$bandKeyGuardOutput=Join-Path $bandKeyRoot 'artifacts\session-lab-host-guard.json'
$bandKeyGuard=Start-Process -FilePath (Join-Path $bandKeyPayload 'BandKey.SessionLab.exe') -ArgumentList @('--check-guest','--output',$bandKeyGuardOutput) -WindowStyle Hidden -PassThru -Wait
$bandKeyGuardReport=Get-Content -LiteralPath $bandKeyGuardOutput -Raw | ConvertFrom-Json
if($bandKeyGuardReport.windowsLockRequested -ne $false -or $bandKeyGuardReport.windowsAuthenticationSubmitted -ne $false -or $bandKeyGuardReport.loginConfigurationModified -ne $false -or $bandKeyGuardReport.credentialsAccessed -ne $false){throw 'Unexpected guest policy side-effect report'}
$bandKeyHostRejected=($bandKeyGuard.ExitCode -eq 2 -and $bandKeyGuardReport.guestAllowed -eq $false)
$bandKeyFiles=@(Get-ChildItem -LiteralPath $bandKeyStage -Recurse -File | ForEach-Object {
 [ordered]@{name=([IO.Path]::GetRelativePath($bandKeyStage,$_.FullName) -replace '\\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
[ordered]@{schema='bandkey-session-lab-package-v1';guestTested=$false;installsProvider=$false;hostLoginConfigurationModified=$false;realRssiHardwareTested=$false;sessionObservationsHardwareVerified=$false;hostGuestRejectionVerified=$bandKeyHostRejected;files=$bandKeyFiles} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bandKeyStage 'manifest.json') -Encoding utf8
$bandKeyZip=Join-Path $bandKeyRoot ('artifacts\'+$bandKeyName+'.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($bandKeyStage,$bandKeyZip,[IO.Compression.CompressionLevel]::Optimal,$false)
& (Join-Path $PSScriptRoot 'new-lab-data-iso.ps1') -SourceDirectory $bandKeyStage
Set-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\latest-session-lab.txt') $bandKeyStage -Encoding utf8
& (Join-Path $PSScriptRoot 'verify-session-lab-package.ps1') -IsoPath ($bandKeyStage+'.iso')
[ordered]@{iso=$bandKeyStage+'.iso';zip=$bandKeyZip;zipSha256=(Get-FileHash -LiteralPath $bandKeyZip).Hash.ToLowerInvariant();hostGuestRejectionVerified=$bandKeyHostRejected;guestTested=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts\latest-session-lab-package.json') -Encoding utf8
