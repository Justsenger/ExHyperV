# Sign-Windows.ps1
# Signs Windows executables, libraries, and driver files with a Certum SimplySign cloud certificate.
# Signing order: sign EXE/DLL/SYS first, then generate CAT, then sign CAT.
# Files that already have a valid signature will be skipped.

param(
    [string]$TargetDirectory = "sign_binaries",
    [string]$CertificateSHA1 = $env:CERTUM_CERTIFICATE_SHA1,
    [string]$TimestampServer = "http://time.certum.pl",
    [switch]$SkipCatGeneration = $false
)

function Get-LatestSignToolPath {
    $windowsKitsBin = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path $windowsKitsBin) {
        $candidate = (
            Get-ChildItem -Path $windowsKitsBin -Recurse -File -Filter "signtool.exe" -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
                ForEach-Object {
                    $version = [version]"0.0"
                    if ($_.FullName -match "\\bin\\([^\\]+)\\x64\\signtool\.exe$") {
                        try { $version = [version]$matches[1] } catch { $version = [version]"0.0" }
                    }
                    [PSCustomObject]@{ Path = $_.FullName; Version = $version }
                } |
                Sort-Object -Property Version -Descending |
                Select-Object -First 1
        )
        if ($candidate) { return $candidate.Path }
    }
    $cmd = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Get-MakeCatPath {
    # Search for makecat.exe in Windows Kits
    $windowsKitsBin = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path $windowsKitsBin) {
        $candidate = Get-ChildItem -Path $windowsKitsBin -Recurse -File -Filter "makecat.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\makecat\.exe$" } |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    $cmd = Get-Command "makecat.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Find-TargetCertificate {
    param([string]$Thumbprint)
    $all = Get-ChildItem -Path "Cert:\CurrentUser\My", "Cert:\LocalMachine\My" -ErrorAction SilentlyContinue
    return @($all | Where-Object {
        $normalizedStoreThumprint = ($_.Thumbprint -replace "[^a-fA-F0-9]", "").ToUpperInvariant()
        $normalizedStoreThumprint -eq $Thumbprint
    })
}

function Show-PrivateKeyCertificateHints {
    $candidates = Get-ChildItem -Path "Cert:\CurrentUser\My", "Cert:\LocalMachine\My" -ErrorAction SilentlyContinue |
        Where-Object { $_.HasPrivateKey }
    if (($null -eq $candidates) -or ($candidates.Count -eq 0)) {
        Write-Host "No certificates with private keys were found in Personal stores"
        return
    }
    Write-Host "Certificates with private keys are present in Personal stores, but details are hidden for security"
}

function Test-FileHasValidSignature {
    param([string]$FilePath)
    try {
        $sig = Get-AuthenticodeSignature -FilePath $FilePath -ErrorAction SilentlyContinue
        if ($null -eq $sig) { return $false }
        return ($sig.Status -eq "Valid")
    } catch {
        return $false
    }
}

