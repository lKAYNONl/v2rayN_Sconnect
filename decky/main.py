"""Secure Connect for Steam Deck (Decky Loader backend).

The plugin ships the Linux build of v2rayN Secure Connect in bin/v2rayN. In
Game Mode it lets you pick one of your v2rayN profiles and runs it with
sing-box in TUN mode, so the whole Deck goes through the tunnel:

  1. "v2rayN --decky list" / "--decky config <id>" (run as the deck user, on
     the same profiles and settings as the app in Desktop Mode) list the
     profiles and write a sing-box config for the chosen one;
  2. this backend (root, see plugin.json) starts the plugin's own sing-box
     with that config and stops it on Disconnect / unload.

Profiles and subscriptions are managed in the app itself (Desktop Mode, menu
entry "Secure Connect (v2rayN)"). Split tunneling and the routing rules switch
can be changed in both places - they are the same settings ("--decky split" /
"--decky split-set"); the plugin adds a "whole Deck" mode of its own and a
picker of running programs (Proton/Wine games, Flatpak apps, AppImages).
"""

import asyncio
import json
import os
import pwd
import re
import shutil
import subprocess
import time
from collections import Counter

import decky

PLUGIN_DIR = decky.DECKY_PLUGIN_DIR
APP_DIR = os.path.join(PLUGIN_DIR, "bin", "v2rayN")
APP_EXE = os.path.join(APP_DIR, "v2rayN")
SING_BOX = os.path.join(APP_DIR, "bin", "sing_box", "sing-box")
SETTINGS_FILE = os.path.join(decky.DECKY_PLUGIN_SETTINGS_DIR, "settings.json")
RUN_CONFIG = os.path.join(decky.DECKY_PLUGIN_RUNTIME_DIR, "sing-box.json")
CORE_LOG = os.path.join(decky.DECKY_PLUGIN_LOG_DIR, "sing-box.log")
# The profile's own core (Xray), when the profile does not run on sing-box.
MAIN_LOG = os.path.join(decky.DECKY_PLUGIN_LOG_DIR, "core.log")
PID_FILE = os.path.join(decky.DECKY_PLUGIN_RUNTIME_DIR, "sing-box.pid")
MAIN_PID_FILE = os.path.join(decky.DECKY_PLUGIN_RUNTIME_DIR, "core.pid")
# sing-box runs from a copy: a plugin update unpacks over the plugin folder,
# and a running binary cannot be overwritten ("Text file busy").
CORE_COPY = os.path.join(decky.DECKY_PLUGIN_RUNTIME_DIR, "sing-box")

# v2rayN keeps its data in ~/.local/share/v2rayN instead of next to the exe:
# the plugin folder is replaced on every plugin update.
DATA_ENV = "V2RAYN_LOCAL_APPLICATION_DATA_V2"
DESKTOP_FILE = "secure-connect-v2rayn.desktop"
SYSTEM_PATH = "/usr/local/sbin:/usr/local/bin:/usr/bin:/bin"

# ----- running programs (split tunneling picker) ------------------------------
#
# sing-box finds the program of a connection by /proc/<pid>/exe and matches it
# against the list (exact path, file name, or a folder). The picker reads the
# same link, so what it adds is exactly what sing-box will see:
#   - Proton / Wine games: the exe is Wine's loader (wine64-preloader), not the
#     game's .exe, and the sockets are also held by wineserver (sing-box may
#     report either), so the whole Proton / Wine build is added (a folder
#     entry): every game on that Proton version matches. A system-wide Wine
#     gets its loader and wineserver paths instead;
#   - Flatpak apps: the path inside the sandbox ("/app/..."), as the kernel
#     reports it;
#   - scripts (Python, Java...): the interpreter is what sing-box sees, so the
#     entry covers every program on that interpreter - the panel says so;
#   - AppImages: mounted at a new /tmp/.mount_* path on every start, so the
#     file name is added instead.

WINE_LOADERS = {"wine", "wine64", "wine-preloader", "wine64-preloader", "wineserver"}
# Wine's own background processes: not shown as the game's name.
WINE_SERVICES = {
    "services.exe", "winedevice.exe", "plugplay.exe", "explorer.exe", "rpcss.exe",
    "svchost.exe", "conhost.exe", "start.exe", "tabtip.exe", "rundll32.exe",
    "wineboot.exe", "winemenubuilder.exe", "steam.exe", "cmd.exe", "xalia.exe",
    "lsteamclient.exe", "regsvr32.exe", "wineserver",
}
# Session / desktop plumbing of the deck user: hidden unless it uses the network.
SYSTEM_PROGRAMS = {
    "systemd", "(sd-pam)", "dbus-daemon", "dbus-broker", "dbus-broker-launch",
    "pipewire", "pipewire-pulse", "wireplumber", "gamescope", "gamescope-wl",
    "gamescopereaper", "gamescope-session", "gamescope-session-plus", "Xwayland",
    "mangoapp", "steam-launch-wrapper", "reaper", "bwrap", "sh", "bash", "dash",
    "sleep", "cat", "tail", "tee", "sed", "grep", "flatpak", "flatpak-portal",
    "flatpak-session-helper", "p11-kit-server", "ssh-agent", "gpg-agent",
    "xdg-desktop-portal", "xdg-desktop-portal-gtk", "xdg-desktop-portal-kde",
    "xdg-document-portal", "xdg-permission-store", "ibus-daemon", "kwalletd5",
    "kwalletd6", "ksecretd", "kded5", "kded6", "kwin_x11", "kwin_wayland",
    "plasmashell", "ksmserver", "kactivitymanagerd", "baloo_file", "obexd",
    "steamos-manager", "sdgyrodsu", "srt-logger", "steam-runtime-launcher-service",
    "pressure-vessel-adverb", "pressure-vessel-wrap", "pv-adverb", "pv-bwrap",
    "xdg-dbus-proxy",
}
INTERPRETER = re.compile(r"^(python[0-9.]*|java|node|nodejs|mono|mono-sgen|perl[0-9.]*|ruby[0-9.]*|lua[0-9.]*|bash|sh|dash)$")
SYSTEM_PREFIXES = ("/usr/lib/systemd/", "/usr/libexec/", "/usr/lib/at-spi")
PROCESS_SCAN_LIMIT = 4000


def _deck_user():
    entry = pwd.getpwnam(decky.DECKY_USER)
    return entry.pw_uid, entry.pw_gid, entry.pw_dir


def _user_env(home):
    return {
        "HOME": home,
        "USER": decky.DECKY_USER,
        "LOGNAME": decky.DECKY_USER,
        "PATH": SYSTEM_PATH,
        "LANG": os.environ.get("LANG") or "C.UTF-8",
        DATA_ENV: "1",
    }


def _fix_permissions():
    """Decky unpacks plugin zips without Unix permissions: make the app and the
    cores executable and the folders reachable for the deck user."""
    for path in (PLUGIN_DIR, os.path.join(PLUGIN_DIR, "bin"), APP_DIR):
        if os.path.isdir(path):
            os.chmod(path, 0o755)
    for root, dirs, files in os.walk(APP_DIR):
        for name in dirs:
            os.chmod(os.path.join(root, name), 0o755)
        for name in files:
            ext = os.path.splitext(name)[1].lower()
            mode = 0o755 if ext in ("", ".so", ".sh") else 0o644
            try:
                os.chmod(os.path.join(root, name), mode)
            except OSError:
                pass


