#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Start', 'Stop', 'Status')]
    [string]$Action = 'Start'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$stateDir = Join-Path $projectRoot '.local'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
$stateFile = Join-Path $stateDir 'environment-tunnel.json'
$process = $null
if (Test-Path -LiteralPath $stateFile) {
    $saved = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    $candidate = Get-Process -Id $saved.Id -ErrorAction SilentlyContinue
    if ($candidate -and $candidate.ProcessName -eq 'ssh' -and
        $candidate.StartTime.ToUniversalTime().Ticks -eq $saved.StartTicks) {
        $process = $candidate
    }
}
if ($Action -eq 'Stop') {
    if ($process) { Stop-Process -Id $process.Id }
    if (Test-Path -LiteralPath $stateFile) { Remove-Item -LiteralPath $stateFile }
    Write-Output 'TGame SSH tunnel stopped.'
    return
}
if ($Action -eq 'Status') {
    if ($process) { Write-Output "TGame SSH tunnel running (PID $($process.Id))." }
    else { Write-Output 'TGame SSH tunnel is not running.' }
    return
}
if ($process) { Write-Output "TGame SSH tunnel already running (PID $($process.Id))."; return }
$ports = [ordered]@{37017=27017; 16379=6379; 2181=2181; 19000=9000; 19001=9001; 19443=9443; 19090=9090; 13000=3000}
foreach ($port in $ports.Keys) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) {
        throw "Local port $port is in use; no tunnel was started."
    }
}
$config = Join-Path $env:USERPROFILE '.ssh/config'
$arguments = @('-F', ('"' + $config + '"'), '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes',
    '-o', 'ConnectTimeout=15', '-o', 'ExitOnForwardFailure=yes', '-o', 'ServerAliveInterval=30',
    '-o', 'ServerAliveCountMax=3', '-NT')
foreach ($entry in $ports.GetEnumerator()) {
    $arguments += @('-L', "127.0.0.1:$($entry.Key):127.0.0.1:$($entry.Value)")
}
$arguments += 'TGame-server'
$logDir = Join-Path $projectRoot 'logs/tools/environment-tunnel'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$process = Start-Process -FilePath (Get-Command ssh -CommandType Application).Source -ArgumentList $arguments `
    -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $logDir 'ssh.log')
Start-Sleep -Seconds 2
if ($process.HasExited) { throw 'SSH tunnel failed; see logs/tools/environment-tunnel/ssh.log.' }
@{Id=$process.Id; StartTicks=$process.StartTime.ToUniversalTime().Ticks;
    Ports=@($ports.GetEnumerator() | ForEach-Object { "$($_.Key):$($_.Value)" })} |
    ConvertTo-Json | Set-Content -LiteralPath $stateFile
Write-Output "TGame SSH tunnel started (PID $($process.Id))."
