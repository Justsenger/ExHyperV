[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,

    [string]$ExpectedVersion,

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

if ((Test-Path -LiteralPath $installDirectory) -or (Get-UninstallEntries).Count -gt 0) {
    throw "An existing ExHyperV installation was found; the test did not modify it"
}
if ($RequireValidSignatures) {
    Assert-ValidSignature $installer "Installer"
}

try {
    Invoke-Process $installer '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LANG=chinesesimplified /MERGETASKS=!desktopicon'
    Assert-Exists $installedExe "Installed application"
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
