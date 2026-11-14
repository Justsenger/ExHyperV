#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)][int]$Port = 5187,
    [string]$Configuration,
    [switch]$NoBrowser,
    [switch]$ReportOnly,
    [switch]$BuildOnly,
    [switch]$Deployment,
    [Parameter(DontShow)][switch]$Elevated
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $OutputEncoding
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repo = [IO.Path]::GetFullPath((Join-Path $projectDirectory '../..'))
$pagePath = [IO.Path]::GetFullPath((Join-Path $projectDirectory 'ResourceInspection/Page.html'))
$defaultConfiguration = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ExHyperV.Core.Tests/resources.json'
$expectedConfiguration = if ($Configuration) { [IO.Path]::GetFullPath($Configuration) } else { $defaultConfiguration }
function Get-InspectionValue([string]$Route) {
    try {
        $request = [Net.WebRequest]::Create("http://127.0.0.1:$Port$Route")
        $request.Proxy = $null
        $request.Timeout = 2000
        $request.ReadWriteTimeout = 2000
        $response = $request.GetResponse()
        try {
            $reader = [IO.StreamReader]::new($response.GetResponseStream())
            try { return ($reader.ReadToEnd() | ConvertFrom-Json) }
            finally { $reader.Dispose() }
        } finally { $response.Dispose() }
    } catch { return $null }
}
function Get-InspectionSession { Get-InspectionValue '/api/session' }
function Test-InspectionSession($Session) {
    if (!$Session -or !$Session.supervised -or ([bool]$Session.deployment -ne [bool]$Deployment) -or
        ![string]::Equals($Session.pagePath, $pagePath, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    # Older default launches predate the configuration identity in session metadata.
    $actualConfiguration = if ($Session.configurationPath) { $Session.configurationPath } else { $defaultConfiguration }
    return [string]::Equals($actualConfiguration, $expectedConfiguration, [StringComparison]::OrdinalIgnoreCase)
}
function Update-PreviousInspection($Session) {
    if ($Session.automaticRebootForNewRuns) { return }
    $active = Get-InspectionValue '/api/tests'
    if ($active -and $active.running) {
        Write-Host '当前轮次保持原版本；结束后再次打开“启动测试”将更新程序。'
        return
    }
    Write-Host '正在更新测试程序…'
    # The service also checks idleness when accepting the update, covering a concurrent test start.
    & (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') -NoProfile -ExecutionPolicy RemoteSigned `
        -File (Join-Path $PSScriptRoot 'Update-Inspection.ps1') -Port $Port
    if ($LASTEXITCODE -ne 0) { throw '测试程序更新失败，请查看上方提示。' }
    $updated = Get-InspectionSession
    if (!(Test-InspectionSession $updated) -or !$updated.automaticRebootForNewRuns) { throw '当前页面仍是旧版，请查看启动器编译结果。' }
}
# A repeated interactive launch opens this checkout's existing page.
$existingSession = Get-InspectionSession
if (!$BuildOnly -and !$ReportOnly -and !$Deployment -and !$Configuration -and !$Elevated -and
    (Test-InspectionSession $existingSession)) {
    Update-PreviousInspection $existingSession
    if (!$NoBrowser) { Start-Process "http://127.0.0.1:$Port/" }
    exit 0
}
# Interactive inspection owns an elevated supervisor, so updates retain the same token.
# Build-only and report-only calls keep the caller's identity for automation.
if (!$BuildOnly -and !$ReportOnly) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $admin = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    finally { $identity.Dispose() }
    if (!$admin) {
        if ($Elevated) { throw '管理员启动未取得所需权限，请检查 Windows 提权设置。' }
        $configurationPath = if ($Configuration) { [IO.Path]::GetFullPath($Configuration) }
            else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ExHyperV.Core.Tests/resources.json' }
        $logDirectory = Join-Path $repo '.artifacts/resource-inspection'
        $null = New-Item -ItemType Directory -Path $logDirectory -Force
        $logPath = Join-Path $logDirectory ('launcher-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
        # Quote PowerShell literals before encoding; paths may contain spaces or apostrophes.
        $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Elevated -Port " + $Port +
            " -Configuration '" + $configurationPath.Replace("'", "''") + "'"
        if ($NoBrowser) { $command += ' -NoBrowser' }
        if ($Deployment) { $command += ' -Deployment' }
        $command += " *> '" + $logPath.Replace("'", "''") + "'"
        if (!$NoBrowser) {
            $safeLog = $logPath.Replace("'", "''")
            $message = "启动失败，请查看日志：`n$logPath".Replace("'", "''")
            $command = "try { $command; if (`$LASTEXITCODE -ne 0) { throw ('启动进程退出：' + `$LASTEXITCODE) } } " +
                "catch { `$_ | Out-String | Add-Content -LiteralPath '$safeLog'; " +
                "Add-Type -AssemblyName System.Windows.Forms; " +
                "[System.Windows.Forms.MessageBox]::Show('$message', 'Core 测试启动失败') | Out-Null; exit 1 }"
        }
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        Write-Host '正在请求管理员启动，请在 Windows 提示中选择“是”…'
        $worker = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') `
            -Verb RunAs -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'RemoteSigned', '-EncodedCommand', $encoded) -PassThru
        Write-Host '正在准备测试页面，首次编译可能需要几分钟，请保留此窗口…'
        $started = [DateTime]::UtcNow
        try {
            while ($true) {
                if (Test-InspectionSession (Get-InspectionSession)) {
                    Write-Progress -Activity '正在启动 Core 测试' -Completed
                    Write-Host "测试页面已就绪：http://127.0.0.1:$Port/"
                    exit 0
                }
                if ($worker.HasExited) { throw "启动进程已退出，请查看日志：$logPath" }
                $elapsed = [int]([DateTime]::UtcNow - $started).TotalSeconds
                Write-Progress -Activity '正在启动 Core 测试' -Status "正在编译并启动页面，已等待 $elapsed 秒"
                Start-Sleep -Milliseconds 500
            }
        } finally {
            Write-Progress -Activity '正在启动 Core 测试' -Completed
            $worker.Dispose()
        }
    }
}
# Windows PowerShell uses .NET Framework. Read the native architecture without
# depending on RuntimeInformation, including a 32-bit / emulated shell.
$nativeArchitecture = $env:PROCESSOR_ARCHITEW6432
if ([string]::IsNullOrWhiteSpace($nativeArchitecture)) { $nativeArchitecture = $env:PROCESSOR_ARCHITECTURE }
$runtime = switch ($nativeArchitecture) {
    'Arm64' { 'win-arm64' }
    'AMD64' { 'win-x64' }
    default { throw '资源检查目前只支持 Windows ARM64 / x64。' }
}
# Keep generated MSBuild paths below the Windows PowerShell path limit, even
# when the checkout lives inside a deeply nested worktree. Separate checkouts
# retain separate build slots.
$hasher = [Security.Cryptography.SHA256]::Create()
try { $buildKey = ([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($repo.ToUpperInvariant())))).Replace('-', '').Substring(0, 16) }
finally { $hasher.Dispose() }
$build = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "ExHyperV.Core.Tests/Build/$buildKey/$runtime"
$reports = Join-Path $repo '.artifacts/resource-inspection/reports'
$launchLock = $null
try {
if (!$BuildOnly -and !$ReportOnly) {
    # Hold this stream for the complete supervisor lifetime, including rebuilds.
    # Different ports use separate output slots; a crashed process releases its lock.
    $build = Join-Path $build "page-$Port"
    $null = New-Item -ItemType Directory -Path $build -Force
    $lockPath = Join-Path $build 'launcher.lock'
    while (!$launchLock) {
        if (Test-InspectionSession (Get-InspectionSession)) {
            if (!$NoBrowser) { Start-Process "http://127.0.0.1:$Port/" }
            exit 0
        }
        try { $launchLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException] {
            if (($_.Exception.HResult -band 0xffff) -notin @(32, 33)) { throw }
            Write-Progress -Activity '正在启动 Core 测试' -Status '首次启动正在进行，等待页面就绪…'
            Start-Sleep -Milliseconds 500
        }
    }
    # The first launcher can publish its session between the probe and lock acquisition.
    if (Test-InspectionSession (Get-InspectionSession)) {
        if (!$NoBrowser) { Start-Process "http://127.0.0.1:$Port/" }
        exit 0
    }
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw '未找到 Visual Studio 安装器。请先准备 VS 2026 / Build Tools。' }
$msbuild = @(& $vswhere -latest -products '*' -version '[18.0,19.0)' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe') | Select-Object -First 1
if (!$msbuild) { throw '未找到 VS 2026 MSBuild。' }

$previousModule = $null
$slot = 0
$firstStart = $true
if ($Configuration) { $Configuration = [IO.Path]::GetFullPath($Configuration) }
while ($true) {
    # Separate slots keep the last working build intact if an update fails.
    $output = if ($BuildOnly -or $ReportOnly) { $build } else { Join-Path $build "host-$slot" }
    Write-Host '正在编译最新 Core 和测试程序…'
    Push-Location $projectDirectory
    try {
        & $msbuild 'ExHyperV.Core.Tests.csproj' /restore /t:Build /nologo /nr:false `
            /p:Configuration=Release "/p:RuntimeIdentifier=$runtime" /p:UseArtifactsOutput=true "/p:ArtifactsPath=$output" '/clp:ErrorsOnly;Summary'
        $buildExit = $LASTEXITCODE
    }
    finally { Pop-Location }
    $updateError = $null
    $builtModule = Join-Path $output "bin/ExHyperV.Core.Tests/release_$runtime/ExHyperV.Core.Tests.exe"
    if ($buildExit -eq 0 -and (Test-Path -LiteralPath $builtModule)) {
        $module = $builtModule
        $previousModule = $module
        $slot = 1 - $slot
    } elseif ($previousModule) {
        $module = $previousModule
        $updateError = '更新构建失败，已继续运行上一版。'
        Write-Warning $updateError
    } else { throw "编译失败（$buildExit），请检查上方错误。" }

    if ($BuildOnly) { Write-Host '编译完成。'; exit 0 }
    $arguments = @('--inspect-resources', '--port', $Port, '--reports', $reports)
    if ($Configuration) { $arguments += @('--configuration', $Configuration) }
    if ($NoBrowser -or !$firstStart) { $arguments += '--no-browser' }
    if ($ReportOnly) { $arguments += '--report-only' }
    else { $arguments += @('--supervised', '--page', (Join-Path $projectDirectory 'ResourceInspection/Page.html')) }
    if ($updateError) { $arguments += @('--update-error', $updateError) }
    if ($Deployment) { $arguments += '--deployment' }
    & $module @arguments
    $runExit = $LASTEXITCODE
    if ($ReportOnly -or $runExit -ne 75) { exit $runExit }
    $firstStart = $false
    Write-Host '正在更新测试程序，浏览器将自动恢复连接…'
}
} finally {
    Write-Progress -Activity '正在启动 Core 测试' -Completed
    if ($launchLock) { $launchLock.Dispose() }
}
