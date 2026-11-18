#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../../..").Path
. (Join-Path $PSScriptRoot 'Import-ServerTypes.ps1')

function Assert-True([bool]$Condition, [string]$Message)
{
    if (!$Condition)
    {
        throw $Message
    }
}

$entryNames = @('StartAll', 'StopAll', 'KillAll')
$entryNames += [Enum]::GetNames([TGame.ProcessType]) | ForEach-Object { 'Start' + $_ }
foreach ($name in $entryNames)
{
    Assert-True (Test-Path -LiteralPath "$root/tools/scripts/$name.bat") "缺少独立入口：$name.bat"
}

$checkout = Join-Path $root ('build/server-scripts-check/' + [guid]::NewGuid().ToString('N') + '/path with spaces')
$scripts = Join-Path $checkout 'tools/scripts'
$output = Join-Path $checkout 'build/bin/TGame.Server/debug'
$fixture = Join-Path $checkout 'code/server/app'
$null = New-Item -ItemType Directory -Path "$scripts/powershell", "$output/config", $fixture -Force
Copy-Item -LiteralPath "$root/code/Directory.Build.props" -Destination "$checkout/code/Directory.Build.props"

$implementationFiles = @('Manage-Servers.ps1', 'Server-ProcessFunctions.ps1', 'Import-ServerTypes.ps1', 'New-ServerRunDirectory.ps1')
$implementationFiles += $entryNames | ForEach-Object { $_ + '.ps1' }
foreach ($file in $implementationFiles)
{
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination "$scripts/powershell/$file"
}

foreach ($name in @($entryNames) + 'Run-ServerScript')
{
    Copy-Item -LiteralPath "$root/tools/scripts/$name.bat" -Destination "$scripts/$name.bat"
}

$sourceFiles = @(Get-ChildItem -LiteralPath "$root/code/server/engine/configuration" -Filter '*.cs')
$sourceFiles += Get-Item -LiteralPath "$root/code/server/engine/identifiers/IdText.cs", "$root/code/server/engine/logging/ServerLogProtocol.cs", "$root/code/shared/csharp/ServerEnvironment.cs"
foreach ($file in $sourceFiles)
{
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
    $destination = Join-Path $checkout $relative
    $null = New-Item -ItemType Directory -Path (Split-Path $destination) -Force
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}

$counts = [ordered]@{}
foreach ($type in [Enum]::GetValues([TGame.ProcessType]))
{
    $counts[$type.ToString()] = 0
}
$counts[[TGame.ProcessType]::Router.ToString()] = 2
$configDir = Join-Path $checkout 'code/server/config'
$null = New-Item -ItemType Directory -Path $configDir
$counts | ConvertTo-Json | Set-Content -LiteralPath "$configDir/processes.json"
Set-Content -LiteralPath "$configDir/server.local.yaml" -Value 'fixture-environment'
[IO.File]::WriteAllText("$configDir/runtime.json", '{"runDirectory":"logs/custom runs"}')
Copy-Item -LiteralPath "$configDir/processes.json" -Destination "$output/config/processes.json"
Copy-Item -LiteralPath "$configDir/runtime.json" -Destination "$output/config/runtime.json"

@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>TGame.Server</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath "$fixture/TGame.Server.csproj"
Copy-Item -LiteralPath "$root/code/server/engine/configuration/ServerRunFiles.cs" -Destination "$fixture/ServerRunFiles.cs"
Copy-Item -LiteralPath "$root/code/shared/csharp/ServerEnvironment.cs" -Destination "$fixture/ServerEnvironment.cs"
Copy-Item -LiteralPath "$root/code/server/engine/configuration/ServerRuntime.cs" -Destination "$fixture/ServerRuntime.cs"
@'
using System.Diagnostics;
using System.Text.Json;
using TGame;

