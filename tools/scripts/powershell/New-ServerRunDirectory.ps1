#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Configuration)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Import-ServerTypes.ps1')
$root = (Resolve-Path "$PSScriptRoot/../../..").Path
$directory = [TGame.ServerConfiguration]::new([IO.Path]::GetFullPath($Configuration)).ResolveRunDirectory($root)
$null = [IO.Directory]::CreateDirectory($directory)
$directory
