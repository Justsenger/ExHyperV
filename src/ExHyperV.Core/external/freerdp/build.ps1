param(
    [Parameter(Mandatory)][string]$DependencyRoot,
    [Parameter(Mandatory)][string]$DependencyPrefix,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [ValidateSet('arm64','x64')][string]$Architecture='arm64'
)
$ErrorActionPreference='Stop'
$dep=(Resolve-Path -LiteralPath $DependencyRoot).Path.Replace('\','/')
$sourcePrefix=(Resolve-Path -LiteralPath $DependencyPrefix).Path
$work=[IO.Path]::GetFullPath($WorkDirectory).Replace('\','/')
if($work.StartsWith($dep,[StringComparison]::OrdinalIgnoreCase)){throw 'Use a dedicated build directory, outside the reference dependency tree.'}
$source="$dep/upstream"
$revision=(& git -C $source rev-parse HEAD).Trim()
if($revision -ne '97eff6ddfbbfaa8e42a126d362686bd001ff8bdc'){throw 'Expected the pinned FreeRDP 3.32.2 source.'}
$llvm="$dep/tools/llvm-mingw-20261020-ucrt-aarch64/bin"
$cmake="$dep/tools/cmake-4.4.4-windows-arm64/bin/cmake.exe"
$prefix="$work/prefix"
# Runtime defaults must describe the distributable application, not a developer's
# staging directory. Actual files are installed only under $work below.
$runtimePrefix='C:/Program Files/ExHyperV/console-native'
New-Item -ItemType Directory -Force $work | Out-Null
if(!(Test-Path -LiteralPath $prefix)){Copy-Item -LiteralPath $sourcePrefix -Destination $prefix -Recurse}
$triple=if($Architecture -eq 'arm64'){'aarch64'}else{'x86_64'}
$machine=if($Architecture -eq 'arm64'){0xaa64}else{0x8664}
$toolchain="$work/toolchain.cmake"
@"
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR $triple)
set(CMAKE_C_COMPILER "$llvm/$triple-w64-mingw32-clang.exe")
set(CMAKE_CXX_COMPILER "$llvm/$triple-w64-mingw32-clang++.exe")
set(CMAKE_RC_COMPILER "$llvm/$triple-w64-mingw32-windres.exe")
set(CMAKE_MAKE_PROGRAM "$llvm/mingw32-make.exe" CACHE FILEPATH "")
set(CMAKE_C_FLAGS_INIT "-D__STDC_NO_THREADS__=1")
"@ | Set-Content -LiteralPath $toolchain
function Run([string]$Name,[string[]]$Arguments){
    & $cmake @Arguments *> "$work/$Name.log"
    $code=$LASTEXITCODE;Set-Content -LiteralPath "$work/$Name.exit" -Value $code
    if($code -ne 0){Get-Content "$work/$Name.log" -Tail 35;throw "$Name failed ($code)."}
}
function RunTool([string]$Name,[string]$Executable,[string[]]$Arguments){
    & $Executable @Arguments *> "$work/$Name.log"
    $code=$LASTEXITCODE;Set-Content -LiteralPath "$work/$Name.exit" -Value $code
    if($code -ne 0){Get-Content "$work/$Name.log" -Tail 35;throw "$Name failed ($code)."}
}

# Rebuild OpenSSL rather than copying DLLs containing the supplier's local prefix.
# Relative source/compiler names also keep buildinf's embedded CFLAGS portable;
# putting an absolute -ffile-prefix-map argument there would disclose it again.
$opensslSource="$work/openssl-source"
$opensslBuild="$work/openssl-build"
if(!(Test-Path -LiteralPath $opensslSource)){Copy-Item -LiteralPath "$dep/openssl-openssl-3.5.9" -Destination $opensslSource -Recurse}
@'
my %targets = (
    "exhyperv-mingw-arm64" => {
        inherit_from => [ "mingw-common" ],
        CC => "aarch64-w64-mingw32-clang", AR => "llvm-ar",
        RANLIB => "llvm-ranlib", RC => "aarch64-w64-mingw32-windres",
        sys_id => "MINGW64", bn_ops => add("SIXTY_FOUR_BIT"),
        asm_arch => 'aarch64', uplink_arch => undef,
        perlasm_scheme => "win64", shared_rcflag => "", multilib => "arm64",
    },
);
'@ | Set-Content -LiteralPath "$opensslSource/Configurations/99-exhyperv.conf"
$perl="$dep/tools/perl/perl/bin/perl.exe"
$make="$dep/tools/perl/c/bin/gmake.exe"
$gitRoot=Split-Path (Split-Path (Get-Command git.exe -ErrorAction Stop).Source)
$shellBin=Join-Path $gitRoot 'usr/bin'
if(!(Test-Path -LiteralPath "$shellBin/sh.exe")){throw 'OpenSSL needs the Unix tools shipped with Git for Windows.'}
$previousPath=$env:PATH
$previousPerlOptions=$env:PERL5OPT
$previousPerlLibrary=$env:PERL5LIB
# Strawberry Perl uses Windows separators, while OpenSSL's MinGW makefiles use
# Unix syntax. Adapt path formatting for these build subprocesses only.
@'
package ExHyperVForwardPaths;
use strict;
use warnings;
use File::Spec;
for my $name (qw(canonpath catdir catfile rel2abs abs2rel)) {
    my $original = File::Spec->can($name);
    no strict 'refs';
    no warnings 'redefine';
    *{"File::Spec::$name"} = sub {
        my $path = $original->(@_);
        $path =~ tr!\\!/!;
        return $path;
    };
}
1;
'@ | Set-Content -LiteralPath "$work/ExHyperVForwardPaths.pm"
try {
    $env:PATH="$llvm;$dep/tools/perl/perl/bin;$dep/tools/perl/c/bin;$shellBin;$previousPath"
    $env:PERL5LIB=$work
    $env:PERL5OPT='-MExHyperVForwardPaths'
    New-Item -ItemType Directory -Force $opensslBuild | Out-Null
    Push-Location $opensslBuild
    try {
        $opensslTarget=if($Architecture -eq 'arm64'){'exhyperv-mingw-arm64'}else{'mingw64'}
        RunTool 'openssl-configure' $perl @('../openssl-source/Configure',$opensslTarget,'no-asm','no-tests','no-docs','shared',
            "--prefix=$runtimePrefix","--openssldir=$runtimePrefix/ssl",'--libdir=lib',
            "CC=$triple-w64-mingw32-clang",'AR=llvm-ar','RANLIB=llvm-ranlib',"RC=$triple-w64-mingw32-windres")
        RunTool 'openssl-build' $make @('-j','4','build_libs','PERL=perl')
    } finally {Pop-Location}
} finally {$env:PATH=$previousPath;$env:PERL5OPT=$previousPerlOptions;$env:PERL5LIB=$previousPerlLibrary}
# Keep installation separate from runtime defaults. The existing prefix supplies
# zlib; replace only the rebuilt OpenSSL libraries and its generated/public headers.
Get-ChildItem -LiteralPath $opensslBuild -File -Filter 'lib*-3*.dll' | Copy-Item -Destination "$prefix/bin"
foreach($name in @('libcrypto.dll.a','libssl.dll.a')){Copy-Item -LiteralPath "$opensslBuild/$name" -Destination "$prefix/lib"}
Get-ChildItem -LiteralPath "$opensslSource/include/openssl" -File -Filter '*.h' | Copy-Item -Destination "$prefix/include/openssl"
Get-ChildItem -LiteralPath "$opensslBuild/include/openssl" -File -Filter '*.h' | Copy-Item -Destination "$prefix/include/openssl"

$options=@('--preset','minimal-library','-S',$source,'-B',"$work/freerdp",'-G','MinGW Makefiles',
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain","-DCMAKE_INSTALL_PREFIX=$runtimePrefix","-DCMAKE_PREFIX_PATH=$prefix",'-DCMAKE_BUILD_TYPE=Release',
    '-DCMAKE_C_FLAGS=-D__STDC_NO_THREADS__=1 -UWINPR_DEFINE_ATTR_NODISCARD -Wno-pre-c23-compat','-DCMAKE_POLICY_VERSION_MINIMUM=3.5',
    '-DWITH_OPENSSL=ON','-DWITH_MBEDTLS=OFF',"-DOPENSSL_ROOT_DIR=$prefix",'-DWITH_CLIENT_COMMON=ON','-DWITH_CLIENT_WINDOWS=OFF','-DWITH_CLIENT_SDL=OFF',
    '-DWITH_SERVER=OFF','-DWITH_WINPR_TOOLS=OFF','-DWITH_NATIVE_SSPI=ON','-DBUILD_TESTING=OFF','-DWITH_MANPAGES=OFF','-DWITH_CHANNELS=ON',
    '-DWITH_SMARTCARD_EMULATE=OFF','-DWITH_KRB5=OFF','-DWITH_MEDIA_FOUNDATION=OFF','-DUSE_UNWIND=OFF')
foreach($channel in Get-ChildItem -LiteralPath "$source/channels" -Directory){if(Test-Path -LiteralPath "$($channel.FullName)/ChannelOptions.cmake"){
    $enabled=if($channel.Name -in @('drdynvc','disp','rdpgfx','cliprdr')){'ON'}else{'OFF'}
    $options+="-DCHANNEL_$($channel.Name.ToUpperInvariant())=$enabled"
    $options+="-DCHANNEL_$($channel.Name.ToUpperInvariant())_CLIENT=$enabled"
    $options+="-DCHANNEL_$($channel.Name.ToUpperInvariant())_SERVER=OFF"
}}
Run 'freerdp-configure' $options
Run 'freerdp-build' @('--build',"$work/freerdp",'-j','4')
Run 'freerdp-install' @('--install',"$work/freerdp",'--prefix',$prefix)
Run 'bridge-configure' @('-S',$PSScriptRoot,'-B',"$work/bridge",'-G','MinGW Makefiles',"-DCMAKE_TOOLCHAIN_FILE=$toolchain","-DCMAKE_PREFIX_PATH=$prefix",'-DCMAKE_BUILD_TYPE=Release')
Run 'bridge-build' @('--build',"$work/bridge",'-j','4')
$out=Join-Path $work ('package-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item -LiteralPath "$work/bridge/ExHyperV.FreeRdp.dll" -Destination $out
$queue=[Collections.Generic.Queue[string]]::new();$queue.Enqueue((Join-Path $out 'ExHyperV.FreeRdp.dll'))
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
while($queue.Count){
    $file=$queue.Dequeue();$bytes=[IO.File]::ReadAllBytes($file);$pe=[BitConverter]::ToInt32($bytes,0x3c)
    if([BitConverter]::ToUInt16($bytes,$pe+4) -ne $machine){throw "Architecture mismatch: $file"}
    $imports=& "$llvm/llvm-readobj.exe" --coff-imports $file
    if($LASTEXITCODE -ne 0){throw "Cannot inspect imports: $file"}
    foreach($line in $imports){if($line -match '^\s+Name: (.+\.dll)$'){
        $name=$Matches[1];if(!$seen.Add($name)){continue}
        $dll=@("$prefix/bin/$name","$llvm/$name") | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
        if($dll){$target=Join-Path $out $name;Copy-Item -LiteralPath $dll -Destination $target;$queue.Enqueue($target)}
        elseif(!(Test-Path -LiteralPath (Join-Path ([Environment]::SystemDirectory) $name)) -and $name -notmatch '^(api|ext)-ms-'){throw "Unresolved dependency: $name"}
    }}
}
RunTool 'strip-debug' "$llvm/llvm-strip.exe" (@('--strip-debug')+@(Get-ChildItem -LiteralPath $out -Filter '*.dll' -File | Select-Object -ExpandProperty FullName))
# Reject development roots in actual payload strings before creating a package.
$privateRoots=@($dep,$work,$PSScriptRoot,[Environment]::GetFolderPath('UserProfile')) |
    Where-Object {$_} | ForEach-Object {$_.Replace('\','/');$_.Replace('/','\')} | Sort-Object -Unique
foreach($dll in Get-ChildItem -LiteralPath $out -Filter '*.dll' -File){
    $bytes=[IO.File]::ReadAllBytes($dll.FullName)
    foreach($encoding in @([Text.Encoding]::GetEncoding(28591),[Text.Encoding]::Unicode,[Text.Encoding]::BigEndianUnicode)){
        $text=$encoding.GetString($bytes)
        foreach($privateRoot in $privateRoots){
            if($text.IndexOf($privateRoot,[StringComparison]::OrdinalIgnoreCase) -ge 0){throw "Development path remains in $($dll.Name); package not written."}
        }
    }
}
Copy-Item -LiteralPath "$source/LICENSE" -Destination "$out/LICENSE-FreeRDP.txt"
Copy-Item -LiteralPath "$dep/openssl-openssl-3.5.9/LICENSE.txt" -Destination "$out/LICENSE-OpenSSL.txt"
Copy-Item -LiteralPath "$dep/zlib/LICENSE" -Destination "$out/LICENSE-zlib.txt"
Copy-Item -LiteralPath "$PSScriptRoot/LICENSE-IronRDP-MIT.txt" -Destination $out
$hashes=[ordered]@{}
Get-ChildItem -LiteralPath $out -Filter '*.dll' -File | Sort-Object Name | ForEach-Object{$hashes[$_.Name]=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
[IO.File]::WriteAllText("$out/sha256.json",($hashes|ConvertTo-Json))
$checks="$work/checks";New-Item -ItemType Directory -Force $checks | Out-Null
Copy-Item -LiteralPath "$work/bridge/ExHyperV.FreeRdpChecks.exe" -Destination $checks
Get-ChildItem -LiteralPath $out -Filter '*.dll' | Copy-Item -Destination $checks
& "$checks/ExHyperV.FreeRdpChecks.exe" *> "$work/selftest.log"
$code=$LASTEXITCODE;Set-Content -LiteralPath "$work/selftest.exit" -Value $code
if($code -ne 0){Get-Content "$work/selftest.log";throw "Native selftest failed ($code)."}
$zip=Join-Path $PSScriptRoot "runtimes/win-$Architecture/console-native.zip"
Compress-Archive -Path "$out/*" -DestinationPath $zip -Force
Write-Output "FreeRDP bridge built and offline checks passed: $zip"
