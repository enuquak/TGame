#requires -Version 7.0
param([Parameter(Mandatory)][string]$Path)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Import-ServerTypes.ps1')

$counts = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
$types = [Enum]::GetNames([TGame.ProcessType])
if ($counts -isnot [System.Collections.IDictionary] -or $counts.Count -ne $types.Count)
{
    throw 'Server group configuration must specify a count for every process type.'
}

$instances = @(foreach ($role in $types)
    {
        $count = $counts[$role]
        if ($role -cnotin $counts.Keys -or $count -isnot [long] -or $count -lt 0 -or $count -gt [int]::MaxValue)
        {
            throw "Invalid or missing process count for $role."
        }

        for ($number = 1; $number -le $count; $number++)
        {
            $name = $role
            if ($number -gt 1)
            {
                $name += $number
            }

            [pscustomobject]@{ Name = $name; Role = $role; Number = $number }
        }
    })

if ($instances.Count -eq 0)
{
    throw 'The server group must contain at least one process.'
}

$instances
