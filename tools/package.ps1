# Builds the portable v0 release: artifacts/SpiceViewer-<version>-<rid>.zip
# Usage: pwsh tools/package.ps1 [-Rid win-x64] [-NoPublish]
# Needs the native DLLs first (bash native/build.sh x64) and, for Native AOT on Windows, the MSVC linker.
param([string]$Rid = "win-x64", [switch]$NoPublish)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src/SpiceViewer/SpiceViewer.csproj"
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$publish = Join-Path $root "out/$Rid"
$arch = $Rid.Split('-')[1]
$native = Join-Path $root "native"
$deps = Join-Path $native "downloads"

if (-not $NoPublish) {
    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        # The AOT linker locates MSVC through vswhere.
        $installer = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
        if (Test-Path $installer) { $env:PATH = "$installer;$env:PATH" }
    }
    dotnet publish $proj -c Release -r $Rid -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

$name = "SpiceViewer-$version-$Rid"
$artifacts = Join-Path $root "artifacts"
$stage = Join-Path $artifacts $name
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

foreach ($f in "SpiceViewer.exe", "spicecodec.dll", "turbojpeg.dll", "opus.dll") {
    $src = Join-Path $publish $f
    if (-not (Test-Path $src)) { throw "missing $src (build native DLLs with: bash native/build.sh $arch)" }
    Copy-Item $src $stage
}

(Get-Content -Raw (Join-Path $PSScriptRoot "package/README.txt")).Replace("{VERSION}", $version).Replace("{RID}", $Rid) |
    Set-Content -NoNewline -Encoding utf8 (Join-Path $stage "README.txt")
# LICENSE.txt: this program's license, then every component built into the package with its version, origin and
# full license text. Fixed texts live in licenses/; version-specific ones come from the pinned downloads and NuGet.
$jpeg = Get-ChildItem $deps -Directory -Filter "libjpeg-turbo-*" | Select-Object -First 1
$opus = Get-ChildItem $deps -Directory -Filter "opus-*" | Select-Object -First 1
$lz4 = Get-ChildItem $deps -Directory -Filter "lz4-*" | Select-Object -First 1
if (-not $jpeg -or -not $opus -or -not $lz4) { throw "native/downloads is missing libjpeg-turbo, opus or lz4 sources (run bash native/fetch.sh)" }
$lic = Join-Path $root "licenses"
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME ".nuget/packages" }
$assets = Get-Content -Raw (Join-Path $root "src/SpiceViewer/obj/project.assets.json") | ConvertFrom-Json
function PackageVersion([string]$id) {
    $key = $assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like "$id/*" } | Select-Object -First 1
    if (-not $key) { throw "$id is not in project.assets.json (restore/publish first)" }
    $key.Split('/')[1]
}
$ilcId = "runtime.$Rid.Microsoft.DotNet.ILCompiler"
$ilcVersion = PackageVersion $ilcId
$csWin32Version = PackageVersion "Microsoft.Windows.CsWin32"
$ilcNotices = Join-Path $nuget "$($ilcId.ToLowerInvariant())/$ilcVersion/THIRD-PARTY-NOTICES.TXT"
if (-not (Test-Path $ilcNotices)) { throw "missing $ilcNotices" }

