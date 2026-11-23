#requires -Version 7.0
[CmdletBinding()]
param([string]$Configuration)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../../..").Path
. "$PSScriptRoot/Import-ServerTypes.ps1"
$originalConfiguration = [Environment]::GetEnvironmentVariable([TGame.EngineEnvironment]::Config)
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

    $selected = $Configuration
    if ([string]::IsNullOrWhiteSpace($selected))
    {
        $selected = $originalConfiguration
    }
    if ([string]::IsNullOrWhiteSpace($selected))
    {
        $selected = [TGame.ServerConfiguration]::ConfigurationPath
    }

    $source = [IO.Path]::GetFullPath($selected, $root)
    # 测试只修改临时工作区中的源文件，保留 include 文件布局。
    $configDirectory = [IO.Directory]::CreateDirectory((Join-Path $checkout 'selected-config')).FullName
    foreach ($file in Get-ChildItem -LiteralPath (Split-Path $source) -Filter '*.yaml')
    {
        Copy-Item -LiteralPath $file.FullName -Destination $configDirectory
    }
    $selectedConfig = Join-Path $configDirectory ([IO.Path]::GetFileName($source))
    [Environment]::SetEnvironmentVariable([TGame.EngineEnvironment]::Config, $selectedConfig)

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
    [Environment]::SetEnvironmentVariable([TGame.EngineEnvironment]::Config, $originalConfiguration)
    Pop-Location
}
