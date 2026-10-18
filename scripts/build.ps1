param([switch]$Publish, [switch]$SkipTests)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $bandKeyRoot
$env:DOTNET_CLI_HOME=Join-Path $bandKeyRoot '.tooling\dotnet-home'
$env:DOTNET_ROOT=Join-Path $bandKeyRoot '.tooling\dotnet'
$env:NUGET_PACKAGES=Join-Path $bandKeyRoot '.tooling\nuget'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
$bandKeyDotnet=Join-Path $bandKeyRoot '.tooling\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $bandKeyDotnet)) { node scripts/bootstrap.mjs; if($LASTEXITCODE){throw 'SDK bootstrap failed'} }
$bandKeyProps=@('-p:TargetPlatformSdkPath='+ (Join-Path $bandKeyRoot '.tooling\platform-sdk\'),'-p:TargetPlatformDisplayName=Windows')
function Invoke-BandKeyDotnet([string[]]$Arguments) {
  & $bandKeyDotnet @Arguments
  if($LASTEXITCODE -ne 0){throw "dotnet failed (exit $LASTEXITCODE)"}
}
New-Item -ItemType Directory -Force -Path artifacts | Out-Null
Invoke-BandKeyDotnet (@('restore','BandKey.slnx','--configfile','NuGet.Config') + $bandKeyProps)
Invoke-BandKeyDotnet (@('build','BandKey.slnx','-c','Release','--no-restore') + $bandKeyProps)
if (!$SkipTests) {
  Invoke-BandKeyDotnet @('run','--project','tests/BandKey.Tests','-c','Release','--no-build','--','artifacts/core-tests.json')
  Invoke-BandKeyDotnet @('run','--project','tests/BandKey.Windows.Tests','-c','Release','--no-build','--','artifacts/windows-tests.json')
}
if ($Publish) {
  Invoke-BandKeyDotnet (@('publish','src/BandKey.Windows','-c','Release','-r','win-x64','--self-contained','true','-o','artifacts/BandKey-win-x64','--configfile','NuGet.Config') + $bandKeyProps)
  & (Join-Path $PSScriptRoot 'package.ps1')
}
