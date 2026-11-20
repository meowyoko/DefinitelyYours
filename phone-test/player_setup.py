"""Windows player settings for the local phone service."""
import json
from pathlib import Path
import subprocess
import sys

from launcher import (
    PLAYER_CONFIG, ROOT, VOICE_ENV, ffmpeg_manual_instructions, find_ffmpeg,
    player_voice_provider, replace_file, voice_environment,
)


CLIENT_FIELDS = (
    "clientExecutable", "clientNodeExecutable", "cliModel", "cliCreate", "cliResume", "cliSessionField",
    "cliProfile", "claudeCommand", "wakeMode", "port", "mcpTokenEnv",
)


def load_config():
    return json.loads(PLAYER_CONFIG.read_text(encoding="utf-8-sig")) if PLAYER_CONFIG.is_file() else {}


def save_preferences(preset, game_directory, provider):
    # Another harness may have written the config while this window stayed open.
    config = load_config()
    previous = config.get("client")
    if previous != preset:
        profiles = config.setdefault("clientProfiles", {})
        if previous:
            profiles[previous] = {field: config.pop(field) for field in CLIENT_FIELDS if field in config}
        saved_profile = profiles.get(preset, {})
        config.update({field: saved_profile[field] for field in CLIENT_FIELDS if field in saved_profile})
    config.update(client=preset, gameDirectory=game_directory, voiceProvider=provider)
    config.pop("voiceEnabled", None)
    config.setdefault("voiceLanguage", "English")
    config.setdefault("textLanguage", "Simplified Chinese")
    config.setdefault("voiceLoudnessLufs", -16)
    if preset != "custom":
        config["wakeMode"] = "cli"
    else:
        config.setdefault("wakeMode", "cli")
    replace_file(PLAYER_CONFIG, data=config)


def connection_instructions(preset, client_name):
    details = {
        "codex": (
            "保留 client=codex 和 wakeMode=cli。检查 Windows 下已有的 codex.exe 或标准 npm 入口 codex.cmd，"
            "用 --version 确认可执行后把完整文件路径写进 clientExecutable。清理不再适用的 clientNodeExecutable。"
            "启动器生成 exec、exec resume、JSONL、stdin 和 HTTP MCP 参数，无需手写命令。"
        ),
        "dsh": (
            "保留 client=dsh 和 wakeMode=cli。查找当前安装实际使用的 dsh.exe、标准 npm dsh.cmd，或已经构建的 JS 入口"
            "（npm 包常见 @deepseek-ai/dsh/lib/bin.js，源码 checkout 常见 apps/cli/lib/bin.js）。路径写入 clientExecutable。"
            "如果选 JS，用实际能运行该入口的 Node 测试 --version，并把 Node 的完整路径写进 clientNodeExecutable。"
            "Node 版本以这个安装自己的要求为准；检查安装附带的启动脚本，它可能设置了 Node 路径。"
            "非标准 .cmd 包装器请解析成 Node + JS，勿把它当作标准 npm shim。"
            "启动器生成 headless、--json、stdin 和 --session-id 参数及带 Bearer 认证的 MCP patch。"
        ),
        "custom": (
            "保留 client=custom，按本机 Agent 的 CLI 配置 wakeMode=cli，写 cliCreate 和 cliResume 为 JSON 参数数组。"
            "每项一个参数，优先用完整的可执行文件路径或解释器加脚本入口。两条命令必须非交互、读取 UTF-8 stdin 并输出 JSONL；"
            "续跑数组必须含 {session}。若会话 ID 字段不同，填 cliSessionField（例如 session_id 或 session.id），"
            "用稳定的 cliProfile 绑定该会话库。为独立的 MCP 配置选一个可用的固定 port，注册地址 http://127.0.0.1:<port>/mcp，"
            "用启动器传给子进程的 PHONE_MCP_TOKEN 作 Bearer 口令，并预先允许电话工具。"
            "可在本启动器目录加一份必要的客户端配置，再由两条命令加载。使用 CONNECT.md 中的约定。"
        ),
    }
    check_argv = json.dumps([sys.executable, "-u", str(ROOT / "phone-test/launcher.py"), "check"], ensure_ascii=False)
    return f"""请在当前 Windows 电脑上配置我已安装的 {client_name}，让 Definitely Yours 的电话服务在游戏来电时唤起它。

工作目录：{ROOT}
配置文件：{PLAYER_CONFIG}
接入文档：{ROOT / "phone-plugin/CONNECT.md"}
预期 client：{preset}

1. 读取上述本地 JSON。如果文件不存在、client 已变更、或你无法访问同一 Windows 环境，请停止并说明原因。
2. 找出这台电脑上已安装的客户端实际可执行入口。将路径当作数据传递；用命令参数数组处理空格和中文。不要假定它装在游戏目录，也不要要求玩家先找程序路径。
3. {details[preset]}
4. 只改该 JSON 中与当前客户端启动有关的字段，保留 gameDirectory、voiceProvider、语音和模型偏好及其他现有键。无需打开 .env、读取密钥、安装新客户端或修改全局 PATH、游戏文件和现有会话。
5. 用识别到的程序验证版本或 headless 用法，再在游戏和旧电话窗口关闭时执行下面的参数数组，从启动器目录运行。不附带 --pause。
   {check_argv}
   该检查确认电话服务和本机 MCP 工具可用。它不会替代真实回电测试。若失败，查看报错并在上述范围内修正。
6. 汇报你填了哪些字段和验证结果。请我回到启动器点「启动电话服务」后在游戏里拨号。不要替我启动游戏、消费模型调用或输出密钥。
"""


