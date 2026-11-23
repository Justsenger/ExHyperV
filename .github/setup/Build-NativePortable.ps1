[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MSBuild,
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][ValidateSet("arm64","x64")][string]$Architecture,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$NumericVersion
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
& (Join-Path $repo 'tools/Use-RepositoryDotNet.ps1')
$null = New-Item -Type Directory -Force -Path $WorkDirectory
$tool = Join-Path $repo 'tools/winui-publish/WinUI.Publish.csproj'
& $MSBuild $tool /restore /t:Build /nologo /nr:false /v:q /p:Configuration=Release /p:UseArtifactsOutput=true "/p:ArtifactsPath=$WorkDirectory/tool"
if ($LASTEXITCODE) { throw "Native archive tool build failed" }
$assets = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $WorkDirectory 'obj/ExHyperV.WinUI3/project.assets.json') | ConvertFrom-Json
$pack = "Microsoft.NETCore.App.Runtime.NativeAOT.win-$Architecture"
$download = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object name -eq $pack | Select-Object -First 1
if (!$download -or $download.version -notmatch '^\[([^,\]]+)') { throw "Cannot resolve the NativeAOT runtime pack for $Architecture" }
$packVersion = $Matches[1]
$lib = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
    Join-Path $_ "$($pack.ToLowerInvariant())/$packVersion/runtimes/win-$Architecture/native"
} | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'brotlidec.lib') } | Select-Object -First 1
if (!$lib) { throw "The Brotli decoder was not found in $pack $packVersion" }
$packRoot = (Get-Item -LiteralPath $lib).Parent.Parent.Parent.FullName
$notices = Join-Path $PublishDirectory 'ThirdPartyNotices'
$null = New-Item -Type Directory -Force -Path $notices
Copy-Item -LiteralPath (Join-Path $packRoot 'LICENSE.TXT') -Destination (Join-Path $notices 'DotNet.txt') -Force
Copy-Item -LiteralPath (Join-Path $packRoot 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $notices 'DotNet-ThirdParty.txt') -Force
$payload = Join-Path $WorkDirectory 'payload.br'
& dotnet (Join-Path $WorkDirectory 'tool/bin/WinUI.Publish/release/WinUI.Publish.dll') native-archive $PublishDirectory $payload "win-$Architecture"
if ($LASTEXITCODE) { throw "Invalid native application payload" }

$source = Join-Path $repo 'src/ExHyperV.WinUI3'
$icon = (Join-Path $source 'Assets/AppIcon.ico').Replace('\','/')
$manifest = (Join-Path $source 'app.manifest').Replace('\','/')
$payloadPath = $payload.Replace('\','/')
$versionFields = $NumericVersion.Replace('.',',')
$rc = Join-Path $WorkDirectory 'Portable.rc'
@"
#include <windows.h>
1 ICON "$icon"
1 RT_MANIFEST "$manifest"
101 RCDATA "$payloadPath"
1 VERSIONINFO
 FILEVERSION $versionFields
 PRODUCTVERSION $versionFields
 FILEOS VOS_NT_WINDOWS32
 FILETYPE VFT_APP
BEGIN
 BLOCK "StringFileInfo"
 BEGIN
  BLOCK "040904b0"
  BEGIN
   VALUE "FileDescription", "ExHyperV"
   VALUE "FileVersion", "$NumericVersion"
   VALUE "InternalName", "ExHyperV"
   VALUE "OriginalFilename", "ExHyperV.exe"
   VALUE "ProductName", "ExHyperV"
   VALUE "ProductVersion", "$Version"
  END
 END
 BLOCK "VarFileInfo"
 BEGIN
  VALUE "Translation", 0x409, 1200
 END
END
"@ | Set-Content -LiteralPath $rc -Encoding Unicode
# The RC compiler does not track changes to RCDATA embedded through a generated .rc.
# The resource script is regenerated for every new payload.
$platform = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
$project = Join-Path $repo 'tools/native-portable/Portable.vcxproj'
& $MSBuild $project /t:Build /nologo /nr:false /v:m /p:Configuration=Release "/p:Platform=$platform" "/p:BrotliLibraryDirectory=$lib" "/p:PortableResourceFile=$rc" "/p:OutDir=$WorkDirectory/portable/" "/p:IntDir=$WorkDirectory/launcher-obj/"
if ($LASTEXITCODE) { throw "Native portable launcher build failed for $Architecture" }
