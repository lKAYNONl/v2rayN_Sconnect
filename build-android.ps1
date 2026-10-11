<#
.SYNOPSIS
    Builds Secure Connect for Android (v2rayN.Android) on Windows: fetches the
    cores, builds hev-socks5-tunnel with the NDK, publishes one APK per ABI.

.DESCRIPTION
    Needs (once):
      * .NET 10 SDK and the Android workload:  dotnet workload install android
      * Android SDK + NDK (Android Studio -> SDK Manager -> SDK Tools ->
        "NDK (Side by side)"), and a JDK 17 (Android Studio's bundled JBR works)
      * git (for hev-socks5-tunnel)

    Output: .\dist\SecureConnect-android-arm64.apk (phones) and
            .\dist\SecureConnect-android-x64.apk  (x86_64 emulator).

    The native parts land in v2rayN\v2rayN.Android\natives\<abi>\ and
    v2rayN\v2rayN.Android\AndroidAssets\geo\ (git-ignored); later builds reuse
    them unless -RefreshNatives is given.

    Signing: by default .NET signs with your local debug key - fine for your own
    phone. For a release key pass -KeyStore/-KeyAlias (passwords are asked for).

.EXAMPLE
    Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
    .\build-android.ps1                         # both ABIs
    .\build-android.ps1 -Rids android-arm64     # phone only
    .\build-android.ps1 -RefreshNatives         # re-download cores, rebuild hev
#>
param(
    [ValidateSet('android-arm64', 'android-x64')]
    [string[]]$Rids = @('android-arm64', 'android-x64'),

    [switch]$RefreshNatives,

    [string]$SingBoxVersion = '1.14.3',
    [string]$XrayVersion = '26.10.10',
    [string]$HevVersion = '2.18.0',

    # Android SDK / NDK / JDK; detected when empty.
    [string]$AndroidSdk = '',
    [string]$Ndk = '',
    [string]$Jdk = '',

    [string]$KeyStore = '',
    [string]$KeyAlias = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root    = $PSScriptRoot
$project = Join-Path $root 'v2rayN\v2rayN.Android'
$csproj  = Join-Path $project 'v2rayN.Android.csproj'
$natives = Join-Path $project 'natives'
$geoDir  = Join-Path $project 'AndroidAssets\geo'
$work    = Join-Path $natives '.work'
$dist    = Join-Path $root 'dist'

if (-not (Test-Path $csproj)) {
    throw "Put build-android.ps1 in the repository root (next to the 'v2rayN' folder)."
}

$abiOf = @{ 'android-arm64' = 'arm64-v8a'; 'android-x64' = 'x86_64' }
$sbArch = @{ 'arm64-v8a' = 'android-arm64'; 'x86_64' = 'android-amd64' }
$xrayArch = @{ 'arm64-v8a' = 'android-arm64-v8a'; 'x86_64' = 'android-amd64' }
$abis = $Rids | ForEach-Object { $abiOf[$_] }

function Test-LinkPlaceholder([string]$path) {
    # A git symlink checked out as a plain file: tiny, one line, just a relative path.
    $info = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
    if (-not $info -or $info.PSIsContainer) { return $false }
    if ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $false }
    if ($info.Length -eq 0 -or $info.Length -gt 512) { return $false }
    return ([IO.File]::ReadAllText($path) -match '^[\w.+/\\-]+$')
}

function Resolve-GitSymlinks([string]$repo) {
    # hev-socks5-tunnel and its submodules keep headers as git symlinks. With the
    # default core.symlinks=false, Git for Windows writes them as text files that
    # only contain the target path, and the C build cannot find the headers.
    # Replace every such placeholder with a copy of its target.
    $repos = @($repo)
    $subs = git -C $repo submodule foreach --recursive --quiet 'echo $displaypath'
    foreach ($sub in $subs) {
        if ($sub) { $repos += (Join-Path $repo ($sub -replace '/', '\')) }
    }
    $fixed = 0
    for ($pass = 0; $pass -lt 4; $pass++) {
        $left = 0
        foreach ($dir in $repos) {
            foreach ($line in (git -C $dir ls-files -s)) {
                if ($line -notmatch '^120000 \S+ \d+\t(.+)$') { continue }
                $link = Join-Path $dir ($Matches[1] -replace '/', '\')
                if (-not (Test-LinkPlaceholder $link)) { continue }
                $target = ([IO.File]::ReadAllText($link)).Trim() -replace '/', '\'
                $source = [IO.Path]::GetFullPath((Join-Path (Split-Path $link) $target))
                if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
                if (Test-LinkPlaceholder $source) { $left++; continue }   # a link to a link: next pass
                Copy-Item -LiteralPath $source -Destination $link -Force
                $fixed++
            }
        }
        if ($left -eq 0) { break }
    }
    if ($fixed -gt 0) { Write-Host "    replaced $fixed symlinked files (git core.symlinks=false)" }
}

function Get-File([string]$url, [string]$path) {
    if ((Test-Path $path) -and -not $RefreshNatives) { return }
    Write-Host "    <- $url"
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile "$path.part"
    Move-Item "$path.part" $path -Force
}

# ---------- Tools ----------
if (-not $AndroidSdk) {
    $AndroidSdk = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, (Join-Path $env:LOCALAPPDATA 'Android\Sdk')) |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $AndroidSdk) { throw 'Android SDK not found. Install Android Studio or pass -AndroidSdk.' }

if (-not $Ndk) {
    $Ndk = @($env:ANDROID_NDK_HOME, $env:ANDROID_NDK_ROOT) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $Ndk) {
        $ndkRoot = Join-Path $AndroidSdk 'ndk'
        if (Test-Path $ndkRoot) {
            $Ndk = Get-ChildItem $ndkRoot -Directory | Sort-Object { [version]($_.Name -replace '[^0-9.].*$', '') } |
                Select-Object -Last 1 -ExpandProperty FullName
        }
    }
}

if (-not $Jdk) {
    $Jdk = @($env:JAVA_HOME, 'C:\Program Files\Android\Android Studio\jbr') |
        Where-Object { $_ -and (Test-Path (Join-Path $_ 'bin\javac.exe')) } | Select-Object -First 1
}

$workloads = (dotnet workload list) -join "`n"
if ($workloads -notmatch '(?m)^\s*android\s') {
    Write-Host 'Installing the .NET Android workload (may need an elevated prompt)...' -ForegroundColor Yellow
    dotnet workload install android
    if ($LASTEXITCODE -ne 0) { throw 'dotnet workload install android failed - run it in an administrator terminal.' }
}

# ---------- Native parts ----------
New-Item -ItemType Directory -Force -Path $work, $geoDir, $dist | Out-Null

foreach ($abi in $abis) {
    $out = Join-Path $natives $abi
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    Write-Host "==> cores for $abi" -ForegroundColor Cyan

    if ($RefreshNatives -or -not (Test-Path (Join-Path $out 'libsingbox.so'))) {
        $arch = $sbArch[$abi]
        $tgz = Join-Path $work "sing-box-$SingBoxVersion-$arch.tar.gz"
        Get-File "https://github.com/SagerNet/sing-box/releases/download/v$SingBoxVersion/sing-box-$SingBoxVersion-$arch.tar.gz" $tgz
        $tmp = Join-Path $work "sb-$abi"
        if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $tmp | Out-Null
        tar -xzf $tgz -C $tmp
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $tgz" }
        Copy-Item (Join-Path $tmp "sing-box-$SingBoxVersion-$arch\sing-box") (Join-Path $out 'libsingbox.so') -Force
    }

    if ($RefreshNatives -or -not (Test-Path (Join-Path $out 'libxray.so'))) {
        $arch = $xrayArch[$abi]
        $zip = Join-Path $work "Xray-$XrayVersion-$arch.zip"
        Get-File "https://github.com/XTLS/Xray-core/releases/download/v$XrayVersion/Xray-$arch.zip" $zip
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $entry = $archive.Entries | Where-Object { $_.Name -eq 'xray' } | Select-Object -First 1
            if (-not $entry) { throw "No xray binary in $zip" }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $out 'libxray.so'), $true)
        }
        finally { $archive.Dispose() }
    }
}

