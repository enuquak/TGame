#requires -Version 7.0

if ('TGame.ServerLogEvents' -as [type])
{
    return
}

$sharedDirectory = Join-Path $PSScriptRoot '../../../code/shared/csharp'
Add-Type -Path (Get-ChildItem -LiteralPath $sharedDirectory -Filter '*.cs').FullName