if (args.Length != 1)
{
    throw new Exception("The server must receive only its process type.");
}
var runDir = ServerRuntime.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "config", ServerRuntime.FileName), Directory.GetCurrentDirectory());
using var process = Process.GetCurrentProcess();
var key = ServerRunFiles.ProcessKey(process.Id, process.StartTime.ToUniversalTime().Ticks);
var config = Environment.GetEnvironmentVariable(ServerEnvironment.Config)!;
File.WriteAllText(Path.Combine(runDir, process.Id + ".started.json"), JsonSerializer.Serialize(new
{
    Role = args[0],
    Environment = File.ReadAllText(config),
    Minimums = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", ServerRunFiles.GroupConfiguration))
}));
while (true)
{
    bool stopping = File.Exists(Path.Combine(runDir, ServerRunFiles.StopProcessPrefix + key));
    if (stopping && !File.Exists(Path.Combine(runDir, "ignore-stop")))
    {
        return;
    }
    Thread.Sleep(20);
}

'@ | Set-Content -LiteralPath "$fixture/Program.cs"

function Invoke-Entry([string]$Name, [string[]]$Options = @(), [int]$ExpectedExit = 0)
{
    Write-Output "检查 $Name $($Options -join ' ')"
    $capture = Join-Path $checkout 'entry-output.log'
    $info = [Diagnostics.ProcessStartInfo]::new($env:ComSpec)
    $info.Arguments = '/d /s /c ""' + "$scripts/$Name.bat" + '" ' + ($Options -join ' ') + ' > "' + $capture + '" 2>&1"'
    $info.WorkingDirectory = $checkout
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.Environment['TGAME_NONINTERACTIVE'] = '1'
    $null = $info.Environment.Remove([TGame.ServerEnvironment]::Config)
    $child = [Diagnostics.Process]::Start($info)
    try
    {
        if (!$child.WaitForExit(15000))
        {
            $child.Kill($true)
            throw "$Name 检查超时。"
        }
        $text = [IO.File]::ReadAllText($capture)
        Assert-True ($child.ExitCode -eq $ExpectedExit) "$Name 退出码 $($child.ExitCode)，预期 $ExpectedExit`n$text"
    }
    finally
    {
        $child.Dispose()
    }
}

function Get-TestProcesses
{
    $pattern = [TGame.ProcessType]::Router.ToString() + '*'
    @(Get-Process -Name $pattern -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($checkout + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    })
}

