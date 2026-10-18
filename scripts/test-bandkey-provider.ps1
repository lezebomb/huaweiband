$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
$bandKeyVswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$bandKeyVs=& $bandKeyVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$bandKeyVs){throw 'Visual Studio C++ tools are required.'}
$bandKeyMsbuild=Join-Path $bandKeyVs 'MSBuild\Current\Bin\MSBuild.exe'
$bandKeyOutput=Join-Path $bandKeyRoot 'artifacts\bandkey-provider-lab'
$bandKeyIntermediates=Join-Path $bandKeyRoot '.tooling\bandkey-provider-lab'
New-Item -ItemType Directory -Force -Path $bandKeyOutput,$bandKeyIntermediates | Out-Null
foreach($bandKeyProject in @('src\BandKey.CredentialProvider\BandKey.CredentialProvider.vcxproj','tests\BandKey.CredentialProvider.Tests\BandKey.CredentialProvider.Tests.vcxproj')){
    $bandKeyName=[IO.Path]::GetFileNameWithoutExtension($bandKeyProject)
    & $bandKeyMsbuild (Join-Path $bandKeyRoot $bandKeyProject) /nologo /verbosity:minimal /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$bandKeyOutput\" "/p:IntDir=$bandKeyIntermediates\$bandKeyName\"
    if($LASTEXITCODE -ne 0){throw "Compilation failed: $bandKeyName"}
}
& (Join-Path $bandKeyOutput 'BandKey.CredentialProvider.Tests.exe') (Join-Path $bandKeyOutput 'BandKey.CredentialProvider.dll') (Join-Path $bandKeyOutput 'tests.json')
if($LASTEXITCODE -ne 0){throw 'BandKey provider ordinary-process tests failed.'}
$bandKeyDllHash=(Get-FileHash -LiteralPath (Join-Path $bandKeyOutput 'BandKey.CredentialProvider.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
('inline constexpr char BandKeyExpectedProviderHash[] = "'+$bandKeyDllHash+'";') | Set-Content -LiteralPath (Join-Path $bandKeyOutput 'ProviderHash.h') -Encoding ascii
& $bandKeyMsbuild (Join-Path $bandKeyRoot 'src\BandKey.CredentialProvider\BandKey.VmSetup.vcxproj') /nologo /verbosity:minimal /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$bandKeyOutput\" "/p:IntDir=$bandKeyIntermediates\setup\"
if($LASTEXITCODE -ne 0){throw 'VM setup compilation failed.'}
$bandKeyCheckPath=Join-Path $bandKeyOutput 'setup-host-check.json'
$bandKeyCheck=Start-Process -FilePath (Join-Path $bandKeyOutput 'BandKey.VmSetup.exe') -ArgumentList ('--check "'+$bandKeyCheckPath+'"') -WindowStyle Hidden -Wait -PassThru
if($bandKeyCheck.ExitCode -ne 0){throw 'Read-only setup check failed.'}
$bandKeyCheckReport=Get-Content -LiteralPath $bandKeyCheckPath -Raw | ConvertFrom-Json
if($bandKeyCheckReport.vmGuardAccepted -or $bandKeyCheckReport.mutationPerformed -or $bandKeyCheckReport.providerRegistered){throw 'Host guard check did not demonstrate an untouched, unsupported host.'}
Write-Output 'Setup read-only check: physical host refused; no registration or mutation.'
