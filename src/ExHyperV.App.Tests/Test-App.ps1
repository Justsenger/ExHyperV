#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppPath,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../../.cache/app-tests')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, System.Drawing.Common
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AppTestWindow {
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@

function Find-Control([string]$Id) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-Until([scriptblock]$Condition, [string]$Expected) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 50
    } while ($timer.Elapsed.TotalSeconds -lt 20)
    throw "Timed out waiting for $Expected"
}

function Get-Control([string]$Id) {
    Wait-Until { Find-Control $Id } $Id
}

function Select-Page([string]$Id, [string]$ReadyControl) {
    $selection = (Get-Control $Id).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    $null = Wait-Until { $selection.Current.IsSelected -and (Find-Control $ReadyControl) } $ReadyControl
}

function Save-Window([string]$File) {
    # Render only this HWND, including its in-window XAML flyouts.
    Start-Sleep -Milliseconds 350
    $rect = [AppTestWindow+Rect]::new()
    if (![AppTestWindow]::GetWindowRect($app.MainWindowHandle, [ref]$rect)) { throw 'Window bounds unavailable.' }
    $bounds = [Drawing.Rectangle]::FromLTRB($rect.Left, $rect.Top, $rect.Right, $rect.Bottom)
    $bitmap = [Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $dc = $graphics.GetHdc()
            try {
                if (![AppTestWindow]::PrintWindow($app.MainWindowHandle, $dc, 2)) { throw 'Window capture failed.' }
            } finally { $graphics.ReleaseHdc($dc) }
        }
        finally { $graphics.Dispose() }
        $bitmap.Save($File, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    return @{ width=$bounds.Width; height=$bounds.Height; dpi=[AppTestWindow]::GetDpiForWindow($app.MainWindowHandle) }
}

function Record-Scene([string]$Id, [string]$Expected, [scriptblock]$Action) {
    $entry = [ordered]@{ id=$Id; expected=$Expected; status='captured'; image=$null }
    $records.Add($entry)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try { & $Action }
    catch { $entry.status='failed'; $entry.error=$_.Exception.Message }
    $entry.actionMs = $timer.ElapsedMilliseconds
    $timer.Restart()
    try {
        $filename = "$Id.png"
        $entry.window = Save-Window (Join-Path $run $filename)
        $entry.image = $filename
    } catch { $entry.status='failed'; $entry.captureError=$_.Exception.Message }
    $entry.captureMs = $timer.ElapsedMilliseconds
    if ($entry.status -eq 'failed') { throw ($entry | ConvertTo-Json -Compress) }
}

$exe = Get-Item -LiteralPath $AppPath
$run = Join-Path $OutputRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
$run = (New-Item -ItemType Directory -Path $run -Force).FullName
$records = [Collections.Generic.List[object]]::new()
$previousDpi = [AppTestWindow]::SetThreadDpiAwarenessContext([IntPtr](-4))
$app = $null
$report = [ordered]@{ executable=$exe.FullName; version=$exe.VersionInfo.ProductVersion; sha256=(Get-FileHash $exe.FullName).Hash; started=(Get-Date -Format o); scenarios=$records }
try {
    $startup = [Diagnostics.Stopwatch]::StartNew()
    $app = Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName -PassThru
    $handle = Wait-Until {
        $app.Refresh()
        if ($app.HasExited) { throw 'App exited before its window opened.' }
        if ($app.MainWindowHandle -ne 0) { $app.MainWindowHandle }
    } 'the desktop application window (use the unpacked app executable)'
    $report.windowMs = $startup.ElapsedMilliseconds
    $root = [Windows.Automation.AutomationElement]::FromHandle($handle)
    $null = Wait-Until {
        $box = Find-Control 'SearchBox'
        $box -and $box.Current.IsEnabled -and !$box.Current.IsOffscreen -and (Find-Control 'VmList')
    } 'visible enabled search box and VM list'
    $report.homeReadyMs = $startup.ElapsedMilliseconds
    $listControl = Get-Control 'VmList'
    $rowCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
    $firstRow = $listControl.FindFirst('Children', $rowCondition)
    if ($firstRow) { $report.firstRowObservedMs = $startup.ElapsedMilliseconds }
    . (Join-Path $PSScriptRoot 'Scenes.ps1')
} catch {
    $report.error = $_.Exception.Message
    throw
} finally {
    if ($app -and !$app.HasExited) { $null = $app.CloseMainWindow() }
    if ($previousDpi -ne [IntPtr]::Zero) { $null = [AppTestWindow]::SetThreadDpiAwarenessContext($previousDpi) }
    $images = @(Get-ChildItem -LiteralPath $run -Filter '*.png')
    $report.imageCount = $images.Count
    $report.imageBytes = [long]($images | Measure-Object Length -Sum).Sum
    $report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $run 'summary.json') -Encoding utf8
    $cards = foreach ($entry in $records) {
        $label = [Net.WebUtility]::HtmlEncode("$($entry.id) — $($entry.expected) [$($entry.status)]")
        "<figure><figcaption>$label<br>操作及状态核对 $($entry.actionMs) ms，截图等待和保存 $($entry.captureMs) ms</figcaption>" + $(if ($entry.image) { "<a href='$($entry.image)'><img src='$($entry.image)' loading='lazy'></a>" }) + '</figure>'
    }
    $milestones = foreach ($key in ([ordered]@{windowMs='检测到主窗口';homeReadyMs='首页搜索框可用';firstRowObservedMs='观察到首条虚拟机数据'}).GetEnumerator()) {
        if ($report.Contains($key.Key)) { "<li>$($key.Value) $($report[$key.Key]) ms</li>" }
    }
    "<!doctype html><meta charset=utf-8><title>App.Tests</title><style>body{font:16px system-ui;max-width:1100px;margin:auto}figure{margin:2em 0}img{max-width:100%}</style><h1>App.Tests</h1><p>$($report.imageCount) PNG / $($report.imageBytes) bytes</p><h2>自启动起</h2><ul>$($milestones -join '')</ul>$($cards -join [Environment]::NewLine)" | Set-Content (Join-Path $run 'index.html') -Encoding utf8
    Write-Host "$run ($($report.imageCount) PNG, $($report.imageBytes) bytes)"
}
