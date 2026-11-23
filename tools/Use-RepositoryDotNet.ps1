# Prefer the workspace SDK for both dotnet and the Visual Studio MSBuild resolver.
$localRoot = Join-Path (Split-Path -Parent $PSScriptRoot) '.dotnet'
if ((Test-Path -LiteralPath (Join-Path $localRoot 'dotnet.exe')) -and
    (Get-ChildItem -Path (Join-Path $localRoot 'sdk/11.*') -Directory -ErrorAction SilentlyContinue)) {
    $env:DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR = $localRoot
    $env:DOTNET_ROOT = $localRoot
    if (($env:PATH -split ';', 2)[0] -ne $localRoot) { $env:PATH = "$localRoot;$env:PATH" }
}
