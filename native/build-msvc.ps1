# Visual Studio/MSBuild entry point. POSIX tools still come from MSYS2.
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [ValidateSet('Build', 'Rebuild', 'Clean')][string]$Action = 'Build',
    [string]$Msys2Root = $env:SPICE_MSYS2_ROOT
)
$ErrorActionPreference = 'Stop'
$Arch = $Arch.ToLowerInvariant()
if (!$Msys2Root) { $Msys2Root = Join-Path $env:USERPROFILE 'scoop/apps/msys2/current' }
$Msys2Root = (Resolve-Path -LiteralPath $Msys2Root).Path
$bash = Join-Path $Msys2Root 'usr/bin/bash.exe'
if (!(Test-Path -LiteralPath $bash)) { throw 'Set SPICE_MSYS2_ROOT to an MSYS2 installation; see native/README.md.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -version '[18.0,19.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Install Visual Studio 2026 Desktop development with C++ (including target architecture tools).' }
$vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvarsall.bat'
$target = if ($Arch -eq 'arm64') { 'x64_arm64' } else { 'x64' }
# Capture, rather than print, the developer environment. Use the x64 host tools.
$developerEnvironment = & $env:ComSpec /d /s /c "call `"$vcvars`" $target >nul && set"
if ($LASTEXITCODE) { throw 'Visual Studio developer environment setup failed.' }
foreach ($entry in $developerEnvironment) {
    if ($entry -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process') }
}
$env:PATH = "$(Join-Path $Msys2Root 'clang64/bin');$(Join-Path $Msys2Root 'usr/bin');$env:PATH"
Push-Location $PSScriptRoot
try {
    if ($Action -ne 'Build') {
        & $bash build-msvc.sh $Arch --clean
        if ($LASTEXITCODE) { throw 'Native clean failed.' }
    }
    if ($Action -ne 'Clean') {
        & $bash build-msvc.sh $Arch
        if ($LASTEXITCODE) { throw 'Native build failed.' }
    }
} finally { Pop-Location }