try
{
    Invoke-Entry 'StopAll'
    Invoke-Entry 'KillAll'
    Invoke-Entry 'StartAll'
    $first = @(Get-TestProcesses)
    Assert-True ($first.Count -eq 2) '应启动两个配置实例。'
    $runs = "$checkout/logs/custom runs"
    $currentFile = "$runs/current"
    $current = (Get-Content -LiteralPath $currentFile -Raw).Trim()
    $runDir = "$runs/$current"
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    while (@(Get-ChildItem -LiteralPath $runDir -Filter '*.started.json').Count -ne 2)
    {
        Assert-True ([DateTime]::UtcNow -lt $deadline) '假进程未收到两个启动参数。'
        Start-Sleep -Milliseconds 20
    }
    $started = Get-Content -LiteralPath "$runDir/$($first[0].Id).started.json" -Raw | ConvertFrom-Json
    Assert-True ($started.Environment.Trim() -eq 'fixture-environment') '未选择本地环境配置。'
    Assert-True (($started.Minimums | ConvertFrom-Json).Router -eq 2) '运行时连接门槛应与启动数量相同。'

    Invoke-Entry 'StartAll' @('-NoBuild')
    $beforeIds = ($first.Id | Sort-Object) -join ','
    $afterIds = (@(Get-TestProcesses).Id | Sort-Object) -join ','
    Assert-True ($afterIds -eq $beforeIds) '重复启动产生了新进程。'
    Set-Content -LiteralPath "$configDir/server.local.yaml" -Value 'changed-environment'
    $first[0].Kill()
    $first[0].WaitForExit()
    $counts[[TGame.ProcessType]::Router.ToString()] = 1
    $counts | ConvertTo-Json | Set-Content -LiteralPath "$configDir/processes.json"
    Invoke-Entry 'StartAll' @('-NoBuild')
    $restored = @(Get-TestProcesses)
    Assert-True ($restored.Count -eq 2 -and $restored.Id -contains $first[1].Id) '补起不应重启存活进程。'
    Assert-True ((Get-Content -LiteralPath $currentFile -Raw).Trim() -eq $current) '补起必须复用当前快照。'
    Assert-True ((Get-Content -LiteralPath "$runDir/server.yaml" -Raw).Trim() -eq 'fixture-environment') '补起改变了环境快照。'

    $lock = [IO.File]::Open("$runs/manage.lock", 'OpenOrCreate', 'ReadWrite', 'None')
    try
    {
        Invoke-Entry 'StartAll' @('-NoBuild') 1
    }
    finally
    {
        $lock.Dispose()
    }

    Invoke-Entry ('Start' + [TGame.ProcessType]::Router.ToString()) @('-NoBuild')
    Assert-True (@(Get-TestProcesses).Count -eq 3) '单类型脚本应额外启动一个实例。'
    Invoke-Entry 'StopAll'
    Assert-True (@(Get-TestProcesses).Count -eq 0) '正常停止后仍有进程。'
    # 模拟构建目录已清理，只留下运行记录。
    $missingSnapshot = '19990101-000000-0000000'
    $null = New-Item -ItemType Directory -Path "$runs/$missingSnapshot"
    Copy-Item -LiteralPath "$runDir/processes.json" -Destination "$runs/$missingSnapshot/processes.json"
    Set-Content -LiteralPath $currentFile -Value $missingSnapshot
    $counts[[TGame.ProcessType]::Router.ToString()] = 2
    $counts | ConvertTo-Json | Set-Content -LiteralPath "$configDir/processes.json"
    Invoke-Entry 'StartAll' @('-NoBuild')
    $current = (Get-Content -LiteralPath $currentFile -Raw).Trim()
    $runDir = "$runs/$current"
    Set-Content -LiteralPath "$runDir/ignore-stop" -Value ''
    Invoke-Entry 'StopAll' @('-TimeoutSeconds', '1') 1
    Assert-True (@(Get-TestProcesses).Count -eq 2) '正常停止超时不能偷偷强退。'

    # 同名 apphost 放到另一个目录，强退必须保留它。
    $foreignExe = "$output/$([TGame.ServerProcess]::FormatName([TGame.ProcessType]::Router.ToString(), 1)).exe"
    Copy-Item -LiteralPath "$output/TGame.Server.exe" -Destination $foreignExe
    $info = [Diagnostics.ProcessStartInfo]::new($foreignExe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WorkingDirectory = $checkout
    $info.ArgumentList.Add([TGame.ProcessType]::Router.ToString())
    [TGame.ServerRuntime]::Save("$output/config/runtime.json", $runDir, $checkout)
    $info.Environment[[TGame.ServerEnvironment]::Config] = "$runDir/server.yaml"
    $foreign = [Diagnostics.Process]::Start($info)
    # 紧急强退不依赖用于启动数量的配置记录。
    Remove-Item -LiteralPath "$runDir/processes.json"
    Invoke-Entry 'KillAll'
    Assert-True (!$foreign.HasExited) '误杀了不同路径的同名进程。'
    Assert-True (@(Get-TestProcesses).Count -eq 1) '强退未清理当前运行组。'
    $foreign.Kill()
    $foreign.WaitForExit()

    Set-Content -LiteralPath $currentFile -Value '../outside'
    Invoke-Entry 'KillAll' @() 1
    Set-Content -LiteralPath $currentFile -Value $current
    Remove-Item -LiteralPath "$output/TGame.Server.exe"
    Invoke-Entry 'StartAll' @('-NoBuild') 1
    foreach ($invalid in @('{}', '{"runDirectory":""}', '{"runDirectory":"C:/absolute"}'))
    {
        Set-Content -LiteralPath "$configDir/runtime.json" -Value $invalid
        Invoke-Entry 'StopAll' @() 1
    }
    Write-Output 'PASS：独立 BAT、按类型启动/补起、配置路径、单参数、快照、互斥、正常停止/超时、强退及同名进程隔离。'
}
finally
{
    foreach ($child in @(Get-TestProcesses))
    {
        $child.Kill()
        $child.WaitForExit()
        $child.Dispose()
    }
}
