<#
.SYNOPSIS
    Builds the Steam Deck (Decky Loader) plugin of v2rayN Secure Connect:
    dist\v2rayN-steamdeck<Suffix>.zip, ready for "Install plugin from ZIP".

.DESCRIPTION
    1. The Linux x64 build of the app with the cores (build-release.ps1
       -Targets linux-x64 -NoZip) -> goes into the plugin as bin\v2rayN.
    2. The plugin UI (decky\src) built with pnpm + rollup -> dist\index.js.
    3. plugin.json, package.json, main.py, README, LICENSE and the above are
       packed as SecureConnect\... with "/" paths (Decky unpacks with Python's
       zipfile, which does not understand "\").

    Needs: .NET 10 SDK (as for the app), Node.js 18+ and pnpm
    (npm install -g pnpm). Put this file in the repository root.

.EXAMPLE
    Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
    .\build-decky.ps1                    # dist\v2rayN-steamdeck_test.zip
    .\build-decky.ps1 -Suffix ''         # dist\v2rayN-steamdeck.zip
    .\build-decky.ps1 -SkipApp           # UI only, reuse the last app build
#>
param(
    [string]$Suffix = '_test',

    # Reuse dist\v2rayN-linux-64_decky from the previous run.
    [switch]$SkipApp
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root    = $PSScriptRoot
$decky   = Join-Path $root 'decky'
$dist    = Join-Path $root 'dist'
$appOut  = Join-Path $dist 'v2rayN-linux-64_decky'
$stage   = Join-Path $dist '_decky'
$plugin  = Join-Path $stage 'SecureConnect'
$zipPath = Join-Path $dist "v2rayN-steamdeck$Suffix.zip"

if (-not (Test-Path (Join-Path $decky 'plugin.json'))) {
    throw "Put build-decky.ps1 in the repository root (next to the 'decky' folder)."
}

function Write-Zip([string]$sourceDir, [string]$outFile) {
    # Zip with "/" separators (ZipFile.CreateFromDirectory in Windows
    # PowerShell writes "\", which Linux tools do not treat as folders).
    $src = Get-Item -LiteralPath $sourceDir
    if (Test-Path $outFile) { Remove-Item $outFile -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($outFile, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $src.FullName -Recurse -File -Force) {
            $rel = $file.FullName.Substring($src.FullName.Length).Replace('\', '/').TrimStart('/')
            $name = "$($src.Name)/$rel"
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $zip.Dispose()
    }
}

# 1. The app (Linux x64) with the cores.
if (-not $SkipApp) {
    Write-Host '==> v2rayN for Linux x64 (with cores)' -ForegroundColor Cyan
    & (Join-Path $root 'build-release.ps1') -Targets linux-x64 -Suffix _decky -NoZip
}
if (-not (Test-Path (Join-Path $appOut 'v2rayN'))) {
    throw "No app build in $appOut - run without -SkipApp."
}
if (-not (Test-Path (Join-Path $appOut 'bin\sing_box\sing-box'))) {
    throw "sing-box is missing in $appOut\bin\sing_box - the plugin runs profiles with it."
}

# 2. The plugin UI.
Write-Host '==> Decky UI (pnpm + rollup)' -ForegroundColor Cyan
if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) {
    throw 'pnpm not found. Install Node.js 18+ (https://nodejs.org) and then: npm install -g pnpm'
}
Push-Location $decky
try {
    pnpm install
    if ($LASTEXITCODE -ne 0) { throw 'pnpm install failed' }
    pnpm run build
    if ($LASTEXITCODE -ne 0) { throw 'pnpm run build failed' }
}
finally {
    Pop-Location
}
if (-not (Test-Path (Join-Path $decky 'dist\index.js'))) {
    throw 'decky\dist\index.js was not built.'
}

# 3. Assemble SecureConnect\ and zip it.
Write-Host '==> Packing the plugin' -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $plugin 'dist'), (Join-Path $plugin 'bin') | Out-Null
foreach ($name in 'plugin.json', 'package.json', 'main.py', 'README.md') {
    Copy-Item (Join-Path $decky $name) $plugin
}
Copy-Item (Join-Path $root 'LICENSE') $plugin
Copy-Item (Join-Path $decky 'dist\index.js') (Join-Path $plugin 'dist')
Copy-Item $appOut (Join-Path $plugin 'bin\v2rayN') -Recurse
# Data of a test run of the Linux build must not ship with the plugin.
foreach ($name in 'guiConfigs', 'guiLogs', 'guiTemps', 'binConfigs') {
    $path = Join-Path $plugin "bin\v2rayN\$name"
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

Write-Zip $plugin $zipPath
Write-Host ("Done: {0} ({1:N0} MB)" -f $zipPath, ((Get-Item $zipPath).Length / 1MB)) -ForegroundColor Green