def _write_desktop_entry():
    """Menu entry for Desktop Mode: the app with the same data as the plugin."""
    uid, gid, home = _deck_user()
    folder = os.path.join(home, ".local", "share", "applications")
    if not os.path.isdir(folder):
        os.makedirs(folder, exist_ok=True)
        for path in (os.path.dirname(folder), folder):
            try:
                os.chown(path, uid, gid)
            except OSError:
                pass
    path = os.path.join(folder, DESKTOP_FILE)
    content = (
        "[Desktop Entry]\n"
        "Type=Application\n"
        "Name=Secure Connect (v2rayN)\n"
        "Comment=Profiles and settings for the Secure Connect plugin\n"
        f"Exec=env {DATA_ENV}=1 {APP_EXE}\n"
        f"Icon={os.path.join(APP_DIR, 'v2rayN.png')}\n"
        "Terminal=false\n"
        "Categories=Network;\n"
    )
    with open(path, "w", encoding="utf-8") as f:
        f.write(content)
    os.chown(path, uid, gid)
    os.chmod(path, 0o755)


def _kill_stale_core():
    """Cores left running by a crashed Decky: sing-box would keep the TUN busy,
    the profile's core its local port."""
    try:
        with open(PID_FILE, "r", encoding="utf-8") as f:
            pid = int(f.read().strip())
        with open(f"/proc/{pid}/cmdline", "rb") as f:
            cmdline = f.read().split(b"\0")
        exe = os.path.realpath(cmdline[0].decode(errors="replace")) if cmdline else ""
        if exe in (os.path.realpath(CORE_COPY), os.path.realpath(SING_BOX)):
            os.kill(pid, 15)
            decky.logger.info(f"stopped a stale sing-box (pid {pid})")
    except (OSError, ValueError):
        pass
    try:
        # "<pid> <exe>": only a process that still is that program is stopped.
        with open(MAIN_PID_FILE, "r", encoding="utf-8") as f:
            pid_text, _, exe = f.read().strip().partition(" ")
        pid = int(pid_text)
        if exe and os.readlink(f"/proc/{pid}/exe") == exe:
            os.kill(pid, 15)
            decky.logger.info(f"stopped a stale core (pid {pid})")
    except (OSError, ValueError):
        pass
    for path in (PID_FILE, MAIN_PID_FILE):
        try:
            os.remove(path)
        except OSError:
            pass


LOG_NOISE = ("Xray, Penetrates Everything", "A unified platform for anti-censorship", "Reading config:")


def _log_tail(lines=6, path=None):
    """The last lines a core wrote in the current session (after the last
    "===== profile =====" marker), without the start-up banner."""
    try:
        with open(path or CORE_LOG, "rb") as f:
            f.seek(0, os.SEEK_END)
            f.seek(max(0, f.tell() - 4096))
            text = f.read().decode("utf-8", errors="replace")
    except OSError:
        return ""
    mark = text.rfind("\n===== ")
    if mark >= 0:
        text = text[mark + 1:].split("\n", 1)[-1]
    kept = [line for line in text.splitlines()
            if line.strip() and not any(noise in line for noise in LOG_NOISE)]
    return "\n".join(kept[-lines:])


def _read_text(path, limit=4096):
    try:
        with open(path, "rb") as f:
            return f.read(limit).decode("utf-8", errors="replace")
    except OSError:
        return ""


def _socket_inodes():
    """Inodes of the TCP/UDP sockets that are open right now."""
    inodes = set()
    for name in ("tcp", "tcp6", "udp", "udp6"):
        try:
            with open(f"/proc/net/{name}", "r", encoding="ascii", errors="replace") as f:
                next(f, None)
                for line in f:
                    parts = line.split()
                    if len(parts) > 9 and parts[9] != "0":
                        inodes.add(f"socket:[{parts[9]}]")
        except OSError:
            pass
    return inodes


def _uses_network(pid, sockets):
    try:
        fds = os.listdir(f"/proc/{pid}/fd")
    except OSError:
        return False
    for fd in fds:
        try:
            if os.readlink(f"/proc/{pid}/fd/{fd}") in sockets:
                return True
        except OSError:
            continue
    return False


def _flatpak_app(pid):
    """Flatpak app id of a sandboxed process; "" for host programs. Steam's own
    game containers also carry this file (com.steampowered.App<id>)."""
    text = _read_text(f"/proc/{pid}/root/.flatpak-info")
    section = ""
    for line in text.splitlines():
        line = line.strip()
        if line.startswith("["):
            section = line
        elif section == "[Application]" and line.startswith("name="):
            return line[5:].strip()
    return ""


def _windows_name(argv0):
    """Game.exe from Wine's argv[0] ("C:\\Games\\Game.exe", "Z:\\home\\...")."""
    name = argv0.replace("\\", "/").rsplit("/", 1)[-1].strip()
    return name if name.lower().endswith(".exe") else ""


def _wine_root(exe):
    """Folder of the Proton / Wine build that runs a game: ".../Proton 9.0/" for
    ".../Proton 9.0/files/bin/wine64-preloader". None for a system-wide Wine
    (a folder entry would cover all of /usr)."""
    folder = os.path.dirname(exe)
    if os.path.basename(folder) == "bin":
        folder = os.path.dirname(folder)
        if os.path.basename(folder) in ("files", "dist"):
            folder = os.path.dirname(folder)
    if folder in ("", "/", "/usr", "/usr/local", "/app", "/opt"):
        return None
    return folder.rstrip("/") + "/"


def _entry_kind(value):
    """What a split tunneling entry is, for the panel."""
    if value.endswith("/"):
        low = value.lower()
        if "proton" in low or "/compatibilitytools.d/" in low or "wine" in low:
            return "proton"
        return "folder"
    if value.startswith("/app/"):
        return "flatpak"
    if "/" in value or "\\" in value:
        return "path"
    return "name"


def _entry_label(value):
    trimmed = value.rstrip("/\\")
    return trimmed.replace("\\", "/").rsplit("/", 1)[-1] or value


