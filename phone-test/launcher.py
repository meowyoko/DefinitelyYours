"""Install the local test build temporarily and restore it after testing."""
import contextlib
import ctypes
from ctypes import wintypes
import hashlib
from http.client import HTTPException
import json
import msvcrt
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
import zipfile


ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / ".phone-test-state"
BACKUP = RUNTIME / "backup"
MANIFEST = RUNTIME / "pending.json"
SOURCE_DLL = ROOT / "mod/bin/Release/netstandard2.1/AiPhone.dll"
CONFIG = ROOT / "phone-test/test-config.json"
PLAYER_CONFIG = ROOT / "phone-test/player-config.json"
VOICE_ENV = PLAYER_CONFIG.with_name(".env")
VOICE_KEYS = {
    "ELEVENLABS_API_KEY", "PHONE_VOICE_ID", "ELEVENLABS_MODEL_ID", "PHONE_TTS_PROVIDER",
    "PHONE_TTS_URL", "PHONE_TTS_MODEL", "PHONE_TTS_VOICE", "PHONE_TTS_API_KEY",
}
# Versioned Essentials ZIP and publisher checksum from Gyan's release page.
FFMPEG_RELEASE = "ffmpeg-9.0.2-essentials_build"
FFMPEG_URL = f"https://www.gyan.dev/ffmpeg/builds/packages/{FFMPEG_RELEASE}.zip"
FFMPEG_SHA256 = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba"
PACKAGED_MOD = ROOT / "mod"
FILES = ("AiPhone.dll", "config.json")
HIDDEN = subprocess.CREATE_NO_WINDOW


def say(message):
    print(message, flush=True)


@contextlib.contextmanager
def exclusive():
    RUNTIME.mkdir(exist_ok=True)
    with (RUNTIME / "launcher.lock").open("a+b") as lock:
        try:
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise RuntimeError("另一个测试窗口或自动恢复进程正在运行。请先退出游戏和原测试窗口。") from error
        try:
            yield
        finally:
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)


def game_running():
    class ProcessEntry(ctypes.Structure):
        _fields_ = [
            ("size", wintypes.DWORD), ("usage", wintypes.DWORD),
            ("pid", wintypes.DWORD), ("heap", ctypes.c_size_t),
            ("module", wintypes.DWORD), ("threads", wintypes.DWORD),
            ("parent", wintypes.DWORD), ("priority", wintypes.LONG),
            ("flags", wintypes.DWORD), ("name", wintypes.WCHAR * 260),
        ]

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateToolhelp32Snapshot.argtypes = (wintypes.DWORD, wintypes.DWORD)
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.Process32FirstW.argtypes = kernel.Process32NextW.argtypes = (
        wintypes.HANDLE, ctypes.POINTER(ProcessEntry),
    )
    kernel.Process32FirstW.restype = kernel.Process32NextW.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
    snapshot = kernel.CreateToolhelp32Snapshot(2, 0)
    if snapshot == wintypes.HANDLE(-1).value:
        raise OSError(ctypes.get_last_error(), "无法检查游戏是否正在运行。")
    try:
        entry = ProcessEntry(size=ctypes.sizeof(ProcessEntry))
        found = kernel.Process32FirstW(snapshot, ctypes.byref(entry))
        while found:
            if entry.name.lower() == "probably stolen.exe":
                return True
            found = kernel.Process32NextW(snapshot, ctypes.byref(entry))
        if ctypes.get_last_error() != 18:
            raise OSError(ctypes.get_last_error(), "读取游戏进程状态失败。")
        return False
    finally:
        kernel.CloseHandle(snapshot)


def replace_file(target, *, source=None, data=None, content=None):
    staged = target.with_name(f".{target.name}.{secrets.token_hex(6)}.tmp")
    try:
        if content is not None:
            staged.write_text(content, encoding="utf-8")
        elif source is None:
            staged.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        else:
            shutil.copyfile(source, staged)
        os.replace(staged, target)
    finally:
        staged.unlink(missing_ok=True)


