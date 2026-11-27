#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('StartAll', 'Start', 'Stop', 'Kill')][string]$Action,
    [string]$ProcessType,
    [switch]$NoBuild,
    [ValidateRange(0, 2147483647)][int]$Slot = 0,
    [ValidateRange(1, 300)][int]$TimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Server-ProcessFunctions.ps1')
$root = (Resolve-Path "$PSScriptRoot/../../..").Path
$context = $null
Push-Location -LiteralPath $root
try
{
    if ($Action -eq 'Start')
    {
        $type = [TGame.ServerProcess]::ParseType($ProcessType)
    }
    $context = Open-ServerRun $root ($Action -in @('Start', 'StartAll')) $NoBuild.IsPresent

    switch ($Action)
    {
        'StartAll'
        {
            foreach ($process in $context.Group.Processes)
            {
                Start-ServerProcess $context $process.Type $process.ChannelId
            }
            Write-Output "启动计划已执行。就绪请查看 PROCESS_READY 日志；运行目录：$($context.RunDirectory)"
        }
        'Start' { Start-ServerProcess $context $type $Slot }
        'Stop' { Stop-ServerProcesses $context $false $TimeoutSeconds }
        'Kill' { Stop-ServerProcesses $context $true $TimeoutSeconds }
    }
}
finally
{
    if ($context)
    {
        $context.Lock.Dispose()
    }
    Pop-Location
}
