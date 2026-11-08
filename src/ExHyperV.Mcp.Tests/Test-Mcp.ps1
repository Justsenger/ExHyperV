#Requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('win-arm64', 'win-x64')][string]$Runtime)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$Runtime) {
    $architecture = $env:PROCESSOR_ARCHITEW6432
    if (!$architecture) { $architecture = $env:PROCESSOR_ARCHITECTURE }
    $Runtime = switch ($architecture) { 'ARM64' { 'win-arm64' }; 'AMD64' { 'win-x64' }; default { throw '测试支持 Windows ARM64 和 x64。' } }
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$msbuild = @(& $vswhere -latest -products '*' -version '[18.0,19.0)' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe') | Select-Object -First 1
if (!$msbuild) { throw '请安装 VS 2026 MSBuild 和 .NET 10 SDK。' }
$artifacts = Join-Path $repo ".artifacts/mcp/tests/$Runtime"
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
& $msbuild (Join-Path $PSScriptRoot 'ExHyperV.Mcp.Tests.csproj') /restore /t:Build /nologo /nr:false `
    /p:Configuration=Release "/p:RuntimeIdentifier=$Runtime" /p:RestoreLockedMode=true `
    /p:UseArtifactsOutput=true "/p:ArtifactsPath=$artifacts" "/flp:logfile=$artifacts/build.log;verbosity=normal" '/clp:ErrorsOnly;Summary'
if ($LASTEXITCODE -ne 0) { throw "MCP 测试构建失败：$LASTEXITCODE" }
$test = Join-Path $artifacts "bin/ExHyperV.Mcp.Tests/release_$Runtime/ExHyperV.Mcp.Tests.exe"
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
& $test --progress off --report-trx --results-directory (Join-Path $artifacts 'results')
if ($LASTEXITCODE -ne 0) { throw "MCP 测试失败：$LASTEXITCODE" }
