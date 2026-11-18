#requires -Version 7.0

. (Join-Path $PSScriptRoot 'Import-ServerTypes.ps1')

function Get-ServerRunProcesses([string]$Snapshot)
{
    if (!(Test-Path -LiteralPath $Snapshot -PathType Container))
    {
        return
    }

    foreach ($file in Get-ChildItem -LiteralPath $Snapshot -Filter '*.exe')
    {
        $slot = $null
        if (![TGame.ServerProcess]::TryParse($file.BaseName, [ref]$slot))
        {
            continue
        }

        foreach ($candidate in Get-Process -Name $file.BaseName -ErrorAction SilentlyContinue)
        {
            $keep = $false
            try
            {
                # 持有内核句柄后核对完整路径，后续操作不重新按 PID 寻找目标。
                $null = $candidate.Handle
                if ($candidate.HasExited)
                {
                    continue
                }

                if ([string]::Equals($candidate.MainModule.FileName, $file.FullName, [StringComparison]::OrdinalIgnoreCase))
                {
                    $keep = $true
                    [pscustomobject]@{ Slot = $slot; Process = $candidate }
                }
            }
            catch
            {
                if (!$candidate.HasExited)
                {
                    throw
                }
            }
            finally
            {
                if (!$keep)
                {
                    $candidate.Dispose()
                }
            }
        }
    }
}

