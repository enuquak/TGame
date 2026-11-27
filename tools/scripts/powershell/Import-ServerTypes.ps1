#requires -Version 7.0

if ('TGame.ServerGroup' -as [type])
{
    return
}

$engineDirectory = Join-Path $PSScriptRoot '../../../code/server/engine'
$project = [xml](Get-Content -LiteralPath (Join-Path $engineDirectory 'TGame.Server.Engine.csproj') -Raw)
$version = ($project.Project.ItemGroup.PackageReference | Where-Object Include -eq 'YamlDotNet').Version
$packages = $env:NUGET_PACKAGES
if ([string]::IsNullOrWhiteSpace($packages))
{
    $packages = Join-Path $env:USERPROFILE '.nuget/packages'
}
$yamlAssembly = Join-Path $packages "yamldotnet/$version/lib/net8.0/YamlDotNet.dll"
if (!(Test-Path -LiteralPath $yamlAssembly))
{
    throw '缺少 YAML 依赖，请先运行 dotnet restore code/server/engine/TGame.Server.Engine.csproj。'
}
Add-Type -Path $yamlAssembly
$sources = @('ProcessType.cs', 'ServerGroup.cs', 'ServerRunFiles.cs', 'ServerConfiguration.cs', 'EngineEnvironment.cs', 'MongoStorageSettings.cs', 'RedisStorageSettings.cs' | ForEach-Object {
    Join-Path $engineDirectory "configuration/$_"
})
$sources += Join-Path $engineDirectory 'identifiers/IdText.cs'
$sources += Join-Path $engineDirectory 'identifiers/IdGeneratorOptions.cs'
$sources += Join-Path $engineDirectory 'ZooKeeperOptions.cs'
$sources += Join-Path $engineDirectory 'ZooKeeperPath.cs'
$sources += Join-Path $engineDirectory 'logging/ServerLogLevel.cs'
$sources += Join-Path $engineDirectory 'logging/ServerLogProtocol.cs'
$references = @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
$references += $yamlAssembly
# YamlDotNet 面向 .NET 8，PowerShell 可在更新运行时加载它；忽略该引用版本统一提示。
Add-Type -Path $sources -ReferencedAssemblies $references -CompilerOptions '/nullable:enable', '/nowarn:1701' -ErrorAction Stop