$components = @(
    @{ Name = "SpiceViewer $version"; In = "SpiceViewer.exe; codec bridge in spicecodec.dll"; License = "MIT"
       Url = "this package; full source with the project"; Texts = @((Join-Path $root "LICENSE")) },
    @{ Name = ".NET runtime $ilcVersion (Native AOT, compiled into the exe)"; In = "SpiceViewer.exe"; License = "MIT, plus the notices of the components it includes"
       Url = "https://github.com/dotnet/runtime"; Texts = @((Join-Path $lic "dotnet-runtime-MIT.txt"), $ilcNotices) },
    @{ Name = "CsWin32 $csWin32Version (generated Win32 interop declarations)"; In = "SpiceViewer.exe"; License = "MIT"
       Url = "https://github.com/microsoft/CsWin32"; Texts = @((Join-Path $lic "cswin32-MIT.txt")) },
    @{ Name = "SpiceSee codec bridge and shims (commit 41435442)"; In = "spicecodec.dll"; License = "MIT"
       Url = "SpiceSee, Packages/CSpiceCodec; see source\native\VENDORED.md"; Texts = @((Join-Path $native "LICENSE.SpiceSee")) },
    @{ Name = "spice-common QUIC/LZ decoders (commit 58d375e5), spice-gtk GLZ decoder (v0.42)"; In = "spicecodec.dll"; License = "LGPL-2.1-or-later (modified; see the notices in each file)"
       Url = "https://gitlab.freedesktop.org/spice/spice-common, https://gitlab.freedesktop.org/spice/spice-gtk"; Texts = @((Join-Path $native "LICENSE.LGPL-2.1")) },
    @{ Name = "FastLZ (incorporated in spice-common lz.c)"; In = "spicecodec.dll"; License = "MIT"
       Url = "https://github.com/ariya/FastLZ"; Texts = @((Join-Path $lic "fastlz-MIT.txt")) },
    @{ Name = "spice-protocol headers (v0.14.3)"; In = "spicecodec.dll"; License = "BSD-3-Clause (macros.h: LGPL-2.1-or-later)"
       Url = "https://gitlab.freedesktop.org/spice/spice-protocol"; Texts = @((Join-Path $lic "spice-protocol-BSD-3-Clause.txt")) },
    @{ Name = "$($lz4.Name) (block decoder, lib/lz4.c)"; In = "spicecodec.dll"; License = "BSD-2-Clause"
       Url = "https://github.com/lz4/lz4"; Texts = @((Join-Path $lz4.FullName "lib/LICENSE")) },
    @{ Name = $jpeg.Name; In = "turbojpeg.dll"; License = "IJG, BSD-3-Clause and zlib"
       Url = "https://libjpeg-turbo.org"; Texts = @((Join-Path $jpeg.FullName "LICENSE.md"), (Join-Path $jpeg.FullName "README.ijg")) },
    @{ Name = $opus.Name; In = "opus.dll"; License = "BSD-3-Clause, with royalty-free patent licenses"
       Url = "https://opus-codec.org"; Texts = @((Join-Path $opus.FullName "COPYING")) },
    @{ Name = "MinGW-w64 runtime (statically linked startup and support code)"; In = "spicecodec.dll, turbojpeg.dll, opus.dll"; License = "ZPL-2.1, public domain and others listed below"
       Url = "https://www.mingw-w64.org"; Texts = @((Join-Path $lic "mingw-w64-runtime.txt")) },
    @{ Name = "LLVM compiler-rt builtins (statically linked)"; In = "spicecodec.dll, turbojpeg.dll, opus.dll"; License = "Apache-2.0 WITH LLVM-exception"
       Url = "https://llvm.org"; Texts = @((Join-Path $lic "llvm-compiler-rt.txt")) }
)
$rule = "=" * 78
$t = [System.Text.StringBuilder]::new()
[void]$t.AppendLine("SpiceViewer $version ($Rid) - license information").AppendLine()
[void]$t.AppendLine("SpiceViewer is free software under the MIT License. This package also contains the components")
[void]$t.AppendLine("below, each under its own license; their full texts follow, in the same order.").AppendLine()
[void]$t.AppendLine("spicecodec.dll is a library under the GNU Lesser General Public License, version 2.1 or later.")
[void]$t.AppendLine("You may replace it with your own modified build: its complete source and the scripts that build")
[void]$t.AppendLine("it are in the source\native folder (start with source\native\README.md).").AppendLine()
[void]$t.AppendLine("The Win32 interop declarations in SpiceViewer.exe are generated by CsWin32 from Microsoft's")
[void]$t.AppendLine("Windows SDK metadata (Microsoft.Windows.SDK.Win32Metadata).").AppendLine()
$i = 0
foreach ($c in $components) {
    $i++
    [void]$t.AppendLine(("{0,2}. {1}" -f $i, $c.Name))
    [void]$t.AppendLine("    in:      $($c.In)").AppendLine("    license: $($c.License)").AppendLine("    source:  $($c.Url)")
}
[void]$t.AppendLine()
$i = 0
foreach ($c in $components) {
    $i++
    [void]$t.AppendLine($rule).AppendLine(("{0}. {1} - {2}" -f $i, $c.Name, $c.License)).AppendLine($rule).AppendLine()
    foreach ($f in $c.Texts) {
        if (-not (Test-Path $f)) { throw "missing license text $f" }
        [void]$t.AppendLine((Get-Content -Raw $f).Replace("`r`n", "`n").TrimEnd().Replace("`n", "`r`n")).AppendLine().AppendLine()
    }
}
Set-Content -NoNewline -Encoding utf8 (Join-Path $stage "LICENSE.txt") $t.ToString()

# Corresponding source for the LGPL library: every committed file under native/, which is everything needed to
# rebuild and replace spicecodec.dll (fetch.sh downloads the pinned libjpeg-turbo, opus and lz4 sources by hash).
$nativeFiles = git -C $root ls-files native
if ($LASTEXITCODE -ne 0 -or -not $nativeFiles) { throw "git ls-files native failed" }
foreach ($f in $nativeFiles) {
    $dst = Join-Path $stage ("source/" + $f)
    New-Item -ItemType Directory -Force (Split-Path -Parent $dst) | Out-Null
    Copy-Item (Join-Path $root $f) $dst
}

$zip = Join-Path $artifacts "$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
Set-Content -Encoding ascii "$zip.sha256" "$hash  $name.zip"
"wrote $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 2)) MB) sha256 $hash"
