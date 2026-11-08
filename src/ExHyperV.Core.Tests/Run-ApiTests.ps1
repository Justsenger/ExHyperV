#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Url = 'http://127.0.0.1:5187',
    [string[]]$CheckId,
    [switch]$Attach,
    [string]$OutputDirectory,
    [ValidateRange(1, 1440)][int]$TimeoutMinutes = 240
)

$ErrorActionPreference = 'Stop'
$base = $Url.TrimEnd('/')
$client = New-Object System.Net.WebClient
$client.Proxy = $null
$client.Encoding = New-Object System.Text.UTF8Encoding($false)
function Read-Api([string]$Path) { $client.DownloadString($base + $Path) | ConvertFrom-Json }
try {
    if ($Attach) {
        $run = Read-Api '/api/tests'
        if (!$run -or !$run.runId) { throw '当前没有测试轮次可收集。' }
    } else {
        $session = Read-Api '/api/session'
        if ($session.restarting) { throw '测试程序正在更新，请等待更新完成。' }
        $resources = Read-Api '/api/report'
        if (!$resources.plan.available) { throw '请先完成资源检查。' }
        $html = $client.DownloadString($base + '/')
        $nonce = [regex]::Match($html, '<script nonce="([A-F0-9]+)">').Groups[1].Value
        if (!$nonce) { throw '测试页面校验令牌缺失。' }
        [string[]]$ids = if ($CheckId) { @($CheckId) } else { @($resources.plan.items | Where-Object eligible | ForEach-Object id) }
        if ($ids.Count -eq 0) { throw '本轮可执行检查点为零，请查看资源条件及准备缺项。' }
        $body = @{ resourceRunId = $resources.runId; requestId = [guid]::NewGuid().ToString(); checkIds = $ids } | ConvertTo-Json -Compress
        $client.Headers['Origin'] = $base
        $client.Headers['X-Inspection-Token'] = $nonce
        $client.Headers['Content-Type'] = 'application/json; charset=utf-8'
        $run = $client.UploadString($base + '/api/tests', 'POST', $body) | ConvertFrom-Json
        Write-Host ('已提交 {0} 个检查点。' -f $ids.Count)
    }
    $runId = $run.runId
    if (!$OutputDirectory) {
        $OutputDirectory = Join-Path $PSScriptRoot ('../../.artifacts/api-runs/' + $runId)
    }
    $null = New-Item -ItemType Directory -Force -Path $OutputDirectory
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $seen = @{}
    $lastPhase = ''
    $labels = @{ waiting = '等待'; running = '执行中'; passed = '通过'; failed = '失败'; skipped = '跳过'; cancelled = '已取消' }
    while ($true) {
        if ($run.runId -ne $runId) { throw '页面轮次已变化，停止收集；原轮次报告保存在测试程序报告目录。' }
        if ($run.phase -ne $lastPhase -and $run.phase -like '*宿主重启等待你确认*') { Write-Host $run.phase }
        $lastPhase = $run.phase
        foreach ($item in @($run.items | Where-Object selected)) {
            if ($seen[$item.id] -ne $item.status) {
                $seen[$item.id] = $item.status
                if ($item.status -ne 'waiting') {
                    Write-Host ('[{0}] {1}' -f $labels[$item.status], $item.name)
                    if ($item.status -in @('failed', 'skipped', 'cancelled')) { Write-Host ('  ' + $item.detail) }
                }
            }
        }
        $json = $run | ConvertTo-Json -Depth 40
        [IO.File]::WriteAllText((Join-Path $output 'results.json'), $json, (New-Object Text.UTF8Encoding($false)))
        if (!$run.running) { break }
        if ((Get-Date) -ge $deadline) { throw '结果收集已超时；页面继续执行，可用 -Attach 重新收集。' }
        Start-Sleep -Seconds 2
        $run = Read-Api '/api/tests'
    }
    $selected = @($run.items | Where-Object selected)
    $summary = [ordered]@{
        runId = $runId; environment = $run.environment; selected = $selected.Count
        passed = @($selected | Where-Object status -eq passed).Count
        failed = @($selected | Where-Object status -eq failed).Count
        skipped = @($selected | Where-Object status -eq skipped).Count
        cancelled = @($selected | Where-Object status -eq cancelled).Count
        omitted = @($run.omitted).Count; error = $run.error
        results = (Join-Path $output 'results.json')
    }
    [IO.File]::WriteAllText((Join-Path $output 'summary.json'), ($summary | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    Write-Host ('通过 {0}，失败 {1}，跳过 {2}，取消 {3}。报告：{4}' -f $summary.passed, $summary.failed, $summary.skipped, $summary.cancelled, $output)
    if ($summary.failed -gt 0 -or $summary.cancelled -gt 0 -or $run.error -or @($selected | Where-Object { $_.status -in @('waiting', 'running') }).Count -gt 0) { exit 1 }
    if ($summary.passed -eq 0) { exit 2 }
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
} finally { $client.Dispose() }