def backup(mod_dir):
    BACKUP.mkdir(exist_ok=True)
    original_files = []
    for name in FILES:
        if (mod_dir / name).is_file():
            replace_file(BACKUP / name, source=mod_dir / name)
            original_files.append(name)
    replace_file(MANIFEST, data={"modDirectory": str(mod_dir), "originalFiles": original_files})


def restore():
    if not MANIFEST.exists():
        return False
    if game_running():
        raise PermissionError("请先退出游戏。")
    saved = json.loads(MANIFEST.read_text(encoding="utf-8"))
    mod_dir = Path(saved["modDirectory"])
    original = saved["originalFiles"]
    if (mod_dir.name.lower() != "aiphone" or mod_dir.parent.name.lower() != "mods"
            or "AiPhone.dll" not in original or any(not (BACKUP / name).is_file() for name in original)):
        raise RuntimeError("测试备份不完整。原文件未自动覆盖，请检查测试状态目录。")
    for name in FILES:
        target = mod_dir / name
        if name in original:
            replace_file(target, source=BACKUP / name)
        else:
            target.unlink(missing_ok=True)
    MANIFEST.unlink()
    return True


def command_template(arguments, mcp, model=None):
    if not isinstance(arguments, list) or not arguments or not all(isinstance(arg, str) for arg in arguments):
        raise ValueError("命令模板必须是非空的 JSON 字符串数组。")
    executable = shutil.which(arguments[0])
    if not executable and arguments[0].lower() == "codex.exe":
        local_app_data = os.environ.get("LOCALAPPDATA")
        if local_app_data:
            bundled = Path(local_app_data) / "OpenAI/Codex/bin"
            candidate = max(bundled.glob("*/codex.exe"), key=lambda path: path.stat().st_mtime, default=None)
            executable = str(candidate) if candidate else None
    if not executable:
        raise FileNotFoundError(f"找不到客户端 {arguments[0]}。可回启动器复制接入说明，请客户端配置完整路径。")
    if any("{model}" in arg for arg in arguments[1:]) and (not isinstance(model, str) or not model.strip()):
        raise ValueError("命令模板使用了 {model}，请在测试配置中填写 cliModel。")
    return [executable, *(arg.replace("{model}", model or "").replace("{mcp}", mcp) for arg in arguments[1:])]


def voice_environment():
    settings = {name: os.environ[name] for name in VOICE_KEYS if name in os.environ}
    if VOICE_ENV.is_file():
        for line in VOICE_ENV.read_text(encoding="utf-8-sig").splitlines():
            key, _, value = line.partition("=")
            if key.strip() in VOICE_KEYS:
                settings[key.strip()] = value.strip().strip("\"'")
    return settings


def player_voice_provider(cfg):
    return cfg.get("voiceProvider", "elevenlabs" if cfg.get("voiceEnabled") else "off")


def find_ffmpeg():
    portable = RUNTIME / "bin/ffmpeg.exe"
    return str(portable) if portable.is_file() else shutil.which("ffmpeg")


def ffmpeg_manual_instructions():
    return (
        f"1. 下载 ZIP：\n{FFMPEG_URL}\n\n"
        f"2. 解压后取出 {FFMPEG_RELEASE}/bin/ffmpeg.exe。\n\n"
        f"3. 将 ffmpeg.exe 放到：\n{RUNTIME / 'bin/ffmpeg.exe'}\n"
        "如果文件夹还不存在，请先新建。放好后返回启动器，再点「启动电话服务」。"
    )


