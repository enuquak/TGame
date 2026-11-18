#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [ValidateRange(0, 2147483647)][int]$Slot = 0,
    [hashtable]$Context
)

$ErrorActionPreference = 'Stop'
$type = [IO.Path]::GetFileNameWithoutExtension($PSCommandPath).Substring('Start'.Length)
& (Join-Path $PSScriptRoot 'Manage-Servers.ps1') -Action Start -ProcessType $type -NoBuild:$NoBuild -Slot $Slot -Context $Context
