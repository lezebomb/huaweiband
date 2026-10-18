$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
$bandKeyOutput=Join-Path $bandKeyRoot 'artifacts\BandKey-win-x64'
if(!(Test-Path -LiteralPath (Join-Path $bandKeyOutput 'BandKey.exe'))){throw 'Publish Windows first.'}
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'docs\portable-readme.md') -Destination (Join-Path $bandKeyOutput 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $bandKeyRoot 'THIRD_PARTY_NOTICES.md') -Destination $bandKeyOutput -Force
$bandKeyPublicDocs=@('development-20261018.md','hardware-checklist.md','portable-readme.md','protocol.md','research.md','verification.md','windows-authentication.md')
$bandKeyPackages=Join-Path $bandKeyRoot '.tooling\nuget'
foreach($bandKeyPackage in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64','microsoft.aspnetcore.app.runtime.win-x64')){
  $bandKeyPackagePath=Join-Path $bandKeyPackages $bandKeyPackage
  $bandKeyVersion=Get-ChildItem -LiteralPath $bandKeyPackagePath -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
  if(!$bandKeyVersion){throw "Missing runtime notices: $bandKeyPackage"}
  $bandKeyLicenses=Join-Path $bandKeyOutput ('licenses\'+$bandKeyPackage)
  New-Item -ItemType Directory -Force -Path $bandKeyLicenses | Out-Null
  Get-ChildItem -LiteralPath $bandKeyVersion.FullName -File | Where-Object { $_.Name -match 'license|third.*part.*notice' } | Copy-Item -Destination $bandKeyLicenses -Force
}
$bandKeyZipPath=Join-Path $bandKeyRoot 'artifacts\BandKey-win-x64.zip'
$bandKeyZipStream=[IO.File]::Open($bandKeyZipPath,[IO.FileMode]::Create)
$bandKeyZip=[IO.Compression.ZipArchive]::new($bandKeyZipStream,[IO.Compression.ZipArchiveMode]::Create)
try {
  Get-ChildItem -LiteralPath $bandKeyOutput -Recurse -File | ForEach-Object {
    $bandKeyRelative=[IO.Path]::GetRelativePath($bandKeyOutput,$_.FullName)
    # Old output directories can contain private docs from earlier local builds.
    # Never inherit their docs tree; add the public source files explicitly below.
    if (($bandKeyRelative -split '[\\/]')[0] -notin @('runtime','docs')) {
      [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($bandKeyZip,$_.FullName,($bandKeyRelative -replace '\\','/'),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
  }
  foreach($bandKeyDocument in $bandKeyPublicDocs){
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($bandKeyZip,(Join-Path $bandKeyRoot ('docs\'+$bandKeyDocument)),('docs/'+$bandKeyDocument),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
  }
} finally { $bandKeyZip.Dispose(); $bandKeyZipStream.Dispose() }
$bandKeyCheck=[IO.Compression.ZipFile]::OpenRead($bandKeyZipPath)
try {
  if(!$bandKeyCheck.GetEntry('BandKey.exe') -or !$bandKeyCheck.GetEntry('coreclr.dll')){throw 'Missing executable/runtime in ZIP'}
  if($bandKeyCheck.Entries | Where-Object { $_.FullName -match '^runtime/' }){throw 'Runtime user data leaked into ZIP'}
  $bandKeyDocEntries=@($bandKeyCheck.Entries | Where-Object { $_.FullName -match '^docs/' })
  if($bandKeyDocEntries.Count -ne $bandKeyPublicDocs.Count -or ($bandKeyDocEntries | Where-Object { $_.FullName.Substring(5) -notin $bandKeyPublicDocs })){throw 'Unexpected documentation in ZIP'}
} finally { $bandKeyCheck.Dispose() }
Get-FileHash -LiteralPath $bandKeyZipPath -Algorithm SHA256
