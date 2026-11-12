#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory, [switch]$RequireLifecycle)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Import-ServerTypes.ps1')

$manifest = Join-Path $RunDirectory 'server-logs.json'
if (Test-Path -LiteralPath $manifest)
{
    $roles = (Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json).roles
}
else
{
    $roles = ([TGame.ProcessType]::Gate.ToString()),([TGame.ProcessType]::Login.ToString()),([TGame.ProcessType]::Base.ToString()),([TGame.ProcessType]::Scene.ToString()),([TGame.ProcessType]::Router.ToString()),([TGame.ProcessType]::DBMgr.ToString())
    if (Test-Path -LiteralPath (Join-Path $RunDirectory 'Profile.log'))
    {
        $roles += ([TGame.ProcessType]::Profile.ToString())
    }

    if (Test-Path -LiteralPath (Join-Path $RunDirectory 'GM.log'))
    {
        $roles += ([TGame.ProcessType]::GM.ToString())
    }
}

$hasProfile = ([TGame.ProcessType]::Profile.ToString()) -in $roles
$hasGM = ([TGame.ProcessType]::GM.ToString()) -in $roles
$records = @(foreach ($role in ($roles + ([TGame.ServerPeerNames]::Client)))
    {
        & "$PSScriptRoot/Read-PingPongLog.ps1" $RunDirectory $role
    })
$cases = Get-Content -LiteralPath (Join-Path $RunDirectory 'cases.json') -Raw | ConvertFrom-Json
$points = @($records | Where-Object ([TGame.ServerLogFields]::Event) -eq ([TGame.ServerLogEvents]::SinglePointInit))
$expectedRoles = $roles
# Older runs had no process Entity, then a Base-only Entity.
if (!$points.Count -and $cases.Count -in 12,13)
{
    $points = @($records | Where-Object ([TGame.ServerLogFields]::Event) -eq ([TGame.ServerLogEvents]::LegacyBaseSinglePointInit))
    $expectedRoles = @()
    if ($cases.Count -eq 13)
    {
        $expectedRoles = @(([TGame.ProcessType]::Base.ToString()))
    }
}

$profileCases = @($cases | Where-Object { $_.ProfileAction })
if ($profileCases.Count -ne (5 * [int]$hasProfile) -or $points.Count -ne $expectedRoles.Count -or $cases.Count -ne (12 + $points.Count + $profileCases.Count) -or
    ($cases.Id | Sort-Object -Unique).Count -ne $cases.Count)
{
    throw 'Unexpected case count, duplicate request ID or duplicate startup Entity.'
}

if ($points.Count -gt 1 -and (@($points.entity | Sort-Object -Unique).Count -ne $roles.Count -or
        @($points.detail | Sort-Object -Unique).Count -ne $roles.Count))
{
    throw 'Expected distinct process Entity IDs and classes.'
}

foreach ($role in $expectedRoles)
{
    $point = @($points | Where-Object ([TGame.ServerLogFields]::Role) -eq $role)
    if ($point.Count -ne 1)
    {
        throw "Missing or duplicate SinglePoint in $role."
    }

    $id = $point[0].entity
    $routes = @($records | Where-Object { $_.ev -eq ([TGame.ServerLogEvents]::EntityRoute) -and $_.role -eq ([TGame.ProcessType]::Router.ToString()) -and $_.peer -eq $role -and $_.entity -ceq $id })
    $routed = $role -in ([TGame.ProcessType]::Login.ToString()),([TGame.ProcessType]::Base.ToString()),([TGame.ProcessType]::Scene.ToString()),([TGame.ProcessType]::DBMgr.ToString()),([TGame.ProcessType]::Profile.ToString()),([TGame.ProcessType]::GM.ToString())
    $probes = @($cases | Where-Object { !$_.ProfileAction -and $_.Entity -ceq $id -and $_.Path[-1] -eq $role -and (!$routed -or $_.Path -contains ([TGame.ProcessType]::Router.ToString())) })
    $type = $point[0].detail
    if ($point[0].ev -eq ([TGame.ServerLogEvents]::LegacyBaseSinglePointInit))
    {
        $type = 'BaseServerSinglePoint'
    }

    # Multiple route cases may now target the same real SinglePoint.
    if (!$type -or $id -cnotmatch '^[0-9a-f]{32}$' -or $routes.Count -ne [int]$routed -or $probes.Count -lt 1)
    {
        throw "Incomplete SinglePoint initialization, registration or probe evidence for $role."
    }

    "ENTITY PASS type=$type role=$role entity=$id"
}