def install_ffmpeg(bin_dir, stop):
    target = bin_dir / "ffmpeg.exe"
    if target.is_file():
        return target
    bin_dir.mkdir(exist_ok=True)
    staged = bin_dir / f".ffmpeg-{secrets.token_hex(6)}.tmp"
    digest = hashlib.sha256()
    downloaded = 0
    progress_at = 10 * 1024 * 1024
    say("首次启用语音，正在下载 FFmpeg 便携版（约 109 MB）……")
    try:
        with tempfile.TemporaryFile(dir=bin_dir) as archive:
            try:
                with urllib.request.urlopen(FFMPEG_URL, timeout=20) as response:
                    while not stop.is_set():
                        chunk = response.read(1024 * 1024)
                        if not chunk:
                            break
                        downloaded += len(chunk)
                        if downloaded > 150 * 1024 * 1024:
                            raise RuntimeError("FFmpeg 下载大小异常。请重试，或先关闭语音。")
                        archive.write(chunk)
                        digest.update(chunk)
                        if downloaded >= progress_at:
                            say(f"FFmpeg 已下载 {downloaded // (1024 * 1024)} MB……")
                            progress_at = downloaded + 10 * 1024 * 1024
            except (OSError, HTTPException) as error:
                if stop.is_set():
                    return None
                raise RuntimeError("FFmpeg 下载失败。请检查网络后重试，或关闭语音继续游玩。") from error
            if stop.is_set():
                return None
            if digest.hexdigest() != FFMPEG_SHA256:
                raise RuntimeError("FFmpeg 下载文件校验失败。请重试，或先关闭语音。")
            archive.seek(0)
            with zipfile.ZipFile(archive) as bundle, bundle.open(f"{FFMPEG_RELEASE}/bin/ffmpeg.exe") as executable:
                with staged.open("wb") as destination:
                    shutil.copyfileobj(executable, destination)
        if stop.is_set():
            return None
        if not target.is_file():
            os.replace(staged, target)
    except (zipfile.BadZipFile, OSError, KeyError) as error:
        raise RuntimeError("FFmpeg 安装失败。请确认启动器目录可写，再重试。") from error
    finally:
        staged.unlink(missing_ok=True)
    say("FFmpeg 已就绪。")
    return target


