#requires -Version 7.0
[CmdletBinding()]
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Manage-Servers.ps1') -Action StartAll -NoBuild:$NoBuild
