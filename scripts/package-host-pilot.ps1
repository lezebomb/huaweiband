param([string]$PhonePublicKeyPath)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
$bandKeyNative=Join-Path $bandKeyRoot 'artifacts/host-pilot'
$bandKeyBuild=Get-Content -LiteralPath (Join-Path $bandKeyNative 'build-result.json') -Raw|ConvertFrom-Json
if($bandKeyBuild.profile -ne 'host-pilot' -or $bandKeyBuild.nativeTests.failed -ne 0){throw 'Successful host native checks required'}
$bandKeyPackage=Join-Path $bandKeyRoot ('artifacts/BandKey-Host-0.10-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $bandKeyPackage|Out-Null
$env:DOTNET_CLI_HOME=Join-Path $bandKeyRoot '.tooling/dotnet-home'
$env:NUGET_PACKAGES=Join-Path $bandKeyRoot '.tooling/nuget'
& (Join-Path $bandKeyRoot '.tooling/dotnet/dotnet.exe') publish (Join-Path $bandKeyRoot 'src/BandKey.Windows') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false ('-p:TargetPlatformSdkPath='+(Join-Path $bandKeyRoot '.tooling/platform-sdk/')) -p:TargetPlatformDisplayName=Windows -o (Join-Path $bandKeyPackage 'Agent')
if($LASTEXITCODE){throw 'Host agent publish failed'}
New-Item -ItemType Directory -Path (Join-Path $bandKeyPackage 'Native')|Out-Null
foreach($bandKeyFile in @('BandKey.HostProvider.dll','BandKey.HostSetup.exe')){Copy-Item -LiteralPath (Join-Path $bandKeyNative $bandKeyFile) -Destination (Join-Path $bandKeyPackage 'Native')}
if($PhonePublicKeyPath){if((Get-Item -LiteralPath $PhonePublicKeyPath).Length -ne 72){throw 'A native P256 public blob is required'};Copy-Item -LiteralPath $PhonePublicKeyPath -Destination (Join-Path $bandKeyPackage 'Native/Phone-public.bandkeypub')}
foreach($bandKeyScript in @('install-host-agent.ps1','uninstall-host-agent.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $bandKeyScript) -Destination $bandKeyPackage}
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'docs/host-resident-pilot.md') -Destination (Join-Path $bandKeyPackage 'README.md')
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'THIRD_PARTY_NOTICES.md') -Destination $bandKeyPackage
$bandKeyNuget=Join-Path $bandKeyRoot '.tooling/nuget'
foreach($bandKeyRuntime in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64','microsoft.aspnetcore.app.runtime.win-x64')){
    $bandKeyVersion=Get-ChildItem -LiteralPath (Join-Path $bandKeyNuget $bandKeyRuntime) -Directory|Sort-Object{[version]$_.Name} -Descending|Select-Object -First 1
    if(!$bandKeyVersion){throw 'Runtime license information missing'}
    $bandKeyLicenseDirectory=Join-Path $bandKeyPackage ('Agent/licenses/'+$bandKeyRuntime)
    New-Item -ItemType Directory -Force -Path $bandKeyLicenseDirectory|Out-Null
    Get-ChildItem -LiteralPath $bandKeyVersion.FullName -File|Where-Object{$_.Name -match 'license|third.*part.*notice'}|Copy-Item -Destination $bandKeyLicenseDirectory
}
$bandKeyFiles=@(Get-ChildItem -LiteralPath $bandKeyPackage -Recurse -File|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($bandKeyPackage,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})
[ordered]@{schema='bandkey.host-package.v1';version='0.10.0-pilot';build=$bandKeyBuild.build;files=$bandKeyFiles;registeredByPackaging=$false}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $bandKeyPackage 'manifest.json') -Encoding utf8
[ordered]@{path=$bandKeyPackage;manifestSha256=(Get-FileHash -LiteralPath (Join-Path $bandKeyPackage 'manifest.json')).Hash.ToLowerInvariant()}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $bandKeyRoot 'artifacts/host-package-current.json')
Write-Output $bandKeyPackage
