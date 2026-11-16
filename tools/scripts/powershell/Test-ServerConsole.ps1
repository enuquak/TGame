#requires -Version 7.0
[CmdletBinding()]
param([string]$Configuration)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../../..").Path
$checkout = Join-Path $root "build/console-check/$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $checkout
$null = New-Item -ItemType File -Path "$checkout/.console-check"
$dotnet = "$env:ProgramFiles/dotnet/dotnet.exe"
if (!(Test-Path -LiteralPath $dotnet))
{
    $dotnet = (Get-Command dotnet -CommandType Application).Source
}

$addedEnvironment = @()
Push-Location -LiteralPath $root

try
{
    $files = @(git ls-files --cached --others --exclude-standard -- 'code/server' 'code/shared' 'code/tests/singlepoint' 'code/Directory.Build.props' 'global.json' 'tools/server-console/project.json')
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

    foreach ($key in 'MONGO_TEST_PASSWORD','REDIS_TEST_PASSWORD')
    {
        if ([Environment]::GetEnvironmentVariable("TGAME_$key"))
        {
            continue
        }

        $line = Get-Content -LiteralPath '.local/secrets/cloud.env' |
            Where-Object { $_.StartsWith("$key=") } | Select-Object -First 1
        if (!$line)
        {
            throw "Missing TGAME_$key."
        }

        [Environment]::SetEnvironmentVariable("TGAME_$key", $line.Substring($key.Length + 1))
        $addedEnvironment += "TGAME_$key"
    }

    & $dotnet run --project tools/server-console/tests/ServerConsole.Check.csproj -- $checkout
    if ($LASTEXITCODE -ne 0)
    {
        exit $LASTEXITCODE
    }

    & $dotnet run --project "$checkout/code/tests/singlepoint/TGame.SinglePointTests.csproj"
    exit $LASTEXITCODE
}
finally
{
    foreach ($key in $addedEnvironment)
    {
        [Environment]::SetEnvironmentVariable($key, $null)
    }

    Pop-Location
}
