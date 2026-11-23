[CmdletBinding()]
param(
    [string]$Version,

    [ValidateSet("x64", "arm64")]
    [string[]]$Architecture = @("x64", "arm64"),

    [string]$OutputDirectory = ".artifacts/release",

    [string]$ApplicationOutputDirectory = ".artifacts/installer-work",

    [switch]$EnableSigning
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
& (Join-Path $repoRoot 'tools/Use-RepositoryDotNet.ps1')
$projectPath = Join-Path $repoRoot "src\ExHyperV.WinUI3\ExHyperV.WinUI3.csproj"
$installerScript = Join-Path $PSScriptRoot "ExHyperV.iss"
$singleFileSigningScript = Join-Path $PSScriptRoot "Sign-File.ps1"

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $candidate = & $vswhere -latest -products '*' -version '[18.0,19.0)' -requires Microsoft.Component.MSBuild -find "MSBuild\Current\Bin\MSBuild.exe" |
            Select-Object -First 1
        if ($candidate) { return $candidate }
    }
    throw "Visual Studio 2026 MSBuild.exe was not found"
}

function Find-InnoCompiler {
    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe")
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
$outputPath = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($repoRoot, $OutputDirectory))
$applicationRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($repoRoot, $ApplicationOutputDirectory))
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
New-Item -ItemType Directory -Path $applicationRoot -Force | Out-Null

$definitions = @{
    x64 = @{ Runtime = "win-x64"; Platform = "x64"; InnoArchitecture = "x64compatible" }
    arm64 = @{ Runtime = "win-arm64"; Platform = "ARM64"; InnoArchitecture = "arm64" }
}

$builtFiles = @()
foreach ($item in $Architecture) {
    $definition = $definitions[$item]
    $buildDirectory = Join-Path $applicationRoot $definition.Runtime
    # Reusing a publish directory can accidentally ship obsolete DLLs from an earlier SDK.
    $publishDirectory = Join-Path $buildDirectory ("publish-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

    & $msbuild $projectPath /restore /t:Publish /nologo /nr:false '/clp:ErrorsOnly;Summary' "/p:Configuration=Release" `
        "/p:UseArtifactsOutput=true" "/p:ArtifactsPath=$buildDirectory" "/p:Platform=$($definition.Platform)" `
        "/p:Version=$Version" "/p:PublishAot=true" "/p:SelfContained=true" `
        "/p:RuntimeIdentifier=$($definition.Runtime)" "/p:ExHyperVConsoleRuntime=$($definition.Runtime)" `
        "/p:DebugType=none" "/p:DebugSymbols=false" "/p:PublishDir=$publishDirectory"
    if ($LASTEXITCODE -ne 0) {
        throw "Application publishing failed for $item"
    }

    $sourceExe = Join-Path $publishDirectory "ExHyperV.exe"
    if (-not (Test-Path -LiteralPath $sourceExe)) {
        throw "Published application was not found: $sourceExe"
    }

    if ($EnableSigning) {
        & $singleFileSigningScript -FilePath $sourceExe
        if ($LASTEXITCODE -ne 0) { throw "Application signing failed for $item" }
    }
    & (Join-Path $PSScriptRoot 'Build-NativePortable.ps1') -MSBuild $msbuild -PublishDirectory $publishDirectory `
        -WorkDirectory $buildDirectory -Architecture $item -Version $Version -NumericVersion $numericVersion
    $portableExe = Join-Path $buildDirectory 'portable/ExHyperV.exe'
    (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceExe).Hash | Set-Content -LiteralPath (Join-Path $buildDirectory 'application.sha256') -Encoding ASCII
    if ($EnableSigning) {
        & $singleFileSigningScript -FilePath $portableExe
        if ($LASTEXITCODE -ne 0) { throw "Portable launcher signing failed for $item" }
    }

    $baseName = "ExHyperV_V${Version}_Setup_${item}"
    $arguments = @(
        "/DAppVersion=$Version",
        "/DNumericVersion=$numericVersion",
        "/DArchitecture=$($definition.InnoArchitecture)",
        "/DPublishDir=$publishDirectory",
        "/DOutputDirectory=$buildDirectory",
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

    $installerPath = Join-Path $buildDirectory "$baseName.exe"
    if (-not (Test-Path -LiteralPath $installerPath)) {
        throw "Installer output was not found: $installerPath"
    }
    $portableName = "ExHyperV_V${Version}_${item}.exe"
    $builtFiles += Copy-Item -LiteralPath $portableExe -Destination (Join-Path $outputPath $portableName) -Force -PassThru
    $builtFiles += Copy-Item -LiteralPath $installerPath -Destination $outputPath -Force -PassThru
}

$builtFiles | Select-Object FullName, Length, LastWriteTime