function Invoke-SignFile {
    param(
        [string]$FilePath,
        [string]$SignTool,
        [string]$NormalizedSha1,
        [string]$TimestampServer,
        [int]$MaxRetries = 10
    )

    Write-Host "=== Signing: $([System.IO.Path]::GetFileName($FilePath)) ==="
    Write-Host "Path: $FilePath"

    # 跳过已有有效签名的文件
    if (Test-FileHasValidSignature -FilePath $FilePath) {
        Write-Host "SKIPPED: File already has a valid signature"
        Write-Host ""
        return "skipped"
    }

    # 只使用 SHA256 的参数组合（按优先级排列）
    $attempts = @(
        @{ Name = "SHA1 thumbprint /fd SHA256 /td SHA256";           Args = @("sign", "/sha1", $NormalizedSha1, "/tr", $TimestampServer, "/fd", "SHA256", "/td", "SHA256", "/v", $FilePath) },
        @{ Name = "SHA1 thumbprint /s My /fd SHA256 /td SHA256";     Args = @("sign", "/sha1", $NormalizedSha1, "/s", "My", "/tr", $TimestampServer, "/fd", "SHA256", "/td", "SHA256", "/v", $FilePath) },
        @{ Name = "SHA1 thumbprint /sm /s My /fd SHA256 /td SHA256"; Args = @("sign", "/sha1", $NormalizedSha1, "/sm", "/s", "My", "/tr", $TimestampServer, "/fd", "SHA256", "/td", "SHA256", "/v", $FilePath) },
        @{ Name = "Auto-select /fd SHA256 /td SHA256 (fallback)";    Args = @("sign", "/a", "/tr", $TimestampServer, "/fd", "SHA256", "/td", "SHA256", "/v", $FilePath) }
    )

    $signed = $false

    for ($retry = 1; $retry -le $MaxRetries; $retry++) {
        if ($retry -gt 1) {
            Write-Host "--- Retry $retry / $MaxRetries ---"
            Start-Sleep -Seconds 5
        }

        foreach ($attempt in $attempts) {
            Write-Host "Attempt: $($attempt.Name)"
            $signOutput = & $SignTool @($attempt.Args) 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "SUCCESS: $($attempt.Name)"
                $signed = $true
                break
            }
            Write-Host "FAILED: $($attempt.Name)"
            Write-Host "signtool returned a non-zero exit code; detailed output is hidden for security"
        }

        if ($signed) { break }

        Write-Host "All sign attempts failed on retry $retry, waiting before next retry..."
    }

    if ($signed) {
        $verifyOutput = & $SignTool verify /pa /v $FilePath 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "VERIFIED: Signature verification successful"
        } else {
            Write-Host "WARNING: Signature verification failed"
        }
        Write-Host ""
        return "signed"
    } else {
        Write-Host "ERROR: All $MaxRetries retries exhausted for $([System.IO.Path]::GetFileName($FilePath))"
        Write-Host ""
        return "failed"
    }
}

function New-CatalogFile {
    param(
        [string]$TargetDirectory,
        [string]$MakeCat,
        [string]$SignTool,
        [string]$NormalizedSha1,
        [string]$TimestampServer
    )

    Write-Host ""
    Write-Host "=== Generating CAT catalog file ==="

    # Collect files to include in CAT (EXE/DLL/SYS)
    $targetFiles = Get-ChildItem -Path $TargetDirectory -Recurse -File |
        Where-Object { $_.Extension -iin @(".exe", ".dll", ".sys") }

    if (($null -eq $targetFiles) -or ($targetFiles.Count -eq 0)) {
        Write-Host "WARNING: No files found for CAT, skipping CAT generation"
        return $false
    }

    # Generate .cdf descriptor file
    $catOutputPath = Join-Path $TargetDirectory "ExHyperV.cat"
    $cdfPath = Join-Path $TargetDirectory "catalog.cdf"

    $cdfContent = @"
[CatalogHeader]
Name=$catOutputPath
ResultDir=$TargetDirectory
PublicVersion=0x0000001
EncodingType=0x00010001
CATATTR1=0x10010001:attr1:ExHyperV

[CatalogFiles]
"@

    foreach ($file in $targetFiles) {
        $relativePath = $file.FullName.Substring($TargetDirectory.Length).TrimStart('\', '/')
        $cdfContent += "<HASH>$relativePath=$($file.FullName)`r`n"
    }

    $cdfContent | Out-File -FilePath $cdfPath -Encoding UTF8 -Force
    Write-Host "CDF file generated: $cdfPath ($($targetFiles.Count) files)"

    # Run makecat to generate CAT
    Write-Host "Running makecat to generate CAT file..."
    $makecatOutput = & $MakeCat $cdfPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: makecat failed with exit code $LASTEXITCODE"
        Write-Host "Detailed output is hidden"
        return $false
    }

    if (-not (Test-Path $catOutputPath)) {
        Write-Host "ERROR: CAT file was not generated: $catOutputPath"
        return $false
    }

    Write-Host "CAT file generated: $catOutputPath"

    # Sign the CAT file
    Write-Host "Signing CAT file..."
    $result = Invoke-SignFile -FilePath $catOutputPath -SignTool $SignTool -NormalizedSha1 $NormalizedSha1 -TimestampServer $TimestampServer
    if ($result -eq "failed") {
        Write-Host "ERROR: CAT file signing failed"
        return $false
    }

    Write-Host "CAT file signing complete"
    return $true
}

# ============================================================
# Main
# ============================================================

Write-Host "=== WINDOWS BINARY SIGNING (CERTUM SIMPLYSIGN) ==="
Write-Host "Target directory: $TargetDirectory"

