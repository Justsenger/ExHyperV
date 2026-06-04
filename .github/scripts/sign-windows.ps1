# Sign-Windows.ps1
# 对 Windows 可执行文件、库和驱动文件进行 Certum SimplySign 云证书签名。
# 签名顺序：先签名 EXE/DLL/SYS，再生成 CAT，再签名 CAT。
# 已有有效签名的文件将被跳过。

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
    # 在 Windows Kits 中查找 makecat.exe
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
        [string]$TimestampServer
    )

    Write-Host "=== 签名: $([System.IO.Path]::GetFileName($FilePath)) ==="
    Write-Host "路径: $FilePath"

    # 检查是否已有有效签名，跳过
    if (Test-FileHasValidSignature -FilePath $FilePath) {
        Write-Host "SKIPPED: 文件已有有效签名，跳过"
        Write-Host ""
        return "skipped"
    }

    $attempts = @(
        @{ Name = "SHA1 thumbprint + /td SHA256"; Args = @("sign", "/sha1", $NormalizedSha1, "/tr", $TimestampServer, "/td", "SHA256", "/fd", "SHA256", "/v", $FilePath) },
        @{ Name = "SHA1 thumbprint in CurrentUser\My"; Args = @("sign", "/sha1", $NormalizedSha1, "/s", "My", "/tr", $TimestampServer, "/td", "SHA256", "/fd", "SHA256", "/v", $FilePath) },
        @{ Name = "SHA1 thumbprint in LocalMachine\My"; Args = @("sign", "/sha1", $NormalizedSha1, "/sm", "/s", "My", "/tr", $TimestampServer, "/td", "SHA256", "/fd", "SHA256", "/v", $FilePath) },
        @{ Name = "Auto-select cert (fallback)"; Args = @("sign", "/a", "/tr", $TimestampServer, "/td", "SHA256", "/fd", "SHA256", "/v", $FilePath) }
    )

    $signed = $false
    foreach ($attempt in $attempts) {
        Write-Host "尝试: $($attempt.Name)"
        $signOutput = & $SignTool @($attempt.Args) 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "SUCCESS: $($attempt.Name)"
            $signed = $true
            break
        }
        Write-Host "FAILED: $($attempt.Name)"
        Write-Host "signtool 返回非零退出码；详细输出已隐藏"
    }

    if ($signed) {
        $verifyOutput = & $SignTool verify /pa /v $FilePath 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "VERIFIED: 签名验证成功"
        } else {
            Write-Host "WARNING: 签名验证失败"
        }
        Write-Host ""
        return "signed"
    } else {
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
    Write-Host "=== 生成 CAT 目录文件 ==="

    # 收集需要纳入 CAT 的文件（EXE/DLL/SYS）
    $targetFiles = Get-ChildItem -Path $TargetDirectory -Recurse -File |
        Where-Object { $_.Extension -iin @(".exe", ".dll", ".sys") }

    if (($null -eq $targetFiles) -or ($targetFiles.Count -eq 0)) {
        Write-Host "WARNING: 未找到可纳入 CAT 的文件，跳过 CAT 生成"
        return $false
    }

    # 生成 .cdf 描述文件
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
    Write-Host "已生成 CDF 文件: $cdfPath，包含 $($targetFiles.Count) 个文件"

    # 运行 makecat 生成 CAT
    Write-Host "运行 makecat 生成 CAT 文件..."
    $makecatOutput = & $MakeCat $cdfPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: makecat 失败，退出码 $LASTEXITCODE"
        Write-Host "详细输出已隐藏"
        return $false
    }

    if (-not (Test-Path $catOutputPath)) {
        Write-Host "ERROR: CAT 文件未生成: $catOutputPath"
        return $false
    }

    Write-Host "CAT 文件已生成: $catOutputPath"

    # 签名 CAT 文件
    Write-Host "对 CAT 文件进行签名..."
    $result = Invoke-SignFile -FilePath $catOutputPath -SignTool $SignTool -NormalizedSha1 $NormalizedSha1 -TimestampServer $TimestampServer
    if ($result -eq "failed") {
        Write-Host "ERROR: CAT 文件签名失败"
        return $false
    }

    Write-Host "CAT 文件签名完成"
    return $true
}

