$ErrorActionPreference = 'Stop'
$payloadDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$manifestPath = Join-Path $payloadDir 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Missing driver manifest: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$winDir = $env:SystemRoot
$guestRepo = Join-Path $winDir 'System32\HostDriverStore\FileRepository'
$sourceRepo = Join-Path $payloadDir 'HostDriverStore\FileRepository'

if (-not (Test-Path -LiteralPath $sourceRepo)) {
    throw "Missing FileRepository payload: $sourceRepo"
}

if (Test-Path -LiteralPath $guestRepo) {
    Write-Host '[ExHyperV] Clearing read-only attributes on HostDriverStore...'
    & attrib.exe -R "$guestRepo\*" /S /D 2>$null | Out-Null
} else {
    New-Item -ItemType Directory -Path $guestRepo -Force | Out-Null
}

Write-Host '[ExHyperV] Copying HostDriverStore\FileRepository...'
& robocopy.exe "$sourceRepo" "$guestRepo" /E /R:1 /W:1 /MT:16 /XJ /NDL /NJH /NJS /NC /NS
if ($LASTEXITCODE -ge 8) {
    throw "robocopy HostDriverStore failed with exit code $LASTEXITCODE"
}
& attrib.exe +R "$guestRepo\*" /S /D 2>$null | Out-Null

$progRootPayload = Join-Path $payloadDir 'ProgramFolders'
if ($manifest.ProgramFolders) {
    foreach ($folder in $manifest.ProgramFolders) {
        $src = Join-Path $payloadDir ($folder.IsoSubDirectory -replace '^_payload[\\/]', '')
        if (-not (Test-Path -LiteralPath $src)) { continue }
        $destRoot = switch ($folder.RootTag) {
            'ProgramFiles' { $env:ProgramFiles }
            'ProgramFilesX86' { ${env:ProgramFiles(x86)} }
            'CommonApplicationData' { $env:ProgramData }
            default { $null }
        }
        if ([string]::IsNullOrWhiteSpace($destRoot)) { continue }
        $folderName = Split-Path -Leaf $folder.SourcePath
        $relLeaf = $folder.IsoSubDirectory -replace ('^_payload[\\/]ProgramFolders[\\/]' + [regex]::Escape($folder.RootTag) + '[\\/]'), ''
        $dest = Join-Path $destRoot $relLeaf
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        Write-Host "[ExHyperV] Copying $dest..."
        & robocopy.exe "$src" "$dest" /E /R:1 /W:1 /MT:16 /XJ /NDL /NJH /NJS /NC /NS
        if ($LASTEXITCODE -ge 8) {
            throw "robocopy ProgramFolders ($dest) failed with exit code $LASTEXITCODE"
        }
    }
}

$preparedDirs = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
function Ensure-SystemDirWritable([string]$dirPath) {
    $full = [System.IO.Path]::GetFullPath($dirPath)
    if ($preparedDirs.Contains($full)) { return }
    & takeown.exe /f "$full" /a 2>$null | Out-Null
    & icacls.exe "$full" /grant '*S-1-5-32-544:F' 2>$null | Out-Null
    [void]$preparedDirs.Add($full)
}

