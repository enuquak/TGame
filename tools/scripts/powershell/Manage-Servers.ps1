#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('StartAll', 'Start', 'Stop', 'Kill')][string]$Action,
    [string]$ProcessType,
    [switch]$NoBuild,
    [ValidateRange(0, 2147483647)][int]$Slot = 0,
    [ValidateRange(1, 300)][int]$TimeoutSeconds = 10,
    [hashtable]$Context
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Server-ProcessFunctions.ps1')
$root = (Resolve-Path "$PSScriptRoot/../../..").Path
$ownsContext = !$Context
Push-Location -LiteralPath $root
try
{
    if ($Action -eq 'Start')
    {
        $type = [TGame.ServerProcess]::ParseType($ProcessType)
    }
    if ($ownsContext)
    {
        $Context = Open-ServerRun $root ($Action -in @('Start', 'StartAll')) $NoBuild.IsPresent
    }

    switch ($Action)
    {
        'StartAll'
        {
            foreach ($process in $Context.Group.Processes)
            {
                $script = Join-Path $PSScriptRoot ('Start' + $process.Role + '.ps1')
                & $script -Slot $process.ChannelId -Context $Context
            }
            Write-Output "启动计划已执行。就绪请查看 PROCESS_READY 日志；运行目录：$($Context.RunDirectory)"
        }
        'Start' { Start-ServerProcess $Context $type $Slot }
        'Stop' { Stop-ServerProcesses $Context $false $TimeoutSeconds }
        'Kill' { Stop-ServerProcesses $Context $true $TimeoutSeconds }
    }
}
finally
{
    if ($ownsContext -and $Context)
    {
        $Context.Lock.Dispose()
    }
    Pop-Location
}
