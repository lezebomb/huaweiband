param([ValidatePattern('^[a-z0-9-]{0,64}$')][string]$BuildLabel='')
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
$bandKeyVswhere=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
$bandKeyVs=& $bandKeyVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$bandKeyVs){throw 'Visual Studio C++ tools are required.'}
$bandKeySuffix=if($BuildLabel){'-'+$BuildLabel}else{''}
$bandKeyOutput=Join-Path $bandKeyRoot ('artifacts/host-account-probe'+$bandKeySuffix)
$bandKeyIntermediate=Join-Path $bandKeyRoot '.tooling/host-account-probe'
New-Item -ItemType Directory -Force -Path $bandKeyOutput,$bandKeyIntermediate|Out-Null
& (Join-Path $bandKeyVs 'MSBuild/Current/Bin/MSBuild.exe') (Join-Path $bandKeyRoot 'src/BandKey.CredentialProvider/BandKey.HostCredentialProbe.vcxproj') /nologo /verbosity:minimal /p:RunCodeAnalysis=true /p:CodeAnalysisTreatWarningsAsErrors=true /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$bandKeyOutput\" "/p:IntDir=$bandKeyIntermediate\"
if($LASTEXITCODE -ne 0){throw 'Host probe build or static analysis failed.'}
$bandKeyExe=Join-Path $bandKeyOutput 'BandKey.HostCredentialProbe.exe'
foreach($bandKeyMode in @(@{arg='--self-test';file='tests.json'},@{arg='--check';file='read-only-check.json'})){
    $bandKeyPath=Join-Path $bandKeyOutput $bandKeyMode.file
    $bandKeyRun=Start-Process -FilePath $bandKeyExe -ArgumentList @($bandKeyMode.arg,('"'+$bandKeyPath+'"')) -WindowStyle Hidden -Wait -PassThru
    if($bandKeyRun.ExitCode -ne 0){throw ('Host probe check failed: '+$bandKeyMode.arg)}
    Get-Content -LiteralPath $bandKeyPath -Raw
}
$bandKeyCheck=Get-Content -LiteralPath (Join-Path $bandKeyOutput 'read-only-check.json') -Raw|ConvertFrom-Json
if($bandKeyCheck.authenticationAttempted -or $bandKeyCheck.credentialPromptRequested -or $bandKeyCheck.providerRegistrationPerformed -or $bandKeyCheck.credentialPersisted -or $bandKeyCheck.installReady){throw 'Read-only mode performed an unexpected action.'}
Get-FileHash -LiteralPath $bandKeyExe -Algorithm SHA256