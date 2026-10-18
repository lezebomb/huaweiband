param([Parameter(Mandatory=$true)][string]$Executable, [Parameter(Mandatory=$true)][string]$DataDirectory, [int]$MaxRestarts=0)
$ErrorActionPreference='Stop'
$bandKeyExecutable=(Resolve-Path -LiteralPath $Executable).Path
$bandKeyData=[IO.Path]::GetFullPath($DataDirectory)
New-Item -ItemType Directory -Force -Path $bandKeyData | Out-Null
$bandKeyRestarts=0
while($true){
    $bandKeyStarted=Get-Date
    $bandKeyProcess=Start-Process -FilePath $bandKeyExecutable -ArgumentList @('--tray','--data',('"'+$bandKeyData+'"')) -WindowStyle Hidden -PassThru
    $bandKeyProcess.WaitForExit()
    if($bandKeyProcess.ExitCode -eq 0){break} # Explicit user Exit is respected.
    $bandKeyRestarts++
    Add-Content -LiteralPath (Join-Path $bandKeyData 'watchdog.log') -Value ((Get-Date -Format o)+' process-exit='+$bandKeyProcess.ExitCode+' restart='+$bandKeyRestarts)
    if($MaxRestarts -gt 0 -and $bandKeyRestarts -ge $MaxRestarts){exit 1}
    if(((Get-Date)-$bandKeyStarted).TotalSeconds -gt 300){$bandKeyRestarts=0}
    Start-Sleep -Seconds ([Math]::Min(60,[Math]::Pow(2,[Math]::Min(6,$bandKeyRestarts))))
}