function Find-DriverSourceFiles([string]$repoRoot, [string]$sourceName, [string[]]$preferredPackages, [bool]$allowFallback) {
    $norm = ($sourceName -replace '/', '\').Trim()
    $parts = @($norm.Split('\', [System.StringSplitOptions]::RemoveEmptyEntries))
    $matches = [System.Collections.Generic.List[System.IO.FileInfo]]::new()

    function Search-Packages([string[]]$dirs) {
        $relDir = [System.IO.Path]::GetDirectoryName($norm)
        $pattern = [System.IO.Path]::GetFileName($norm)
        $opt = if ($parts.Count -eq 1) { [System.IO.SearchOption]::AllDirectories } else { [System.IO.SearchOption]::TopDirectoryOnly }
        foreach ($d in $dirs) {
            $searchRoot = if ([string]::IsNullOrEmpty($relDir)) { $d } else { Join-Path $d $relDir }
            if (-not (Test-Path -LiteralPath $searchRoot)) { continue }
            foreach ($f in ([System.IO.DirectoryInfo]::new($searchRoot)).GetFiles($pattern, $opt)) {
                [void]$matches.Add($f)
            }
        }
    }

    if ($preferredPackages -and $preferredPackages.Count -gt 0) {
        $prefDirs = @($preferredPackages | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Join-Path $repoRoot $_ })
        Search-Packages $prefDirs
    }
    if ($matches.Count -eq 0 -and $allowFallback) {
        if ($parts.Count -gt 1) {
            $allPkg = @([System.IO.Directory]::EnumerateDirectories($repoRoot))
            Search-Packages $allPkg
        } else {
            foreach ($f in ([System.IO.DirectoryInfo]::new($repoRoot)).GetFiles($norm, [System.IO.SearchOption]::AllDirectories)) {
                [void]$matches.Add($f)
            }
        }
    }
    return @($matches | Sort-Object FullName)
}

function Compare-SourceNewer([System.IO.FileInfo]$srcFile, [string]$destPath, [bool]$isReparse) {
    try {
        $tsPath = $destPath
        if ($isReparse) {
            $item = Get-Item -LiteralPath $destPath -Force -ErrorAction SilentlyContinue
            $linkTarget = $null
            if ($item -and $item.PSObject.Properties['Target'] -and $item.Target) {
                $linkTarget = @($item.Target)[0]
            } elseif ($item -and $item.PSObject.Properties['LinkTarget']) {
                $linkTarget = $item.LinkTarget
            }
            if ([string]::IsNullOrWhiteSpace($linkTarget)) { return $true }
            $resolved = if ([System.IO.Path]::IsPathRooted($linkTarget)) { $linkTarget } else { [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $destPath) $linkTarget)) }
            if (-not ([System.IO.Path]::GetFullPath($resolved).Equals([System.IO.Path]::GetFullPath($srcFile.FullName), [System.StringComparison]::OrdinalIgnoreCase))) {
                return $true
            }
            $tsPath = $resolved
        }
        if (-not (Test-Path -LiteralPath $tsPath)) { return $true }
        $ext = [System.IO.Path]::GetExtension($destPath)
        if ($ext -ieq '.dll' -or $ext -ieq '.exe') {
            $sv = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($srcFile.FullName)
            $dv = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($tsPath)
            foreach ($pair in @(
                @($sv.FileMajorPart, $dv.FileMajorPart),
                @($sv.FileMinorPart, $dv.FileMinorPart),
                @($sv.FileBuildPart, $dv.FileBuildPart),
                @($sv.FilePrivatePart, $dv.FilePrivatePart)
            )) {
                if ($pair[0] -ne $pair[1]) { return ($pair[0] -gt $pair[1]) }
            }
        }
        return ($srcFile.LastWriteTimeUtc -gt ([System.IO.File]::GetLastWriteTimeUtc($tsPath)))
    } catch {
        return $true
    }
}

