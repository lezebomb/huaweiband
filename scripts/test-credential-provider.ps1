param([string]$UpstreamPath=(Join-Path (Split-Path -Parent $PSScriptRoot) '.research\Windows-classic-samples'))
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
$bandKeyRevision='434f6002bdf9cf9829406c3ff2b33387982d6168'
if(!(Test-Path -LiteralPath $UpstreamPath)){throw 'Clone microsoft/Windows-classic-samples at the documented pinned revision, or pass -UpstreamPath.'}
$UpstreamPath=(Resolve-Path -LiteralPath $UpstreamPath).Path
$bandKeyHead=git -c "safe.directory=$UpstreamPath" -C $UpstreamPath rev-parse HEAD
if($LASTEXITCODE -ne 0 -or $bandKeyHead -ne $bandKeyRevision){throw 'Reference source revision does not match the reviewed commit.'}
git -c "safe.directory=$UpstreamPath" -C $UpstreamPath diff --quiet HEAD -- Samples/CredentialProvider/cpp
if($LASTEXITCODE -ne 0){throw 'Reference provider source has local modifications.'}
$bandKeyVswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$bandKeyVisualStudio=& $bandKeyVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$bandKeyVisualStudio){throw 'Visual Studio C++ build tools and Windows SDK are required.'}
$bandKeyMsbuild=Join-Path $bandKeyVisualStudio 'MSBuild\Current\Bin\MSBuild.exe'
$bandKeyOutput=Join-Path $bandKeyRoot 'artifacts\credential-provider-lab'
$bandKeyIntermediates=Join-Path $bandKeyRoot '.tooling\credential-provider-lab'
New-Item -ItemType Directory -Force -Path $bandKeyOutput,$bandKeyIntermediates | Out-Null
$bandKeyCommon=@('/nologo','/verbosity:minimal','/p:Configuration=Release','/p:Platform=x64','/p:PlatformToolset=v143','/p:WindowsTargetPlatformVersion=10.0.26100.0')
$bandKeyReference=Join-Path $UpstreamPath 'Samples\CredentialProvider\cpp\SampleV2CredentialProvider.vcxproj'
& $bandKeyMsbuild $bandKeyReference @bandKeyCommon "/p:OutDir=$bandKeyOutput\" "/p:IntDir=$bandKeyIntermediates\reference\"
if($LASTEXITCODE -ne 0){throw 'Reference provider compilation failed.'}
& $bandKeyMsbuild (Join-Path $bandKeyRoot 'tests\CredentialProvider.Lab\CredentialProvider.Lab.vcxproj') @bandKeyCommon "/p:OutDir=$bandKeyOutput\" "/p:IntDir=$bandKeyIntermediates\host\"
if($LASTEXITCODE -ne 0){throw 'Unregistered test host compilation failed.'}
& $bandKeyMsbuild (Join-Path $bandKeyRoot 'tests\CredentialProvider.Lab\CredentialProvider.Lab.vcxproj') @bandKeyCommon '/p:LabGui=true' "/p:OutDir=$bandKeyOutput\" "/p:IntDir=$bandKeyIntermediates\gui\"
if($LASTEXITCODE -ne 0){throw 'Graphical test host compilation failed.'}
# Direct COM factory loading only. No regsvr32, registration scripts, LogonUI or LsaLogonUser.
& (Join-Path $bandKeyOutput 'CredentialProvider.Lab.exe') (Join-Path $bandKeyOutput 'SampleV2CredentialProvider.dll') (Join-Path $bandKeyOutput 'tests.json')
if($LASTEXITCODE -ne 0){throw 'Credential Provider in-process tests failed; inspect the local JSON report.'}
$bandKeyGui=Start-Process -FilePath (Join-Path $bandKeyOutput 'CredentialProvider.Lab.Gui.exe') -ArgumentList ('"'+(Join-Path $bandKeyOutput 'SampleV2CredentialProvider.dll')+'" "'+(Join-Path $bandKeyOutput 'gui-tests.json')+'" --quiet') -WindowStyle Hidden -Wait -PassThru
if($bandKeyGui.ExitCode -ne 0){throw 'Graphical lab entry point failed.'}
$bandKeyGuiReport=Get-Content -LiteralPath (Join-Path $bandKeyOutput 'gui-tests.json') -Raw | ConvertFrom-Json
if($bandKeyGuiReport.failed -ne 0 -or $bandKeyGuiReport.registered -or $bandKeyGuiReport.windowsAuthenticationSubmitted){throw 'Unsafe or failing graphical lab report.'}
# Missing dependencies and an unwritable report must fail, rather than claim success.
$bandKeyNegativePath=Join-Path $bandKeyOutput 'missing-dll-tests.json'
$bandKeyNegative=Start-Process -FilePath (Join-Path $bandKeyOutput 'CredentialProvider.Lab.Gui.exe') -ArgumentList ('"'+(Join-Path $bandKeyOutput 'does-not-exist.dll')+'" "'+$bandKeyNegativePath+'" --quiet') -WindowStyle Hidden -Wait -PassThru
$bandKeyNegativeReport=Get-Content -LiteralPath $bandKeyNegativePath -Raw | ConvertFrom-Json
if($bandKeyNegative.ExitCode -eq 0 -or $bandKeyNegativeReport.failed -lt 1 -or $bandKeyNegativeReport.windowsAuthenticationSubmitted){throw 'Missing-DLL failure was not reported safely.'}
$bandKeyUnwritable=Start-Process -FilePath (Join-Path $bandKeyOutput 'CredentialProvider.Lab.Gui.exe') -ArgumentList ('"'+(Join-Path $bandKeyOutput 'SampleV2CredentialProvider.dll')+'" "'+$bandKeyOutput+'" --quiet') -WindowStyle Hidden -Wait -PassThru
if($bandKeyUnwritable.ExitCode -eq 0){throw 'Report write failure incorrectly returned success.'}
Write-Output 'Graphical lab: 42 interface checks and both failure-path checks passed (ordinary host process; no Windows authentication).'
Get-Content -LiteralPath (Join-Path $bandKeyOutput 'tests.json') -Raw
