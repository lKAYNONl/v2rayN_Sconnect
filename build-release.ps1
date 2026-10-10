<#
.SYNOPSIS
    Builds v2rayN Secure Connect release zips for Windows, Linux and macOS
    (x64 + arm64) on a Windows machine.

.DESCRIPTION
    For every target it publishes the Avalonia app (self-contained) and the
    AmazTool updater, adds the cores (sing-box, xray, mihomo, geo files) from
    2dust/v2rayN-core-bin and packs dist\v2rayN-<os>-<arch>.zip - the same
    names the official v2rayN releases use. SHA256SUMS.txt is written next to
    the zips.

    Put this file in the repository root (next to the "v2rayN" folder).
    Output goes to .\dist (keep it out of git). Your own .\v2rayN\publish
    folder with profiles is never touched.

.EXAMPLE
    Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
    .\build-release.ps1                              # all six targets
    .\build-release.ps1 -Targets win-x64             # one target
    .\build-release.ps1 -Targets win-x64,linux-x64 -NoCores
#>
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string[]]$Targets = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),

    # Skip downloading the cores (smaller zips; users add cores themselves).
    [switch]$NoCores
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # much faster Invoke-WebRequest in Windows PowerShell
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root  = $PSScriptRoot
$src   = Join-Path $root 'v2rayN'
$dist  = Join-Path $root 'dist'
$cache = Join-Path $dist '_cores'

if (-not (Test-Path (Join-Path $src 'v2rayN.Desktop\v2rayN.Desktop.csproj'))) {
    throw "Put build-release.ps1 in the repository root (next to the 'v2rayN' folder)."
}
if (-not (Test-Path (Join-Path $src 'GlobalHotKeys\src'))) {
    Write-Host 'Fetching submodules...' -ForegroundColor Yellow
    git -C $root submodule update --init --recursive
    if ($LASTEXITCODE -ne 0) { throw 'git submodule update failed' }
}
New-Item -ItemType Directory -Force -Path $dist, $cache | Out-Null

# Release asset names (as in official v2rayN releases and its updater).
$names = @{
    'win-x64'     = 'windows-64'
    'win-arm64'   = 'windows-arm64'
    'linux-x64'   = 'linux-64'
    'linux-arm64' = 'linux-arm64'
    'osx-x64'     = 'macos-64'
    'osx-arm64'   = 'macos-arm64'
}

function Get-CoresBin([string]$pkg) {
    # Returns the "bin" folder of the matching core bundle, downloading it once.
    $zip = Join-Path $cache "$pkg.zip"
    $dir = Join-Path $cache $pkg
    if (-not (Test-Path $zip)) {
        Write-Host "    downloading cores $pkg.zip (~80 MB)..."
        Invoke-WebRequest -UseBasicParsing -OutFile "$zip.part" `
            -Uri "https://github.com/2dust/v2rayN-core-bin/raw/refs/heads/master/$pkg.zip"
        Move-Item "$zip.part" $zip -Force
    }
    if (-not (Test-Path $dir)) {
        if (Test-Path "$dir.part") { Remove-Item "$dir.part" -Recurse -Force }
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, "$dir.part")
        Move-Item "$dir.part" $dir -Force
    }
    $bin = Get-ChildItem -Path $dir -Directory -Recurse -Filter 'bin' | Select-Object -First 1
    if (-not $bin) { throw "No bin folder inside $zip" }
    return $bin.FullName
}

foreach ($rid in $Targets) {
    $pkg = "v2rayN-$($names[$rid])"
    $out = Join-Path $dist $pkg
    Write-Host "==> $rid -> $pkg" -ForegroundColor Cyan
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    dotnet publish (Join-Path $src 'v2rayN.Desktop\v2rayN.Desktop.csproj') `
        -c Release -r $rid -p:SelfContained=true -o $out
    if ($LASTEXITCODE -ne 0) { throw "v2rayN publish failed for $rid" }

    dotnet publish (Join-Path $src 'AmazTool\AmazTool.csproj') `
        -c Release -r $rid -p:SelfContained=true -p:PublishTrimmed=true -o $out
    if ($LASTEXITCODE -ne 0) { throw "AmazTool publish failed for $rid" }

    Get-ChildItem -Path $out -Recurse -Filter '*.pdb' | Remove-Item -Force

    if (-not $NoCores) {
        Copy-Item -Path (Get-CoresBin $pkg) -Destination $out -Recurse -Force
    }

    # .NET zip writes "/" separators, so Linux/macOS unzip the folders correctly
    # (Compress-Archive in Windows PowerShell 5.1 does not).
    $zipPath = Join-Path $dist "$pkg.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $out, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host ("    {0} ({1:N0} MB)" -f $zipPath, ((Get-Item $zipPath).Length / 1MB))
}

$sums = Get-ChildItem -Path $dist -Filter 'v2rayN-*.zip' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
}
$sums | Set-Content -Path (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Done. Upload from: $dist" -ForegroundColor Green
$sums