function Open-ServerRun([string]$Root, [bool]$Starting, [bool]$NoBuild)
{
    $runtimeConfig = Join-Path $Root ([TGame.ServerRuntime]::ConfigurationPath)
    $runs = & (Join-Path $PSScriptRoot 'New-ServerRunDirectory.ps1') -Configuration $runtimeConfig
    $lock = [IO.File]::Open((Join-Path $runs 'manage.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try
    {
        $currentFile = Join-Path $runs 'current'
        $context = @{ Root = $Root; Lock = $lock; RunDirectory = $null; Snapshot = $null; Group = $null }
        if (Test-Path -LiteralPath $currentFile)
        {
            $name = [IO.File]::ReadAllText($currentFile).Trim()
            if ($name -notmatch '\A[0-9]{8}-[0-9]{6}-[0-9]{7}\z')
            {
                throw "运行组记录无效：$currentFile"
            }

            $context.RunDirectory = Join-Path $runs $name
            $context.Snapshot = Join-Path $Root "build/server-runs/$name"
            $live = @(Get-ServerRunProcesses $context.Snapshot)
            $alive = $live.Count -gt 0
            foreach ($item in $live)
            {
                $item.Process.Dispose()
            }

            if (!$Starting -or $alive)
            {
                if ($Starting)
                {
                    $context.Group = [TGame.ServerGroup]::Load((Join-Path $context.RunDirectory ([TGame.ServerGroup]::FileName)))
                }
                return $context
            }
        }

        if (!$Starting)
        {
            return $context
        }

        $group = [TGame.ServerGroup]::Load((Join-Path $Root ([TGame.ServerGroup]::ConfigurationPath)))
        $environmentFile = [Environment]::GetEnvironmentVariable([TGame.ServerEnvironment]::Config)
        if ([string]::IsNullOrWhiteSpace($environmentFile))
        {
            $environmentFile = 'code/server/config/server.local.yaml'
        }
        $environmentFile = [IO.Path]::GetFullPath($environmentFile, $Root)
        if (!(Test-Path -LiteralPath $environmentFile -PathType Leaf))
        {
            throw "找不到服务器环境配置：$environmentFile"
        }

        if (!$NoBuild)
        {
            & dotnet build (Join-Path $Root 'code/server/app/TGame.Server.csproj') --nologo | Out-Host
            if ($LASTEXITCODE -ne 0)
            {
                throw "服务器构建失败，退出码 $LASTEXITCODE。"
            }
        }

        $source = Join-Path $Root 'build/bin/TGame.Server/debug'
        if (!(Test-Path -LiteralPath (Join-Path $source 'TGame.Server.exe')))
        {
            throw '没有可用的服务器编译产物，请去掉 -NoBuild 后重试。'
        }

        $name = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff', [Globalization.CultureInfo]::InvariantCulture)
        $snapshot = Join-Path $Root "build/server-runs/$name"
        $runDirectory = Join-Path $runs $name
        $null = New-Item -ItemType Directory -Path $snapshot
        foreach ($file in Get-ChildItem -LiteralPath $source)
        {
            Copy-Item -LiteralPath $file.FullName -Destination $snapshot -Recurse
        }

        $snapshotConfig = Join-Path $snapshot 'config'
        $null = New-Item -ItemType Directory -Path $snapshotConfig -Force
        $runtimeConfig = Join-Path $snapshotConfig ([TGame.ServerRuntime]::FileName)
        [TGame.ServerRuntime]::Save($runtimeConfig, $runDirectory, $Root)
        $null = & (Join-Path $PSScriptRoot 'New-ServerRunDirectory.ps1') -Configuration $runtimeConfig
        $group.Save((Join-Path $runDirectory ([TGame.ServerGroup]::FileName)))
        $group.Save((Join-Path $snapshotConfig ([TGame.ServerGroup]::FileName)))
        Copy-Item -LiteralPath $environmentFile -Destination (Join-Path $runDirectory 'server.yaml')

        # 先发布当前组，再启动子进程；即使脚本中断，下次仍可接管。
        [IO.File]::WriteAllText($currentFile + '.tmp', $name)
        [IO.File]::Move($currentFile + '.tmp', $currentFile, $true)
        $context.RunDirectory = $runDirectory
        $context.Snapshot = $snapshot
        $context.Group = $group
        return $context
    }
    catch
    {
        $lock.Dispose()
        throw
    }
}

function Start-ServerProcess([hashtable]$Context, [TGame.ProcessType]$Type, [int]$Slot)
{
    $live = @(Get-ServerRunProcesses $Context.Snapshot)
    try
    {
        $used = @($live | Where-Object { $_.Slot.Type -eq $Type } | ForEach-Object { $_.Slot.ChannelId })
        if ($Slot -gt 0 -and $used -contains $Slot)
        {
            Write-Output "$([TGame.ServerProcess]::FormatName($Type.ToString(), $Slot)) 启动槽位已运行。"
            return
        }

        if ($Slot -eq 0)
        {
            $Slot = 1
            while ($used -contains $Slot)
            {
                $Slot++
            }
        }
    }
    finally
    {
        foreach ($item in $live)
        {
            $item.Process.Dispose()
        }
    }

    $slotName = [TGame.ServerProcess]::FormatName($Type.ToString(), $Slot)
    $exe = Join-Path $Context.Snapshot "$slotName.exe"
    if (!(Test-Path -LiteralPath $exe))
    {
        Copy-Item -LiteralPath (Join-Path $Context.Snapshot 'TGame.Server.exe') -Destination $exe
    }

    $configKey = [TGame.ServerEnvironment]::Config
    $logKey = [TGame.EngineEnvironment]::LogRoot
    $previousConfig = [Environment]::GetEnvironmentVariable($configKey)
    $previousLog = [Environment]::GetEnvironmentVariable($logKey)
    try
    {
        [Environment]::SetEnvironmentVariable($configKey, (Join-Path $Context.RunDirectory 'server.yaml'))
        [Environment]::SetEnvironmentVariable($logKey, (Join-Path $Context.Root 'logs/server'))
        # ShellExecute 启动独立隐藏进程，避免服务器继承调用者的输出管道。
        $child = Start-Process -FilePath $exe -ArgumentList $Type.ToString() -WorkingDirectory $Context.Root -WindowStyle Hidden -PassThru
        try
        {
            if ($child.WaitForExit(100))
            {
                throw "$slotName 启动后退出，退出码 $($child.ExitCode)。请检查环境配置和服务器日志。"
            }
            Write-Output "$slotName 启动槽位已拉起，PID $($child.Id)；实际实例编号由 ZooKeeper 分配。"
        }
        finally
        {
            $child.Dispose()
        }
    }
    finally
    {
        [Environment]::SetEnvironmentVariable($configKey, $previousConfig)
        [Environment]::SetEnvironmentVariable($logKey, $previousLog)
    }
}

function Stop-ServerProcesses([hashtable]$Context, [bool]$Force, [int]$TimeoutSeconds)
{
    if (!$Context.Snapshot)
    {
        Write-Output '没有脚本管理的运行组。'
        return
    }

    $live = @(Get-ServerRunProcesses $Context.Snapshot)
    try
    {
        foreach ($item in $live)
        {
            $child = $item.Process
            if ($child.HasExited)
            {
                continue
            }

            if ($Force)
            {
                $child.Kill()
            }
            else
            {
                $key = [TGame.ServerRunFiles]::ProcessKey($child.Id, $child.StartTime.ToUniversalTime().Ticks)
                $signal = Join-Path $Context.RunDirectory ([TGame.ServerRunFiles]::StopProcessPrefix + $key)
                [IO.File]::WriteAllText($signal, '')
            }
        }

        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while (@($live | Where-Object { !$_.Process.HasExited }).Count -gt 0)
        {
            if ([DateTime]::UtcNow -ge $deadline)
            {
                throw "等待退出超过 $TimeoutSeconds 秒。仍存活的进程未被强退，可执行 KillAll.bat。"
            }
            Start-Sleep -Milliseconds 100
        }
        Write-Output "已结束 $($live.Count) 个脚本管理的进程。"
    }
    finally
    {
        foreach ($item in $live)
        {
            $item.Process.Dispose()
        }
    }
}
