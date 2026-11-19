#requires -Version 7.0
[CmdletBinding()]
param([ValidateRange(1, 300)][int]$TimeoutSeconds = 10)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Manage-Servers.ps1') -Action Kill -TimeoutSeconds $TimeoutSeconds
