[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,

    [string]$ExpectedVersion,

    [string]$PortablePath,

    [string]$ExpectedApplicationHash,

    [switch]$VerifyLaunch,

    [switch]$RequireValidSignatures
)

$ErrorActionPreference = "Stop"
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$installDirectory = Join-Path $env:LOCALAPPDATA "Programs\ExHyperV"
$installedExe = Join-Path $installDirectory "ExHyperV.exe"
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "ExHyperV\ExHyperV.lnk"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "ExHyperV.lnk"
$uninstaller = Join-Path $installDirectory "unins000.exe"

function Get-UninstallEntries {
    $entries = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -eq "ExHyperV" }
    return ,@($entries)
}

function Invoke-Process([string]$filePath, [string]$arguments) {
    $process = Start-Process -FilePath $filePath -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Process failed with exit code $($process.ExitCode): $filePath"
    }
}

function Assert-Exists([string]$path, [string]$description) {
    if (-not (Test-Path -LiteralPath $path)) { throw "$description was not created: $path" }
}

function Assert-ShortcutTarget([string]$path) {
    Assert-Exists $path "Shortcut"
    $shell = New-Object -ComObject WScript.Shell
    $target = $shell.CreateShortcut($path).TargetPath
    if (-not [string]::Equals($target, $installedExe, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected shortcut target: $target"
    }
}

function Assert-ValidSignature([string]$path, [string]$description) {
    Assert-Exists $path $description
    $signature = Get-AuthenticodeSignature -FilePath $path
    if ($signature.Status -ne "Valid") {
        throw "$description does not have a valid Authenticode signature: $($signature.Status)"
    }
}

function Assert-ApplicationWindow([string]$path, [switch]$Portable) {
    $process = Start-Process -FilePath $path -WorkingDirectory (Split-Path -Parent $path) -PassThru
    $window = $process
    try {
        $deadline = (Get-Date).AddSeconds(60)
        do {
            Start-Sleep -Milliseconds 300
            $process.Refresh()
            if ($process.HasExited) { throw "Application exited with code $($process.ExitCode): $path" }
            if ($Portable -and $window.Id -eq $process.Id) {
                $child = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.Id) AND Name = 'ExHyperV.exe'" | Select-Object -First 1
                if ($child) { $window = Get-Process -Id $child.ProcessId -ErrorAction Stop }
            }
            $window.Refresh()
            if ($window.MainWindowHandle -ne 0) { break }
        } while ((Get-Date) -lt $deadline)
        Start-Sleep -Seconds 5
        $window.Refresh()
        # A custom WinUI title bar can expose an empty Win32 caption.
        if ($window.HasExited -or $window.MainWindowHandle -eq 0 -or !$window.Responding -or
            ($window.MainWindowTitle -and $window.MainWindowTitle -ne 'ExHyperV')) {
            throw "ExHyperV did not display a responsive main window: $path"
        }
        foreach ($running in @($process, $window)) {
            $modules = @($running.Modules | ForEach-Object ModuleName)
            if ($modules -match '^(hostfxr|hostpolicy|coreclr|clrjit|clr)\.dll$') {
                throw "Shared .NET components loaded by $($running.Id): $modules"
            }
        }
    }
    finally {
        if ($window.Id -ne $process.Id) {
            if (!$window.HasExited) {
                $null = $window.CloseMainWindow()
                if (!$window.WaitForExit(10000)) { $window.Kill() }
            }
            $window.Dispose()
            $null = $process.WaitForExit(10000)
        }
        if (!$process.HasExited) {
            $null = $process.CloseMainWindow()
            if (!$process.WaitForExit(10000)) { $process.Kill() }
        }
        $process.Dispose()
    }
}

if ((Test-Path -LiteralPath $installDirectory) -or (Get-UninstallEntries).Count -gt 0 -or
    (Test-Path -LiteralPath $startMenuShortcut) -or (Test-Path -LiteralPath $desktopShortcut)) {
    throw "Run this test in a clean Windows account. An existing ExHyperV installation or shortcut was found."
}
if ($RequireValidSignatures) {
    Assert-ValidSignature $installer "Installer"
}
if ($PortablePath) {
    $portable = (Resolve-Path -LiteralPath $PortablePath).Path
    if ($RequireValidSignatures) { Assert-ValidSignature $portable "Portable application" }
    if ($VerifyLaunch) { Assert-ApplicationWindow $portable -Portable }
}

try {
    Invoke-Process $installer '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LANG=chinesesimplified /MERGETASKS=!desktopicon'
    Assert-Exists $installedExe "Installed application"
    if ($ExpectedApplicationHash -and (Get-FileHash -LiteralPath $installedExe).Hash -ne $ExpectedApplicationHash) {
        throw "The installed executable differs from the NativeAOT publish output"
    }
    if ($VerifyLaunch) { Assert-ApplicationWindow $installedExe }
    if ($RequireValidSignatures) {
        Assert-ValidSignature $installedExe "Installed application"
        Assert-ValidSignature $uninstaller "Uninstaller"
    }
    Assert-ShortcutTarget $startMenuShortcut
    if (Test-Path -LiteralPath $desktopShortcut) {
        throw "The desktop shortcut was created even though it was disabled"
    }

    $entries = Get-UninstallEntries
    if ($entries.Count -ne 1) { throw "Expected one uninstall entry, found $($entries.Count)" }
    if ($ExpectedVersion -and $entries[0].DisplayVersion -ne $ExpectedVersion) {
        throw "Unexpected installed version: $($entries[0].DisplayVersion)"
    }

    Invoke-Process $installer '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LANG=english /MERGETASKS=desktopicon'
    Assert-ShortcutTarget $startMenuShortcut
    Assert-ShortcutTarget $desktopShortcut
    if ((Get-UninstallEntries).Count -ne 1) {
        throw "The in-place upgrade created duplicate uninstall entries"
    }

    Assert-Exists $uninstaller "Uninstaller"
    Invoke-Process $uninstaller '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    if ((Test-Path -LiteralPath $installDirectory) -or
        (Test-Path -LiteralPath $startMenuShortcut) -or
        (Test-Path -LiteralPath $desktopShortcut) -or
        (Get-UninstallEntries).Count -gt 0) {
        throw "Installer residue remained after uninstall"
    }
}
finally {
    if (Test-Path -LiteralPath $uninstaller) {
        Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait | Out-Null
    }
}

Write-Host "Installer test passed"
