#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z]+$')][string]$Role,
    [string]$Event
)
$ErrorActionPreference = 'Stop'
$layout = $null
$manifest = Join-Path $RunDirectory 'server-logs.json'
if ($Role -ne 'Client' -and (Test-Path -LiteralPath $manifest)) {
    $layout = Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json
    if ($layout.runId -cnotmatch '^[0-9a-f]{32}$' -or $Role -cnotin $layout.roles) { throw "Invalid log manifest for $Role." }
    $directory = Join-Path $RunDirectory $layout.serverRoot ("${Role}Server")
    if (!(Test-Path -LiteralPath $directory -PathType Container)) { return }
    $files = Get-ChildItem -LiteralPath $directory -File -Filter "$($layout.runId)-*.log" |
        Sort-Object { $_.BaseName.Split('_')[0] }, { [int]$_.BaseName.Split('_')[1] }
} else {
    # Test client stdout and server logs from before the rolling-file layout.
    $files = Get-Item -LiteralPath (Join-Path $RunDirectory "$Role.log")
}
foreach ($file in $files) {
    $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite, Delete')
    $reader = [IO.StreamReader]::new($stream)
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            if (!$line.StartsWith('{"time":')) { continue }
            if ($Event -and !$line.Contains('"ev":"' + $Event + '"')) { continue }
            $row = $line | ConvertFrom-Json
            if ($row.role -cne $Role -or ($layout -and $row.runId -cne $layout.runId)) {
                throw "Unexpected process/run in $($file.FullName)."
            }
            $row
        }
    } finally { $reader.Dispose() }
}