$serverPids = foreach ($role in $roles)
{
    $rows = @($records | Where-Object ([TGame.ServerLogFields]::Role) -eq $role)
    $events = ([TGame.ServerLogEvents]::Listen),([TGame.ServerLogEvents]::ZkRegister),([TGame.ServerLogEvents]::ZkDiscover)
    if ($RequireLifecycle -or $records.ev -contains ([TGame.ServerLogEvents]::SinglePointReady) -or $records.ev -contains ([TGame.ServerLogEvents]::LinksReady))
    {
        $events += ([TGame.ServerLogEvents]::SinglePointReady),([TGame.ServerLogEvents]::LinksReady)
        $init = @($rows | Where-Object ([TGame.ServerLogFields]::Event) -eq ([TGame.ServerLogEvents]::SinglePointInit))
        $ready = @($rows | Where-Object ([TGame.ServerLogFields]::Event) -eq ([TGame.ServerLogEvents]::SinglePointReady))
        if ($init.Count -ne 1 -or $ready.Count -ne 1 -or $ready[0].entity -cne $init[0].entity -or
            $ready[0].detail -cne $init[0].detail -or $rows.IndexOf($init[0]) -ge $rows.IndexOf($ready[0]))
        {
            throw "Missing or out-of-order SinglePoint completion for $role."
        }

        $links = @($rows | Where-Object ([TGame.ServerLogFields]::Event) -eq ([TGame.ServerLogEvents]::LinkUp))
        $complete = @($rows | Where-Object ([TGame.ServerLogFields]::Event) -eq ([TGame.ServerLogEvents]::LinksReady))
        if ($complete.Count -ne 1 -or $complete[0].detail -ne [string]$links.Count -or
            $rows.IndexOf($links[-1]) -ge $rows.IndexOf($complete[0]) -or $rows.IndexOf($complete[0]) -ge $rows.IndexOf($ready[0]))
        {
            throw "Missing or out-of-order link readiness for $role."
        }
    }
    else
    {
        $events += [TGame.ServerLogEvents]::LegacyReady
    }

    foreach ($name in $events)
    {
        $found = @($rows | Where-Object ([TGame.ServerLogFields]::Event) -eq $name)
        if ($found.Count -ne 1)
        {
            throw "Missing or duplicate $role $name."
        }
    }

    $found[0].pid
}

if (($serverPids | Sort-Object -Unique).Count -ne $roles.Count)
{
    throw 'Expected distinct server processes.'
}

$activeLinks = @{}
$pairs = @('Gate Login','Gate Base','Login Router','Base Router','Scene Router','DBMgr Router','Base DBMgr')
if ($hasProfile)
{
    $pairs += 'Profile Router'
}

if ($hasGM)
{
    $pairs += 'GM Router'
}

foreach ($pair in $pairs)
{
    $left, $right = $pair.Split(' ')
    $ends = @($records | Where-Object { $_.ev -eq ([TGame.ServerLogEvents]::LinkUp) -and
            (($_.role -eq $left -and $_.peer -eq $right) -or ($_.role -eq $right -and $_.peer -eq $left)) })
    if ($ends.Count -ne 2 -or ($ends.role | Sort-Object -Unique).Count -ne 2 -or
        !$ends[0].link -or $ends[0].link -ne $ends[1].link)
    {
        throw "Invalid stream evidence for $pair."
    }

    $activeLinks["$left/$right"] = $activeLinks["$right/$left"] = $ends[0].link
    "LINK PASS $left <-> $right stream=$($ends[0].link)"
}