def prepare(cfg, *, player=False):
    mode = cfg["wakeMode"]
    if mode not in {"cli", "poll", "claude"}:
        raise ValueError("wakeMode 必须是 cli、poll 或 claude。")
    port = cfg.get("port", 0 if mode == "cli" else 3900)
    if type(port) is not int or not 0 <= port <= 65535:
        raise ValueError("端口必须为 0（自动）或 1 到 65535 的整数。")
    if port == 0 and mode != "cli":
        raise ValueError("长轮询和 Claude 频道需要固定端口。")
    timeout = cfg.get("replyTimeoutMs", 42000)
    if type(timeout) is not int or not 0 < timeout < 60000:
        raise ValueError("replyTimeoutMs 必须为正整数，且小于游戏的 60000 毫秒请求超时。")
    with socket.socket() as check:
        try:
            check.bind(("127.0.0.1", port))
        except OSError as error:
            raise RuntimeError(f"电话端口 {port} 已被占用或无法监听。请关掉旧服务或更换端口。") from error
        port = check.getsockname()[1]
    game_dir = cfg.get("gameDirectory")
    if game_dir:
        game_dir = (ROOT / game_dir).resolve()
    else:
        game_dir = next((path for path in (ROOT, *ROOT.parents) if (path / "Probably Stolen.exe").is_file()), None)
        if game_dir is None:
            raise FileNotFoundError("找不到游戏目录。请在启动器选择游戏目录，或在测试配置中填写 gameDirectory。")
    mod_dir = game_dir / "Mods" / "AiPhone"
    if player and not (mod_dir.parent.parent / "Probably Stolen.exe").is_file():
        raise FileNotFoundError("找不到游戏程序。请选择包含 Probably Stolen.exe 的目录。")
    if not player and not all((mod_dir / name).is_file() for name in ("manifest.xml", "AiPhone.dll")):
        raise FileNotFoundError("游戏目录里找不到已安装的 Mods/AiPhone。")
    if not player and not SOURCE_DLL.is_file():
        raise FileNotFoundError("找不到测试版 DLL，请先构建 mod/AiPhone.csproj 的 Release 版本。")
    if game_running():
        raise RuntimeError("请先退出游戏，再双击开始测试。")

    url = f"http://127.0.0.1:{port}"
    # Keep test state independent of the live phone plugin's data and settings.
    state_dir = RUNTIME / ("player-service" if player else "service")
    state_dir.mkdir(exist_ok=True)
    env = os.environ.copy()
    ffmpeg = find_ffmpeg()
    if ffmpeg:
        env["PHONE_FFMPEG_PATH"] = ffmpeg
    else:
        env.pop("PHONE_FFMPEG_PATH", None)
    voice_loudness = cfg.get("voiceLoudnessLufs")
    env.update(PHONE_HOST="127.0.0.1", PHONE_PORT=str(port), PHONE_WAKE_MODE=mode, PHONE_TIMEOUT_MS=str(timeout),
               PHONE_STATE_DIR=str(state_dir), PHONE_TOKEN=secrets.token_urlsafe(24),
               PHONE_PERSONA_DIR=str(CONFIG.parent), PHONE_LOCAL_PERSONA_EDITOR="1",
               PHONE_AUDIO_FORMAT=cfg.get("audioFormat", "wav"), PHONE_CLI_CWD=str(state_dir),
               PHONE_VOICE_LANGUAGE=cfg.get("voiceLanguage", ""), PHONE_TEXT_LANGUAGE=cfg.get("textLanguage", ""),
               PHONE_VOICE_LOUDNESS_LUFS=str(voice_loudness) if voice_loudness is not None else "",
               PHONE_AUTO_OPEN="1", PHONE_IGNORE_DOTENV="1", PHONE_STOP_ON_STDIN_EOF="1", PYTHONUTF8="1")
    env.pop("PHONE_TTS_CONFIG_FILE", None)
    env.update(voice_environment())
    if player:
        provider = player_voice_provider(cfg)
        env["PHONE_TTS_PROVIDER"] = provider
        if provider == "elevenlabs" and not (env.get("ELEVENLABS_API_KEY") and env.get("PHONE_VOICE_ID")):
            raise ValueError("请填写 ElevenLabs API Key 和音色 ID，或关闭语音。")
        if provider == "openai" and not all(env.get(name) for name in ("PHONE_TTS_URL", "PHONE_TTS_MODEL", "PHONE_TTS_VOICE")):
            raise ValueError("请填写兼容语音服务的接口地址、模型和音色。")
        if provider != "off":
            if env["PHONE_AUDIO_FORMAT"] != "wav":
                raise ValueError("游戏语音需要 WAV，请在高级配置中将 audioFormat 设为 wav。")
    env["PHONE_MCP_TOKEN"] = env.get(cfg.get("mcpTokenEnv", "PHONE_MCP_TOKEN")) or secrets.token_urlsafe(24)
    args = [sys.executable, "-u", str(ROOT / "phone-plugin/server.py")]
    if mode == "cli":
        mcp = (f'mcp_servers.game_phone={{url="{url}/mcp",bearer_token_env_var="PHONE_MCP_TOKEN",'
               'required=true,default_tools_approval_mode="approve"}')
        preset = cfg.get("client")
        create, resume = cli_commands(cfg, preset, url, state_dir)
        model = cfg.get("cliModel")
        env["PHONE_CLI_CREATE"] = json.dumps(command_template(create, mcp, model))
        env["PHONE_CLI_RESUME"] = json.dumps(command_template(resume, mcp, model))
        field = cfg.get("cliSessionField")
        if field:
            if not isinstance(field, str) or not all(part.isidentifier() for part in field.split(".")):
                raise ValueError("会话 ID 字段须为字段名或点分路径，例如 session_id 或 session.id。")
            env["PHONE_CLI_SESSION_FIELD"] = field
        if preset in {"codex", "dsh"}:
            env["PHONE_CLI_PROFILE"] = preset
        elif cfg.get("cliProfile"):
            env["PHONE_CLI_PROFILE"] = cfg["cliProfile"]
    elif mode == "poll" and not os.environ.get(cfg.get("mcpTokenEnv", "PHONE_MCP_TOKEN")):
        raise ValueError("长轮询模式需要先在启动环境中设置配置指定的 MCP 口令，供桌面客户端使用。")
    elif mode == "claude":
        args = command_template(cfg["claudeCommand"], "")
        env.pop("PHONE_STOP_ON_STDIN_EOF")
    return mod_dir, url, env, args, mode


