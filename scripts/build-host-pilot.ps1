$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
$bandKeyOutput=Join-Path $bandKeyRoot 'artifacts/host-pilot'
$bandKeyIntermediate=Join-Path $bandKeyRoot '.tooling/host-pilot'
New-Item -ItemType Directory -Force -Path $bandKeyOutput,$bandKeyIntermediate | Out-Null
$bandKeyMachine=(Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Cryptography' -Name MachineGuid).MachineGuid
$bandKeyBuild=(Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name CurrentBuildNumber).CurrentBuildNumber
if([int]$bandKeyBuild -lt 22000){throw 'Windows 11 required'}
$bandKeyBinding=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::Unicode.GetBytes($bandKeyMachine.ToLowerInvariant()))).ToLowerInvariant()
('inline constexpr wchar_t BandKeyHostExpectedMachine[] = L"'+$bandKeyBinding+'";'+[Environment]::NewLine+'inline constexpr wchar_t BandKeyHostExpectedBuild[] = L"'+$bandKeyBuild+'";') | Set-Content -LiteralPath (Join-Path $bandKeyOutput 'HostBinding.h') -Encoding ascii
$bandKeyVswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$bandKeyVs=& $bandKeyVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$bandKeyVs -and (Test-Path -LiteralPath (Join-Path $bandKeyRoot '.tooling/vs-buildtools/MSBuild/Current/Bin/MSBuild.exe'))){$bandKeyVs=Join-Path $bandKeyRoot '.tooling/vs-buildtools'}
if(!$bandKeyVs){throw 'C++ build tools required'}
$bandKeyMsbuild=Join-Path $bandKeyVs 'MSBuild/Current/Bin/MSBuild.exe'
function Invoke-BandKeyHostBuild([string]$Project,[string]$Target){
    & $bandKeyMsbuild (Join-Path $bandKeyRoot $Project) /nologo /verbosity:minimal /p:RunCodeAnalysis=true /p:CodeAnalysisTreatWarningsAsErrors=true /p:Configuration=Release /p:Platform=x64 /p:BandKeyHostPilot=true ("/p:BandKeyBindingDirectory=$bandKeyOutput") ("/p:OutDir=$bandKeyOutput\") ("/p:IntDir=$bandKeyIntermediate\$Target\") ("/p:TargetName=$Target")
    if($LASTEXITCODE){throw ('Host pilot compilation failed: '+$Target)}
}
Invoke-BandKeyHostBuild 'src/BandKey.CredentialProvider/BandKey.CredentialProvider.vcxproj' 'BandKey.HostProvider'
Invoke-BandKeyHostBuild 'tests/BandKey.CredentialProvider.Tests/BandKey.CredentialProvider.Tests.vcxproj' 'BandKey.HostProvider.Tests'
$bandKeyInterop=Join-Path $bandKeyOutput ('interop-'+[guid]::NewGuid().ToString('N'))
& (Join-Path $bandKeyOutput 'BandKey.HostProvider.Tests.exe') (Join-Path $bandKeyOutput 'BandKey.HostProvider.dll') (Join-Path $bandKeyOutput 'native-tests.json') $bandKeyInterop
if($LASTEXITCODE){throw 'Host-profile ordinary-process checks failed'}
$bandKeyHash=(Get-FileHash -LiteralPath (Join-Path $bandKeyOutput 'BandKey.HostProvider.dll')).Hash.ToLowerInvariant()
('inline constexpr char BandKeyExpectedProviderHash[] = "'+$bandKeyHash+'";')|Set-Content -LiteralPath (Join-Path $bandKeyOutput 'ProviderHash.h') -Encoding ascii
Invoke-BandKeyHostBuild 'src/BandKey.CredentialProvider/BandKey.VmSetup.vcxproj' 'BandKey.HostSetup'
$bandKeyCheckPath=Join-Path $bandKeyOutput 'preflight.json'
$bandKeyCheck=Start-Process -FilePath (Join-Path $bandKeyOutput 'BandKey.HostSetup.exe') -ArgumentList ('--check "'+$bandKeyCheckPath+'"') -WindowStyle Hidden -Wait -PassThru
if($bandKeyCheck.ExitCode){throw 'Host pilot read-only check failed'}
$bandKeyCheckReport=Get-Content -LiteralPath $bandKeyCheckPath -Raw|ConvertFrom-Json
if(!$bandKeyCheckReport.environmentAccepted -or $bandKeyCheckReport.profile -ne 'host-pilot' -or $bandKeyCheckReport.mutationPerformed){throw 'Host profile binding not verified'}
[ordered]@{schema='bandkey.host-pilot-build.v1';profile='host-pilot';build=[int]$bandKeyBuild;nativeTests=(Get-Content -LiteralPath (Join-Path $bandKeyOutput 'native-tests.json') -Raw|ConvertFrom-Json);dllSha256=$bandKeyHash;setupSha256=(Get-FileHash -LiteralPath (Join-Path $bandKeyOutput 'BandKey.HostSetup.exe')).Hash.ToLowerInvariant();registeredByBuild=$false}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $bandKeyOutput 'build-result.json') -Encoding utf8
Write-Output 'Host profile built and checked; no registration or account enrollment performed.'