if (@($records | Where-Object ([TGame.ServerLogFields]::Event) -eq ([TGame.ServerLogEvents]::LinkUp)).Count -ne (2 * $pairs.Count))
{
    throw 'Unexpected additional stream.'
}

$gateSessions = @($records | Where-Object { $_.role -eq ([TGame.ProcessType]::Gate.ToString()) -and $_.peer -eq ([TGame.ServerPeerNames]::Client) } |
    Select-Object -ExpandProperty link -Unique)
if ($gateSessions.Count -ne 1 -or !$gateSessions[0])
{
    throw 'Expected one WSS connection across both rounds.'
}

foreach ($case in $cases)
{
    $events = @($records | Where-Object ([TGame.ServerLogFields]::Id) -eq $case.Id)
    if ($case.ProfileAction)
    {
        $profileEvents = @($events | Where-Object { $_.ev -like ([TGame.ServerLogEvents]::ProfilePrefix + '*') })
        $events = @($events | Where-Object { $_.ev -notlike ([TGame.ServerLogEvents]::ProfilePrefix + '*') })
        $evidence = @($profileEvents | Where-Object { $_.role -eq ([TGame.ProcessType]::Profile.ToString()) -and $_.ev -ceq ([TGame.ServerLogEvents]::ProfilePrefix + $case.Evidence) -and $_.entity -ceq $case.ProfileSubject })
        if ($profileEvents.Count -ne 9 -or $evidence.Count -ne 1)
        {
            throw "Missing storage/cache evidence for $($case.Id)."
        }

        $inner = @($case.Path[-1], ([TGame.ProcessType]::Router.ToString()), ([TGame.ProcessType]::Profile.ToString()))

        for ($i = 1; $i -lt $inner.Count; $i++)
        {
            $src,$dst = $inner[$i-1],$inner[$i]
            foreach ($expected in @(
                "$([TGame.ServerLogEvents]::ProfileRequestTx) $src $dst"
                "$([TGame.ServerLogEvents]::ProfileRequestRx) $dst $src"
                "$([TGame.ServerLogEvents]::ProfileReplyTx) $dst $src"
                "$([TGame.ServerLogEvents]::ProfileReplyRx) $src $dst"
            ))
            {
                $kind,$sender,$peer = $expected.Split(' ')
                $match = @($profileEvents | Where-Object { $_.ev -ceq $kind -and $_.role -eq $sender -and $_.peer -eq $peer -and
                        $_.entity -ceq $case.ProfileSubject -and $_.link -eq $activeLinks["$src/$dst"] })
                if ($match.Count -ne 1)
                {
                    throw "Invalid Profile hop $expected for $($case.Id)."
                }
            }
        }

        "PROFILE PASS id=$($case.Id) $($case.ProfileAction) $($case.Evidence) path=$($inner -join ' -> ')"
    }

    if ($events.Count -ne (4 * ($case.Path.Count - 1) + [int]$case.Storage))
    {
        throw "Unexpected event count for $($case.Id)."
    }

    for ($i = 1; $i -lt $case.Path.Count; $i++)
    {
        $src, $dst = $case.Path[$i-1], $case.Path[$i]
        foreach ($expected in @(
            "$([TGame.ServerLogEvents]::PingTx) $src $dst"
            "$([TGame.ServerLogEvents]::PingRx) $dst $src"
            "$([TGame.ServerLogEvents]::PongTx) $dst $src"
            "$([TGame.ServerLogEvents]::PongRx) $src $dst"
        ))
        {
            $kind,$sender,$peer = $expected.Split(' ')
            $found = @($events | Where-Object { $_.ev -eq $kind -and $_.role -eq $sender -and $_.peer -eq $peer -and $_.entity -ceq $case.Entity })
            if ($found.Count -ne 1)
            {
                throw "Missing or duplicate $expected for $($case.Id)."
            }

            if ($i -gt 1 -and $found[0].link -ne $activeLinks["$src/$dst"])
            {
                throw "Stream mismatch for $expected."
            }
        }
    }

    if ($case.Storage -and @($events | Where-Object { $_.ev -eq ([TGame.ServerLogEvents]::MongoPong) -and $_.role -eq ([TGame.ProcessType]::DBMgr.ToString()) -and $_.entity -ceq $case.Entity }).Count -ne 1)
    {
        throw "Missing MongoDB acknowledgement for $($case.Id)."
    }

    "PONG PASS id=$($case.Id) entity=$($case.Entity) path=$($case.Path -join ' -> ')"
}