def cli_commands(cfg, preset, url, state_dir):
    if preset not in {"codex", "dsh"}:
        if not cfg.get("cliCreate") or not cfg.get("cliResume"):
            raise ValueError("自定义客户端尚未配置。请回启动器复制接入说明。")
        return cfg["cliCreate"], cfg["cliResume"]
    executable = cfg.get("clientExecutable")
    if preset == "codex":
        executable = executable or shutil.which("codex.exe") or shutil.which("codex.cmd") or "codex.exe"
        command = npm_command(executable, "@openai/codex/bin/codex.js")
        options = ["--skip-git-repo-check", "--json", "-c", "{mcp}"]
        if cfg.get("cliModel"):
            options += ["-m", "{model}"]
        return [*command, "exec", *options, "-"], [*command, "exec", "resume", *options, "{session}", "-"]

    selected = Path(executable) if executable else None
    if selected and selected.suffix.lower() in {".js", ".mjs", ".cjs"}:
        entry = selected.resolve()
        if not entry.is_file():
            raise FileNotFoundError("找不到已配置的 DSH 入口。请回启动器复制接入说明，更新客户端路径。")
        bundled_node = next((parent / "node/bin/node.exe" for parent in entry.parents
                             if parent.name.lower() == "node_modules" and (parent / "node/bin/node.exe").is_file()), None)
        explicit_node = cfg.get("clientNodeExecutable")
        node = shutil.which(explicit_node) if explicit_node else str(bundled_node) if bundled_node else shutil.which("node.exe")
        if explicit_node and not node:
            raise FileNotFoundError("找不到已配置的 Node 程序。请用接入说明更新 Node 路径。")
        if not node:
            raise FileNotFoundError("找到了 DSH 入口，但找不到配套的 Node。请回启动器复制接入说明。")
        command = [node, str(entry)]
    else:
        found = shutil.which(executable or "dsh")
        if not found:
            raise FileNotFoundError("在 PATH 中找不到 DSH。请回启动器复制接入说明，让 DSH 配置自己的入口。")
        command = npm_command(found, "@deepseek-ai/dsh/lib/bin.js")
    sample = ROOT / "phone-test/dsh-mcp.example.yml"
    patch = state_dir / "dsh-mcp.yml"
    patch.write_text(sample.read_text(encoding="utf-8").replace("http://127.0.0.1:3901", url), encoding="utf-8")
    options = [*command, "--profile", "headless", "--patch", str(patch), "--json"]
    return [*options, "-"], [*options, "--session-id", "{session}", "-"]


def npm_command(executable, entrypoint):
    if Path(executable).suffix.lower() != ".cmd":
        return [executable]
    found = shutil.which(executable)
    if not found:
        raise FileNotFoundError("找不到所选客户端程序，请重新选择。")
    entry = Path(found).parent / "node_modules" / entrypoint
    node = shutil.which("node.exe")
    if not node or not entry.is_file():
        raise FileNotFoundError("找到了 npm 客户端入口，但缺少 Node 或客户端安装文件。请重新检查安装。")
    return [node, str(entry)]


def stop_service(process, mode):
    if process is None or process.poll() is not None:
        return
    if process.stdin:
        process.stdin.close()
        try:
            process.wait(timeout=6)
            return
        except subprocess.TimeoutExpired:
            pass
    subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], creationflags=HIDDEN,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    process.wait(timeout=5)