Write-Host '[ExHyperV] Creating driver symlinks...'
if ($manifest.Rules) {
    foreach ($rule in $manifest.Rules) {
        try {
            $pref = @()
            if ($rule.PreferredPackageNames) { $pref = @($rule.PreferredPackageNames) }
            $sources = @(Find-DriverSourceFiles $guestRepo $rule.SourceName $pref ([bool]$rule.RequirePreferredPackage -eq $false))
            if ($sources.Count -eq 0) { continue }
            $sourceFile = $sources[0]
            $destDir = Join-Path (Join-Path $winDir $rule.TargetSubDir) ''
            $destDir = $destDir.TrimEnd('\')
            $linkPath = Join-Path $destDir $rule.TargetName
            $linkFolder = Split-Path -Parent $linkPath
            if (-not (Test-Path -LiteralPath $linkFolder)) {
                New-Item -ItemType Directory -Path $linkFolder -Force | Out-Null
            }
            if ($rule.TargetSubDir -match '^(System32|SysWOW64|SyChpe32)($|\\)') {
                Ensure-SystemDirWritable $destDir
            }

            $exists = Test-Path -LiteralPath $linkPath
            $isReparse = $false
            if ($exists) {
                $item = Get-Item -LiteralPath $linkPath -Force
                if ($item.PSIsContainer) { continue }
                $isReparse = (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
                $shouldReplace = switch ($rule.Mode) {
                    'Overwrite' { $true }
                    'WhenNewer' { Compare-SourceNewer $sourceFile $linkPath $isReparse }
                    default { $isReparse }
                }
                if (-not $shouldReplace) { continue }
                [System.IO.File]::SetAttributes($linkPath, [System.IO.FileAttributes]::Normal)
                [System.IO.File]::Delete($linkPath)
            }
            $mk = & cmd.exe /c "mklink `"$linkPath`" `"$($sourceFile.FullName)`"" 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "mklink failed ($LASTEXITCODE): $mk"
            }
        } catch {
            Write-Warning "[ExHyperV] Link skipped ($($rule.SourceName) -> $($rule.TargetName)): $($_.Exception.Message)"
        }
    }
}

function Set-SpecRegistryValue($regKey, $spec) {
    switch ($spec.Kind) {
        'ExpandString' { $regKey.SetValue($spec.Name, [string]$spec.StringValue, [Microsoft.Win32.RegistryValueKind]::ExpandString) }
        'MultiString'  { $regKey.SetValue($spec.Name, [string[]]$spec.MultiStringValue, [Microsoft.Win32.RegistryValueKind]::MultiString) }
        'DWord'        { $regKey.SetValue($spec.Name, [int]$spec.DwordValue, [Microsoft.Win32.RegistryValueKind]::DWord) }
        'Binary'       { $regKey.SetValue($spec.Name, [byte[]]$spec.BinaryValue, [Microsoft.Win32.RegistryValueKind]::Binary) }
        default        { $regKey.SetValue($spec.Name, [string]$spec.StringValue, [Microsoft.Win32.RegistryValueKind]::String) }
    }
}

function Test-CompatibleIntelRuntimeKey($regKey) {
    if (-not $regKey) { return $true }
    $prov = [string]$regKey.GetValue('ProviderName', '')
    $inf = [string]$regKey.GetValue('InfPath', '')
    $desc = [string]$regKey.GetValue('DriverDesc', '')
    if ([string]::IsNullOrWhiteSpace($prov) -and [string]::IsNullOrWhiteSpace($inf) -and [string]::IsNullOrWhiteSpace($desc)) { return $true }
    return ($prov -like '*Intel*') -or ($inf -like 'iigd*') -or ($desc -like '*Intel*')
}

if ($manifest.NvidiaServices) {
    Write-Host '[ExHyperV] Applying NVIDIA service registry entries...'
    foreach ($svc in $manifest.NvidiaServices) {
        $keyPath = 'SYSTEM\CurrentControlSet\Services\nvlddmkm'
        $rk = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($keyPath, $true)
        try {
            foreach ($val in $svc.Values) { Set-SpecRegistryValue $rk $val }
        } finally {
            $rk.Dispose()
        }
    }
}

if ($manifest.IntelRuntimes) {
    Write-Host '[ExHyperV] Applying Intel runtime registry entries...'
    $displayClassGuid = '{4d36e968-e325-11ce-bfc1-08002be10318}'
    $classRootPath = "SYSTEM\CurrentControlSet\Control\Class\$displayClassGuid"
    foreach ($intel in $manifest.IntelRuntimes) {
        $targetKeyPath = "$classRootPath\$($intel.DisplayClassSubKeyName)"
        $rk = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($targetKeyPath, $true)
        try {
            if (-not (Test-CompatibleIntelRuntimeKey $rk)) {
                Write-Warning "[ExHyperV] Skipped Intel runtime key $($intel.DisplayClassSubKeyName): owned by another display driver."
                continue
            }
            foreach ($val in $intel.RuntimeValues) { Set-SpecRegistryValue $rk $val }
        } finally {
            $rk.Dispose()
        }
        if (-not [string]::IsNullOrWhiteSpace($intel.LevelZeroDriverPath)) {
            $classRoot = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($classRootPath, $false)
            if ($classRoot) {
                try {
                    foreach ($subName in ($classRoot.GetSubKeyNames() | Sort-Object)) {
                        $cand = $classRoot.OpenSubKey($subName, $false)
                        if (-not $cand) { continue }
                        try {
                            $desc = [string]$cand.GetValue('DriverDesc', '')
                            $prov = [string]$cand.GetValue('ProviderName', '')
                            $inf = [string]$cand.GetValue('InfPath', '')
                            if ((($desc -like '*Hyper-V Video*') -or ($inf -ieq 'wvmbusvideo.inf')) -and ($prov -like '*Microsoft*')) {
                                $anchor = $classRoot.OpenSubKey($subName, $true)
                                try {
                                    $anchor.SetValue('LevelZeroDriverPath', [string]$intel.LevelZeroDriverPath, [Microsoft.Win32.RegistryValueKind]::String)
                                } finally { $anchor.Dispose() }
                                break
                            }
                        } finally { $cand.Dispose() }
                    }
                } finally { $classRoot.Dispose() }
            }
        }
    }
}

Write-Host '[ExHyperV] Done.'