def choose_ffmpeg_download(window, tk, ttk):
    dialog = tk.Toplevel(window)
    dialog.title("准备 FFmpeg")
    dialog.transient(window)
    dialog.resizable(False, False)
    panel = ttk.Frame(dialog, padding=16)
    panel.pack()
    ttk.Label(panel, text="语音需要 FFmpeg。你可以自动下载约 109 MB，也可以手动下载。").pack(anchor="w", pady=(0, 12))
    buttons = ttk.Frame(panel)
    buttons.pack(anchor="e")
    auto = False

    def automatic():
        nonlocal auto
        auto = True
        dialog.destroy()

    def manual():
        buttons.destroy()
        instructions = ffmpeg_manual_instructions()
        details = tk.Text(panel, width=76, height=11, wrap="word", padx=8, pady=8)
        details.insert("1.0", instructions)
        details.configure(state="disabled")
        details.pack(fill="both")
        manual_buttons = ttk.Frame(panel)
        manual_buttons.pack(anchor="e", pady=(12, 0))

        def copy_steps():
            dialog.clipboard_clear()
            dialog.clipboard_append(instructions)
            copy_button.configure(text="已复制")

        copy_button = ttk.Button(manual_buttons, text="复制手动说明", command=copy_steps)
        copy_button.pack(side="left", padx=5)
        ttk.Button(manual_buttons, text="返回启动器", command=dialog.destroy).pack(side="left")

    ttk.Button(buttons, text="自动下载", command=automatic).pack(side="left", padx=5)
    ttk.Button(buttons, text="手动下载", command=manual).pack(side="left", padx=5)
    ttk.Button(buttons, text="取消", command=dialog.destroy).pack(side="left")
    dialog.bind("<Escape>", lambda _: dialog.destroy())
    dialog.grab_set()
    window.wait_window(dialog)
    return auto