def install_player_mod(mod_dir):
    dll = PACKAGED_MOD / "AiPhone.dll"
    if not dll.is_file():
        dll = SOURCE_DLL
    assets = (PACKAGED_MOD / "manifest.xml", PACKAGED_MOD / "Textures/Items")
    if not dll.is_file() or not assets[0].is_file() or not assets[1].is_dir():
        raise FileNotFoundError("缺少 Mod 发布文件。请完整解压玩家安装包。")
    if (mod_dir / "AiPhone.dll").is_file():
        if not (mod_dir / "manifest.xml").is_file():
            raise FileNotFoundError("原 Mod 缺少 manifest.xml。请先修复原安装。")
    else:
        mod_dir.mkdir(parents=True, exist_ok=True)
        replace_file(mod_dir / "manifest.xml", source=assets[0])
        shutil.copytree(assets[1], mod_dir / "Textures/Items", dirs_exist_ok=True)
        replace_file(mod_dir / "AiPhone.dll", source=dll)

    starter_events = PACKAGED_MOD / "events.starter.txt"
    events = mod_dir / "events.txt"
    if starter_events.is_file() and not events.exists():
        replace_file(events, source=starter_events)


def check_mcp(http, url, token):
    def rpc(method, params, request_id=None):
        message = {"jsonrpc": "2.0", "method": method, "params": params}
        if request_id is not None:
            message["id"] = request_id
        request = urllib.request.Request(url + "/mcp", json.dumps(message).encode("utf-8"), {
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
            "Authorization": f"Bearer {token}",
        })
        with http.open(request, timeout=3) as response:
            return json.load(response) if request_id is not None else None

    initialized = rpc("initialize", {
        "protocolVersion": "2025-03-26", "capabilities": {},
        "clientInfo": {"name": "Definitely Yours connection check", "version": "1"},
    }, 1)
    if "result" not in initialized:
        raise RuntimeError("MCP 初始化失败，请检查电话服务配置。")
    rpc("notifications/initialized", {})
    tools = rpc("tools/list", {}, 2)
    if not any(tool["name"] == "phone_reply" for tool in tools.get("result", {}).get("tools", [])):
        raise RuntimeError("MCP 已连接，但找不到电话回复工具。")


def watch(*, player=False, check_only=False, download_ffmpeg=False):
    stop = threading.Event()

    def watch_window():
        try:
            while os.read(sys.stdin.fileno(), 4096):
                pass
        finally:
            stop.set()

    threading.Thread(target=watch_window, daemon=True).start()
    with exclusive():
        if MANIFEST.exists():
            raise RuntimeError("上次测试的备份尚未还原，请先运行恢复原配置.bat。")
        config = PLAYER_CONFIG if player else CONFIG
        if not config.is_file():
            raise FileNotFoundError("找不到配置文件。测试请从 test-config.example.json 创建；游玩请打开连接设置。")
        cfg = json.loads(config.read_text(encoding="utf-8-sig"))
        mod_dir, url, env, args, mode = prepare(cfg, player=player)
        if stop.is_set():
            return
        if not check_only:
            if player and env["PHONE_TTS_PROVIDER"] != "off" and "PHONE_FFMPEG_PATH" not in env:
                if not download_ffmpeg:
                    raise RuntimeError("语音需要 FFmpeg。请回启动器选择自动下载，或按以下步骤手动准备。\n\n"
                                       + ffmpeg_manual_instructions())
                ffmpeg = install_ffmpeg(RUNTIME / "bin", stop)
                if ffmpeg is None:
                    return
                env["PHONE_FFMPEG_PATH"] = str(ffmpeg)
            if player:
                install_player_mod(mod_dir)
            backup(mod_dir)
        process = None
        voice_snapshot = None
        try:
            if not player:
                replace_file(mod_dir / "AiPhone.dll", source=SOURCE_DLL)
            if not check_only:
                replace_file(mod_dir / "config.json", data={"baseUrl": url, "token": env["PHONE_TOKEN"]})
            if mode == "claude":
                voice_snapshot = Path(env["PHONE_STATE_DIR"]) / "voice.env"
                replace_file(voice_snapshot, content="".join(f"{name}={env.pop(name)}\n" for name in VOICE_KEYS if name in env))
                env["PHONE_TTS_CONFIG_FILE"] = str(voice_snapshot)
                process = subprocess.Popen(args, env=env, cwd=ROOT / "phone-plugin", creationflags=subprocess.CREATE_NEW_CONSOLE)
            else:
                process = subprocess.Popen(args, env=env, stdin=subprocess.PIPE, stderr=subprocess.STDOUT)
            http = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            for _ in range(160 if mode == "claude" else 60):
                if stop.is_set():
                    return
                if process.poll() is not None:
                    raise RuntimeError(f"电话服务启动失败（退出码 {process.returncode}）。")
                try:
                    with http.open(url + "/phone/health", timeout=0.5) as response:
                        health = json.load(response)
                except OSError:
                    stop.wait(0.25)
                    continue
                if health.get("ok"):
                    if mode != "claude" and (player or check_only):
                        check_mcp(http, url, env["PHONE_MCP_TOKEN"])
                    if check_only:
                        say("电话服务与 MCP 检查通过。客户端登录和实际回电请在游戏里验证。")
                        return
                    say("可以开游戏了，从电话簿选择 AI 联系人。" if player else "可以开游戏了，拨 10000")
                    say("语音已配置，关闭游戏里的纯文字模式即可计时。" if health.get("voice")
                        else "当前未配置语音，先用纯文字测试。")
                    say("结束时退出游戏，然后关闭本窗口或按 Ctrl+C，文件会自动还原。")
                    while process.poll() is None and not stop.wait(0.25):
                        pass
                    if process.poll() not in (None, 0) and not stop.is_set():
                        raise RuntimeError(f"电话服务已退出（退出码 {process.returncode}）。")
                    return
            raise RuntimeError("等待电话服务就绪超时。")
        finally:
            try:
                stop_service(process, mode)
            finally:
                if voice_snapshot is not None:
                    voice_snapshot.unlink(missing_ok=True)
                if not check_only and game_running():
                    say("正在等待游戏退出，退出后会自动还原 DLL 和配置。")
                while not check_only and game_running():
                    time.sleep(2)
                if not check_only and restore():
                    say("原 DLL 和配置已还原。")


