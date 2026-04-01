# Build ClipboardXShellNavigate.dll (x64) and ClipboardXShellNavigate32.dll (Win32).
# Requires: Visual Studio or Build Tools with MSVC + Windows SDK.
param(
    [switch]$InstallBuildTools
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$proj = Join-Path $here 'ClipboardXShellNavigate.vcxproj'
$repoRoot = (Resolve-Path (Join-Path $here '..\..')).Path
$outDirs = @(
    (Join-Path $repoRoot 'bin\Release\net8.0-windows'),
    (Join-Path $repoRoot 'bin\Debug\net8.0-windows')
)

function Find-MsBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $null }
    $inst = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
    if (-not $inst) { return $null }
    foreach ($rel in @(
            'MSBuild\Current\Bin\MSBuild.exe',
            'MSBuild\17.0\Bin\MSBuild.exe',
            'MSBuild\16.0\Bin\MSBuild.exe',
            'MSBuild\15.0\Bin\MSBuild.exe')) {
        $p = Join-Path $inst $rel
        if (Test-Path $p) { return $p }
    }
    return $null
}

$msb = Find-MsBuild
if (-not $msb -and $InstallBuildTools) {
    Write-Host 'Installing VS 2022 Build Tools (C++ workload) via winget, passive mode...'
    winget install -e --id Microsoft.VisualStudio.2022.BuildTools --accept-package-agreements --accept-source-agreements `
        --override '--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended'
    $msb = Find-MsBuild
}

if (-not $msb) {
    Write-Host 'MSBuild not found. Install Visual Studio or Build Tools with C++, or run:'
    Write-Host '  .\build.ps1 -InstallBuildTools'
    exit 1
}

Write-Host "MSBuild: $msb"

& $msb $proj /p:Configuration=Release /p:Platform=x64 /v:m
& $msb $proj /p:Configuration=Release /p:Platform=Win32 /v:m

$dll64 = Join-Path $here 'bin\x64\Release\ClipboardXShellNavigate.dll'
$dll32 = Join-Path $here 'bin\Win32\Release\ClipboardXShellNavigate32.dll'
if (-not (Test-Path $dll64)) {
    Write-Host 'x64 build failed. Try changing PlatformToolset in vcxproj (v142 vs v143).'
    exit 1
}

foreach ($outNet in $outDirs) {
    New-Item -ItemType Directory -Force -Path $outNet | Out-Null
    Copy-Item $dll64 (Join-Path $outNet 'ClipboardXShellNavigate.dll') -Force
    if (Test-Path $dll32) {
        Copy-Item $dll32 (Join-Path $outNet 'ClipboardXShellNavigate32.dll') -Force
    }
    Write-Host "Copied to: $outNet"
}
Write-Host "OK: $dll64"
