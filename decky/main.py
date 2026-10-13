"""Secure Connect for Steam Deck (Decky Loader backend).

The plugin ships the Linux build of v2rayN Secure Connect in bin/v2rayN. In
Game Mode it lets you pick one of your v2rayN profiles and runs it with
sing-box in TUN mode, so the whole Deck goes through the tunnel:

  1. "v2rayN --decky list" / "--decky config <id>" (run as the deck user, on
     the same profiles and settings as the app in Desktop Mode) list the
     profiles and write a sing-box config for the chosen one;
  2. this backend (root, see plugin.json) starts the plugin's own sing-box
     with that config and stops it on Disconnect / unload.

Profiles, subscriptions, routes and split tunneling are managed in the app
itself (Desktop Mode, menu entry "Secure Connect (v2rayN)").
"""

import asyncio
import json
import os
import pwd
import shutil

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
        answer = await self._v2rayn("config", profile_id)
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
