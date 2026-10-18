param([switch]$CreateIso)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'test-credential-provider.ps1')
$bandKeyInput=Join-Path $bandKeyRoot 'artifacts\credential-provider-lab'
$bandKeyOutput=Join-Path $bandKeyRoot ('virtualmachine\interface-lab-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $bandKeyOutput | Out-Null
foreach($bandKeyName in @('CredentialProvider.Lab.exe','CredentialProvider.Lab.Gui.exe','SampleV2CredentialProvider.dll')){
    Copy-Item -LiteralPath (Join-Path $bandKeyInput $bandKeyName) -Destination $bandKeyOutput
}
$bandKeyVswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$bandKeyVs=& $bandKeyVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$bandKeyRedist=Get-ChildItem -LiteralPath (Join-Path $bandKeyVs 'VC\Redist\MSVC') -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if(!$bandKeyRedist){throw 'Visual C++ redistributable directory not found.'}
foreach($bandKeyName in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')){
    $bandKeyRuntime=Join-Path $bandKeyRedist.FullName ('x64\Microsoft.VC143.CRT\'+$bandKeyName)
    $bandKeySignature=Get-AuthenticodeSignature -LiteralPath $bandKeyRuntime
    if($bandKeySignature.Status -ne 'Valid' -or $bandKeySignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation'){
        throw "Visual C++ runtime signature validation failed: $bandKeyName"
    }
    Copy-Item -LiteralPath $bandKeyRuntime -Destination $bandKeyOutput
}
@'
BandKey REFERENCE INTERFACE LAB - not a Windows login provider installation

Double-click CredentialProvider.Lab.Gui.exe inside the Windows test VM.
No administrator rights or VMware Tools are needed. No registration is performed.
It loads the Microsoft reference DLL in an ordinary process, using a synthetic
user and random in-memory password. It does not submit Windows authentication.
The dialog shows interface checks, actual OS build and JSON report location.
The report is written to %LOCALAPPDATA%\BandKeyLab, not to this read-only medium.

Expected result: 42/42 checks. This does NOT prove Windows login or unlock.
Keep the baseline VM snapshot before any future login-component experiment.
Do not run regsvr32 or register this reference provider on the host or guest.
'@ | Set-Content -LiteralPath (Join-Path $bandKeyOutput 'READ-ME-FIRST.txt') -Encoding UTF8
$bandKeyFiles=@(Get-ChildItem -LiteralPath $bandKeyOutput -File | Sort-Object Name | ForEach-Object {
    [ordered]@{name=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
[ordered]@{
    referenceProviderOnly=$true
    registerProvider=$false
    windowsAuthenticationSubmitted=$false
    windowsUnlockTested=$false
    sourceRevision='434f6002bdf9cf9829406c3ff2b33387982d6168'
    hostInterfaceChecks=42
    hostFailurePathChecks=2
    guestTested=$false
    files=$bandKeyFiles
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $bandKeyOutput 'manifest.json') -Encoding UTF8
Write-Output "Lab folder: $bandKeyOutput"
if(!$CreateIso){return}
& (Join-Path $PSScriptRoot 'new-lab-data-iso.ps1') -SourceDirectory $bandKeyOutput