Write-Host '==> geo files' -ForegroundColor Cyan
foreach ($f in 'geoip.dat', 'geosite.dat') {
    Get-File "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/$f" (Join-Path $geoDir $f)
}

$hevMissing = $abis | Where-Object { -not (Test-Path (Join-Path $natives "$_\libhev-socks5-tunnel.so")) }
if ($RefreshNatives -or $hevMissing) {
    Write-Host "==> hev-socks5-tunnel $HevVersion" -ForegroundColor Cyan
    if (-not $Ndk -or -not (Test-Path (Join-Path $Ndk 'ndk-build.cmd'))) {
        throw 'Android NDK not found: install "NDK (Side by side)" in Android Studio SDK Manager, or pass -Ndk.'
    }
    $hev = Join-Path $work 'hev-socks5-tunnel'
    if (-not (Test-Path (Join-Path $hev '.git'))) {
        git clone --depth 1 --branch $HevVersion --recursive --shallow-submodules https://github.com/heiher/hev-socks5-tunnel.git $hev
        if ($LASTEXITCODE -ne 0) { throw 'git clone of hev-socks5-tunnel failed' }
    }
    Resolve-GitSymlinks $hev
    # Called through P/Invoke: remove the JNI glue's JNI_OnLoad (it needs a Java
    # class this app does not have, and .NET preloads JNI_OnLoad libraries).
    $jni = Join-Path $hev 'src\hev-jni.c'
    $original = [IO.File]::ReadAllText($jni)
    $patched = [regex]::Replace($original, '(?m)^JNI_OnLoad \(', 'sc_unused_jni_onload (')
    if ($patched -match '(?m)^JNI_OnLoad') { throw 'Could not disable JNI_OnLoad in hev-jni.c' }
    [IO.File]::WriteAllText($jni, $patched)
    try {
        Push-Location $hev
        & (Join-Path $Ndk 'ndk-build.cmd') 'NDK_PROJECT_PATH=.' 'APP_BUILD_SCRIPT=./Android.mk' 'NDK_APPLICATION_MK=./Application.mk' "APP_ABI=$($abis -join ' ')" "-j$([Environment]::ProcessorCount)"
        if ($LASTEXITCODE -ne 0) { throw 'ndk-build failed' }
    }
    finally {
        Pop-Location
        [IO.File]::WriteAllText($jni, $original)
    }
    foreach ($abi in $abis) {
        Copy-Item (Join-Path $hev "libs\$abi\libhev-socks5-tunnel.so") (Join-Path $natives "$abi\libhev-socks5-tunnel.so") -Force
    }
}

