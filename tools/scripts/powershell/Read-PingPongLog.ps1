#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z]+[0-9]*$')][string]$Role,
    [string]$Event
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Import-ServerTypes.ps1')

$recordPrefix = '{"' + [TGame.ServerLogFields]::Time + '":'
$eventFilter = '"' + [TGame.ServerLogFields]::Event + '":"' + $Event + '"'
$layout = $null
$manifest = Join-Path $RunDirectory 'server-logs.json'
if ($Role -ne [TGame.ServerPeerNames]::Client -and (Test-Path -LiteralPath $manifest))
{
    $layout = Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json
    $identity = $layout.processes.$Role
    if ($null -eq $identity -or $identity.pid -le 0 -or $identity.startedUtcTicks -le 0)
    {
        throw "Invalid log manifest for $Role."
    }

    $directory = Join-Path $RunDirectory $layout.serverRoot ("${Role}Server")
    if (!(Test-Path -LiteralPath $directory -PathType Container))
    {
        return
    }

    $files = Get-ChildItem -LiteralPath $directory -File -Filter "$($identity.pid)-$($identity.startedUtcTicks)-*.log" |
        Sort-Object { $_.BaseName.Split('_')[0] }, { [int]$_.BaseName.Split('_')[1] }
}
else
{
    # Test client stdout and server logs from before the rolling-file layout.
    $files = Get-Item -LiteralPath (Join-Path $RunDirectory "$Role.log")
}

foreach ($file in $files)
{
    $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite, Delete')
    $reader = [IO.StreamReader]::new($stream)

    try
    {
        while ($null -ne ($line = $reader.ReadLine()))
        {
            if (!$line.StartsWith($recordPrefix))
            {
                continue
            }

            if ($Event -and !$line.Contains($eventFilter))
            {
                continue
            }

            $row = $line | ConvertFrom-Json
            if (!$row.instance)
            {
                $row | Add-Member -NotePropertyName instance -NotePropertyValue $row.role
            }

            if ($row.instance -cne $Role -or ($layout -and $row.pid -ne $identity.pid))
            {
                throw "Unexpected process in $($file.FullName)."
            }

            $row
        }
    }
    finally
    {
        $reader.Dispose()
    }
}
