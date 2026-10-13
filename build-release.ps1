<#
.SYNOPSIS
    Builds v2rayN Secure Connect release zips for Windows, Linux and macOS
    (x64 + arm64) on a Windows machine.

.DESCRIPTION
    For every target it publishes the Avalonia app (self-contained) and the
    AmazTool updater, adds the cores (sing-box, xray, mihomo, geo files) from
    2dust/v2rayN-core-bin and packs the folder: Windows ->
    dist\v2rayN-windows-<arch>.zip, Linux/macOS -> dist\v2rayN-<os>-<arch>.tar.gz.
    SHA256SUMS.txt is written next to the archives.

    Linux and macOS get a .tar.gz, not a zip: a zip made by Windows PowerShell
    has "\" in its paths and no Unix permissions, so Finder / file managers
    unpack it into a flat mess and "./v2rayN" is not executable. The tar.gz
    has "/" paths and 0755 on the programs and cores.

    Put this file in the repository root (next to the "v2rayN" folder).
    Output goes to .\dist (keep it out of git). Your own .\v2rayN\publish
    folder with profiles is never touched.

.EXAMPLE
    Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
    .\build-release.ps1                              # all six targets
    .\build-release.ps1 -Targets win-x64             # one target
    .\build-release.ps1 -Targets win-x64,linux-x64 -NoCores
    .\build-release.ps1 -Targets win-x64 -Suffix _test -NoZip   # dist\v2rayN-windows-64_test
#>
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string[]]$Targets = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),

    # Skip downloading the cores (smaller zips; users add cores themselves).
    [switch]$NoCores,

    # Appended to the output folder/zip name, e.g. "_test" -> dist\v2rayN-windows-64_test.
    [string]$Suffix = '',

    # Only build the folder, no zip (handy for local test builds).
    [switch]$NoZip
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

function Write-TarGz([string]$sourceDir, [string]$outFile) {
    # Packs $sourceDir (as its own top folder) into a gzip'ed ustar archive with
    # "/" paths and Unix modes: 0755 for folders and for files without an
    # extension or ending in .sh/.so/.dylib (the app, AmazTool, the cores),
    # 0644 for everything else. Plain .NET, works in Windows PowerShell 5.1.
    $root = Get-Item -LiteralPath $sourceDir
    $top = $root.Name
    $items = @($root) + @(Get-ChildItem -LiteralPath $root.FullName -Recurse -Force | Sort-Object FullName)
    $ascii = [System.Text.Encoding]::ASCII
    $fs = [System.IO.File]::Create($outFile)
    $gz = New-Object System.IO.Compression.GZipStream($fs, [System.IO.Compression.CompressionLevel]::Optimal)
    try {
        foreach ($item in $items) {
            $rel = $item.FullName.Substring($root.FullName.Length).Replace('\', '/').TrimStart('/')
            $path = if ($rel) { "$top/$rel" } else { $top }
            $isDir = $item.PSIsContainer
            if ($isDir) {
                $path += '/'
                $mode = 493    # 0755
                $size = [long]0
            } else {
                $ext = $item.Extension.ToLowerInvariant()
                $mode = if ($ext -eq '' -or $ext -eq '.sh' -or $ext -eq '.so' -or $ext -eq '.dylib') { 493 } else { 420 }   # 0755 / 0644
                $size = [long]$item.Length
            }

            # ustar: name up to 100 bytes, longer paths split into prefix/name at a "/".
            $name = $path
            $prefix = ''
            if ($ascii.GetByteCount($name) -gt 100) {
                $cut = $path.LastIndexOf('/', [Math]::Min($path.Length - 2, 155))
                while ($cut -gt 0 -and $ascii.GetByteCount($path.Substring($cut + 1)) -gt 100) {
                    $cut = $path.LastIndexOf('/', $cut - 1)
                }
                if ($cut -le 0) { throw "Path too long for tar: $path" }
                $prefix = $path.Substring(0, $cut)
                $name = $path.Substring($cut + 1)
            }

            $header = New-Object byte[] 512
            $put = {
                param([string]$text, [int]$offset)
                $bytes = $ascii.GetBytes($text)
                [Array]::Copy($bytes, 0, $header, $offset, $bytes.Length)
            }
            $mtime = [DateTimeOffset]::new($item.LastWriteTimeUtc).ToUnixTimeSeconds()
            & $put $name 0
            & $put ([Convert]::ToString($mode, 8).PadLeft(7, '0')) 100
            & $put '0000000' 108
            & $put '0000000' 116
            & $put ([Convert]::ToString($size, 8).PadLeft(11, '0')) 124
            & $put ([Convert]::ToString([long]$mtime, 8).PadLeft(11, '0')) 136
            & $put '        ' 148
            & $put $(if ($isDir) { '5' } else { '0' }) 156
            & $put 'ustar' 257
            & $put '00' 263
            & $put $prefix 345
            $sum = 0
            foreach ($b in $header) { $sum += $b }
            & $put (([Convert]::ToString($sum, 8).PadLeft(6, '0')) + [char]0 + ' ') 148
            $gz.Write($header, 0, 512)

            if (-not $isDir) {
                $in = [System.IO.File]::OpenRead($item.FullName)
                try { $in.CopyTo($gz) } finally { $in.Dispose() }
                $pad = (512 - ($size % 512)) % 512
                if ($pad -gt 0) { $gz.Write((New-Object byte[] $pad), 0, $pad) }
            }
        }
        $gz.Write((New-Object byte[] 1024), 0, 1024)   # end of archive
    }
    finally {
        $gz.Dispose()
        $fs.Dispose()
    }
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
    $pkg = "v2rayN-$($names[$rid])"   # core bundle name
    $name = "$pkg$Suffix"              # output folder / zip name
    $out = Join-Path $dist $name
    Write-Host "==> $rid -> $name" -ForegroundColor Cyan
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

    if ($NoZip) {
        Write-Host "    $out"
        continue
    }

    if ($rid.StartsWith('win')) {
        $archive = Join-Path $dist "$name.zip"
        if (Test-Path $archive) { Remove-Item $archive -Force }
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $out, $archive, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    } else {
        # Linux / macOS: tar.gz keeps "/" paths and the executable bits.
        $archive = Join-Path $dist "$name.tar.gz"
        if (Test-Path $archive) { Remove-Item $archive -Force }
        $stale = Join-Path $dist "$name.zip"   # zips from older script versions are broken there
        if (Test-Path $stale) { Remove-Item $stale -Force }
        Write-TarGz $out $archive
    }
    Write-Host ("    {0} ({1:N0} MB)" -f $archive, ((Get-Item $archive).Length / 1MB))
}

$sums = Get-ChildItem -Path $dist -File | Where-Object { $_.Name -like 'v2rayN-*.zip' -or $_.Name -like 'v2rayN-*.tar.gz' } | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
}
$sums | Set-Content -Path (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Done. Upload from: $dist" -ForegroundColor Green
$sums
