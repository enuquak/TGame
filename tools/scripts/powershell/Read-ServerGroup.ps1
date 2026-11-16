#requires -Version 7.0
param([Parameter(Mandatory)][string]$Path)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Import-ServerTypes.ps1')

$group = [TGame.ServerGroup]::Load((Resolve-Path -LiteralPath $Path).Path)
$group.Processes