def start(*, player=False, check_only=False, download_ffmpeg=False):
    RUNTIME.mkdir(exist_ok=True)
    logfile = RUNTIME / f"test-{time.strftime('%Y%m%d-%H%M%S')}-{secrets.token_hex(3)}.log"
    with logfile.open("wb") as output:
        action = "check-player" if check_only else "watch-player" if player else "watch"
        args = [sys.executable, "-u", __file__, action]
        if download_ffmpeg:
            args.append("--download-ffmpeg")
        process = subprocess.Popen(args, stdin=subprocess.PIPE,
                                   stdout=output, stderr=output,
                                   creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP)
    say(f"测试日志：{logfile}")
    with logfile.open(encoding="utf-8", errors="replace") as log:
        try:
            while process.poll() is None:
                for line in log:
                    say(line.rstrip())
                time.sleep(0.20)
        except KeyboardInterrupt:
            say("正在停止测试并还原文件……")
        finally:
            process.stdin.close()
        while process.poll() is None:
            for line in log:
                say(line.rstrip())
            time.sleep(0.20)
        for line in log:
            say(line.rstrip())
    return process.returncode


if __name__ == "__main__":
    try:
        if sys.version_info < (3, 10):
            raise RuntimeError("请安装 Python 3.10 或更新版本。")
        action = sys.argv[1]
        if action == "watch":
            watch()
        elif action == "start":
            sys.exit(start())
        elif action in {"watch-player", "check-player"}:
            watch(player=True, check_only=action == "check-player", download_ffmpeg="--download-ffmpeg" in sys.argv)
        elif action in {"play", "check"}:
            result = start(player=True, check_only=action == "check", download_ffmpeg="--download-ffmpeg" in sys.argv)
            if "--pause" in sys.argv and (result or action == "check"):
                input("按回车关闭窗口。")
            sys.exit(result)
        elif action == "setup":
            from player_setup import show
            show()
        elif action == "restore":
            with exclusive():
                say("原 DLL 和配置已还原。" if restore() else "没有待还原的测试备份。")
    except (OSError, RuntimeError, ValueError, KeyError, subprocess.SubprocessError) as error:
        say(f"测试工具：{error}")
        if "--pause" in sys.argv:
            input("按回车关闭窗口。")
        sys.exit(1)