def _list_programs(uid, exclude_prefixes):
    """Running programs of the deck user, grouped by the split tunneling entries
    that would match them ("values"). Programs with open network connections
    come first."""
    sockets = _socket_inodes()
    groups = {}
    scanned = 0
    for name in os.listdir("/proc"):
        if not name.isdigit():
            continue
        scanned += 1
        if scanned > PROCESS_SCAN_LIMIT:
            break
        pid = int(name)
        try:
            if os.stat(f"/proc/{pid}").st_uid != uid:
                continue
            exe = os.readlink(f"/proc/{pid}/exe")
            exe.encode("utf-8")  # a non-UTF-8 path cannot go into the app's settings
        except (OSError, UnicodeError):
            continue  # exited, kernel thread, not ours, or an odd file name
        if exe.endswith(" (deleted)"):
            continue
        if any(exe.startswith(prefix) for prefix in exclude_prefixes):
            continue
        base = os.path.basename(exe)
        comm = _read_text(f"/proc/{pid}/comm", 64).strip()
        argv = _read_text(f"/proc/{pid}/cmdline").split("\0")
        network = _uses_network(pid, sockets)

        game = ""
        if base in WINE_LOADERS:
            kind = "proton"
            root = _wine_root(exe)
            if root:
                key, value, title = root, root, os.path.basename(root.rstrip("/"))
            else:
                # System-wide Wine: its loader and wineserver files (both run
                # while a game does), not a folder like /usr/bin/.
                key, value, title = "wine:" + os.path.dirname(exe), exe, "Wine"
            game = _windows_name(argv[0] if argv else "") or (comm if comm.lower().endswith(".exe") else "")
            if game.lower() in WINE_SERVICES:
                game = ""
        elif exe.startswith("/tmp/.mount_"):
            kind = "appimage"
            key = value = title = base
        else:
            key = value = exe
            app_id = _flatpak_app(pid)
            if exe.startswith("/app/") and app_id and not app_id.startswith("com.steampowered."):
                # The app itself; its sandbox helpers (bwrap, D-Bus proxy, shells)
                # see the same .flatpak-info but run host / runtime files.
                kind = "flatpak"
                title = app_id
            elif "/ubuntu12_32/steam" in exe or base == "steamwebhelper":
                kind = "steam"
                title = "Steam" if base == "steam" else f"Steam ({base})"
            else:
                if not network and (base in SYSTEM_PROGRAMS or comm in SYSTEM_PROGRAMS
                                    or exe.startswith(SYSTEM_PREFIXES)):
                    continue
                kind = "interpreter" if INTERPRETER.match(base) else "app"
                # The file name is what matches; the process name (a script's
                # name for interpreters, cut to 15 characters) is a hint.
                title = base
                if comm and not base.startswith(comm) and comm not in SYSTEM_PROGRAMS:
                    title = f"{base} ({comm})"
                if app_id and not app_id.startswith("com.steampowered."):
                    title = f"{title} · {app_id}"

        group = groups.get(key)
        if group is None:
            group = groups[key] = {
                "value": key,
                "values": [],
                "title": title,
                "kind": kind,
                "detail": exe,
                "games": [],
                "network": False,
                "count": 0,
            }
        if value not in group["values"]:
            group["values"].append(value)
        group["count"] += 1
        group["network"] = group["network"] or network
        if game and game not in group["games"]:
            group["games"].append(game)

    result = list(groups.values())
    for group in result:
        group["values"].sort()
        group["detail"] = ", ".join(group["values"]) if group["kind"] == "proton" else group["detail"]
        group["games"] = sorted(group["games"], key=str.lower)[:6]
        if group["kind"] == "proton" and group["games"]:
            group["label"] = f"{', '.join(group['games'][:3])} · {group['title']}"
        else:
            group["label"] = group["title"]
    result.sort(key=lambda g: (not g["network"], g["label"].lower()))
    return result


# ----- connection check (the "Check" button) ----------------------------------

