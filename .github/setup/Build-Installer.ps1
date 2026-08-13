[CmdletBinding()]
param(
    [string]$Version,

    [ValidateSet("x64", "arm64")]
    [string[]]$Architecture = @("x64", "arm64"),

    [string]$OutputDirectory = "artifacts/installers",

    [string]$ApplicationOutputDirectory = "artifacts/installer-work",

    [switch]$EnableSigning
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repoRoot "src\ExHyperV.csproj"
$installerScript = Join-Path $PSScriptRoot "ExHyperV.iss"
$singleFileSigningScript = Join-Path $PSScriptRoot "Sign-File.ps1"

function Find-MSBuild {
    $command = Get-Command "MSBuild.exe" -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $candidate = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" |
            Select-Object -First 1
        if ($candidate) { return $candidate }
    }
    throw "MSBuild.exe was not found"
}

function Find-InnoCompiler {
    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @(
        (Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    throw "Inno Setup 6 was not found"
}

function Get-ProjectVersion {
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    $node = $project.SelectSingleNode("/Project/PropertyGroup/Version")
    if (-not $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "The project version was not found"
    }
    return $node.InnerText.Trim()
}

function Get-NumericVersion([string]$value) {
    $match = [regex]::Match($value, '^(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?')
    if (-not $match.Success) {
        throw "Version must begin with a numeric version: $value"
    }

    $parts = for ($index = 1; $index -le 4; $index++) {
        if ($match.Groups[$index].Success) { [int]$match.Groups[$index].Value } else { 0 }
    }
    return $parts -join '.'
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ProjectVersion
}
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') {
    throw "Version contains characters that are unsafe in a path or file name: $Version"
}

$msbuild = Find-MSBuild
$iscc = Find-InnoCompiler
$numericVersion = Get-NumericVersion $Version
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$applicationRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ApplicationOutputDirectory))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$artifactsPrefix = $artifactsRoot.TrimEnd('\') + '\'
if (-not $applicationRoot.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "ApplicationOutputDirectory must be inside the repository artifacts directory"
}
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
New-Item -ItemType Directory -Path $applicationRoot -Force | Out-Null

$definitions = @{
    x64 = @{ Runtime = "win-x64"; InnoArchitecture = "x64compatible" }
    arm64 = @{ Runtime = "win-arm64"; InnoArchitecture = "arm64" }
}

$builtInstallers = @()
foreach ($item in $Architecture) {
    $definition = $definitions[$item]
    $publishDirectory = Join-Path $applicationRoot "$Version\$($definition.Runtime)"
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

    & $msbuild $projectPath "/t:Restore;Publish" "/p:Configuration=Release" `
        "/p:RuntimeIdentifier=$($definition.Runtime)" "/p:SelfContained=true" `
        "/p:PublishSingleFile=true" "/p:IncludeNativeLibrariesForSelfExtract=true" `
        "/p:DebugType=none" "/p:DebugSymbols=false" `
        "/p:PublishDir=$publishDirectory\"
    if ($LASTEXITCODE -ne 0) {
        throw "Application publishing failed for $item"
    }

    $sourceExe = Join-Path $publishDirectory "ExHyperV.exe"
    if (-not (Test-Path -LiteralPath $sourceExe)) {
        throw "Published application was not found: $sourceExe"
    }

    if ($EnableSigning) {
        & (Join-Path $repoRoot ".github\scripts\sign-windows.ps1") `
            -TargetDirectory $publishDirectory -SkipCatGeneration
        if ($LASTEXITCODE -ne 0) { throw "Application signing failed for $item" }
    }

    $baseName = "ExHyperV_V${Version}_Setup_${item}"
    $arguments = @(
        "/DAppVersion=$Version",
        "/DNumericVersion=$numericVersion",
        "/DArchitecture=$($definition.InnoArchitecture)",
        "/DSourceExe=$sourceExe",
        "/DOutputDirectory=$outputPath",
        "/DOutputBaseFilename=$baseName"
    )
    if ($EnableSigning) {
        $signCommand = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' +
            $singleFileSigningScript + '" -FilePath $q$f$q'
        $arguments += "/DEnableSigning=1"
        $arguments += "/SExHyperVSign=$signCommand"
    }
    $arguments += $installerScript

    & $iscc @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Installer compilation failed for $item"
    }

    $installerPath = Join-Path $outputPath "$baseName.exe"
    if (-not (Test-Path -LiteralPath $installerPath)) {
        throw "Installer output was not found: $installerPath"
    }
    $builtInstallers += Get-Item -LiteralPath $installerPath
}

$builtInstallers | Select-Object FullName, Length, LastWriteTime