if ($hasGM)
{
    $checks = Get-Content -LiteralPath (Join-Path $RunDirectory 'gm-checks.json') -Raw | ConvertFrom-Json
    if (($checks.Check -join ',') -cne 'missing-token,wrong-token,invalid-id,unknown-id,delivered' -or
        ($checks.Status -join ',') -ne '401,401,400,404,200' -or ($checks.Id | Sort-Object -Unique).Count -ne 5)
    {
        throw 'Incomplete GM HTTP checks.'
    }

    foreach ($check in $checks)
    {
        $events = @($records | Where-Object ([TGame.ServerLogFields]::Id) -ceq $check.Id)
        $audit = @($events | Where-Object { $_.role -eq ([TGame.ProcessType]::GM.ToString()) -and $_.ev -ceq ([TGame.ServerLogEvents]::GMCommand) -and $_.peer -ceq $check.Actor -and
                $_.detail -ceq "ping $($check.Status)" -and $_.entity -ceq $check.Entity })
        $path = switch ($check.Status)
        {
            ([int][Net.HttpStatusCode]::OK)
            {
                ([TGame.ProcessType]::GM.ToString()),([TGame.ProcessType]::Router.ToString()),([TGame.ProcessType]::Scene.ToString())
            }

            ([int][Net.HttpStatusCode]::NotFound)
            {
                ([TGame.ProcessType]::GM.ToString()),([TGame.ProcessType]::Router.ToString())
            }

            default
            {
                ([TGame.ProcessType]::GM.ToString())
            }
        }

        if ($audit.Count -ne 1 -or $events.Count -ne (1 + 4 * ($path.Count - 1)))
        {
            throw "GM audit mismatch for $($check.Check)."
        }

        $pingTx = [TGame.ServerLogEvents]::GMPingTx
        $pingRx = [TGame.ServerLogEvents]::GMPingRx
        $pongTx = [TGame.ServerLogEvents]::GMPongTx
        $pongRx = [TGame.ServerLogEvents]::GMPongRx
        if ($events.ev -contains ([TGame.ServerLogEvents]::PingTx))
        {
            # Historical logs used the diagnostic protocol for this management command.
            $pingTx = [TGame.ServerLogEvents]::PingTx
            $pingRx = [TGame.ServerLogEvents]::PingRx
            $pongTx = [TGame.ServerLogEvents]::PongTx
            $pongRx = [TGame.ServerLogEvents]::PongRx
        }

        for ($i = 1; $i -lt $path.Count; $i++)
        {
            $src,$dst = $path[$i-1],$path[$i]
            foreach ($expected in @(
                "$pingTx $src $dst"
                "$pingRx $dst $src"
                "$pongTx $dst $src"
                "$pongRx $src $dst"
            ))
            {
                $kind,$sender,$peer = $expected.Split(' ')
                if (@($events | Where-Object { $_.ev -ceq $kind -and $_.role -eq $sender -and $_.peer -eq $peer -and
                            $_.entity -ceq $check.Entity -and $_.link -ceq $activeLinks["$src/$dst"] }).Count -ne 1)
                {
                    throw "Missing GM hop $expected."
                }
            }
        }

        "GM PASS $($check.Check) HTTP $($check.Status) id=$($check.Id)"
    }
}

$summary = "PASS processes=$($roles.Count) grpc_links=$($pairs.Count) wss_sessions=1 rounds=2 pong=$($cases.Count) mongo_ping=2"
if ($hasGM)
{
    $summary += ' gm_http=5'
}

$summary
