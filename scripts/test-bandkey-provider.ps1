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
    & $bandKeyMsbuild (Join-Path $bandKeyRoot $bandKeyProject) /nologo /verbosity:minimal /p:RunCodeAnalysis=true /p:CodeAnalysisTreatWarningsAsErrors=true /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$bandKeyOutput\" "/p:IntDir=$bandKeyIntermediates\$bandKeyName\"
    if($LASTEXITCODE -ne 0){throw "Compilation failed: $bandKeyName"}
}
$bandKeyInterop=Join-Path $bandKeyOutput ('interop-'+[Guid]::NewGuid().ToString('N'))
& (Join-Path $bandKeyOutput 'BandKey.CredentialProvider.Tests.exe') (Join-Path $bandKeyOutput 'BandKey.CredentialProvider.dll') (Join-Path $bandKeyOutput 'tests.json') $bandKeyInterop
if($LASTEXITCODE -ne 0){throw 'BandKey provider ordinary-process tests failed.'}
$env:DOTNET_CLI_HOME=Join-Path $bandKeyRoot '.tooling\dotnet-home'
$env:DOTNET_ROOT=Join-Path $bandKeyRoot '.tooling\dotnet'
$env:NUGET_PACKAGES=Join-Path $bandKeyRoot '.tooling\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$bandKeyDotnet=Join-Path $env:DOTNET_ROOT 'dotnet.exe'
& $bandKeyDotnet restore (Join-Path $bandKeyRoot 'tests\BandKey.Grant.Interop') --configfile (Join-Path $bandKeyRoot 'NuGet.Config')
if($LASTEXITCODE -ne 0){throw 'Grant interop restore failed.'}
& $bandKeyDotnet run --project (Join-Path $bandKeyRoot 'tests\BandKey.Grant.Interop') -c Release --no-restore -- $bandKeyInterop
if($LASTEXITCODE -ne 0){throw 'CSharp verification of native grant failed.'}
& (Join-Path $bandKeyOutput 'BandKey.CredentialProvider.Tests.exe') --verify-interop $bandKeyInterop
if($LASTEXITCODE -ne 0){throw 'Native verification of CSharp grant failed.'}
Copy-Item -LiteralPath (Join-Path $bandKeyInterop 'csharp-result.json') -Destination (Join-Path $bandKeyOutput 'grant-interop.json') -Force
$bandKeyJavac=if($env:JAVA_HOME){Join-Path $env:JAVA_HOME 'bin\javac.exe'}else{'javac'}
$bandKeyJava=if($env:JAVA_HOME){Join-Path $env:JAVA_HOME 'bin\java.exe'}else{'java'}
$bandKeyJavaClasses=Join-Path $bandKeyInterop 'java-classes'
New-Item -ItemType Directory -Path $bandKeyJavaClasses | Out-Null
& $bandKeyJavac -encoding UTF-8 -d $bandKeyJavaClasses (Join-Path $bandKeyRoot 'android\app\src\main\java\dev\bandkey\bridge\WindowsGrantWire.java') (Join-Path $bandKeyRoot 'tests\interop\WindowsGrantInterop.java')
if($LASTEXITCODE -ne 0){throw 'Android grant codec JDK compilation failed'}
& $bandKeyJava -cp $bandKeyJavaClasses WindowsGrantInterop $bandKeyInterop
if($LASTEXITCODE -ne 0){throw 'Android grant codec interoperability failed'}
& $bandKeyDotnet run --project (Join-Path $bandKeyRoot 'tests\BandKey.Grant.Interop') -c Release --no-build -- --verify-java $bandKeyInterop
if($LASTEXITCODE -ne 0){throw 'CSharp verification of Java native grant failed'}
& (Join-Path $bandKeyOutput 'BandKey.CredentialProvider.Tests.exe') --verify-java $bandKeyInterop
if($LASTEXITCODE -ne 0){throw 'CNG verification of Java native grant failed'}
Copy-Item -LiteralPath (Join-Path $bandKeyInterop 'java-result.json') -Destination (Join-Path $bandKeyOutput 'java-grant-interop.json') -Force
$bandKeyDllHash=(Get-FileHash -LiteralPath (Join-Path $bandKeyOutput 'BandKey.CredentialProvider.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
('inline constexpr char BandKeyExpectedProviderHash[] = "'+$bandKeyDllHash+'";') | Set-Content -LiteralPath (Join-Path $bandKeyOutput 'ProviderHash.h') -Encoding ascii
& $bandKeyMsbuild (Join-Path $bandKeyRoot 'src\BandKey.CredentialProvider\BandKey.VmSetup.vcxproj') /nologo /verbosity:minimal /p:RunCodeAnalysis=true /p:CodeAnalysisTreatWarningsAsErrors=true /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$bandKeyOutput\" "/p:IntDir=$bandKeyIntermediates\setup\"
if($LASTEXITCODE -ne 0){throw 'VM setup compilation failed.'}
$bandKeyCheckPath=Join-Path $bandKeyOutput 'setup-host-check.json'
$bandKeyCheck=Start-Process -FilePath (Join-Path $bandKeyOutput 'BandKey.VmSetup.exe') -ArgumentList ('--check "'+$bandKeyCheckPath+'"') -WindowStyle Hidden -Wait -PassThru
if($bandKeyCheck.ExitCode -ne 0){throw 'Read-only setup check failed.'}
$bandKeyCheckReport=Get-Content -LiteralPath $bandKeyCheckPath -Raw | ConvertFrom-Json
if($bandKeyCheckReport.vmGuardAccepted -or $bandKeyCheckReport.mutationPerformed -or $bandKeyCheckReport.providerRegistered){throw 'Host guard check did not demonstrate an untouched, unsupported host.'}
Write-Output 'Setup read-only check: physical host refused; no registration or mutation.'
& $bandKeyMsbuild (Join-Path $bandKeyRoot 'src\BandKey.CredentialProvider\BandKey.VmConfirmation.vcxproj') /nologo /verbosity:minimal /p:RunCodeAnalysis=true /p:CodeAnalysisTreatWarningsAsErrors=true /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$bandKeyOutput\" "/p:IntDir=$bandKeyIntermediates\confirmation\"
if($LASTEXITCODE -ne 0){throw 'VM confirmation client compilation failed.'}
