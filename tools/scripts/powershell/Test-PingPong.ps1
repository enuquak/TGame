#requires -Version 7.0
[CmdletBinding()]
param([switch]$Hold, [string]$Configuration)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Import-ServerTypes.ps1')

$startupTimeoutSeconds = 60
$readyIntervalMilliseconds = 150
$stopTimeoutMilliseconds = 5000
$caseJsonDepth = 4

$gateRole = [TGame.ProcessType]::Gate.ToString()
$baseRole = [TGame.ProcessType]::Base.ToString()
$sceneRole = [TGame.ProcessType]::Scene.ToString()
$routerRole = [TGame.ProcessType]::Router.ToString()
$loginRole = [TGame.ProcessType]::Login.ToString()
$dbMgrRole = [TGame.ProcessType]::DBMgr.ToString()
$profileRole = [TGame.ProcessType]::Profile.ToString()
$gmRole = [TGame.ProcessType]::GM.ToString()

$root = (Resolve-Path "$PSScriptRoot/../../..").Path
$directoryName = [datetime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff', [cultureinfo]::InvariantCulture)
$runDir = Join-Path $root "logs/tools/pingpong/$directoryName"
$serverRoot = Join-Path $root 'logs/server'
$readLog = Join-Path $PSScriptRoot 'Read-PingPongLog.ps1'
$null = New-Item -ItemType Directory -Path $runDir
$children = @{}
$dotnet = "$env:ProgramFiles/dotnet/dotnet.exe"
if (!(Test-Path -LiteralPath $dotnet))
{
    $dotnet = (Get-Command dotnet -CommandType Application).Source
}

$serverDll = Join-Path $root 'build/bin/TGame.Server/debug/TGame.Server.dll'
$clientDll = Join-Path $root 'build/bin/TGame.ProbeClient/debug/TGame.ProbeClient.dll'
Write-Host "Server logs: $serverRoot/<Instance>Server/"
Write-Host "PingPong results: $runDir"
Push-Location $root

try
{
    & $dotnet build code/server/app/TGame.Server.csproj --nologo
    if ($LASTEXITCODE -ne 0)
    {
        throw "Server build failed ($LASTEXITCODE)."
    }

    & $dotnet build code/tests/probe-client/TGame.ProbeClient.csproj --nologo
    if ($LASTEXITCODE -ne 0)
    {
        throw "Probe client build failed ($LASTEXITCODE)."
    }

    $idBatchSize = 256
    $idQueue = [Collections.Generic.Queue[string]]::new()
    $idConfig = Join-Path $root 'code/server/config/id-generator.json'
    $serverConfig = Join-Path $root 'code/server/config/server.local.yaml'
    $generatedIds = @(& $dotnet $clientDll --ids $idConfig $serverConfig $idBatchSize)
    if ($LASTEXITCODE -ne 0 -or $generatedIds.Count -ne $idBatchSize)
    {
        throw 'Failed to allocate test IDs through the engine generator.'
    }

    foreach ($id in $generatedIds)
    {
        $idQueue.Enqueue($id)
    }

    function New-TestId
    {
        return $idQueue.Dequeue()
    }

    $password = [Environment]::GetEnvironmentVariable([TGame.ServerEnvironment]::MongoPassword)
    if (!$password)
    {
        $setting = Get-Content -LiteralPath '.local/secrets/cloud.env' | Where-Object { $_ -cmatch '^MONGO_TEST_PASSWORD=' } | Select-Object -First 1
        if (!$setting)
        {
            throw 'Set TGAME_MONGO_PASSWORD or configure the project test credentials.'
        }

        $password = $setting.Substring('MONGO_TEST_PASSWORD='.Length)
    }

    $dbMgrPassword = [Environment]::GetEnvironmentVariable([TGame.ServerEnvironment]::DBMgrMongoPassword)
    if ([string]::IsNullOrWhiteSpace($dbMgrPassword))
    {
        $dbMgrPassword = $password
    }

    $redisPassword = [Environment]::GetEnvironmentVariable([TGame.ServerEnvironment]::RedisPassword)
    if (!$redisPassword)
    {
        $setting = Get-Content -LiteralPath '.local/secrets/cloud.env' | Where-Object { $_ -cmatch '^REDIS_TEST_PASSWORD=' } | Select-Object -First 1
        if (!$setting)
        {
            throw 'Set TGAME_REDIS_PASSWORD or configure the project test credentials.'
        }

        $redisPassword = $setting.Substring('REDIS_TEST_PASSWORD='.Length)
    }

    if (!$Configuration)
    {
        $Configuration = Join-Path $root 'code/server/config/processes.json'
    }

    $groupFile = Join-Path $runDir ([TGame.ServerRunFiles]::GroupConfiguration)
    Copy-Item -LiteralPath $Configuration -Destination $groupFile
    $instances = @(& "$PSScriptRoot/Read-ServerGroup.ps1" -Path $groupFile)
    $roles = @($instances.Name)
    foreach ($required in [Enum]::GetNames([TGame.ProcessType]))
    {
        if ($required -cnotin $instances.Role)
        {
            throw "The full protocol smoke test requires at least one $required instance."
        }
    }

    $processes = @{}

    foreach ($role in $roles)
    {
        $processRole = ($instances | Where-Object Name -CEQ $role).Role
        $launch = @{
            FilePath = $dotnet
            ArgumentList = ('"{0}" {1} "{2}"' -f $serverDll,$role,$runDir)
            WindowStyle = 'Hidden'
            PassThru = $true
            WorkingDirectory = $root
            RedirectStandardError = "$runDir/$role.stderr.log"
            Environment = @{
                ([TGame.ServerEnvironment]::DBMgrMongoPassword) = $null
                ([TGame.ServerEnvironment]::MongoPassword) = $null
                ([TGame.ServerEnvironment]::RedisPassword) = $null
                ([TGame.ServerEnvironment]::LogRoot) = $serverRoot
                ([TGame.ServerEnvironment]::Config) = (Join-Path $root 'code/server/config/server.local.yaml')
                ([TGame.ServerEnvironment]::Diagnostics) = 'true'
            }
        }

        if ($processRole -eq $dbMgrRole)
        {
            $launch.Environment[[TGame.ServerEnvironment]::DBMgrMongoPassword] = $dbMgrPassword
        }

        if ($processRole -eq $profileRole)
        {
            $launch.Environment[[TGame.ServerEnvironment]::MongoPassword] = $password
            $launch.Environment[[TGame.ServerEnvironment]::RedisPassword] = $redisPassword
        }

        $children[$role] = Start-Process @launch
        $processes[$role] = @{
            pid = $children[$role].Id
            startedUtcTicks = $children[$role].StartTime.ToUniversalTime().Ticks
        }
    }

    @{
        serverRoot = [IO.Path]::GetRelativePath($runDir,$serverRoot)
        processes = $processes
    } |
        ConvertTo-Json -Depth $caseJsonDepth | Set-Content -LiteralPath "$runDir/server-logs.json" -Encoding utf8

    $deadline = [datetime]::UtcNow.AddSeconds($startupTimeoutSeconds)
    do
    {
        $ready = 0

        foreach ($role in $roles)
        {
            if ($children[$role].HasExited)
            {
                throw "$role exited before probes. See $serverRoot/${role}Server and $runDir/$role.stderr.log."
            }

            if ((& $readLog $runDir $role -Event ([TGame.ServerLogEvents]::SinglePointReady)) -and (& $readLog $runDir $role -Event ([TGame.ServerLogEvents]::LinksReady)))
            {
                $ready++
            }
        }

        if ($ready -eq $roles.Count)
        {
            break
        }

        if ([datetime]::UtcNow -gt $deadline)
        {
            throw "Only $ready/$($roles.Count) ready. Check SSH tunnel and role logs."
        }

        Start-Sleep -Milliseconds $readyIntervalMilliseconds
    }
    while ($true)

    $pointIds = @{}
    foreach ($role in $roles)
    {
        $point = @(& $readLog $runDir $role -Event ([TGame.ServerLogEvents]::SinglePointInit))
        if ($point.Count -ne 1 -or ![TGame.IdText]::IsValid($point[0].entity))
        {
            throw "Expected one startup Entity in $role."
        }

        if (($instances | Where-Object Name -CEQ $role).Number -eq 1)
        {
            $pointIds[$point[0].role] = $point[0].entity
        }
    }

    $cases = @(foreach ($round in 1..2)
        {
            foreach ($route in @(
                $loginRole
                $baseRole
                "$baseRole,$routerRole,$sceneRole"
                "$loginRole,$routerRole,$sceneRole"
                "$baseRole,$routerRole,$dbMgrRole"
                "$baseRole,$dbMgrRole"
            ))
            {
                $path = @(([TGame.ServerPeerNames]::Client), $gateRole) + $route.Split(',')
                [pscustomobject]@{
                    Id = (New-TestId)
                    Entity = $pointIds[$path[-1]]
                    Path = $path
                    Storage = ($route -eq "$baseRole,$dbMgrRole")
                }
            }
        })

    foreach ($route in @(
        $gateRole
        "$gateRole,$baseRole,$routerRole,$loginRole"
        "$gateRole,$loginRole,$routerRole,$baseRole"
        "$gateRole,$baseRole,$routerRole,$sceneRole"
        "$gateRole,$baseRole,$routerRole"
        "$gateRole,$loginRole,$routerRole,$dbMgrRole"
        "$gateRole,$baseRole,$routerRole,$profileRole"
        "$gateRole,$baseRole,$routerRole,$gmRole"
    ))
    {
        $path = @(([TGame.ServerPeerNames]::Client)) + $route.Split(',')
        $cases += [pscustomobject]@{
            Id = (New-TestId)
            Entity = $pointIds[$path[-1]]
            Path = $path
            Storage = $false
        }
    }

    # A Base publishes its own summary, two different entities pull it, then Base withdraws it.
    foreach ($step in @(
        "$baseRole Publish Stored STORED"
        "$baseRole,$routerRole,$sceneRole Fetch Found MONGO_READ"
        "$loginRole Fetch Found CACHE_HIT"
        "$baseRole Withdraw Removed REMOVED"
        "$baseRole,$routerRole,$sceneRole Fetch Missing MISSING"
    ))
    {
        $route,$action,$expected,$evidence = $step.Split(' ')
        $path = @(([TGame.ServerPeerNames]::Client), $gateRole) + $route.Split(',')
        $cases += [pscustomobject]@{
            Id = (New-TestId)
            Entity = $pointIds[$path[-1]]
            Path = $path
            Storage = $false
            ProfileAction = $action
            ProfileSubject = $pointIds[$baseRole]
            ProfileResult = $expected
            Evidence = $evidence
        }
    }

    $cases | ConvertTo-Json -Depth $caseJsonDepth -AsArray | Set-Content -LiteralPath "$runDir/cases.json" -Encoding utf8
    $listen = & $readLog $runDir ([TGame.ServerProcess]::FormatName($gateRole, 1)) -Event ([TGame.ServerLogEvents]::Listen)
    $endpoint = 'wss://localhost:{0}/probe' -f ([uri]$listen.detail).Port
    & $dotnet $clientDll $runDir $endpoint $listen.link > "$runDir/Client.log" 2> "$runDir/Client.err.log"
    if ($LASTEXITCODE -ne 0)
    {
        throw "Client failed. See Client.err.log."
    }

    foreach ($child in $children.Values)
    {
        if ($child.HasExited)
        {
            throw "Server exited during probes."
        }
    }

    & "$PSScriptRoot/Test-PingPongLogs.ps1" -RunDirectory $runDir -RequireLifecycle | Tee-Object -FilePath "$runDir/result.log"
    if ($Hold)
    {
        Read-Host "Servers are running; press Enter to stop" | Out-Null
    }
}
catch
{
    "FAIL $($_.Exception.Message)" | Tee-Object -FilePath "$runDir/result.log" -Append | Write-Host
    exit 1
}
finally
{
    $null = New-Item -ItemType File -Path (Join-Path $runDir ([TGame.ServerRunFiles]::StopGroup)) -Force

    foreach ($child in $children.Values)
    {
        if (!$child.HasExited -and !$child.WaitForExit($stopTimeoutMilliseconds))
        {
            $child.Kill($true)
            $child.WaitForExit()
        }

        $child.Dispose()
    }

    Pop-Location
}
