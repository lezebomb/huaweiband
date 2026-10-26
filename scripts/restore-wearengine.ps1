param([string]$Destination, [string]$ArchiveMaven)
$ErrorActionPreference='Stop'
$bandKeyRoot=Split-Path -Parent $PSScriptRoot
if(!$Destination){$Destination=Join-Path $bandKeyRoot '.tooling/wearengine-maven'}
$bandKeyDestination=[IO.Path]::GetFullPath($Destination)
$bandKeyPins=Get-Content -LiteralPath (Join-Path $bandKeyRoot 'android/wearengine-artifacts.json') -Raw|ConvertFrom-Json
if($bandKeyPins.schema -ne 'bandkey.wearengine-artifact-pins.v1' -or $bandKeyPins.repository -cne 'https://developer.huawei.com/repo/' -or $bandKeyPins.artifacts.Count -ne 4){throw 'Invalid SDK pin catalog'}
$bandKeyAllowed=@('com.huawei.hms:wearengine:5.0.3.304:aar','com.huawei.hms:wearengine:5.0.3.304:pom','com.huawei.hmf:tasks:1.5.2.206:jar','com.huawei.hmf:tasks:1.5.2.206:pom')
$bandKeySeen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($bandKeyPin in $bandKeyPins.artifacts){
    $bandKeyCoordinate=$bandKeyPin.group+':'+$bandKeyPin.module+':'+$bandKeyPin.version+':'+$bandKeyPin.extension
    if($bandKeyAllowed -cnotcontains $bandKeyCoordinate -or !$bandKeySeen.Add($bandKeyCoordinate) -or $bandKeyPin.sha256 -cnotmatch '^[a-f0-9]{64}$'){throw 'Unexpected or duplicate SDK artifact'}
}
function Assert-BandKeyOrdinaryPath([string]$Path){
    $bandKeyCursor=[IO.Path]::GetFullPath($Path)
    while($bandKeyCursor){
        if(Test-Path -LiteralPath $bandKeyCursor){
            $bandKeyInfo=Get-Item -LiteralPath $bandKeyCursor -Force
            if($bandKeyInfo.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'SDK restore does not follow reparse points'}
        }
        $bandKeyCursor=[IO.Path]::GetDirectoryName($bandKeyCursor)
    }
}
Assert-BandKeyOrdinaryPath $bandKeyDestination
if($ArchiveMaven){$ArchiveMaven=(Resolve-Path -LiteralPath $ArchiveMaven).Path;Assert-BandKeyOrdinaryPath $ArchiveMaven}
Add-Type -AssemblyName System.Net.Http
$bandKeyHandler=[Net.Http.HttpClientHandler]::new();$bandKeyHandler.AllowAutoRedirect=$false
$bandKeyHttp=[Net.Http.HttpClient]::new($bandKeyHandler);$bandKeyHttp.Timeout=[TimeSpan]::FromSeconds(45)
$bandKeyRecords=@()
try{
    foreach($bandKeyPin in $bandKeyPins.artifacts){
        $bandKeyName=$bandKeyPin.module+'-'+$bandKeyPin.version+'.'+$bandKeyPin.extension
        $bandKeyRelative=$bandKeyPin.group.Replace('.','/')+'/'+$bandKeyPin.module+'/'+$bandKeyPin.version+'/'+$bandKeyName
        $bandKeyTarget=Join-Path $bandKeyDestination $bandKeyRelative
        Assert-BandKeyOrdinaryPath $bandKeyTarget
        $bandKeyOrigin='existing-verified-file'
        if(Test-Path -LiteralPath $bandKeyTarget){
            if(!(Test-Path -LiteralPath $bandKeyTarget -PathType Leaf) -or (Get-FileHash -LiteralPath $bandKeyTarget).Hash.ToLowerInvariant() -cne $bandKeyPin.sha256){throw ('Existing SDK artifact differs; preserved without overwriting: '+$bandKeyName)}
        }else{
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($bandKeyTarget))|Out-Null
            $bandKeyTemporary=$bandKeyTarget+'.'+[Guid]::NewGuid().ToString('N')+'.part'
            try{
                if($ArchiveMaven){
                    $bandKeySource=Join-Path $ArchiveMaven $bandKeyRelative;Assert-BandKeyOrdinaryPath $bandKeySource
                    if(!(Test-Path -LiteralPath $bandKeySource -PathType Leaf)){throw ('Archived SDK artifact missing: '+$bandKeyName)}
                    [IO.File]::Copy($bandKeySource,$bandKeyTemporary,$false);$bandKeyOrigin='local-archive'
                }else{
                    $bandKeyResponse=$bandKeyHttp.GetAsync(($bandKeyPins.repository+$bandKeyRelative),[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                    try{
                        if([int]$bandKeyResponse.StatusCode -ne 200){throw ('Official SDK download failed (HTTP '+[int]$bandKeyResponse.StatusCode+'): '+$bandKeyName)}
                        $bandKeyInput=$bandKeyResponse.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                        $bandKeyOutput=[IO.File]::Open($bandKeyTemporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                        $bandKeyBudget=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(45))
                        try{
                            $bandKeyBuffer=New-Object byte[] 65536;$bandKeyTotal=0
                            while(($bandKeyRead=$bandKeyInput.ReadAsync($bandKeyBuffer,0,$bandKeyBuffer.Length,$bandKeyBudget.Token).GetAwaiter().GetResult()) -gt 0){
                                $bandKeyTotal+=$bandKeyRead;if($bandKeyTotal -gt 8388608){throw 'SDK download exceeds expected size limit'}
                                $bandKeyOutput.Write($bandKeyBuffer,0,$bandKeyRead)
                            }
                            $bandKeyOutput.Flush($true)
                        }finally{$bandKeyBudget.Dispose();$bandKeyOutput.Dispose();$bandKeyInput.Dispose()}
                    }finally{$bandKeyResponse.Dispose()}
                    $bandKeyOrigin='official-huawei-repository'
                }
                if((Get-FileHash -LiteralPath $bandKeyTemporary).Hash.ToLowerInvariant() -cne $bandKeyPin.sha256){throw ('SDK checksum mismatch: '+$bandKeyName)}
                [IO.File]::Move($bandKeyTemporary,$bandKeyTarget)
            }finally{if(Test-Path -LiteralPath $bandKeyTemporary){Remove-Item -LiteralPath $bandKeyTemporary -Force}}
        }
        $bandKeyRecords+=[pscustomobject]@{artifact=$bandKeyName;sha256=$bandKeyPin.sha256;source=$bandKeyOrigin}
    }
}finally{$bandKeyHttp.Dispose()}
[pscustomobject]@{verified=$true;maven=$bandKeyDestination;artifacts=$bandKeyRecords;serviceAuthorizationRestored=$false}
