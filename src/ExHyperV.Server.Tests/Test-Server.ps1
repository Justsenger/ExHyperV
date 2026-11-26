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
if (!$msbuild) { throw '请安装 VS 2026 MSBuild 和 .NET 11 SDK。' }
$artifacts = Join-Path $repo ".cache/server/tests/$Runtime"
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
& $msbuild (Join-Path $PSScriptRoot 'ExHyperV.Server.Tests.csproj') /restore /t:Build /nologo /nr:false `
    /p:Configuration=Release "/p:RuntimeIdentifier=$Runtime" /p:RestoreLockedMode=true /p:NuGetAudit=false `
    /p:UseArtifactsOutput=true "/p:ArtifactsPath=$artifacts" "/flp:logfile=$artifacts/build.log;verbosity=normal" '/clp:ErrorsOnly;Summary'
if ($LASTEXITCODE -ne 0) { throw "Server 测试构建失败：$LASTEXITCODE" }
$test = Join-Path $artifacts "bin/ExHyperV.Server.Tests/release_$Runtime/ExHyperV.Server.Tests.exe"
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
$results = Join-Path $artifacts 'results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$arguments = @('--progress', 'off', '--no-ansi', '--minimum-expected-tests', '88', '--zero-tests-policy', 'strict', '--results-directory', $results)
# Windows PowerShell represents native stderr as ErrorRecord when redirecting it.
# Preserve output from both streams and let the test process decide success.
function Invoke-ServerTests([string[]]$TestArguments, [string]$Log) {
    $ErrorActionPreference = 'Continue'
    & $test @TestArguments 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $Log | Out-Host
    return $LASTEXITCODE
}
$log = Join-Path $results 'test.log'
# TRX uses a second process. Probe the local pipe before starting the controller,
# whose default connection timeout is five minutes even on an access denial.
function Test-ReportPipe {
    $name = 'ExHyperV.Server.Tests.' + [Guid]::NewGuid().ToString('N')
    $server = $null
    $client = $null
    try {
        $server = [IO.Pipes.NamedPipeServerStream]::new($name)
        $client = [IO.Pipes.NamedPipeClientStream]::new('.', $name, [IO.Pipes.PipeDirection]::InOut)
        $client.Connect(1000)
        return $true
    } catch [UnauthorizedAccessException] {
        return $false
    } finally {
        if ($client) { $client.Dispose() }
        if ($server) { $server.Dispose() }
    }
}
$report = Test-ReportPipe
if ($report) {
    $testExit = Invoke-ServerTests ($arguments + '--report-trx') $log
} else {
    Write-Host '当前环境限制本地命名管道，使用控制台报告运行全部测试。'
    $testExit = Invoke-ServerTests $arguments $log
}
if ($report -and $testExit -ne 0) {
    $output = Get-Content -Raw -LiteralPath $log
    # The TRX controller failed before discovery. Run the same selection in process.
    if ($output -match 'System\.UnauthorizedAccessException' -and $output -match 'NamedPipeClientStream\.TryConnect' -and
        $output -match 'ConnectToTestHostProcessMonitorIfAvailableAsync' -and $output -notmatch '(?im)^\s*(?:Test run summary:|测试运行摘要[:：]|(?:failed|passed|skipped|失败|成功|已跳过)\s)') {
        Write-Warning 'TRX 报告进程无法连接本地命名管道，改用同一测试程序集的控制台报告。'
        $testExit = Invoke-ServerTests $arguments (Join-Path $results 'test-console.log')
    }
}
if ($testExit -ne 0) { throw "Server 测试失败：$testExit" }
