#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Revision,
    [ValidatePattern('^[0-9a-f]{32}$')][string]$RunId = ([guid]::NewGuid().ToString('N')),
    [string]$Configuration,
    [string[]]$CheckId,
    [switch]$IncludeRebootTests,
    [switch]$SkipTemplatePreparation,
    [switch]$DirectNetwork,
    [switch]$Foreground
)
$ErrorActionPreference = 'Stop'
if ($DirectNetwork) {
    $env:NO_PROXY='*'
    foreach($key in 'HTTP_PROXY','HTTPS_PROXY','ALL_PROXY'){[Environment]::SetEnvironmentVariable($key,$null,'Process')}
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (!([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '自动测试需要已具备管理员权限的执行账户。请先完成本机部署。'
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root = [IO.Path]::GetFullPath($RunDirectory)
$parent = Split-Path $root -Parent
$null = New-Item -ItemType Directory -Path $parent -Force
$submitLock = [IO.File]::Open((Join-Path $parent 'submission.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
$manifest = Join-Path $root 'request.json'
if (Test-Path -LiteralPath $manifest) {
    $saved = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($saved.runId -ne $RunId -or $saved.sourceRevision -ne $Revision) { throw '该目录已有其他测试轮次。' }
    Write-Output "本轮已经提交，保留原执行；结果目录：$root"
    return
}
foreach ($peer in @(Get-ChildItem -LiteralPath $parent -Directory | Where-Object Name -Match '^[0-9a-f]{32}$')) {
    if ($peer.FullName -eq $root -or !(Test-Path (Join-Path $peer.FullName 'request.json'))) { continue }
    $peerStatus = Join-Path $peer.FullName 'status.json'
    $prior = if (Test-Path $peerStatus) { Get-Content $peerStatus -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    if (!$prior -or $prior.status -notin @('completed','failed','blocked')) {
        throw ('已有轮次执行或等待恢复：' + $peer.Name + '。请先处理其持久报告。')
    }
}
$git = Get-Command git -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if (!$git) {
    $candidates = @((Join-Path $env:ProgramFiles 'Git/cmd/git.exe')) + @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'GitHubDesktop') -Directory -Filter 'app-*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | ForEach-Object { Join-Path $_.FullName 'resources/app/git/cmd/git.exe' })
    $git = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$git) { throw '缺少 Git，请先安装 Git for Windows 或 GitHub Desktop。' }
$env:PATH = (Split-Path $git -Parent) + ';' + $env:PATH
$actual = (& $git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actual -ne $Revision) { throw '源码提交与请求相异。' }
$changes = @(& $git -C $repo status --porcelain -- src/ExHyperV.Core src/ExHyperV.Core.Tests)
if ($LASTEXITCODE -ne 0 -or $changes.Count) { throw 'Core 或测试源码存在未提交变更，请先准备固定提交的工作副本。' }
if (!$Configuration) { $Configuration = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ExHyperV.Core.Tests/resources.json' }
$null = New-Item -ItemType Directory -Path $root -Force
if (Test-Path -LiteralPath $Configuration) { Copy-Item -LiteralPath $Configuration -Destination (Join-Path $root 'resources.json') }
else { [IO.File]::WriteAllText((Join-Path $root 'resources.json'), '{}', [Text.UTF8Encoding]::new($false)) }
$configurationCopy = Join-Path $root 'resources.json'
$architecture = $env:PROCESSOR_ARCHITEW6432
if (!$architecture) { $architecture = $env:PROCESSOR_ARCHITECTURE }
$runtime = switch ($architecture) { 'ARM64' {'win-arm64'} 'AMD64' {'win-x64'} default {throw '仅支持 Windows ARM64 与 x64。'} }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw '请先准备 Visual Studio 2026 / Build Tools。' }
$msbuild = @(& $vswhere -latest -products '*' -version '[18.0,19.0)' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe') | Select-Object -First 1
if (!$msbuild) { throw '缺少 VS 2026 MSBuild。' }
$build = Join-Path $root 'build'
Push-Location $PSScriptRoot
try {
    & $msbuild ExHyperV.Core.Tests.csproj /restore /t:Build /nologo /nr:false /p:Configuration=Release `
        "/p:RuntimeIdentifier=$runtime" /p:UseArtifactsOutput=true "/p:ArtifactsPath=$build" /p:RestoreLockedMode=true `
        '/clp:ErrorsOnly;Summary' "/flp:LogFile=$root/build.log;Verbosity=normal"
    if ($LASTEXITCODE) { throw "构建失败：$LASTEXITCODE" }
} finally { Pop-Location }
$moduleDirectory = Join-Path $build "bin/ExHyperV.Core.Tests/release_$runtime"
$payload = Join-Path $root 'payload'
Copy-Item -LiteralPath $moduleDirectory -Destination $payload -Recurse
$exe = Join-Path $payload 'ExHyperV.Core.Tests.exe'
# The same fixed payload first checks its offline contracts, then runs real APIs.
$offline = Join-Path $root 'offline'
$null = New-Item -ItemType Directory -Path $offline -Force
$oldWindows = $env:EXHYPERV_TEST_WINDOWS
try {
    $env:EXHYPERV_TEST_WINDOWS = '1'
    & $exe --filter '(TestCategory=Regression|TestCategory=WindowsIntegration)' --results-directory $offline `
        --report-trx --report-trx-filename tests.trx --minimum-expected-tests 1 --timeout 3m --no-ansi --progress off
    $testExit = $LASTEXITCODE
} finally { $env:EXHYPERV_TEST_WINDOWS = $oldWindows }
$trx = Join-Path $offline 'tests.trx'
if (!(Test-Path -LiteralPath $trx)) { throw '自动回归缺少本轮 TRX 结果。' }
[xml]$offlineResults = Get-Content -LiteralPath $trx -Raw -Encoding UTF8
$counts = $offlineResults.TestRun.ResultSummary.Counters
$offlineSummary = @{passed=[int]$counts.passed;failed=[int]$counts.failed;total=[int]$counts.total;notExecuted=[int]$counts.notExecuted}
[IO.File]::WriteAllText((Join-Path $offline 'summary.json'), ($offlineSummary|ConvertTo-Json), [Text.UTF8Encoding]::new($false))
if ($testExit -or $offlineSummary.total -le 0 -or $offlineSummary.passed -ne $offlineSummary.total) { throw '自动回归尚未全部通过，实机步骤保留未执行。' }
$request = [ordered]@{
    runId=$RunId; sourceRevision=$Revision; configurationPath=$configurationCopy; sid=$identity.User.Value
    reportsRoot=(Join-Path $root 'reports'); coreSha256=(Get-FileHash (Join-Path $payload 'ExHyperV.Core.dll') -Algorithm SHA256).Hash
    includeRebootTests=[bool]$IncludeRebootTests; prepareTemplate=(!$SkipTemplatePreparation); directNetwork=[bool]$DirectNetwork; checkIds=$(if($CheckId){@($CheckId)}else{$null})
}
[IO.File]::WriteAllText($manifest, ($request | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
if ($Foreground) { & $exe --run-api-suite $manifest; exit $LASTEXITCODE }
$taskName = 'ExHyperV.Core.Tests.Automation.' + $RunId
$action = New-ScheduledTaskAction -Execute $exe -Argument ('--run-api-suite "' + $manifest + '"') -WorkingDirectory $payload
$principal = New-ScheduledTaskPrincipal -UserId $identity.User.Value -LogonType S4U -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
$task = New-ScheduledTask -Action $action -Principal $principal -Trigger (New-ScheduledTaskTrigger -AtStartup) -Settings $settings
Register-ScheduledTask -TaskName $taskName -InputObject $task -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
[IO.File]::WriteAllText((Join-Path $root 'launch.json'), (@{runId=$RunId;revision=$Revision;task=$taskName;runtime=$runtime;sid=$identity.User.Value;startedUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Output "已启动自动测试：$RunId"
Write-Output "结果目录：$root"
} finally { $submitLock.Dispose() }
