#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Start', 'Stop', 'Status')]
    [string]$Action = 'Start'
)

$ErrorActionPreference = 'Stop'
$requiredHost = '127.0.0.1'
$requiredPort = 1933
$stopTimeoutMilliseconds = 10000
$readyTimeoutSeconds = 3
$readyAttempts = 90
$readyIntervalSeconds = 2
$readyStatus = 'ready'

$serviceDir = Join-Path $env:USERPROFILE '.openviking'
$configFile = Join-Path $serviceDir 'ov.conf'
$stateFile = Join-Path $serviceDir 'server-process.json'
$executable = Join-Path $env:USERPROFILE '.local/bin/openviking-server.exe'
$managed = $null
if (Test-Path -LiteralPath $stateFile)
{
    $saved = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    $candidate = Get-Process -Id $saved.Id -ErrorAction SilentlyContinue
    if ($candidate -and $candidate.Path -eq $executable -and
        $candidate.StartTime.ToUniversalTime().Ticks -eq $saved.StartTicks)
    {
        $managed = $candidate
    }
}

if ($Action -eq 'Stop')
{
    if ($managed)
    {
        # The uv console launcher owns a Python child process; stop only its tree.
        $managed.Kill($true)
        $managed.WaitForExit($stopTimeoutMilliseconds) | Out-Null
    }

    Remove-Item -LiteralPath $stateFile -Force -ErrorAction SilentlyContinue
    Write-Output 'TGame local OpenViking stopped. Persistent data was retained.'
    return
}

if ($Action -eq 'Status' -and -not $managed)
{
    Write-Output 'TGame local OpenViking is not running.'
    return
}

if (-not (Test-Path -LiteralPath $configFile))
{
    throw 'OpenViking config missing. See docs/OpenViking本机配置.md.'
}

$config = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
if ($config.server.host -ne $requiredHost -or $config.server.port -ne $requiredPort)
{
    throw 'This project requires OpenViking on 127.0.0.1:1933. Check ov.conf.'
}

$headers = @{}
if ($config.server.root_api_key)
{
    $headers['X-API-Key'] = $config.server.root_api_key
}

function Test-Ready
{
    try
    {
        $ready = Invoke-RestMethod -Uri "http://${requiredHost}:$requiredPort/ready" -Headers $headers -NoProxy -TimeoutSec $readyTimeoutSeconds
        return $ready.status -eq $readyStatus
    }
    catch
    {
        return $false
    }
}

if ($Action -eq 'Status')
{
    Write-Output "TGame local OpenViking running (PID $($managed.Id)); ready=$(Test-Ready)."
    return
}

if ($managed)
{
    if (Test-Ready)
    {
        Write-Output "TGame local OpenViking already ready (PID $($managed.Id)) at http://127.0.0.1:1933."
        return
    }

    throw 'Managed OpenViking is still initializing or unhealthy. Check ~/.openviking/server.*.log.'
}

if (-not (Test-Path -LiteralPath $executable))
{
    throw 'OpenViking executable missing. See the local setup guide.'
}

if (Get-NetTCPConnection -State Listen -LocalPort $requiredPort -ErrorAction SilentlyContinue)
{
    throw 'Local port 1933 is in use; no new OpenViking process was started.'
}

$previousUtf8 = $env:PYTHONUTF8

try
{
    $env:PYTHONUTF8 = '1'
    $managed = Start-Process -FilePath $executable -ArgumentList @('--config', ('"' + $configFile + '"')) `
        -WorkingDirectory $serviceDir -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $serviceDir 'server.stdout.log') `
        -RedirectStandardError (Join-Path $serviceDir 'server.stderr.log')
}
finally
{
    $env:PYTHONUTF8 = $previousUtf8
}

@{
    Id = $managed.Id
    StartTicks = $managed.StartTime.ToUniversalTime().Ticks
} |
    ConvertTo-Json | Set-Content -LiteralPath $stateFile
Write-Output 'Starting local OpenViking; first startup may take a few minutes...'

for ($attempt = 0; $attempt -lt $readyAttempts; $attempt++)
{
    if ($managed.HasExited)
    {
        throw 'OpenViking exited. Check ~/.openviking/server.*.log.'
    }

    if (Test-Ready)
    {
        Write-Output "TGame local OpenViking ready (PID $($managed.Id)) at http://127.0.0.1:1933."
        return
    }

    Start-Sleep -Seconds $readyIntervalSeconds
}

throw 'OpenViking readiness timed out; process retained for diagnosis. Check logs or run -Action Stop.'
