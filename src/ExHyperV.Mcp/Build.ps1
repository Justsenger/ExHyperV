#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('win-arm64', 'win-x64')][string]$Runtime,
    [string]$OutputDirectory,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$Runtime) {
    $architecture = $env:PROCESSOR_ARCHITEW6432
    if (!$architecture) { $architecture = $env:PROCESSOR_ARCHITECTURE }
    $Runtime = switch ($architecture) {
        'ARM64' { 'win-arm64' }
        'AMD64' { 'win-x64' }
        default { throw 'MCP 发布支持 Windows ARM64 和 x64。' }
    }
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ".artifacts/mcp/$Runtime" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw '请安装 VS 2026 或对应 Build Tools，以构建 Core 的 COM 引用。' }
$msbuild = @(& $vswhere -latest -products '*' -version '[18.0,19.0)' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe') | Select-Object -First 1
if (!$msbuild) { throw '请安装 VS 2026 MSBuild。' }
$selfContained = (!$FrameworkDependent).ToString().ToLowerInvariant()
$build = Join-Path $repo ".artifacts/mcp/build/$Runtime"
& $msbuild (Join-Path $PSScriptRoot 'ExHyperV.Mcp.csproj') /restore /t:Publish /nologo /nr:false `
    /p:Configuration=Release "/p:RuntimeIdentifier=$Runtime" "/p:SelfContained=$selfContained" `
    /p:RestoreLockedMode=true /p:UseArtifactsOutput=true "/p:ArtifactsPath=$build" `
    "/p:PublishDir=$OutputDirectory" '/clp:ErrorsOnly;Summary'
if ($LASTEXITCODE -ne 0) { throw "MCP 构建失败，退出码 $LASTEXITCODE。" }
Write-Host (Join-Path $OutputDirectory 'ExHyperV.Mcp.exe')
