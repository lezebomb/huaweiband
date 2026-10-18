param([Parameter(Mandatory=$true)][string]$SourceDirectory)
$ErrorActionPreference='Stop'
$bandKeyOutput=(Resolve-Path -LiteralPath $SourceDirectory).Path
if(!(Get-Item -LiteralPath $bandKeyOutput).PSIsContainer){throw 'A source directory is required.'}
# Windows IMAPI builds a data-only image; no recorder, mount, autorun or VM mutation.
# https://learn.microsoft.com/windows/win32/api/imapi2fs/nf-imapi2fs-ifilesystemimage-createresultimage
if(!('BandKeyLab.ImageWriter' -as [type])){
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
namespace BandKeyLab {
    public static class ImageWriter {
        public static long Save(object input, string path) {
            var stream = (IStream)input;
            var count = Marshal.AllocHGlobal(sizeof(int));
            try {
                byte[] buffer = new byte[65536]; long total = 0;
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) {
                    while (true) {
                        Marshal.WriteInt32(count, 0);
                        stream.Read(buffer, buffer.Length, count);
                        int read = Marshal.ReadInt32(count);
                        if (read == 0) break;
                        if (read < 0 || read > buffer.Length) throw new IOException("Invalid image stream length.");
                        file.Write(buffer, 0, read); total += read;
                    }
                }
                return total;
            } finally { Marshal.FreeHGlobal(count); }
        }
    }
}
'@
}
$bandKeyImage=$null; $bandKeyResult=$null; $bandKeyStream=$null
try {
    $bandKeyImage=New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    $bandKeyImage.FileSystemsToCreate=3 # ISO9660 + Joliet for long filenames
    $bandKeyImage.VolumeName='BANDKEY_LAB'
    $bandKeyImage.Root.AddTree($bandKeyOutput,$false)
    $bandKeyResult=$bandKeyImage.CreateResultImage()
    $bandKeyStream=$bandKeyResult.ImageStream
    $bandKeyIso=$bandKeyOutput+'.iso'
    $bandKeyBytes=[BandKeyLab.ImageWriter]::Save($bandKeyStream,$bandKeyIso)
    if($bandKeyBytes -le 0){throw 'Image stream was empty.'}
    [ordered]@{path=$bandKeyIso;bytes=$bandKeyBytes;sha256=(Get-FileHash -LiteralPath $bandKeyIso -Algorithm SHA256).Hash.ToLowerInvariant();guestTested=$false} |
        ConvertTo-Json | Set-Content -LiteralPath ($bandKeyIso+'.json') -Encoding UTF8
    Write-Output "Read-only data ISO: $bandKeyIso"
} finally {
    foreach($bandKeyCom in @($bandKeyStream,$bandKeyResult,$bandKeyImage)){
        if($bandKeyCom -and [Runtime.InteropServices.Marshal]::IsComObject($bandKeyCom)){
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($bandKeyCom)
        }
    }
}