# ============================================================
# 主流程
# ============================================================

Write-Host "=== WINDOWS BINARY SIGNING (CERTUM SIMPLYSIGN) ==="
Write-Host "目标目录: $TargetDirectory"

if (-not (Test-Path $TargetDirectory)) {
    Write-Host "ERROR: 目标目录不存在: $TargetDirectory"
    exit 1
}

if (-not $CertificateSHA1) {
    Write-Host "ERROR: 未提供 CERTUM_CERTIFICATE_SHA1 环境变量"
    exit 1
}

$normalizedSha1 = ($CertificateSHA1 -replace "[^a-fA-F0-9]", "").ToUpperInvariant()
if ($normalizedSha1.Length -ne 40) {
    Write-Host "ERROR: CERTUM_CERTIFICATE_SHA1 规范化后无效"
    Write-Host "原始长度: $($CertificateSHA1.Length)，规范化后长度: $($normalizedSha1.Length)"
    exit 1
}

Write-Host "签名证书指纹已接收（已隐藏）"

$targetCerts = Find-TargetCertificate -Thumbprint $normalizedSha1
if (($null -eq $targetCerts) -or ($targetCerts.Count -eq 0)) {
    Write-Host "ERROR: 在证书库中未找到目标证书"
    Write-Host "认证可能失败或 CERTUM_CERTIFICATE_SHA1 不正确"
    Show-PrivateKeyCertificateHints
    exit 1
}

$targetWithPrivateKey = @($targetCerts | Where-Object { $_.HasPrivateKey })
if (($null -eq $targetWithPrivateKey) -or ($targetWithPrivateKey.Count -eq 0)) {
    Write-Host "ERROR: 目标证书存在但无可用私钥"
    Show-PrivateKeyCertificateHints
    exit 1
}

Write-Host "正在查找 signtool..."
$signTool = Get-LatestSignToolPath
if (-not $signTool) {
    Write-Host "ERROR: 未找到 signtool.exe"
    exit 1
}
Write-Host "找到 signtool: $signTool"

# 第一阶段：签名 EXE / DLL / SYS
Write-Host ""
Write-Host "=== 第一阶段：签名 EXE / DLL / SYS ==="
$filesToSign = Get-ChildItem -Path $TargetDirectory -Recurse -File |
    Where-Object { $_.Extension -iin @(".exe", ".dll", ".sys") }

if (($null -eq $filesToSign) -or ($filesToSign.Count -eq 0)) {
    Write-Host "WARNING: 未找到可签名文件 (.exe, .dll, .sys)"
} else {
    Write-Host "找到 $($filesToSign.Count) 个文件"
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

    Write-Host "=== 第一阶段汇总 ==="
    Write-Host "总文件数: $($filesToSign.Count)"
    Write-Host "已签名:   $signedCount"
    Write-Host "已跳过:   $skippedCount（已有有效签名）"
    Write-Host "失败:     $failedCount"

    if ($failedCount -gt 0) {
        Write-Host "ERROR: 部分文件签名失败"
        exit 1
    }
}

# 第二阶段：生成并签名 CAT
if (-not $SkipCatGeneration) {
    Write-Host ""
    Write-Host "=== 第二阶段：生成并签名 CAT ==="
    $makeCat = Get-MakeCatPath
    if (-not $makeCat) {
        Write-Host "WARNING: 未找到 makecat.exe，跳过 CAT 生成"
        Write-Host "如需生成 CAT，请确保已安装 Windows Driver Kit (WDK)"
    } else {
        Write-Host "找到 makecat: $makeCat"
        $catResult = New-CatalogFile `
            -TargetDirectory (Resolve-Path $TargetDirectory).Path `
            -MakeCat $makeCat `
            -SignTool $signTool `
            -NormalizedSha1 $normalizedSha1 `
            -TimestampServer $TimestampServer

        if (-not $catResult) {
            Write-Host "WARNING: CAT 生成或签名失败，但不阻断整体流程"
        }
    }
} else {
    Write-Host "已通过参数跳过 CAT 生成"
}

Write-Host ""
Write-Host "=== 所有 WINDOWS 二进制文件签名完成 ==="
exit 0