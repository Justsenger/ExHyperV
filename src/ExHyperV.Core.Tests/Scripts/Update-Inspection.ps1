#Requires -Version 5.1
[CmdletBinding()]
param([ValidateRange(1024, 65535)][int]$Port = 5187)

$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$url = "http://127.0.0.1:$Port"
try { $session = Invoke-RestMethod "$url/api/session" -TimeoutSec 5 }
catch { throw '当前服务尚未接入自动更新。请首次运行新版 Inspect-Resources.ps1。' }
$pagePath = [IO.Path]::GetFullPath((Join-Path $projectDirectory 'ResourceInspection/Page.html'))
if (!$session.supervised -or ![string]::Equals($session.pagePath, $pagePath, [StringComparison]::OrdinalIgnoreCase)) {
    throw '端口上的服务并非由本项目的启动器管理。'
}
$page = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5
$nonce = [regex]::Match($page.Content, '<script nonce="([A-F0-9]+)">').Groups[1].Value
if (!$nonce) { throw '页面校验令牌缺失。' }
Invoke-WebRequest "$url/api/restart" -Method Post -Headers @{ Origin=$url; 'X-Inspection-Token'=$nonce } -UseBasicParsing -TimeoutSec 5 | Out-Null
Write-Host '已交给启动器编译并重启，正在等待新程序…'
$deadline = [DateTime]::UtcNow.AddMinutes(3)
while ([DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $next = $null
    try { $next = Invoke-RestMethod "$url/api/session" -TimeoutSec 2 } catch { }
    if ($next -and $next.instanceId -ne $session.instanceId) {
        if ($next.updateError) { throw $next.updateError }
        Write-Host '更新完成，打开的页面会自动重新连接。'
        exit 0
    }
}
throw '更新仍在进行，请查看启动器窗口的编译输出。'