DIAG_FILE = "secure-connect-diag.txt"
LOG_ROTATE_BYTES = 8 * 1024 * 1024
LOG_ANALYZE_BYTES = 1024 * 1024
TEST_SITES = (
    ("Google", "https://www.google.com/generate_204"),
    ("YouTube", "https://www.youtube.com/generate_204"),
    ("Discord", "https://discord.com/api/v9/gateway"),
)
TEST_HOST = "discord.com"
PROXY_ENV_KEYS = ("http_proxy", "https_proxy", "all_proxy", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY")
LOG_LINE = re.compile(r"\b(TRACE|DEBUG|INFO|WARN|ERROR|FATAL|PANIC)\b.*?\[(\d+) [^\]]*\] (.+)$")


def _rotate_core_log():
    """The logs grow with every session (a lot with the detailed log on)."""
    for path in (CORE_LOG, MAIN_LOG):
        try:
            if os.path.getsize(path) > LOG_ROTATE_BYTES:
                os.replace(path, path + ".old")
        except OSError:
            pass


async def _wait_for_port(port, proc, timeout=8.0):
    """True once something accepts connections on 127.0.0.1:port; False when
    the process exits first or the time runs out."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if proc.returncode is not None:
            return False
        try:
            _, writer = await asyncio.wait_for(asyncio.open_connection("127.0.0.1", port), 0.5)
            writer.close()
            return True
        except (OSError, asyncio.TimeoutError):
            await asyncio.sleep(0.15)
    return False


def _run(argv, timeout=10):
    """A root command for the report: its output, or why it failed."""
    try:
        r = subprocess.run(argv, capture_output=True, text=True, timeout=timeout,
                           env={"PATH": SYSTEM_PATH, "LANG": "C"})
        return (r.stdout + r.stderr).strip()
    except (OSError, subprocess.SubprocessError) as e:
        return f"<{e}>"


def _session_log():
    """The current session's part of the sing-box log (after the last marker)."""
    try:
        with open(CORE_LOG, "rb") as f:
            f.seek(0, os.SEEK_END)
            f.seek(max(0, f.tell() - LOG_ANALYZE_BYTES))
            text = f.read().decode("utf-8", errors="replace")
    except OSError:
        return ""
    mark = text.rfind("\n===== ")
    return text[mark + 1:] if mark >= 0 else text


def _analyze_log(text):
    """Connections of the current session grouped by program: how many, which
    outbound they took, and their errors. Needs the detailed (info) log."""
    conns = {}
    order = []
    errors = []
    for line in text.splitlines():
        m = LOG_LINE.search(line)
        if not m:
            continue
        level, cid, msg = m.groups()
        c = conns.get(cid)
        if c is None:
            c = conns[cid] = {"proc": "", "dest": "", "domain": "", "out": "", "err": ""}
            order.append(cid)
        if "inbound connection to " in msg or "inbound packet connection to " in msg:
            c["dest"] = msg.rsplit(" to ", 1)[-1].strip()
        elif "found process path: " in msg:
            c["proc"] = msg.split("found process path: ", 1)[1].split(", user:", 1)[0].strip()
        elif "domain: " in msg and "sniffed" in msg:
            c["domain"] = msg.rsplit("domain: ", 1)[-1].strip()
        elif msg.startswith("outbound/") and " connection to " in msg:
            c["out"] = msg.split("]", 1)[0].split("[", 1)[-1]
        if level in ("ERROR", "FATAL", "PANIC", "WARN"):
            c["err"] = c["err"] or msg[:200]
            errors.append(msg[:200])
    procs = {}
    for cid in order:
        c = conns[cid]
        if not (c["dest"] or c["out"]):
            continue
        key = c["proc"] or "(программа не определена)"
        p = procs.setdefault(key, {"count": 0, "out": Counter(), "errors": Counter(), "sites": Counter()})
        p["count"] += 1
        dns = c["dest"].endswith(":53")
        p["out"]["dns" if dns and not c["out"] else (c["out"] or "без исхода")] += 1
        if c["err"]:
            p["errors"][re.sub(r"\d+\.\d+\.\d+\.\d+(:\d+)?", "<ip>", c["err"])[-120:]] += 1
        if not dns:
            p["sites"][c["domain"] or c["dest"].rsplit(":", 1)[0]] += 1
    return procs, errors[-30:]


def _describe_proc(path, p):
    outs = ", ".join(f"{name} {n}" for name, n in p["out"].most_common())
    line = f"{path}: {p['count']} соедин. ({outs})"
    if p["errors"]:
        line += f"; ошибок {sum(p['errors'].values())}: {p['errors'].most_common(1)[0][0]}"
    return line


def _read_ini(path, section):
    values = {}
    current = ""
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if line.startswith("["):
                    current = line
                elif current == section and "=" in line:
                    key, value = line.split("=", 1)
                    values[key.strip()] = value.strip()
    except OSError:
        pass
    return values


def _disable_kde_proxy_file():
    """ProxyType=0 in the deck user's kioslaverc, when kwriteconfig is missing."""
    uid, gid, home = _deck_user()
    path = os.path.join(home, ".config", "kioslaverc")
    try:
        with open(path, "r", encoding="utf-8") as f:
            lines = f.read().splitlines()
    except OSError:
        return False
    section = ""
    changed = False
    for i, line in enumerate(lines):
        stripped = line.strip()
        if stripped.startswith("["):
            section = stripped
        elif section == "[Proxy Settings]" and stripped.startswith("ProxyType=") and stripped != "ProxyType=0":
            lines[i] = "ProxyType=0"
            changed = True
    if changed:
        with open(path, "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")
        os.chown(path, uid, gid)
    return changed


def _collect_root_facts(uid, home):
    """Everything the check reads as root: network, DNS, proxies, Flatpak apps."""
    facts = {}
    facts["addr"] = _run(["ip", "-brief", "address"])
    v6 = _run(["ip", "-6", "-o", "address", "show", "scope", "global"])
    facts["global_v6"] = [f"{m.group(1)} {m.group(2)}" for m in
                          (re.match(r"^\d+:\s+(\S+)\s+inet6\s+(\S+)", line) for line in v6.splitlines())
                          if m and not m.group(1).startswith("singbox")]
    facts["rules4"] = _run(["ip", "rule"])
    facts["rules6"] = _run(["ip", "-6", "rule"])
    facts["v6_blocked"] = "unreachable" in facts["rules6"]
    facts["routes"] = _run(["ip", "route", "show", "default"]) + "\n" + _run(["ip", "-6", "route", "show", "default"])
    try:
        facts["resolv_link"] = os.path.realpath("/etc/resolv.conf")
        with open("/etc/resolv.conf", "r", encoding="utf-8", errors="replace") as f:
            facts["resolv"] = "\n".join(l for l in f.read().splitlines() if l.strip() and not l.startswith("#"))
    except OSError as e:
        facts["resolv"] = f"<{e}>"
    facts["resolvectl"] = _run(["resolvectl", "dns"], timeout=5)

    # Proxy settings the desktop app may have left (it sets both KDE and GNOME
    # ones for 127.0.0.1:<port> and clears them only on a clean exit).
    kde = _read_ini(os.path.join(home, ".config", "kioslaverc"), "[Proxy Settings]")
    facts["kde_proxy"] = (f"ProxyType={kde.get('ProxyType', '0')} http={kde.get('httpProxy', '')}"
                          if kde else "")
    facts["kde_proxy_on"] = kde.get("ProxyType", "0") not in ("", "0")

    # Running programs: Flatpak apps and proxy variables in their environment.
    flatpaks = {}
    env_proxies = {}
    for name in os.listdir("/proc"):
        if not name.isdigit():
            continue
        try:
            if os.stat(f"/proc/{name}").st_uid != uid:
                continue
            exe = os.readlink(f"/proc/{name}/exe")
        except OSError:
            continue
        app_id = _flatpak_app(name) if exe.startswith("/app/") else ""
        if app_id and not app_id.startswith("com.steampowered."):
            flatpaks.setdefault(app_id, set()).add(exe)
        try:
            with open(f"/proc/{name}/environ", "rb") as f:
                environ = f.read(65536).split(b"\0")
        except OSError:
            continue
        for item in environ:
            key, _, value = item.decode("utf-8", errors="replace").partition("=")
            if key in PROXY_ENV_KEYS and value:
                env_proxies.setdefault(f"{key}={value}", set()).add(app_id or os.path.basename(exe))
    facts["flatpaks"] = {k: sorted(v) for k, v in flatpaks.items()}
    facts["env_proxies"] = {k: sorted(v)[:5] for k, v in env_proxies.items()}

    overrides = {}
    for folder in (os.path.join(home, ".local", "share", "flatpak", "overrides"), "/var/lib/flatpak/overrides"):
        for app_id in ["global"] + sorted(flatpaks):
            path = os.path.join(folder, app_id)
            try:
                with open(path, "r", encoding="utf-8", errors="replace") as f:
                    overrides[path] = f.read().strip()
            except OSError:
                pass
    facts["overrides"] = overrides
    return facts


def _config_summary(path):
    """The running sing-box config without credentials: TUN, DNS, routing."""
    try:
        with open(path, "r", encoding="utf-8") as f:
            config = json.load(f)
    except (OSError, ValueError) as e:
        return [f"<{e}>"], {}
    lines = []
    tun = next((i for i in config.get("inbounds") or [] if i.get("type") == "tun"), {})
    lines.append("tun: " + json.dumps({k: tun.get(k) for k in (
        "address", "auto_route", "strict_route", "stack", "mtu", "route_exclude_address")}, ensure_ascii=False))
    lines.append("inbounds: " + ", ".join(f"{i.get('type')}[{i.get('tag')}]" for i in config.get("inbounds") or []))
    lines.append("outbounds: " + ", ".join(f"{o.get('type')}[{o.get('tag')}]" for o in config.get("outbounds") or []))
    dns = config.get("dns") or {}
    for server in dns.get("servers") or []:
        shown = {k: server.get(k) for k in ("tag", "type", "address", "server", "detour") if server.get(k) is not None}
        lines.append("dns server: " + json.dumps(shown, ensure_ascii=False))
    lines.append(f"dns final: {dns.get('final')}, strategy: {dns.get('strategy')}, rules: {len(dns.get('rules') or [])}")
    route = config.get("route") or {}
    lines.append(f"route final: {route.get('final')}, rules: {len(route.get('rules') or [])}")
    for rule in (route.get("rules") or [])[:40]:
        text = json.dumps(rule, ensure_ascii=False)
        lines.append("  rule: " + (text if len(text) < 300 else text[:300] + "…"))
    # The servers' own addresses also appear in DNS / routing rules.
    servers = {str(o.get("server")) for o in config.get("outbounds") or [] if o.get("server")}
    for server in sorted(servers, key=len, reverse=True):
        lines = [line.replace(server, "<server>") for line in lines]
    return lines, tun


def _parse_answer(stdout, stderr):
    """v2rayN prints one JSON line; anything else on stdout is ignored."""
    for line in reversed(stdout.decode("utf-8", errors="replace").splitlines()):
        line = line.strip()
        if line.startswith("{"):
            try:
                return json.loads(line)
            except ValueError:
                continue
    err = stderr.decode("utf-8", errors="replace").strip().splitlines()
    return {"ok": False, "error": err[-1][:300] if err else "v2rayN did not answer"}


class Plugin:
    def __init__(self):
        self.proc = None
        self.watch = None
        # The profile's own core (Xray) behind the sing-box TUN, if any.
        self.main_proc = None
        self.main_watch = None
        self.lock = asyncio.Lock()
        # One "v2rayN --decky" at a time: parallel runs would race on the
        # app's first-start setup (cores copy, database).
        self.cli_lock = asyncio.Lock()
        self.busy = False
        self.profile_id = ""
        self.profile_name = ""
        self.error = ""

    # ----- settings -------------------------------------------------------

    def _load_settings(self):
        try:
            with open(SETTINGS_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except (OSError, ValueError):
            return {}

    def _save_settings(self, **values):
        data = self._load_settings()
        data.update(values)
        os.makedirs(os.path.dirname(SETTINGS_FILE), exist_ok=True)
        with open(SETTINGS_FILE, "w", encoding="utf-8") as f:
            json.dump(data, f)

    # ----- v2rayN (headless, as the deck user) ----------------------------

    async def _v2rayn(self, *args, timeout=120):
        async with self.cli_lock:
            return await self._v2rayn_run(args, timeout)

    async def _v2rayn_run(self, args, timeout):
        uid, gid, home = _deck_user()
        try:
            proc = await asyncio.create_subprocess_exec(
                APP_EXE, "--decky", *args,
                cwd=APP_DIR,
                env=_user_env(home),
                user=uid,
                group=gid,
                extra_groups=[],
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
        except OSError as e:
            return {"ok": False, "error": f"v2rayN could not start: {e}"}
        try:
            out, err = await asyncio.wait_for(proc.communicate(), timeout)
        except asyncio.TimeoutError:
            proc.kill()
            await proc.wait()
            return {"ok": False, "error": "v2rayN did not answer in time"}
        answer = _parse_answer(out, err)
        if not answer.get("ok"):
            decky.logger.warning(f"v2rayN --decky {' '.join(args)}: {answer.get('error')}")
        return answer

    # ----- the cores: sing-box TUN (root) + the profile's own core ---------

    def _running(self):
        return self.proc is not None and self.proc.returncode is None

    def _status(self):
        return {
            "running": self._running(),
            "busy": self.busy,
            "profile_id": self.profile_id,
            "profile_name": self.profile_name,
            "error": self.error,
            "verbose": self._load_settings().get("verbose") is True,
        }

    @staticmethod
    def _prepare_config(source, verbose=False):
        """The generated config, adjusted for the plugin: log to our log file,
        sing-box cache in the plugin's runtime folder (root-owned files must not
        end up in the deck user's v2rayN folder), only the TUN inbound, no IPv6
        around the tunnel. verbose: every connection in the log (for the check)."""
        with open(source, "r", encoding="utf-8") as f:
            config = json.load(f)
        log = config.setdefault("log", {})
        log["disabled"] = False
        # Our log file (stdout), not the app's guiLogs: a root-owned file there
        # would block the app's own sing-box.
        log.pop("output", None)
        if verbose:
            log["level"] = "info"
        else:
            log.setdefault("level", "warn")
        log["timestamp"] = True
        experimental = config.get("experimental")
        if isinstance(experimental, dict):
            cache = experimental.get("cache_file")
            if isinstance(cache, dict):
                cache["path"] = os.path.join(decky.DECKY_PLUGIN_RUNTIME_DIR, "cache.db")
            # The app's statistics API port: not used here, and it would clash
            # with the app if it runs in Desktop Mode at the same time.
            experimental.pop("clash_api", None)
        # Only the TUN inbound: the app's local proxy port (10808) stays free
        # for the app itself.
        inbounds = config.get("inbounds")
        if isinstance(inbounds, list):
            config["inbounds"] = [i for i in inbounds if isinstance(i, dict) and i.get("type") == "tun"]
            for tun in config["inbounds"]:
                if not any(":" in str(a) for a in tun.get("address") or []):
                    # No IPv6 on the TUN: without strict_route IPv6 connections go
                    # around the tunnel (browsers and Electron apps prefer IPv6),
                    # with it IPv6 is unreachable and they fall back to IPv4.
                    tun["strict_route"] = True
        os.makedirs(os.path.dirname(RUN_CONFIG), exist_ok=True)
        with open(RUN_CONFIG, "w", encoding="utf-8") as f:
            json.dump(config, f)
        return RUN_CONFIG

    @staticmethod
    async def _terminate(proc):
        if proc is None or proc.returncode is not None:
            return
        proc.terminate()
        try:
            await asyncio.wait_for(proc.wait(), 5)
        except asyncio.TimeoutError:
            proc.kill()
            await proc.wait()

    async def _stop_core(self):
        for name in ("watch", "main_watch"):
            task = getattr(self, name)
            if task is not None:
                task.cancel()
                setattr(self, name, None)
        proc, self.proc = self.proc, None
        main, self.main_proc = self.main_proc, None
        for path in (PID_FILE, MAIN_PID_FILE):
            try:
                os.remove(path)
            except OSError:
                pass
        # The TUN first: with it gone the system is back on its own routes.
        await self._terminate(proc)
        await self._terminate(main)

    async def _watch_core(self, proc):
        code = await proc.wait()
        if self.proc is proc:
            self.proc = None
            tail = _log_tail(3)
            self.error = f"sing-box stopped (code {code})" + (f": {tail}" if tail else "")
            decky.logger.warning(self.error)
            # Nothing listens on the tunnel any more: stop the profile's core too.
            main, self.main_proc = self.main_proc, None
            await self._terminate(main)

    async def _watch_main(self, proc, name):
        code = await proc.wait()
        if self.main_proc is proc:
            self.main_proc = None
            tail = _log_tail(3, MAIN_LOG)
            self.error = f"{name} stopped (code {code})" + (f": {tail}" if tail else "")
            decky.logger.warning(self.error)
            # Without it the tunnel leads nowhere - a dead TUN would only cut
            # the Deck off the network. Back to the direct connection.
            tun, self.proc = self.proc, None
            await self._terminate(tun)

    @staticmethod
    def _quiet_access_log(path, verbose):
        """Xray prints a line per connection (its access log) unless told not to:
        kept only with the detailed log on. The file stays the deck user's."""
        if verbose or not path:
            return
        try:
            with open(path, "r", encoding="utf-8") as f:
                config = json.load(f)
            log = config.get("log")
            if isinstance(log, dict) and "loglevel" in log and not log.get("access"):
                log["access"] = "none"
                with open(path, "w", encoding="utf-8") as f:  # same file: owner unchanged
                    json.dump(config, f)
        except (OSError, ValueError) as e:
            decky.logger.warning(f"access log: {e}")

    async def _start_main(self, main):
        """The profile's own core (Xray) as the deck user: sing-box sends the
        tunnel's traffic to its local port. It must run from the path the app
        reported - the TUN config lets that program's own connections out."""
        uid, gid, home = _deck_user()
        exe = main["exe"]
        name = main.get("core") or os.path.basename(exe)
        env = _user_env(home)
        env.update({k: str(v) for k, v in (main.get("env") or {}).items()})
        self._quiet_access_log(main.get("config"), self._load_settings().get("verbose") is True)
        with open(MAIN_LOG, "ab") as log:
            log.write(f"\n===== {self.profile_name} =====\n".encode("utf-8"))
            log.flush()
            self.main_proc = await asyncio.create_subprocess_exec(
                exe, *[str(a) for a in main.get("args") or []],
                cwd=os.path.dirname(main.get("config") or exe),
                env=env, user=uid, group=gid, extra_groups=[],
                stdout=log, stderr=asyncio.subprocess.STDOUT,
                start_new_session=True,
            )
        with open(MAIN_PID_FILE, "w", encoding="utf-8") as f:
            f.write(f"{self.main_proc.pid} {exe}")
        port = int(main.get("port") or 0)
        ready = await _wait_for_port(port, self.main_proc) if port else True
        if not ready:
            code = self.main_proc.returncode
            tail = _log_tail(6, MAIN_LOG)
            proc, self.main_proc = self.main_proc, None
            await self._terminate(proc)
            reason = f"exited (code {code})" if code is not None else f"does not listen on port {port}"
            raise RuntimeError(f"{name} {reason}" + (f":\n{tail}" if tail else ""))
        self.main_watch = asyncio.create_task(self._watch_main(self.main_proc, name))

    @staticmethod
    def _core_copy():
        """The plugin's sing-box, copied to the runtime folder when it changed."""
        src = os.stat(SING_BOX)
        try:
            dst = os.stat(CORE_COPY)
            fresh = dst.st_size == src.st_size and int(dst.st_mtime) == int(src.st_mtime)
        except OSError:
            fresh = False
        if not fresh:
            os.makedirs(os.path.dirname(CORE_COPY), exist_ok=True)
            tmp = CORE_COPY + ".new"
            shutil.copy2(SING_BOX, tmp)
            os.chmod(tmp, 0o755)
            os.replace(tmp, CORE_COPY)
        return CORE_COPY

    async def _start_core(self, answer):
        run_config = self._prepare_config(answer["config"], self._load_settings().get("verbose") is True)
        core = self._core_copy()
        os.makedirs(os.path.dirname(CORE_LOG), exist_ok=True)
        _rotate_core_log()
        if isinstance(answer.get("main"), dict):
            await self._start_main(answer["main"])
        with open(CORE_LOG, "ab") as log:
            log.write(f"\n===== {self.profile_name} =====\n".encode("utf-8"))
            log.flush()
            self.proc = await asyncio.create_subprocess_exec(
                core, "run", "-c", run_config, "--disable-color",
                cwd=answer.get("cwd") or APP_DIR,
                env={"PATH": SYSTEM_PATH},
                stdout=log,
                stderr=asyncio.subprocess.STDOUT,
                start_new_session=True,
            )
        with open(PID_FILE, "w", encoding="utf-8") as f:
            f.write(str(self.proc.pid))
        # A wrong config or a busy TUN makes sing-box exit right away.
        await asyncio.sleep(2)
        if self.proc.returncode is not None:
            code = self.proc.returncode
            self.proc = None
            tail = _log_tail()
            await self._stop_core()  # the profile's core, if it was started
            raise RuntimeError(f"sing-box exited (code {code})" + (f":\n{tail}" if tail else ""))
        self.watch = asyncio.create_task(self._watch_core(self.proc))

    # ----- called by the frontend -----------------------------------------

    # ----- split tunneling (shared with the app) ----------------------------

    def _whole_deck(self):
        """The plugin's own "whole Deck" mode: the app's lists stay saved but are
        not used. Not set yet: whatever the app's settings say."""
        return self._load_settings().get("whole") is True

    def _split_view(self, answer):
        if not answer.get("ok"):
            return {"ok": False, "error": answer.get("error") or "v2rayN error"}
        apps = [a for a in answer.get("apps") or [] if isinstance(a, str)]
        subnets = [a for a in answer.get("subnets") or [] if isinstance(a, str)]
        settings = self._load_settings()
        whole = settings.get("whole")
        if whole is None:
            whole = not apps and not subnets
        if whole:
            mode = "all"
        else:
            mode = "only" if answer.get("whitelist", True) else "except"
        labels = settings.get("labels") or {}
        return {
            "ok": True,
            "mode": mode,
            "routing": bool(answer.get("useRoutingRules", True)),
            "apps": [
                {"value": a, "label": labels.get(a) or _entry_label(a), "kind": _entry_kind(a)}
                for a in apps
            ],
            "subnets": len(subnets),
        }

    async def get_split(self):
        return self._split_view(await self._v2rayn("split"))

    async def update_split(self, change):
        """change: {"mode": "all"|"only"|"except", "routing": bool,
        "add": [entry], "remove": [entry], "labels": {entry: label}}.
        A running session is reconnected so the change applies at once."""
        change = change if isinstance(change, dict) else {}
        async with self.lock:
            self.busy = True
            self.error = ""
            try:
                split = await self._update_split(change)
            except Exception as e:  # noqa: BLE001 - shown in the panel
                self.error = str(e)
                decky.logger.error(f"split settings: {e}")
                split = {"ok": False, "error": str(e)}
            finally:
                self.busy = False
        return {"split": split, "status": self._status()}

    async def _update_split(self, change):
        current = await self._v2rayn("split")
        if not current.get("ok"):
            return self._split_view(current)
        payload = {}
        whole = None

        mode = change.get("mode")
        if mode in ("all", "only", "except"):
            whole = mode == "all"
            if not whole and bool(current.get("whitelist", True)) != (mode == "only"):
                payload["whitelist"] = mode == "only"

        if "routing" in change and bool(change["routing"]) != bool(current.get("useRoutingRules", True)):
            payload["useRoutingRules"] = bool(change["routing"])

        apps = [a for a in current.get("apps") or [] if isinstance(a, str)]
        add = [a.strip() for a in change.get("add") or [] if isinstance(a, str) and a.strip()]
        remove = {a for a in change.get("remove") or [] if isinstance(a, str)}
        new_apps = [a for a in apps if a not in remove]
        new_apps += [a for a in dict.fromkeys(add) if a not in new_apps]
        if new_apps != apps:
            payload["apps"] = new_apps

        answer = current
        if payload:
            answer = await self._v2rayn("split-set", json.dumps(payload))
            if not answer.get("ok"):
                # Nothing changed: the panel keeps the current settings and says why.
                view = self._split_view(current)
                view["applied"] = False
                view["error"] = answer.get("error") or "v2rayN error"
                return view
        changed = bool(payload)
        # The plugin's own "whole Deck" mode, saved once the app took the rest.
        if whole is not None and self._load_settings().get("whole") is not whole:
            self._save_settings(whole=whole)
            changed = True

        # Names the picker showed for the entries (the entries themselves are
        # paths); kept only for entries that are still in the list.
        labels = dict(self._load_settings().get("labels") or {})
        given = change.get("labels")
        if isinstance(given, dict):
            labels.update({k: str(v)[:120] for k, v in given.items() if isinstance(k, str)})
        saved = [a for a in answer.get("apps") or [] if isinstance(a, str)]
        labels = {k: v for k, v in labels.items() if k in saved}
        if labels != (self._load_settings().get("labels") or {}):
            self._save_settings(labels=labels)

        if changed and self._running() and self.profile_id:
            try:
                await self._connect(self.profile_id)
            except Exception as e:  # noqa: BLE001 - shown in the panel
                self.error = str(e)
                decky.logger.error(f"reconnect failed: {e}")
        return self._split_view(answer)

    async def get_programs(self):
        """Running programs for the picker (see _list_programs)."""
        uid, _, _ = _deck_user()
        started = time.monotonic()
        programs = await asyncio.to_thread(_list_programs, uid, (APP_DIR, CORE_COPY))
        decky.logger.info(f"programs: {len(programs)} in {time.monotonic() - started:.2f}s")
        return {"ok": True, "programs": programs}

    async def get_profiles(self):
        answer = await self._v2rayn("list")
        if answer.get("ok"):
            last = self._load_settings().get("profile_id", "")
            ids = {p.get("id") for p in answer.get("profiles", [])}
            answer["selected"] = self.profile_id or (last if last in ids else answer.get("active", ""))
        return answer

    async def get_status(self):
        return self._status()

    async def _connect(self, profile_id):
        # Config first: a profile that cannot be built leaves the current session up.
        extra = ["--all"] if self._whole_deck() else []
        answer = await self._v2rayn("config", profile_id, *extra)
        if not answer.get("ok"):
            self.error = answer.get("error") or "v2rayN error"
            return
        await self._stop_core()
        self.profile_id = answer.get("id") or profile_id
        self.profile_name = answer.get("name") or ""
        self._save_settings(profile_id=self.profile_id)
        await self._start_core(answer)
        decky.logger.info(f"connected: {self.profile_name}")

    async def connect(self, profile_id):
        async with self.lock:
            self.busy = True
            self.error = ""
            try:
                await self._connect(profile_id)
            except Exception as e:  # noqa: BLE001 - shown in the panel
                self.error = str(e)
                decky.logger.error(f"connect failed: {e}")
            finally:
                self.busy = False
        return self._status()

    async def disconnect(self):
        async with self.lock:
            self.busy = True
            try:
                await self._stop_core()
                self.error = ""
                decky.logger.info("disconnected")
            finally:
                self.busy = False
        return self._status()

    # ----- connection check -------------------------------------------------

    async def _as_user(self, *argv, timeout=20):
        """A command as the deck user in their session (D-Bus for gsettings)."""
        uid, gid, home = _deck_user()
        env = _user_env(home)
        env["XDG_RUNTIME_DIR"] = f"/run/user/{uid}"
        env["DBUS_SESSION_BUS_ADDRESS"] = f"unix:path=/run/user/{uid}/bus"
        try:
            proc = await asyncio.create_subprocess_exec(
                *argv, cwd=home, env=env, user=uid, group=gid, extra_groups=[],
                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
            )
        except OSError as e:
            return 127, "", str(e)
        try:
            out, err = await asyncio.wait_for(proc.communicate(), timeout)
        except asyncio.TimeoutError:
            proc.kill()
            await proc.wait()
            return 124, "", "timeout"
        return (proc.returncode,
                out.decode("utf-8", errors="replace").strip(),
                err.decode("utf-8", errors="replace").strip())

    async def set_verbose(self, on):
        """The detailed sing-box log (every connection with its program and
        outbound) for the check; a running session is reconnected."""
        async with self.lock:
            self.busy = True
            self.error = ""
            try:
                self._save_settings(verbose=bool(on))
                if self._running() and self.profile_id:
                    await self._connect(self.profile_id)
            except Exception as e:  # noqa: BLE001 - shown in the panel
                self.error = str(e)
                decky.logger.error(f"verbose log: {e}")
            finally:
                self.busy = False
        return self._status()

    async def reset_system_proxy(self):
        """Turns off the system proxy the desktop app may have left behind
        (GNOME and KDE settings of the deck user)."""
        done = []
        rc, _, err = await self._as_user("gsettings", "set", "org.gnome.system.proxy", "mode", "none", timeout=10)
        if rc == 0:
            done.append("GNOME")
        elif rc != 127:
            decky.logger.warning(f"gsettings: {err}")
        for tool in ("kwriteconfig6", "kwriteconfig5"):
            if shutil.which(tool, path=SYSTEM_PATH):
                rc, _, err = await self._as_user(tool, "--file", "kioslaverc", "--group", "Proxy Settings",
                                                 "--key", "ProxyType", "0", timeout=10)
                if rc == 0:
                    done.append("KDE")
                    break
                decky.logger.warning(f"{tool}: {err}")
        else:
            if _disable_kde_proxy_file():
                done.append("KDE")
        if not done:
            return {"ok": False, "message": "Не удалось изменить настройки прокси."}
        return {"ok": True, "message": "Системный прокси выключен (" + ", ".join(done) +
                                       "). Перезапустите приложения, которые висели."}

    async def diagnose(self):
        """Checks what an app's traffic hits: the tunnel, IPv6 around it, a
        dead system proxy, DNS; with the detailed log - where each program's
        connections went. Writes a full report to ~/Desktop."""
        uid, gid, home = _deck_user()
        status = self._status()
        split = self._split_view(await self._v2rayn("split"))
        mode = split.get("mode", "?")
        running = status["running"]

        async def site(name, url, family):
            rc, out, err = await self._as_user(
                "curl", "-sS", "-o", "/dev/null", "-m", "12", f"-{family}",
                "-w", "%{http_code} %{remote_ip} %{time_total}s", url, timeout=20)
            ok = rc == 0 and out[:1] in ("2", "3")
            return name, family, ok, out if rc == 0 else (err.splitlines()[-1] if err else f"exit {rc}")

        gathered = await asyncio.gather(
            asyncio.to_thread(_collect_root_facts, uid, home),
            self._as_user("curl", "-sS", "-m", "12", "-4", "https://api.ipify.org"),
            self._as_user("curl", "-sS", "-m", "12", "-6", "https://api6.ipify.org"),
            self._as_user("getent", "ahostsv4", TEST_HOST, timeout=15),
            self._as_user("getent", "ahostsv6", TEST_HOST, timeout=15),
            self._as_user("gsettings", "get", "org.gnome.system.proxy", "mode", timeout=10),
            self._as_user("gsettings", "get", "org.gnome.system.proxy.http", "host", timeout=10),
            self._as_user("gsettings", "get", "org.gnome.system.proxy.http", "port", timeout=10),
            *(site(n, u, f) for n, u in TEST_SITES for f in ("4", "6")),
        )
        facts, ip4, ip6, dns4, dns6, gs_mode, gs_host, gs_port = gathered[:8]
        sites = gathered[8:]
        config_lines, tun = _config_summary(RUN_CONFIG) if running else (["(не подключено)"], {})
        procs, log_errors = _analyze_log(_session_log()) if running else ({}, [])
        main_tail = _log_tail(25, MAIN_LOG) if running and self.main_proc is not None else ""
        verbose = status["verbose"]

        verdicts = []
        if not running:
            verdicts.append("Туннель не подключён. Подключитесь и повторите проверку.")

        # IPv6 around the tunnel
        tun_v6 = any(":" in str(a) for a in tun.get("address") or [])
        if running and not tun_v6 and ip6[0] == 0 and ip6[1]:
            verdicts.append(f"⚠ IPv6 идёт мимо туннеля (внешний IPv6 {ip6[1]}): приложения, которые "
                            "предпочитают IPv6 (браузеры, Discord), ходят напрямую.")
        elif running and facts["global_v6"]:
            verdicts.append("IPv6 у сети есть, мимо туннеля не уходит ✓")

        # A system proxy nobody listens on
        leftovers = []
        if gs_mode[0] == 0 and "manual" in gs_mode[1]:
            leftovers.append(f"GNOME {gs_host[1].strip(chr(39))}:{gs_port[1]}")
        if facts["kde_proxy_on"]:
            leftovers.append("KDE " + facts["kde_proxy"].split("http=", 1)[-1])
        proxy_leftover = bool(leftovers)
        if leftovers:
            verdicts.append("⚠ В системе включён прокси: " + "; ".join(leftovers) + ". Его ставит приложение "
                            "в режиме рабочего стола. Браузеры и Electron-приложения идут на него, а в "
                            "игровом режиме там никто не слушает — они висят. Нажмите «Сбросить системный прокси».")
        for value, apps in facts["env_proxies"].items():
            verdicts.append(f"⚠ Прокси в окружении программ ({', '.join(apps)}): {value}. "
                            "Если он указывает на 127.0.0.1 — уберите его (Flatseal / overrides).")

        # The server refusing the handshake: nothing else can work then.
        refused = [e for e in log_errors if "reality verification failed" in e]
        if refused or "REALITY" in main_tail and "failed" in main_tail:
            verdicts.append("⚠ Сервер отклоняет подключение (REALITY): ключ, short id или SNI профиля не подходят, "
                            "либо сервер не принимает это ядро. Весь туннель не работает, включая DNS.")
        if running:
            cores = "sing-box (TUN)" + (" + ядро профиля" if self.main_proc is not None else "")
            verdicts.append(f"Ядра: {cores}")

        # DNS and sites
        if dns4[0] != 0:
            verdicts.append(f"⚠ DNS не отвечает: {TEST_HOST} не разрешается ({dns4[2] or 'нет ответа'}).")
        v4 = [s for s in sites if s[1] == "4"]
        failed = [s[0] for s in v4 if not s[2]]
        if running:
            note = " (curl не в списке, в режиме «Только выбранные» он идёт напрямую)" if mode == "only" else ""
            if failed:
                verdicts.append(f"⚠ Не открываются{note}: " + ", ".join(failed) + ".")
            else:
                verdicts.append(f"Сайты открываются{note}: " + ", ".join(s[0] for s in v4) + " ✓")
        if ip4[0] == 0 and ip4[1]:
            verdicts.append(f"Внешний IPv4: {ip4[1]}")

        # Where each program's connections went
        if running and not verbose:
            verdicts.append("Чтобы увидеть, куда идут соединения приложения: включите «Подробный журнал», "
                            "запустите приложение, дождитесь зависания и нажмите «Проверить» ещё раз.")
        elif running:
            for app_id, exes in facts["flatpaks"].items():
                mine = {path: p for path, p in procs.items() if path in exes}
                if not mine:
                    verdicts.append(f"⚠ {app_id}: в туннеле нет ни одного его соединения — приложение "
                                    "ходит мимо (системный прокси, IPv6) или ещё не подключалось.")
                for path, p in mine.items():
                    verdicts.append(f"{app_id} — " + _describe_proc(os.path.basename(path), p))
            if not facts["flatpaks"]:
                verdicts.append("Flatpak-приложения сейчас не запущены — запустите нужное и повторите проверку.")

        # The full report
        r = [f"Secure Connect — проверка {time.strftime('%Y-%m-%d %H:%M:%S')}", ""]
        r += ["== Итог"] + verdicts + [""]
        r += ["== Плагин",
              f"подключено: {running}, профиль: {status['profile_name']}, ошибка: {status['error'] or '-'}",
              f"режим: {mode}, правила маршрутизации: {split.get('routing')}, подробный журнал: {verbose}",
              "список: " + (", ".join(a["value"] for a in split.get("apps") or []) or "-"), ""]
        r += ["== Конфиг sing-box (без ключей и адресов серверов)"] + config_lines + [""]
        r += ["== Сеть", facts["addr"], "", "глобальные IPv6: " + (", ".join(facts["global_v6"]) or "нет"),
              "", "ip rule:", facts["rules4"], "", "ip -6 rule:", facts["rules6"], "", "default:", facts["routes"], ""]
        r += ["== DNS", f"/etc/resolv.conf -> {facts['resolv_link']}", facts["resolv"], "", facts["resolvectl"], "",
              f"getent ahostsv4 {TEST_HOST}: " + (dns4[1].splitlines()[0] if dns4[1] else dns4[2]),
              f"getent ahostsv6 {TEST_HOST}: " + (dns6[1].splitlines()[0] if dns6[1] else dns6[2] or "-"), ""]
        r += ["== Проверка сайтов (curl от пользователя deck)"]
        r += [f"{name} IPv{fam}: {'OK' if ok else 'FAIL'} {info}" for name, fam, ok, info in sites]
        r += [f"внешний IPv4: {ip4[1] or ip4[2]}", f"внешний IPv6: {ip6[1] or ip6[2]}", ""]
        r += ["== Прокси", f"GNOME: mode={gs_mode[1] or gs_mode[2]} host={gs_host[1]} port={gs_port[1]}",
              f"KDE (kioslaverc): {facts['kde_proxy'] or '-'}",
              "в окружении: " + (json.dumps(facts["env_proxies"], ensure_ascii=False) if facts["env_proxies"] else "-"), ""]
        r += ["== Flatpak", "запущены: " + (json.dumps(facts["flatpaks"], ensure_ascii=False) or "-")]
        for path, text in facts["overrides"].items():
            r += [f"--- {path}", text]
        r += [""]
        r += ["== Соединения по программам (журнал текущей сессии)"]
        for path, p in sorted(procs.items(), key=lambda kv: -kv[1]["count"])[:25]:
            r.append(_describe_proc(path, p))
            sites_seen = ", ".join(f"{d} {n}" for d, n in p["sites"].most_common(6))
            if sites_seen:
                r.append(f"    сайты: {sites_seen}")
        if not procs:
            r.append("(нет данных — включите «Подробный журнал»)" if not verbose else "(соединений нет)")
        r += ["", "== Последние ошибки sing-box"] + (log_errors or ["-"])
        r += ["", "== Журнал ядра профиля (core.log)", main_tail or "-"]

        desktop = os.path.join(home, "Desktop")
        path = os.path.join(desktop if os.path.isdir(desktop) else home, DIAG_FILE)
        try:
            with open(path, "w", encoding="utf-8") as f:
                f.write("\n".join(str(x) for x in r) + "\n")
            os.chown(path, uid, gid)
            shown = path.replace(home, "~", 1)
        except OSError as e:
            shown = f"не удалось сохранить: {e}"
        decky.logger.info(f"check: {len(verdicts)} findings, report {path}")
        return {"ok": True, "verdicts": verdicts, "report": shown, "proxy_leftover": proxy_leftover}

    # ----- Decky lifecycle ------------------------------------------------

    async def _main(self):
        _kill_stale_core()
        try:
            _fix_permissions()
        except OSError as e:
            decky.logger.error(f"permissions: {e}")
        try:
            _write_desktop_entry()
        except (OSError, KeyError) as e:
            decky.logger.error(f"desktop entry: {e}")
        decky.logger.info("Secure Connect loaded")

    async def _unload(self):
        await self._stop_core()
        decky.logger.info("Secure Connect unloaded")

    async def _uninstall(self):
        await self._stop_core()
        try:
            _, _, home = _deck_user()
            os.remove(os.path.join(home, ".local", "share", "applications", DESKTOP_FILE))
        except (OSError, KeyError):
            pass
