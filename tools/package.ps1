# Builds the portable release: artifacts/SpiceViewer-<version>-<rid>.zip
# Usage: pwsh tools/package.ps1 [-Rid win-x64] [-NoPublish] [-NoLauncher]
# Needs the native DLLs first (bash native/build.sh x64) and, for Native AOT on Windows, the MSVC linker.
# The zip holds the viewer and, unless -NoLauncher, the v1 PVE launcher (PveLauncher\, with the Windows App SDK it
# needs) and the terminal bridge PveTerm.exe next to the viewer (docs/decisions/0038).
param([string]$Rid = "win-x64", [switch]$NoPublish, [switch]$NoLauncher)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src/SpiceViewer/SpiceViewer.csproj"
$termProj = Join-Path $root "src/PveTerm/PveTerm.csproj"
$launcherProj = Join-Path $root "src/PveLauncher/PveLauncher.csproj"
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$publish = Join-Path $root "out/$Rid"
$termPublish = Join-Path $root "out/$Rid-pveterm"
$launcherPublish = Join-Path $root "out/$Rid-launcher"
$arch = $Rid.Split('-')[1]
$native = Join-Path $root "native"
$deps = Join-Path $native "downloads"
$withLauncher = -not $NoLauncher

if (-not $NoPublish) {
    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        # The AOT linker locates MSVC through vswhere.
        $installer = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
        if (Test-Path $installer) { $env:PATH = "$installer;$env:PATH" }
    }
    dotnet publish $proj -c Release -r $Rid -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    if ($withLauncher) {
        dotnet publish $termProj -c Release -r $Rid -o $termPublish
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish of PveTerm failed" }
        # A clean folder: the launcher's publish output is copied whole (minus symbols) into PveLauncher\.
        if (Test-Path $launcherPublish) { Remove-Item -Recurse -Force $launcherPublish }
        dotnet publish $launcherProj -c Release -r $Rid -o $launcherPublish
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish of PveLauncher failed" }
    }
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
if ($withLauncher) {
    $src = Join-Path $termPublish "PveTerm.exe"
    if (-not (Test-Path $src)) { throw "missing $src (publish without -NoPublish first)" }
    Copy-Item $src $stage
    if (-not (Test-Path (Join-Path $launcherPublish "PveLauncher.exe"))) { throw "missing $launcherPublish\PveLauncher.exe" }
    # The launcher finds SpiceViewer.exe and PveTerm.exe in its parent folder; its Windows App SDK files stay in
    # PveLauncher\ so the viewer's folder stays readable.
    $launcherStage = Join-Path $stage "PveLauncher"
    # WinUI's own strings ship as <lang>\*.dll.mui; only en-US is kept, which is also MUI's last fallback (human,
    # 2026-10-31: "這版先只留英文").
    # data\ is where a launcher run from the publish folder keeps its settings and log; never ship it.
    $launcherData = Join-Path $launcherPublish "data"
    $files = Get-ChildItem -Recurse -File $launcherPublish |
        Where-Object { $_.Extension -ne ".pdb" -and ($_.Extension -ne ".mui" -or $_.Directory.Name -eq "en-US") } |
        Where-Object { -not $_.FullName.StartsWith($launcherData + [IO.Path]::DirectorySeparatorChar) }
    foreach ($f in $files) {
        $dst = Join-Path $launcherStage $f.FullName.Substring($launcherPublish.Length + 1)
        New-Item -ItemType Directory -Force (Split-Path -Parent $dst) | Out-Null
        Copy-Item $f.FullName $dst
    }
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

$exes = if ($withLauncher) { "SpiceViewer.exe, PveTerm.exe, PveLauncher\PveLauncher.exe" } else { "SpiceViewer.exe" }
$components = @(
    @{ Name = "SpiceViewer $version"; In = "$exes; codec bridge in spicecodec.dll"; License = "MIT"
       Url = "this package; full source with the project"; Texts = @((Join-Path $root "LICENSE")) },
    @{ Name = ".NET runtime $ilcVersion (Native AOT, compiled into the exe)"; In = $exes; License = "MIT, plus the notices of the components it includes"
       Url = "https://github.com/dotnet/runtime"; Texts = @((Join-Path $lic "dotnet-runtime-MIT.txt"), $ilcNotices) },
    @{ Name = "CsWin32 $csWin32Version (generated Win32 interop declarations)"; In = $exes; License = "MIT"
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
if ($withLauncher) {
    # The launcher's own packages (docs/decisions/0038), from its restore.
    $launcherAssets = Get-Content -Raw (Join-Path $root "src/PveLauncher/obj/project.assets.json") | ConvertFrom-Json
    function LauncherPackageVersion([string]$id) {
        $key = $launcherAssets.libraries.PSObject.Properties.Name | Where-Object { $_ -like "$id/*" } | Select-Object -First 1
        if (-not $key) { throw "$id is not in PveLauncher's project.assets.json (restore/publish first)" }
        $key.Split('/')[1]
    }
    $appSdk = LauncherPackageVersion "Microsoft.WindowsAppSDK.WinUI"
    $appSdkBaseVersion = LauncherPackageVersion "Microsoft.WindowsAppSDK.Base"
    $appSdkFoundation = LauncherPackageVersion "Microsoft.WindowsAppSDK.Foundation"
    $appSdkIxp = LauncherPackageVersion "Microsoft.WindowsAppSDK.InteractiveExperiences"
    $webView = LauncherPackageVersion "Microsoft.Web.WebView2"
    # The Windows SDK license: the projection package names only https://aka.ms/WinSDKLicenseURL, so the text comes
    # from the Windows SDK metadata package CsWin32 already restores, which ships it as sdk_license.txt.
    $win32MetadataVersion = PackageVersion "Microsoft.Windows.SDK.Win32Metadata"
    $sdkLicense = Join-Path $nuget "microsoft.windows.sdk.win32metadata/$win32MetadataVersion/sdk_license.txt"
    $sdkRef = ($launcherAssets.project.frameworks.PSObject.Properties.Value.downloadDependencies |
        Where-Object { $_.name -eq "Microsoft.Windows.SDK.NET.Ref" } | Select-Object -First 1).version.Trim('[', ']').Split(',')[0].Trim()
    $sdkRefDir = Join-Path $nuget "microsoft.windows.sdk.net.ref/$sdkRef"
    $winrt = Get-ChildItem -Recurse -Filter WinRT.Runtime.dll (Join-Path $sdkRefDir "lib") | Select-Object -First 1
    if (-not $winrt) { throw "WinRT.Runtime.dll not found under $sdkRefDir" }
    $winrtVersion = $winrt.VersionInfo.ProductVersion.Split('+')[0]
    $appSdkDir = Join-Path $nuget "microsoft.windowsappsdk.winui/$appSdk"
    $appSdkBase = Join-Path $nuget "microsoft.windowsappsdk.base/$appSdkBaseVersion"
    $webViewDir = Join-Path $nuget "microsoft.web.webview2/$webView"
    $components += @(
        @{ Name = "Windows App SDK (WinUI $appSdk, Foundation $appSdkFoundation, InteractiveExperiences $appSdkIxp, Base $appSdkBaseVersion; self-contained)"
           In = "PveLauncher\ (every file but PveLauncher.exe and the WebView2 files)"
           License = "Microsoft Software License Terms (distributable code), plus the third-party notices below"
           Url = "https://github.com/microsoft/WindowsAppSDK"
           Texts = @((Join-Path $appSdkDir "license.txt"), (Join-Path $appSdkDir "NOTICE.txt"), (Join-Path $appSdkBase "NOTICE.txt")) },
        @{ Name = "WebView2 SDK $webView (loader and WinRT component that WinUI brings along; the launcher shows no web content)"
           In = "PveLauncher\WebView2Loader.dll, PveLauncher\Microsoft.Web.WebView2.Core.dll"; License = "BSD-3-Clause (Microsoft), plus notices"
           Url = "https://aka.ms/webview2"; Texts = @((Join-Path $webViewDir "LICENSE.txt"), (Join-Path $webViewDir "NOTICE.txt")) },
        @{ Name = "C#/WinRT runtime $winrtVersion (WinRT.Runtime, compiled into the exe)"; In = "PveLauncher\PveLauncher.exe"; License = "MIT"
           Url = "https://github.com/microsoft/CsWinRT"; Texts = @((Join-Path $lic "cswinrt-MIT.txt")) },
        @{ Name = "Windows SDK .NET projection (Microsoft.Windows.SDK.NET.Ref $sdkRef, the parts used are compiled into the exe)"
           In = "PveLauncher\PveLauncher.exe"; License = "Microsoft Windows SDK license terms"; Url = "https://aka.ms/WinSDKLicenseURL"
           Texts = @($sdkLicense)
           Note = "That NuGet package carries no license file, only the link above. The text below is the Windows SDK license as Microsoft ships it in Microsoft.Windows.SDK.Win32Metadata $win32MetadataVersion." }
    )
}
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
if ($withLauncher) {
    [void]$t.AppendLine("The PVE launcher (PveLauncher\PveLauncher.exe) is part of SpiceViewer and under the MIT License.")
    [void]$t.AppendLine("The other files in PveLauncher\ are Microsoft's Windows App SDK and WebView2 SDK. They are not")
    [void]$t.AppendLine("free software: they are distributed under Microsoft's license terms, included below, and by")
    [void]$t.AppendLine("using them you agree to those terms. The viewer and PveTerm.exe do not use them.").AppendLine()
}
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
    if ($c.Note) { [void]$t.AppendLine($c.Note).AppendLine() }
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
