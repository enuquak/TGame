#requires -Version 7.0
[CmdletBinding()]
param([string]$Configuration)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../../..").Path
$checkout = [IO.Directory]::CreateTempSubdirectory('tgame-console-check-').FullName
$null = New-Item -ItemType File -Path "$checkout/.console-check"
$dotnet = "$env:ProgramFiles/dotnet/dotnet.exe"
if (!(Test-Path -LiteralPath $dotnet))
{
    $dotnet = (Get-Command dotnet -CommandType Application).Source
}

Push-Location -LiteralPath $root

try
{
    $files = @(git ls-files --cached --others --exclude-standard -- 'code/server' 'code/shared' 'code/tests/engine' 'code/tests/fixtures' 'code/Directory.Build.props' 'global.json' 'tools/server-console/project.json' 'tools/scripts/powershell/New-ServerRunDirectory.ps1' 'tools/scripts/powershell/Import-ServerTypes.ps1')
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Unable to list server inputs.'
    }

    foreach ($file in $files)
    {
        $target = Join-Path $checkout $file
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Copy((Join-Path $root $file), $target)
    }

    if ($Configuration)
    {
        Copy-Item -LiteralPath $Configuration -Destination (Join-Path $checkout 'code/server/config/processes.json') -Force
    }

    & $dotnet run --project tools/server-console/tests/ServerConsole.Check.csproj -- $checkout
    if ($LASTEXITCODE -ne 0)
    {
        exit $LASTEXITCODE
    }

    & $dotnet run --project "$checkout/code/tests/engine/TGame.EngineTests.csproj"
    exit $LASTEXITCODE
}
finally
{
    Pop-Location
}