# ---------- APKs ----------
$common = @("-p:AndroidSdkDirectory=$AndroidSdk", '-p:AcceptAndroidSDKLicenses=True')
if ($Jdk) { $common += "-p:JavaSdkDirectory=$Jdk" }

Write-Host '==> Android SDK packages' -ForegroundColor Cyan
dotnet build $csproj -t:InstallAndroidDependencies -f net10.0-android @common
if ($LASTEXITCODE -ne 0) { throw 'InstallAndroidDependencies failed' }

$sign = @()
if ($KeyStore) {
    $storePass = Read-Host 'Keystore password' -AsSecureString
    $keyPass = Read-Host 'Key password (Enter = same)' -AsSecureString
    $env:SC_STORE_PASS = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($storePass))
    $plainKey = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($keyPass))
    $env:SC_KEY_PASS = if ($plainKey) { $plainKey } else { $env:SC_STORE_PASS }
    $sign = @("-p:AndroidSigningKeyStore=$KeyStore", "-p:AndroidSigningKeyAlias=$KeyAlias",
              '-p:AndroidSigningStorePass=env:SC_STORE_PASS', '-p:AndroidSigningKeyPass=env:SC_KEY_PASS')
}

try {
    foreach ($rid in $Rids) {
        Write-Host "==> APK $rid" -ForegroundColor Cyan
        $out = Join-Path $root "v2rayN\v2rayN.Android\bin\publish\$rid"
        dotnet publish $csproj -c Release -f net10.0-android -r $rid @common @sign -o $out
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }
        $apk = Get-ChildItem $out -Filter '*-Signed.apk' | Select-Object -First 1
        if (-not $apk) { throw "No signed APK in $out" }
        $target = Join-Path $dist "SecureConnect-$rid.apk"
        Copy-Item $apk.FullName $target -Force
        Write-Host ("    {0} ({1:N1} MB)" -f $target, ((Get-Item $target).Length / 1MB))
    }
}
finally {
    Remove-Item Env:SC_STORE_PASS, Env:SC_KEY_PASS -ErrorAction SilentlyContinue
}

Write-Host 'Done. Install on the phone with: adb install -r dist\SecureConnect-android-arm64.apk' -ForegroundColor Green
