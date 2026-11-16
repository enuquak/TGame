#requires -Version 7.0
[CmdletBinding()]
param([switch]$Hold)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Import-ServerTypes.ps1')

$startupTimeoutSeconds = 60
$readyIntervalMilliseconds = 150
$gmTokenBytes = 32
$gmRequestTimeoutSeconds = 20
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
Write-Host "Server logs: $serverRoot/<Role>Server/"
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

    $gmToken = [Environment]::GetEnvironmentVariable([TGame.ServerEnvironment]::GMToken)
    if (!$gmToken)
    {
        $gmToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes($gmTokenBytes))
    }

    $gmActor = [Environment]::GetEnvironmentVariable([TGame.ServerEnvironment]::GMOperator)
    if (!$gmActor)
    {
        $gmActor = 'local-smoke'
    }

    $roles = @(Get-Content -Raw -LiteralPath 'code/server/config/processes.json' | ConvertFrom-Json)
    $processes = @{}

    foreach ($role in $roles)
    {
        $launch = @{
            FilePath = $dotnet
            ArgumentList = ('"{0}" {1} "{2}"' -f $serverDll,$role,$runDir)
            WindowStyle = 'Hidden'
            PassThru = $true
            WorkingDirectory = $root
            RedirectStandardError = "$runDir/$role.stderr.log"
            Environment = @{
                ([TGame.ServerEnvironment]::GMToken) = $null
                ([TGame.ServerEnvironment]::GMOperator) = $null
                ([TGame.ServerEnvironment]::DBMgrMongoPassword) = $null
                ([TGame.ServerEnvironment]::MongoPassword) = $null
                ([TGame.ServerEnvironment]::RedisPassword) = $null
                ([TGame.ServerEnvironment]::LogRoot) = $serverRoot
                ([TGame.ServerEnvironment]::Config) = (Join-Path $root 'code/server/config/server.local.yaml')
                ([TGame.ServerEnvironment]::Diagnostics) = 'true'
            }
        }

        if ($role -eq $gmRole)
        {
            $launch.Environment[[TGame.ServerEnvironment]::GMToken] = $gmToken
            $launch.Environment[[TGame.ServerEnvironment]::GMOperator] = $gmActor
        }

        if ($role -eq $dbMgrRole)
        {
            $launch.Environment[[TGame.ServerEnvironment]::DBMgrMongoPassword] = $dbMgrPassword
        }

        if ($role -eq $profileRole)
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

        $pointIds[$role] = $point[0].entity
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
    $listen = & $readLog $runDir $gateRole -Event ([TGame.ServerLogEvents]::Listen)
    $endpoint = 'wss://localhost:{0}/probe' -f ([uri]$listen.detail).Port
    & $dotnet $clientDll $runDir $endpoint $listen.link > "$runDir/Client.log" 2> "$runDir/Client.err.log"
    if ($LASTEXITCODE -ne 0)
    {
        throw "Client failed. See Client.err.log."
    }

    $gmListen = & $readLog $runDir $gmRole -Event ([TGame.ServerLogEvents]::Listen)
    $gmUri = [uri]$gmListen.detail
    if ($gmUri.Host -ne '127.0.0.1' -or $gmUri.Scheme -ne 'http')
    {
        throw 'Unexpected GM HTTP endpoint.'
    }

    $unknownId = (New-TestId)
    $checks = @(foreach ($check in 'missing-token','wrong-token','invalid-id','unknown-id','delivered')
        {
            $target = $pointIds[$sceneRole]
            $headers = @{
                Authorization = "Bearer $gmToken"
            }

            $expected = switch ($check)
            {
                'missing-token'
                {
                    $headers = @{}
                    [int][Net.HttpStatusCode]::Unauthorized
                }

                'wrong-token'
                {
                    $headers.Authorization = 'Bearer wrong'
                    [int][Net.HttpStatusCode]::Unauthorized
                }

                'invalid-id'
                {
                    $target = 'invalid'
                    [int][Net.HttpStatusCode]::BadRequest
                }

                'unknown-id'
                {
                    $target = $unknownId
                    [int][Net.HttpStatusCode]::NotFound
                }

                'delivered'
                {
                    [int][Net.HttpStatusCode]::OK
                }
            }

            $response = Invoke-WebRequest -Method Post -Uri "$($gmUri.AbsoluteUri)gm/commands/ping/$target" -Headers $headers -NoProxy -MaximumRedirection 0 -TimeoutSec $gmRequestTimeoutSeconds -SkipHttpErrorCheck
            $body = $response.Content | ConvertFrom-Json
            if ($response.StatusCode -ne $expected -or ![TGame.IdText]::IsValid($body.requestId) -or
                $response.Headers['X-Request-Id'][0] -cne $body.requestId -or
                ($expected -eq [int][Net.HttpStatusCode]::OK -and ($body.entityId -cne $target -or $body.responder -cne $sceneRole)))
            {
                throw "GM HTTP check failed: $check, status=$($response.StatusCode)."
            }

            [pscustomobject]@{
                Check = $check
                Id = $body.requestId
                Entity = $body.entityId
                Status = $expected
                Actor = $(if ($expected -eq [int][Net.HttpStatusCode]::Unauthorized)
                    {
                        'unauthenticated'
                    }
                    else
                    {
                        $gmActor
                    })
            }
        })
    $checks | ConvertTo-Json -AsArray | Set-Content -LiteralPath "$runDir/gm-checks.json" -Encoding utf8

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
        Read-Host "Servers are running. GM HTTP $gmUri ; press Enter to stop" | Out-Null
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
