[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FilePath,

    [string]$CertificateSHA1 = $env:CERTUM_CERTIFICATE_SHA1,

    [string]$TimestampServer = "http://time.certum.pl"
)

$ErrorActionPreference = "Stop"

function Find-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $kits) {
        $candidate = Get-ChildItem -LiteralPath $kits -Recurse -File -Filter "signtool.exe" -ErrorAction SilentlyContinue |
            Where-Object FullName -Match '\\x64\\signtool\.exe$' |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw "signtool.exe was not found"
}

$resolvedFile = (Resolve-Path -LiteralPath $FilePath).Path
$existingSignature = Get-AuthenticodeSignature -FilePath $resolvedFile
if ($existingSignature.Status -eq "Valid") {
    Write-Host "Already signed: $resolvedFile"
    exit 0
}

$thumbprint = ($CertificateSHA1 -replace '[^a-fA-F0-9]', '').ToUpperInvariant()
if ($thumbprint.Length -ne 40) {
    throw "CERTUM_CERTIFICATE_SHA1 is missing or invalid"
}

$certificate = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
    Where-Object {
        $_.HasPrivateKey -and
        (($_.Thumbprint -replace '[^a-fA-F0-9]', '').ToUpperInvariant() -eq $thumbprint)
    } |
    Select-Object -First 1
if (-not $certificate) {
    throw "The configured signing certificate is unavailable"
}

$signTool = Find-SignTool
for ($attempt = 1; $attempt -le 10; $attempt++) {
    & $signTool sign /sha1 $thumbprint /tr $TimestampServer /fd SHA256 /td SHA256 /v $resolvedFile
    if ($LASTEXITCODE -eq 0) {
        & $signTool verify /pa $resolvedFile
        if ($LASTEXITCODE -eq 0) { exit 0 }
    }
    if ($attempt -lt 10) { Start-Sleep -Seconds 5 }
}

throw "Signing failed after 10 attempts: $resolvedFile"
