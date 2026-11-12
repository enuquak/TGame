#requires -Version 7.0
param([Parameter(Mandatory)][string]$SourceRunDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Import-ServerTypes.ps1')

$root = (Resolve-Path "$PSScriptRoot/../../..").Path
$destination = Join-Path $root "build/log-compatibility/$([guid]::NewGuid().ToString('N'))"
$roles = @([TGame.ProcessType]::Gate, [TGame.ProcessType]::Login, [TGame.ProcessType]::Base,
    [TGame.ProcessType]::Scene, [TGame.ProcessType]::Router, [TGame.ProcessType]::DBMgr) | ForEach-Object ToString
$baseRole = [TGame.ProcessType]::Base.ToString()
$routerRole = [TGame.ProcessType]::Router.ToString()
$client = [TGame.ServerPeerNames]::Client
$cases = @(Get-Content -LiteralPath (Join-Path $SourceRunDirectory 'cases.json') -Raw | ConvertFrom-Json)
$records = @(foreach ($role in ($roles + $client))
    {
        & "$PSScriptRoot/Read-PingPongLog.ps1" $SourceRunDirectory $role
    })

$baseCase = $cases | Where-Object { !$_.ProfileAction -and $_.Path[-1] -eq $baseRole -and $_.Path -contains $routerRole } | Select-Object -First 1
if ($cases.Count -lt 13 -or !$baseCase)
{
    throw 'Use a successful current single-instance protocol smoke run as the source.'
}

$failures = @()
foreach ($caseCount in 12,13)
{
    $folder = Join-Path $destination $caseCount
    $null = New-Item -ItemType Directory -Path $folder -Force
    $selected = @($cases | Select-Object -First 12)
    if ($caseCount -eq 13)
    {
        $selected += $baseCase
    }

    $selected | ConvertTo-Json -Depth 6 -AsArray | Set-Content -LiteralPath (Join-Path $folder 'cases.json')
    foreach ($role in ($roles + $client))
    {
        $lines = @(foreach ($source in ($records | Where-Object role -EQ $role))
            {
                if ($source.id -and $source.id -cnotin $selected.Id)
                {
                    continue
                }

                if ($source.ev -in ([TGame.ServerLogEvents]::LinkUp),([TGame.ServerLogEvents]::LinksLost),([TGame.ServerLogEvents]::EntityRoute) -and
                    $source.peer -and $source.peer -cnotin $roles)
                {
                    continue
                }

                if ($source.ev -eq [TGame.ServerLogEvents]::SinglePointReady)
                {
                    continue
                }

                $row = $source | ConvertTo-Json -Depth 6 | ConvertFrom-Json
                if ($row.ev -eq [TGame.ServerLogEvents]::SinglePointInit)
                {
                    if ($caseCount -ne 13 -or $role -ne $baseRole)
                    {
                        continue
                    }

                    $row.ev = [TGame.ServerLogEvents]::LegacyBaseSinglePointInit
                }

                if ($row.ev -eq [TGame.ServerLogEvents]::LinksReady)
                {
                    $row.ev = [TGame.ServerLogEvents]::LegacyReady
                }

                $row.PSObject.Properties.Remove([TGame.ServerLogFields]::Instance)
                $row | ConvertTo-Json -Depth 6 -Compress
            })
        $lines | Set-Content -LiteralPath (Join-Path $folder "$role.log")
    }

    try
    {
        & "$PSScriptRoot/Test-PingPongLogs.ps1" -RunDirectory $folder > (Join-Path $folder 'result.log')
        Write-Output "PASS historical $caseCount-case logs"
    }
    catch
    {
        $failures += "Historical $caseCount-case logs: $($_.Exception.Message)"
    }
}

if ($failures.Count)
{
    throw ($failures -join [Environment]::NewLine)
}