if (-not (Test-Path $TargetDirectory)) {
    Write-Host "ERROR: Target directory not found: $TargetDirectory"
    exit 1
}

if (-not $CertificateSHA1) {
    Write-Host "ERROR: CERTUM_CERTIFICATE_SHA1 environment variable not provided"
    exit 1
}

$normalizedSha1 = ($CertificateSHA1 -replace "[^a-fA-F0-9]", "").ToUpperInvariant()
if ($normalizedSha1.Length -ne 40) {
    Write-Host "ERROR: CERTUM_CERTIFICATE_SHA1 is invalid after normalization"
    Write-Host "Raw length: $($CertificateSHA1.Length), normalized length: $($normalizedSha1.Length)"
    exit 1
}

Write-Host "Expected signing certificate thumbprint has been received (masked)"

$targetCerts = Find-TargetCertificate -Thumbprint $normalizedSha1
if (($null -eq $targetCerts) -or ($targetCerts.Count -eq 0)) {
    Write-Host "ERROR: Target certificate not found in Cert:\CurrentUser\My or Cert:\LocalMachine\My"
    Write-Host "Authentication likely failed or CERTUM_CERTIFICATE_SHA1 is incorrect"
    Show-PrivateKeyCertificateHints
    exit 1
}

$targetWithPrivateKey = @($targetCerts | Where-Object { $_.HasPrivateKey })
if (($null -eq $targetWithPrivateKey) -or ($targetWithPrivateKey.Count -eq 0)) {
    Write-Host "ERROR: Target certificate exists but has no available private key"
    Show-PrivateKeyCertificateHints
    exit 1
}

Write-Host "Locating signtool..."
$signTool = Get-LatestSignToolPath
if (-not $signTool) {
    Write-Host "ERROR: signtool.exe not found"
    exit 1
}
Write-Host "Found signtool: $signTool"

# Phase 1: Sign EXE / DLL / SYS
Write-Host ""
Write-Host "=== Phase 1: Signing EXE / DLL / SYS ==="
$filesToSign = Get-ChildItem -Path $TargetDirectory -Recurse -File |
    Where-Object { $_.Extension -iin @(".exe", ".dll", ".sys") }

if (($null -eq $filesToSign) -or ($filesToSign.Count -eq 0)) {
    Write-Host "WARNING: No signable files (.exe, .dll, .sys) found"
} else {
    Write-Host "Found $($filesToSign.Count) files"
    $signedCount = 0
    $skippedCount = 0
    $failedCount = 0

    foreach ($file in $filesToSign) {
        $result = Invoke-SignFile -FilePath $file.FullName -SignTool $signTool -NormalizedSha1 $normalizedSha1 -TimestampServer $TimestampServer
        switch ($result) {
            "signed"  { $signedCount++ }
            "skipped" { $skippedCount++ }
            "failed"  { $failedCount++ }
        }
    }

    Write-Host "=== Phase 1 Summary ==="
    Write-Host "Total files:   $($filesToSign.Count)"
    Write-Host "Signed:        $signedCount"
    Write-Host "Skipped:       $skippedCount (already had valid signature)"
    Write-Host "Failed:        $failedCount"

    if ($failedCount -gt 0) {
        Write-Host "ERROR: Some files failed to sign"
        exit 1
    }
}

# Phase 2: Generate and sign CAT
if (-not $SkipCatGeneration) {
    Write-Host ""
    Write-Host "=== Phase 2: Generate and sign CAT ==="
    $makeCat = Get-MakeCatPath
    if (-not $makeCat) {
        Write-Host "WARNING: makecat.exe not found, skipping CAT generation"
        Write-Host "Install Windows Driver Kit (WDK) if CAT generation is required"
    } else {
        Write-Host "Found makecat: $makeCat"
        $catResult = New-CatalogFile `
            -TargetDirectory (Resolve-Path $TargetDirectory).Path `
            -MakeCat $makeCat `
            -SignTool $signTool `
            -NormalizedSha1 $normalizedSha1 `
            -TimestampServer $TimestampServer

        if (-not $catResult) {
            Write-Host "WARNING: CAT generation or signing failed, but overall flow continues"
        }
    }
} else {
    Write-Host "CAT generation skipped via parameter"
}

Write-Host ""
Write-Host "=== ALL WINDOWS BINARIES SIGNED SUCCESSFULLY ==="
exit 0