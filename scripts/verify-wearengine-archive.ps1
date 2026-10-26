param([Parameter(Mandatory=$true)][string]$Path)
$ErrorActionPreference='Stop'
$bandKeyRoot=(Resolve-Path -LiteralPath $Path).Path
$bandKeyManifestPath=Join-Path $bandKeyRoot 'manifest.json'
$bandKeyManifest=Get-Content -LiteralPath $bandKeyManifestPath -Raw|ConvertFrom-Json
if($bandKeyManifest.schema -ne 'bandkey.wearengine-offline-archive.v1' -or !$bandKeyManifest.files -or $bandKeyManifest.files.Count -gt 2000){throw 'Invalid archive manifest'}
$bandKeyNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($bandKeyFile in $bandKeyManifest.files){
    $bandKeyName=[string]$bandKeyFile.name
    if([IO.Path]::IsPathRooted($bandKeyName) -or $bandKeyName -match '(^|[\\/])\.\.([\\/]|$)|:' -or !$bandKeyNames.Add($bandKeyName)){throw 'Unsafe or duplicate archive member'}
    $bandKeyTarget=[IO.Path]::GetFullPath((Join-Path $bandKeyRoot $bandKeyName))
    if(!$bandKeyTarget.StartsWith($bandKeyRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Archive member escaped its root'}
    $bandKeyInfo=Get-Item -LiteralPath $bandKeyTarget -Force
    if($bandKeyInfo.PSIsContainer -or ($bandKeyInfo.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Archive member is not an ordinary file'}
    $bandKeyParent=$bandKeyInfo.Directory
    while($bandKeyParent -and $bandKeyParent.FullName.StartsWith($bandKeyRoot,[StringComparison]::OrdinalIgnoreCase)){
        if($bandKeyParent.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Archive directory is a reparse point'}
        if($bandKeyParent.FullName -eq $bandKeyRoot){break}
        $bandKeyParent=$bandKeyParent.Parent
    }
    if($bandKeyFile.sha256 -cnotmatch '^[a-f0-9]{64}$' -or $bandKeyInfo.Length -ne $bandKeyFile.bytes -or
       (Get-FileHash -LiteralPath $bandKeyTarget -Algorithm SHA256).Hash.ToLowerInvariant() -cne $bandKeyFile.sha256){throw ('Archive hash mismatch: '+$bandKeyName)}
}
$bandKeyActual=@(Get-ChildItem -LiteralPath $bandKeyRoot -Recurse -File -Force)
if($bandKeyActual.Count -ne $bandKeyNames.Count+1){throw 'Archive has missing or unexpected files'}
[pscustomobject]@{valid=$true;filesVerified=$bandKeyNames.Count;sourceTree=$bandKeyManifest.sourceTree}