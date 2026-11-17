#requires -Version 7.0

if ('TGame.ServerGroup' -as [type])
{
    return
}

$engineDirectory = Join-Path $PSScriptRoot '../../../code/server/engine'
$sources = @((Get-ChildItem -LiteralPath (Join-Path $engineDirectory 'configuration') -Filter '*.cs').FullName)
$sources += Join-Path $engineDirectory 'identifiers/IdText.cs'
$sources += Join-Path $engineDirectory 'logging/ServerLogProtocol.cs'
$sources += Join-Path $PSScriptRoot '../../../code/shared/csharp/ServerEnvironment.cs'
Add-Type -Path $sources
