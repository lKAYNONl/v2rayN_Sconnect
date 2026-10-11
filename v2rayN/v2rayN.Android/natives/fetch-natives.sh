#!/usr/bin/env bash
# Puts the native parts of Secure Connect for Android in place:
#   natives/<abi>/libsingbox.so            sing-box CLI (official Android build)
#   natives/<abi>/libxray.so               Xray CLI (official Android build)
#   natives/<abi>/libhev-socks5-tunnel.so  hev-socks5-tunnel, built here with the NDK
#   ../AndroidAssets/geo/geo{ip,site}.dat  geo files for Xray routing
#
# The cores are executables; they are named lib*.so so that Android installs
# them into the app's native library dir, the one place an app may exec from.
#
# Needs: curl, tar, unzip, git; ANDROID_NDK_HOME (or ANDROID_NDK_LATEST_HOME on
# GitHub runners) for hev. Versions can be overridden with the env vars below.
set -euo pipefail

SING_BOX_VERSION="${SING_BOX_VERSION:-1.14.3}"
XRAY_VERSION="${XRAY_VERSION:-26.10.10}"
HEV_VERSION="${HEV_VERSION:-2.18.0}"
GEO_URL="${GEO_URL:-https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download}"
ABIS="${ABIS:-arm64-v8a x86_64}"

here="$(cd "$(dirname "$0")" && pwd)"
project="$(dirname "$here")"
work="${WORK_DIR:-$here/.work}"
mkdir -p "$work"

download() { # url file
    echo "  <- $1"
    curl -fsSL --retry 3 --retry-delay 2 -o "$2.part" "$1"
    mv "$2.part" "$2"
}

for abi in $ABIS; do
    case "$abi" in
        arm64-v8a) sb_arch=android-arm64; xray_arch=android-arm64-v8a ;;
        x86_64)    sb_arch=android-amd64; xray_arch=android-amd64 ;;
        *) echo "Unsupported ABI: $abi" >&2; exit 1 ;;
    esac
    out="$here/$abi"
    mkdir -p "$out"
    echo "==> $abi"

    sb_tgz="$work/sing-box-$SING_BOX_VERSION-$sb_arch.tar.gz"
    [ -f "$sb_tgz" ] || download "https://github.com/SagerNet/sing-box/releases/download/v$SING_BOX_VERSION/sing-box-$SING_BOX_VERSION-$sb_arch.tar.gz" "$sb_tgz"
    rm -rf "$work/sb-$abi" && mkdir -p "$work/sb-$abi"
    tar -xzf "$sb_tgz" -C "$work/sb-$abi"
    install -m 755 "$work/sb-$abi/sing-box-$SING_BOX_VERSION-$sb_arch/sing-box" "$out/libsingbox.so"

    xray_zip="$work/Xray-$XRAY_VERSION-$xray_arch.zip"
    [ -f "$xray_zip" ] || download "https://github.com/XTLS/Xray-core/releases/download/v$XRAY_VERSION/Xray-$xray_arch.zip" "$xray_zip"
    rm -rf "$work/xray-$abi" && mkdir -p "$work/xray-$abi"
    unzip -q -o "$xray_zip" xray -d "$work/xray-$abi"
    install -m 755 "$work/xray-$abi/xray" "$out/libxray.so"
done

echo "==> geo files"
geo="$project/AndroidAssets/geo"
mkdir -p "$geo"
for f in geoip.dat geosite.dat; do
    download "$GEO_URL/$f" "$geo/$f"
done

echo "==> hev-socks5-tunnel $HEV_VERSION"
ndk="${ANDROID_NDK_HOME:-${ANDROID_NDK_LATEST_HOME:-${ANDROID_NDK_ROOT:-}}}"
if [ -z "$ndk" ] || [ ! -x "$ndk/ndk-build" ]; then
    echo "Android NDK not found (set ANDROID_NDK_HOME). hev-socks5-tunnel is required." >&2
    exit 1
fi
hev="$work/hev-socks5-tunnel"
if [ ! -d "$hev/.git" ]; then
    git clone --depth 1 --branch "$HEV_VERSION" --recursive --shallow-submodules \
        https://github.com/heiher/hev-socks5-tunnel.git "$hev"
fi
# Secure Connect calls the C API through P/Invoke. Drop the JNI glue's
# JNI_OnLoad: it registers natives on a Java class this app does not have,
# and .NET for Android preloads every library that exports JNI_OnLoad.
sed -i.orig 's/^JNI_OnLoad (/sc_unused_jni_onload (/' "$hev/src/hev-jni.c"
if grep -q '^JNI_OnLoad' "$hev/src/hev-jni.c"; then
    echo "Could not disable JNI_OnLoad in hev-jni.c" >&2
    exit 1
fi
(
    cd "$hev"
    "$ndk/ndk-build" NDK_PROJECT_PATH=. APP_BUILD_SCRIPT=./Android.mk NDK_APPLICATION_MK=./Application.mk \
        APP_ABI="$ABIS" -j"$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)"
)
for abi in $ABIS; do
    install -m 644 "$hev/libs/$abi/libhev-socks5-tunnel.so" "$here/$abi/libhev-socks5-tunnel.so"
done
mv -f "$hev/src/hev-jni.c.orig" "$hev/src/hev-jni.c"

echo "Done:"
ls -l "$here"/*/ "$geo"
