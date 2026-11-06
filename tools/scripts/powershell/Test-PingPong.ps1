#requires -Version 7.0
[CmdletBinding()]
param([switch]$Hold)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../../..").Path
$runDir = Join-Path $root ('logs/server/pingpong/' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runDir
$children = @{}
$dotnet = "$env:ProgramFiles/dotnet/dotnet.exe"
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -CommandType Application).Source }
$dll = Join-Path $root 'build/bin/TGame.PingPong/debug/TGame.PingPong.dll'
Write-Host "PingPong logs: $runDir"
Push-Location $root
try {
    & $dotnet build code/server/app/TGame.PingPong.csproj --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
    $password = $env:TGAME_MONGO_TEST_PASSWORD
    if (!$password) {
        $setting = Get-Content -LiteralPath '.local/secrets/cloud.env' | Where-Object { $_ -cmatch '^MONGO_TEST_PASSWORD=' } | Select-Object -First 1
        if (!$setting) { throw 'Set TGAME_MONGO_TEST_PASSWORD or configure the project test credentials.' }
        $password = $setting.Substring('MONGO_TEST_PASSWORD='.Length)
    }
    $redisPassword = $env:TGAME_REDIS_TEST_PASSWORD
    if (!$redisPassword) {
        $setting = Get-Content -LiteralPath '.local/secrets/cloud.env' | Where-Object { $_ -cmatch '^REDIS_TEST_PASSWORD=' } | Select-Object -First 1
        if (!$setting) { throw 'Set TGAME_REDIS_TEST_PASSWORD or configure the project test credentials.' }
        $redisPassword = $setting.Substring('REDIS_TEST_PASSWORD='.Length)
    }
    $gmToken = $env:TGAME_GM_TOKEN
    if (!$gmToken) { $gmToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
    $gmActor = $env:TGAME_GM_OPERATOR
    if (!$gmActor) { $gmActor = 'local-smoke' }
    $roles = 'Router','DBMgr','Scene','Login','Base','Profile','GM','Gate'
    foreach ($role in $roles) {
        $launch = @{
            FilePath = $dotnet; ArgumentList = ('"{0}" {1} "{2}"' -f $dll,$role,$runDir)
            WindowStyle = 'Hidden'; PassThru = $true; WorkingDirectory = $root
            RedirectStandardOutput = "$runDir/$role.log"; RedirectStandardError = "$runDir/$role.err.log"
            Environment = @{ TGAME_GM_TOKEN = $null; TGAME_GM_OPERATOR = $null }
        }
        if ($role -eq 'GM') {
            $launch.Environment.TGAME_GM_TOKEN = $gmToken
            $launch.Environment.TGAME_GM_OPERATOR = $gmActor
        }
        if ($role -eq 'DBMgr') { $launch.Environment.TGAME_MONGO_TEST_PASSWORD = $password }
        if ($role -eq 'Profile') {
            $launch.Environment.TGAME_MONGO_TEST_PASSWORD = $password
            $launch.Environment.TGAME_REDIS_TEST_PASSWORD = $redisPassword
        }
        $children[$role] = Start-Process @launch
    }
    $deadline = [datetime]::UtcNow.AddSeconds(60)
    do {
        $ready = 0
        foreach ($role in $roles) {
            if ($children[$role].HasExited) { throw "$role exited before probes. See $role.err.log." }
            if (Select-String -LiteralPath "$runDir/$role.log" -SimpleMatch '"ev":"READY"' -Quiet) { $ready++ }
        }
        if ($ready -eq $roles.Count) { break }
        if ([datetime]::UtcNow -gt $deadline) { throw "Only $ready/$($roles.Count) ready. Check SSH tunnel and role logs." }
        Start-Sleep -Milliseconds 150
    } while ($true)

    $cases = @(foreach ($round in 1..2) {
        foreach ($route in @('Login login-1','Base body-1','Base,Router,Scene soul-1','Login,Router,Scene soul-1',
            'Base,Router,DBMgr db-probe-1','Base,DBMgr db-probe-1')) {
            $tail,$entity = $route.Split(' ')
            [pscustomobject]@{ Id = [guid]::NewGuid().ToString('N'); Entity = $entity
                Path = @('Client','Gate') + $tail.Split(','); Storage = ($tail -eq 'Base,DBMgr') }
        }
    })
    $pointIds = @{}
    foreach ($route in 'Gate','Gate,Base,Router,Login','Gate,Login,Router,Base',
        'Gate,Base,Router,Scene','Gate,Base,Router','Gate,Login,Router,DBMgr','Gate,Base,Router,Profile','Gate,Base,Router,GM') {
        $path = @('Client') + $route.Split(',')
        $role = $path[-1]
        $point = @(Select-String -LiteralPath "$runDir/$role.log" -SimpleMatch '"ev":"SINGLE_POINT_INIT"' |
            ForEach-Object { $_.Line | ConvertFrom-Json })
        if ($point.Count -ne 1 -or $point[0].entity -cnotmatch '^[0-9a-f]{32}$') { throw "Expected one startup Entity in $role." }
        $pointIds[$role] = $point[0].entity
        $cases += [pscustomobject]@{ Id = [guid]::NewGuid().ToString('N'); Entity = $point[0].entity
            Path = $path; Storage = $false }
    }
    # A Base publishes its own summary, two different entities pull it, then Base withdraws it.
    foreach ($step in 'Base Publish Stored STORED','Base,Router,Scene Fetch Found MONGO_READ','Login Fetch Found CACHE_HIT',
        'Base Withdraw Removed REMOVED','Base,Router,Scene Fetch Missing MISSING') {
        $route,$action,$expected,$evidence = $step.Split(' ')
        $path = @('Client','Gate') + $route.Split(',')
        $cases += [pscustomobject]@{ Id = [guid]::NewGuid().ToString('N'); Entity = $pointIds[$path[-1]]
            Path = $path; Storage = $false; ProfileAction = $action; ProfileSubject = $pointIds['Base']
            ProfileResult = $expected; Evidence = $evidence }
    }
    $cases | ConvertTo-Json -Depth 4 -AsArray | Set-Content -LiteralPath "$runDir/cases.json" -Encoding utf8
    $listen = (Select-String -LiteralPath "$runDir/Gate.log" -SimpleMatch '"ev":"LISTEN"').Line | ConvertFrom-Json
    $endpoint = 'wss://localhost:{0}/probe' -f ([uri]$listen.detail).Port
    & $dotnet $dll client $runDir $endpoint $listen.link > "$runDir/Client.log" 2> "$runDir/Client.err.log"
    if ($LASTEXITCODE -ne 0) { throw "Client failed. See Client.err.log." }
    $gmListen = (Select-String -LiteralPath "$runDir/GM.log" -SimpleMatch '"ev":"LISTEN"').Line | ConvertFrom-Json
    $gmUri = [uri]$gmListen.detail
    if ($gmUri.Host -ne '127.0.0.1' -or $gmUri.Scheme -ne 'http') { throw 'Unexpected GM HTTP endpoint.' }
    $unknownId = [guid]::NewGuid().ToString('N')
    $checks = @(foreach ($check in 'missing-token','wrong-token','invalid-id','unknown-id','delivered') {
        $target = $pointIds.Scene
        $headers = @{ Authorization = "Bearer $gmToken" }
        $expected = switch ($check) {
            'missing-token' { $headers = @{}; 401 }
            'wrong-token' { $headers.Authorization = 'Bearer wrong'; 401 }
            'invalid-id' { $target = 'invalid'; 400 }
            'unknown-id' { $target = $unknownId; 404 }
            'delivered' { 200 }
        }
        $response = Invoke-WebRequest -Method Post -Uri "$($gmUri.AbsoluteUri)gm/commands/ping/$target" -Headers $headers -NoProxy -MaximumRedirection 0 -TimeoutSec 20 -SkipHttpErrorCheck
        $body = $response.Content | ConvertFrom-Json
        if ($response.StatusCode -ne $expected -or $body.requestId -cnotmatch '^[0-9a-f]{32}$' -or
            $response.Headers['X-Request-Id'][0] -cne $body.requestId -or
            ($expected -eq 200 -and ($body.entityId -cne $target -or $body.responder -cne 'Scene'))) {
            throw "GM HTTP check failed: $check, status=$($response.StatusCode)."
        }
        [pscustomobject]@{ Check=$check; Id=$body.requestId; Entity=$body.entityId; Status=$expected
            Actor=$(if ($expected -eq 401) { 'unauthenticated' } else { $gmActor }) }
    })
    $checks | ConvertTo-Json -AsArray | Set-Content -LiteralPath "$runDir/gm-checks.json" -Encoding utf8
    foreach ($child in $children.Values) { if ($child.HasExited) { throw "Server exited during probes." } }
    & "$PSScriptRoot/Test-PingPongLogs.ps1" -RunDirectory $runDir | Tee-Object -FilePath "$runDir/result.log"
    if ($Hold) { Read-Host "Servers are running. GM HTTP $gmUri ; press Enter to stop" | Out-Null }
}
catch {
    "FAIL $($_.Exception.Message)" | Tee-Object -FilePath "$runDir/result.log" -Append | Write-Host
    exit 1
}
finally {
    $null = New-Item -ItemType File -Path "$runDir/stop" -Force
    foreach ($child in $children.Values) {
        if (!$child.HasExited -and !$child.WaitForExit(5000)) { $child.Kill($true); $child.WaitForExit() }
        $child.Dispose()
    }
    Pop-Location
}