def show():
    try:
        import tkinter as tk
        from tkinter import filedialog, messagebox, ttk
    except ImportError as error:
        raise RuntimeError("连接设置需要 Tk。请安装带 Tcl/Tk 的 Windows Python。") from error

    config = load_config()
    try:
        window = tk.Tk()
    except tk.TclError as error:
        raise RuntimeError("无法打开连接设置。请检查 Windows Python 的 Tcl/Tk 安装。") from error
    window.title("Definitely Yours · 电话连接")
    window.resizable(False, False)
    panel = ttk.Frame(window, padding=16)
    panel.grid()
    clients = {"Codex": "codex", "DeepSeek Harness": "dsh", "其他客户端": "custom"}
    folder = tk.StringVar(value=config.get("gameDirectory", ""))
    client = tk.StringVar(value=next((label for label, value in clients.items() if value == config.get("client")), "Codex"))
    providers = {"关闭（纯文字）": "off", "ElevenLabs": "elevenlabs", "OpenAI 兼容 TTS": "openai"}
    voice = tk.StringVar(value=next((label for label, value in providers.items() if value == player_voice_provider(config)), "关闭（纯文字）"))
    saved_voice = voice_environment()
    voice_fields = {
        "elevenlabs": (
            ("音色 ID", "PHONE_VOICE_ID"),
            ("模型（可选）", "ELEVENLABS_MODEL_ID"),
            ("API Key", "ELEVENLABS_API_KEY"),
        ),
        "openai": (
            ("完整接口地址", "PHONE_TTS_URL"),
            ("模型", "PHONE_TTS_MODEL"),
            ("音色", "PHONE_TTS_VOICE"),
            ("API Key（可选）", "PHONE_TTS_API_KEY"),
        ),
    }
    fields = {}
    clear_key = tk.BooleanVar(value=False)

    def browse(target, picker):
        selection = picker()
        if selection:
            target.set(selection)

    ttk.Label(panel, text="游戏目录").grid(row=0, column=0, sticky="w")
    ttk.Entry(panel, textvariable=folder, width=46).grid(row=0, column=1, padx=8, pady=4)
    ttk.Button(panel, text="选择目录", command=lambda: browse(folder, filedialog.askdirectory)).grid(row=0, column=2)
    ttk.Label(panel, text="AI 客户端").grid(row=1, column=0, sticky="w")
    client_picker = ttk.Combobox(panel, values=list(clients), textvariable=client, state="readonly")
    client_picker.grid(row=1, column=1, sticky="w", padx=8, pady=4)
    voice_panel = ttk.LabelFrame(panel, text="可选语音", padding=8)
    voice_panel.grid(row=2, column=0, columnspan=3, sticky="ew", pady=10)
    ttk.Label(voice_panel, text="语音服务").grid(row=0, column=0, sticky="w")
    voice_picker = ttk.Combobox(voice_panel, values=list(providers), textvariable=voice, state="readonly")
    voice_picker.grid(row=0, column=1, sticky="w", padx=8, pady=4)
    provider_panels = {}
    for provider, options in voice_fields.items():
        options_panel = ttk.Frame(voice_panel)
        provider_panels[provider] = options_panel
        for row, (label, name) in enumerate(options):
            secret = name.endswith("_API_KEY")
            fields[name] = tk.StringVar(value="" if secret else saved_voice.get(name, ""))
            ttk.Label(options_panel, text=label).grid(row=row, column=0, sticky="w")
            ttk.Entry(options_panel, textvariable=fields[name], show="*" if secret else "", width=45).grid(
                row=row, column=1, padx=8, pady=4)
        ttk.Label(options_panel, text="API Key 留空时沿用已保存的值。").grid(row=len(options), column=0, columnspan=2, sticky="w")
        if provider == "openai":
            ttk.Checkbutton(options_panel, text="清除已保存的 Key（服务无需 Key 时）", variable=clear_key).grid(
                row=len(options) + 1, column=0, columnspan=2, sticky="w")

    def show_provider(_=None):
        for options_panel in provider_panels.values():
            options_panel.grid_remove()
        provider = providers[voice.get()]
        if provider in provider_panels:
            provider_panels[provider].grid(row=1, column=0, columnspan=2, sticky="w")

    voice_picker.bind("<<ComboboxSelected>>", show_provider)
    show_provider()

    def run(action):
        try:
            if not folder.get() or not (Path(folder.get()) / "Probably Stolen.exe").is_file():
                raise ValueError("请选择包含 Probably Stolen.exe 的游戏目录。")
            preset = clients[client.get()]
            provider = providers[voice.get()]
            updates = {name: fields[name].get().strip() for _, name in voice_fields.get(provider, ())}
            key_name = "PHONE_TTS_API_KEY" if provider == "openai" else "ELEVENLABS_API_KEY"
            if not updates.get(key_name):
                updates.pop(key_name, None)
            if provider == "openai" and clear_key.get():
                updates[key_name] = ""
            settings = {**saved_voice, **updates}
            if provider == "elevenlabs" and not (settings.get("ELEVENLABS_API_KEY") and settings.get("PHONE_VOICE_ID")):
                raise ValueError("请填写 ElevenLabs API Key 和音色 ID。")
            if provider == "openai" and not all(settings.get(name) for name in ("PHONE_TTS_URL", "PHONE_TTS_MODEL", "PHONE_TTS_VOICE")):
                raise ValueError("请填写兼容语音服务的接口地址、模型和音色。")
            if any(char in value for value in updates.values() for char in "\r\n\x00"):
                raise ValueError("语音设置只能填写单行。")
            if updates:
                lines = VOICE_ENV.read_text(encoding="utf-8-sig").splitlines() if VOICE_ENV.is_file() else []
                lines = [line for line in lines if line.partition("=")[0].strip() not in updates]
                lines.extend(f"{name}={value}" for name, value in updates.items())
                replace_file(VOICE_ENV, content="\n".join(lines) + "\n")
                saved_voice.update(updates)
                fields[key_name].set("")
                clear_key.set(False)
            save_preferences(preset, folder.get(), provider)
            if action == "copy":
                window.clipboard_clear()
                window.clipboard_append(connection_instructions(preset, client.get()))
                copied.set("接入说明已复制，请粘贴给所选的 AI 客户端。")
                return
            command = [sys.executable, "-u", str(ROOT / "phone-test/launcher.py"), action]
            if action == "play" and provider != "off" and not find_ffmpeg():
                if not choose_ffmpeg_download(window, tk, ttk):
                    return
                command.append("--download-ffmpeg")
            subprocess.Popen(command + ["--pause"], cwd=ROOT, creationflags=subprocess.CREATE_NEW_CONSOLE)
            if action == "play":
                window.destroy()
        except (ValueError, OSError, tk.TclError) as error:
            messagebox.showerror("电话设置", str(error), parent=window)

    copied = tk.StringVar(value="")
    ttk.Label(panel, textvariable=copied).grid(row=3, column=0, columnspan=3, sticky="w")
    buttons = ttk.Frame(panel)
    buttons.grid(row=4, column=0, columnspan=3, sticky="e")
    ttk.Button(buttons, text="复制接入说明", command=lambda: run("copy")).pack(side="left", padx=5)
    ttk.Button(buttons, text="测试连接", command=lambda: run("check")).pack(side="left", padx=5)
    ttk.Button(buttons, text="启动电话服务", command=lambda: run("play")).pack(side="left")
    window.mainloop()
