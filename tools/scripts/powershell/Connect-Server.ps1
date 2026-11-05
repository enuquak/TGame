#requires -Version 7.0
# Implementation entry; use ../Connect-Server.bat for interactive operation.
[CmdletBinding(DefaultParameterSetName = 'Interactive')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Command')]
    [ValidateNotNullOrEmpty()]
    [string]$RemoteCommand,

    [Parameter(Mandatory = $true, ParameterSetName = 'Check')]
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$sshExecutable = (Get-Command ssh -CommandType Application -ErrorAction Stop).Source
$sshConfigPath = Join-Path $env:USERPROFILE '.ssh\config'

if (-not (Test-Path -LiteralPath $sshConfigPath -PathType Leaf)) {
    throw "SSH config not found: $sshConfigPath. See the server connection guide in docs."
}

$sshArguments = @(
    '-F', $sshConfigPath,
    '-o', 'StrictHostKeyChecking=yes',
    '-o', 'ConnectTimeout=15'
)

if ($PSCmdlet.ParameterSetName -ne 'Interactive') {
    $sshArguments += @('-o', 'BatchMode=yes', '-T')
}

$sshArguments += 'TGame-server'

if ($PSCmdlet.ParameterSetName -eq 'Check') {
    $sshArguments += 'id -un && hostname && uname -srm && cat /etc/os-release'
}
elseif ($PSCmdlet.ParameterSetName -eq 'Command') {
    $sshArguments += $RemoteCommand
}

& $sshExecutable @sshArguments
exit $LASTEXITCODE
