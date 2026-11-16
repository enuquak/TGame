#requires -Version 7.0

if ('TGame.ServerGroup' -as [type])
{
    return
}

$sharedDirectory = Join-Path $PSScriptRoot '../../../code/shared/csharp'
$sources = @((Get-ChildItem -LiteralPath $sharedDirectory -Filter '*.cs').FullName)
$sources += Join-Path $PSScriptRoot '../../../code/shared/configuration/ServerGroup.cs'
Add-Type -Path $sources
