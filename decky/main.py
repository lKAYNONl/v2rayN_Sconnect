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
import time

import decky

PLUGIN_DIR = decky.DECKY_PLUGIN_DIR
APP_DIR = os.path.join(PLUGIN_DIR, "bin", "v2rayN")
APP_EXE = os.path.join(APP_DIR, "v2rayN")
SING_BOX = os.path.join(APP_DIR, "bin", "sing_box", "sing-box")
SETTINGS_FILE = os.path.join(decky.DECKY_PLUGIN_SETTINGS_DIR, "settings.json")
RUN_CONFIG = os.path.join(decky.DECKY_PLUGIN_RUNTIME_DIR, "sing-box.json")
CORE_LOG = os.path.join(decky.DECKY_PLUGIN_LOG_DIR, "sing-box.log")
PID_FILE = os.path.join(decky.DECKY_PLUGIN_RUNTIME_DIR, "sing-box.pid")
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
    """A sing-box left running by a crashed Decky would keep the TUN busy."""
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
        os.remove(PID_FILE)
    except OSError:
        pass


def _log_tail(lines=6):
    try:
        with open(CORE_LOG, "rb") as f:
            f.seek(0, os.SEEK_END)
            f.seek(max(0, f.tell() - 4096))
            text = f.read().decode("utf-8", errors="replace")
        return "\n".join(line for line in text.splitlines()[-lines:] if line.strip())
    except OSError:
        return ""


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

    # ----- sing-box (root) ------------------------------------------------

    def _running(self):
        return self.proc is not None and self.proc.returncode is None

    def _status(self):
        return {
            "running": self._running(),
            "busy": self.busy,
            "profile_id": self.profile_id,
            "profile_name": self.profile_name,
            "error": self.error,
        }

    @staticmethod
    def _prepare_config(source):
        """The generated config, adjusted for the plugin: log to our log file,
        sing-box cache in the plugin's runtime folder (root-owned files must not
        end up in the deck user's v2rayN folder), only the TUN inbound."""
        with open(source, "r", encoding="utf-8") as f:
            config = json.load(f)
        log = config.setdefault("log", {})
        log["disabled"] = False
        # Our log file (stdout), not the app's guiLogs: a root-owned file there
        # would block the app's own sing-box.
        log.pop("output", None)
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
        os.makedirs(os.path.dirname(RUN_CONFIG), exist_ok=True)
        with open(RUN_CONFIG, "w", encoding="utf-8") as f:
            json.dump(config, f)
        return RUN_CONFIG

    async def _stop_core(self):
        if self.watch is not None:
            self.watch.cancel()
            self.watch = None
        proc, self.proc = self.proc, None
        try:
            os.remove(PID_FILE)
        except OSError:
            pass
        if proc is None or proc.returncode is not None:
            return
        proc.terminate()
        try:
            await asyncio.wait_for(proc.wait(), 5)
        except asyncio.TimeoutError:
            proc.kill()
            await proc.wait()

    async def _watch_core(self, proc):
        code = await proc.wait()
        if self.proc is proc:
            self.proc = None
            tail = _log_tail(3)
            self.error = f"sing-box stopped (code {code})" + (f": {tail}" if tail else "")
            decky.logger.warning(self.error)

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
        run_config = self._prepare_config(answer["config"])
        core = self._core_copy()
        os.makedirs(os.path.dirname(CORE_LOG), exist_ok=True)
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
