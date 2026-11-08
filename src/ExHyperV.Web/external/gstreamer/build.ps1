param(
    [Parameter(Mandatory)][string]$GStreamerRoot,
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$ReadObj,
    [Parameter(Mandatory)][ValidateSet('win-arm64','win-x64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$sdk = (Resolve-Path -LiteralPath $GStreamerRoot).Path
$output = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; this script never merges an old runtime.' }
$null = New-Item -ItemType Directory -Path "$output/bin", "$output/lib/gstreamer-1.0", "$output/libexec/gstreamer-1.0" -Force
$include = $sdk.Replace('\','/')
$compileArgs = @('-shared','-O2','-std=c11','-D_WIN32_WINNT=0x0a00','-DG_DISABLE_CAST_CHECKS',
    "-I$include/include/gstreamer-1.0", "-I$include/include/glib-2.0", "-I$include/lib/glib-2.0/include",
    (Join-Path $PSScriptRoot 'web_media.c'))
# Explicit MSVC import libraries: some vendor SDKs also contain import archives for another architecture.
foreach ($name in 'gstapp-1.0','gstvideo-1.0','gstwebrtc-1.0','gstsdp-1.0','gstreamer-1.0','gobject-2.0','glib-2.0') {
    $compileArgs += "$sdk/lib/$name.lib"
}
$compileArgs += @('-Wl,--exclude-all-symbols','-o',"$output/bin/exhyperv-web-media.dll")
& $Compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Native bridge compilation failed.' }
$plugins = @('coreelements','app','videoconvertscale','x264','mediafoundation',
    'webrtc','nice','dtls','srtp','rtp','rtpmanager','videoparsersbad','typefindfunctions','udp','sctp')
foreach ($name in $plugins) {
    Copy-Item -LiteralPath "$sdk/lib/gstreamer-1.0/gst$name.dll" -Destination "$output/lib/gstreamer-1.0/"
}
Copy-Item -LiteralPath "$sdk/libexec/gstreamer-1.0/gst-plugin-scanner.exe" -Destination "$output/libexec/gstreamer-1.0/"

$queue = [Collections.Generic.Queue[string]]::new()
Get-ChildItem -LiteralPath $output -Recurse -File | ForEach-Object { $queue.Enqueue($_.FullName) }
$visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$system = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$architecture = if ($RuntimeIdentifier -eq 'win-arm64') { 'COFF-ARM64' } else { 'COFF-x86-64' }
while ($queue.Count) {
    $file = $queue.Dequeue()
    if (!$visited.Add($file)) { continue }
    $imports = & $ReadObj --coff-imports $file
    if ($LASTEXITCODE -ne 0 -or !($imports -contains "Format: $architecture")) { throw "Wrong architecture: $file" }
    foreach ($line in $imports) {
        if ($line -notmatch '^\s+Name: (.+)$') { continue }
        $name = $Matches[1]
        if (Test-Path -LiteralPath "$sdk/bin/$name") {
            if (!(Test-Path -LiteralPath "$output/bin/$name")) { Copy-Item -LiteralPath "$sdk/bin/$name" -Destination "$output/bin/" }
            $queue.Enqueue("$output/bin/$name")
        } else { $null = $system.Add($name) }
    }
}
# Preserve upstream notices, including transitive libraries. Unused notices do not imply unused plugins are shipped.
Copy-Item -LiteralPath "$sdk/share/licenses" -Destination "$output/licenses" -Recurse
$files = Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object Extension -In '.dll','.exe' | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($output,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;bytes=$_.Length}
}
[ordered]@{runtime=$RuntimeIdentifier;gstreamer='1.28.8';origin='https://gstreamer.freedesktop.org/data/pkg/windows/1.28.8/msvc/';
    systemDependencies=@($system | Sort-Object);files=@($files)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$output/manifest.json" -Encoding utf8
Write-Output "Packaged $($files.Count) native binaries to $output"
