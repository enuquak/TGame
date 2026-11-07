#requires -Version 7.0
[CmdletBinding()]
param([switch]$Check)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../../..").Path
$dotnet = "$env:ProgramFiles/dotnet/dotnet.exe"
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -CommandType Application).Source }
Push-Location -LiteralPath $root
try {
    & $dotnet build 'tools/server-console/TGame.ServerConsole.csproj' --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $arguments = @('build/bin/TGame.ServerConsole/debug/TGame.ServerConsole.dll', $root)
    if ($Check) { $arguments += '--check' }
    & $dotnet @arguments
    exit $LASTEXITCODE
} finally { Pop-Location }
