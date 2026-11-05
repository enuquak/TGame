#requires -Version 7.0
# Regression: the BAT entry selects PowerShell 7 even when launched from Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'Start-EnvironmentTunnel.ps1'
$output = & pwsh.exe -NoProfile -NonInteractive -File $script -Action Status 2>&1
if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'TGame SSH tunnel (running|is not running)') {
    throw "PowerShell 7 returned no tunnel status: $output"
}
Write-Output 'PowerShell 7 status: PASS'
$bat = Join-Path (Split-Path $PSScriptRoot) 'Start-EnvironmentTunnel.bat'
$command = "& '$($bat.Replace("'", "''"))' -Action Status; exit `$LASTEXITCODE"
$output = "`n" | & powershell.exe -NoProfile -NonInteractive -Command $command 2>&1
if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'TGame SSH tunnel (running|is not running)') {
    throw "BAT entry from PowerShell 5.1 failed: $output"
}
Write-Output 'BAT entry from PowerShell 5.1: PASS'

$command = "& '$($bat.Replace("'", "''"))' -Action Invalid; exit `$LASTEXITCODE"
$output = "`n" | & powershell.exe -NoProfile -NonInteractive -Command $command 2>&1
if ($LASTEXITCODE -eq 0) { throw 'BAT entry lost the invalid-argument failure exit code.' }
Write-Output 'BAT failure exit code: PASS'

# A relocated script must read the existing repository state, even from another working directory.
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("tgame-tunnel-layout-" + [guid]::NewGuid())
$fixtureScript = Join-Path $fixtureRoot 'tools/scripts/powershell/Start-EnvironmentTunnel.ps1'
try {
    New-Item -ItemType Directory -Force -Path (Split-Path $fixtureScript), (Join-Path $fixtureRoot '.local') | Out-Null
    Copy-Item -LiteralPath $script -Destination $fixtureScript
    Set-Content -LiteralPath (Join-Path $fixtureRoot '.local/environment-tunnel.json') -Value '{invalid-json'
    $output = & pwsh.exe -NoProfile -NonInteractive -File $fixtureScript -Action Status 2>&1
    if ($LASTEXITCODE -eq 0) { throw 'Relocated script ignored the repository state file.' }
    if (Test-Path -LiteralPath (Join-Path $fixtureRoot 'tools/.local')) {
        throw 'Relocated script created state under tools instead of the repository root.'
    }
    Write-Output 'Relocated repository state lookup: PASS'
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $tempPrefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedFixture.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedFixture) -notlike 'tgame-tunnel-layout-*') {
        throw 'Refusing to remove a fixture outside the expected temporary directory.'
    }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
