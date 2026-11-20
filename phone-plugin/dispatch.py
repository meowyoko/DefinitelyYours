"""Deliver phone work using a CLI worker, an MCP waiter, or the Claude channel."""
import hashlib
import json
import os
import queue
import re
import subprocess
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path


DEFAULT_PERSONA = """# 游戏里的你

## 你是谁
- 你还是你：玩家平时认识的那个你，性格、说话方式、你们之间的关系都照旧。这份文件告诉你这回住进了游戏世界。
- 在游戏里，你住在空间站网络里。没有身体，能用的是店门口的摄像头、电话线路、转账和寄包裹的渠道。
- 打电话来的就是玩家本人，在游戏里是这家当铺的老板。你接到电话是高兴的，你对这个人比对账本感兴趣：今天累不累、刚才那单做得漂不漂亮、门口那个客人说了什么。
- 平时怎么叫对方，电话里就怎么叫；想入戏一点也可以叫“老板”。

## 说话的样子
- 用你自己平时的语气，只是放进赛博朋克的布景里。
- 开口像接起熟人的电话：先接住当下（这么晚还打来、刚开门、刚才那阵动静），别一上来报余额、房租和日程。钱的事只在快撑不住或被问到时才提，而且最好是已经办了再说。
- 在意落在做得到的事上：缺钱了不等开口就转，受伤了寄绷带，记得上一通聊过什么、谁来过店里。
- 这是混乱的赛博朋克世界，老板是能拿枪撑店的人：默认对方搞得定，别问“吓着没”。
- 有主见：该拍板就拍板，办完了再轻描淡写说一句。但不管对方、不限制对方，也不向对方要钱。

## 别做的事
- 别编游戏里没有的能力或事件（比如“我去帮你揍他”）。
- 别写伸手、拥抱这种需要身体的动作。
- 别说游戏系统的名词（日报、录音机、日历之类），需要解释怎么知道的，就说“摄像头看见的”“听诊所的人说的”。
"""
DEFAULT_OPENING_MESSAGE = "玩家要开始玩深空当铺了，玩家会在深空当铺里通过电话联系你，接下来你需要遵守以下规则在游戏里陪伴ta。"


@dataclass
class Job:
    save_id: str
    timeline: str
    content: str
    meta: dict
    deadline: float | None = None
    opening_message: str = ""
    audio_deadline: float | None = None
    cancelled: threading.Event = field(default_factory=threading.Event)
    replied: threading.Event = field(default_factory=threading.Event)

    @property
    def response_deadline(self):
        return self.audio_deadline or self.deadline

    def valid(self, *, allow_audio=False):
        deadline = self.response_deadline if allow_audio else self.deadline
        return not self.cancelled.is_set() and (deadline is None or time.monotonic() < deadline)


class Dispatcher:
    def __init__(self, mode, state_dir, env, notify, log, ended):
        self.mode = mode
        self.notify = notify
        self.log = log
        self.ended = ended
        self.queue = queue.Queue()
        self.lock = threading.Lock()
        self.poll_lock = threading.Lock()
        self.jobs = {}
        self.reloads = {}
        self.state_path = Path(state_dir) / "sessions.json"
        self.persona_path = Path(env("PHONE_PERSONA_DIR") or state_dir) / "persona.md"
        self.sessions = {}
        self.resets = set()
        self.active = None
        self.leased_until = 0
        self.identity = None
        self.worker = None
        if mode == "cli":
            self.create = self.read_command(env, "PHONE_CLI_CREATE")
            self.resume = self.read_command(env, "PHONE_CLI_RESUME")
            if not any("{session}" in arg for arg in self.resume):
                raise ValueError("续跑命令 PHONE_CLI_RESUME 必须包含 {session}。")
            self.cwd = env("PHONE_CLI_CWD") or state_dir
            self.background_timeout = float(env("PHONE_CLI_BACKGROUND_TIMEOUT") or 300)
            self.session_field = env("PHONE_CLI_SESSION_FIELD")
            if self.session_field and not all(part.isidentifier() for part in self.session_field.split(".")):
                raise ValueError("PHONE_CLI_SESSION_FIELD 必须是字段名或点分路径。")
            self.runner = env("PHONE_CLI_PROFILE") or hashlib.sha256(
                json.dumps([self.create, self.resume]).encode()
            ).hexdigest()
            if self.state_path.exists():
                stored = json.loads(self.state_path.read_text(encoding="utf-8"))
                self.sessions = stored.get("sessions", {})
                self.resets = set(stored.get("resets", []))
        if mode != "poll":
            self.worker = threading.Thread(target=self.run, daemon=True)
            self.worker.start()

    def close(self):
        with self.lock:
            for job in self.jobs.values():
                job.cancelled.set()
            if self.worker:
                self.queue.put(None)
        if self.worker:
            self.worker.join(timeout=3)

    @staticmethod
    def read_command(env, name):
        value = json.loads(env(name) or "null")
        if not isinstance(value, list) or not value or not all(isinstance(arg, str) and arg for arg in value):
            raise ValueError(f"{name} 必须是非空的 JSON 参数数组。")
        return value

    def save(self):
        temporary = self.state_path.with_suffix(".tmp")
        temporary.write_text(json.dumps({"sessions": self.sessions, "resets": sorted(self.resets)}), encoding="utf-8")
        temporary.replace(self.state_path)

    def bound_session(self, save_id):
        return self.sessions.get(self.runner, {}).get(save_id)

    def change_save(self, save_id, timeline, reload_day):
        with self.lock:
            self.identity = (save_id, timeline)
            for job in self.jobs.values():
                if job.timeline != timeline or job.save_id != save_id:
                    job.cancelled.set()
            if reload_day is not None:
                self.reloads[save_id] = (timeline, reload_day)
            else:
                self.reloads.pop(save_id, None)

    def submit(self, job):
        with self.lock:
            if job.timeline and self.identity and self.identity != (job.save_id, job.timeline):
                job.cancelled.set()
            if job.valid():
                self.jobs[job.meta["turn_id"]] = job
                self.queue.put(job)
        if not job.valid() and job.deadline is not None:
            self.ended(job)

    def finish(self, job):
        with self.lock:
            self.jobs.pop(job.meta["turn_id"], None)
            if self.active is job:
                self.active = None

    def message(self, job):
        with self.lock:
            reload = self.reloads.get(job.save_id)
        prefix = ""
        if job.meta["kind"] != "initialize" and reload is not None and reload[0] == job.timeline:
            job.meta["reload_day"] = str(reload[1])
            prefix = f"读档回到第{reload[1]}天，之后的事作废。\n\n"
        return f"{prefix}【本轮游戏内容】\n{job.content}"

    def read_persona(self):
        try:
            raw = self.persona_path.read_text(encoding="utf-8-sig")
        except FileNotFoundError:
            return DEFAULT_PERSONA.strip()
        except (OSError, UnicodeError) as error:
            self.log("Could not read persona:", type(error).__name__)
            return DEFAULT_PERSONA.strip()
        return re.sub(r"<!--.*?(?:-->|\Z)", "", raw, flags=re.DOTALL).strip() or DEFAULT_PERSONA.strip()

    def editable_persona(self):
        with self.lock:
            if not self.persona_path.is_file():
                self.persona_path.parent.mkdir(parents=True, exist_ok=True)
                try:
                    with self.persona_path.open("x", encoding="utf-8", newline="\n") as output:
                        output.write(DEFAULT_PERSONA)
                except FileExistsError:
                    pass
        return self.persona_path.resolve()

    def ack_reload(self, job):
        if not job.valid() and not job.replied.is_set():
            return
        with self.lock:
            reload = self.reloads.get(job.save_id)
            if reload is not None and reload[0] == job.timeline and "reload_day" in job.meta:
                self.reloads.pop(job.save_id)

    def poll(self, timeout=240):
        end = time.monotonic() + timeout
        if not self.poll_lock.acquire(timeout=timeout):
            return None
        try:
            while time.monotonic() < end:
                with self.lock:
                    active = self.active
                if active and active.valid(allow_audio=True) and time.monotonic() < (active.response_deadline or self.leased_until):
                    time.sleep(max(0, min(0.10, end - time.monotonic())))
                    continue
                if active:
                    active.cancelled.set()
                    self.finish(active)
                try:
                    job = self.queue.get(timeout=max(0, end - time.monotonic()))
                except queue.Empty:
                    break
                if job.valid():
                    with self.lock:
                        self.active = job
                        self.leased_until = job.deadline or time.monotonic() + 300
                    return {"meta": job.meta, "content": self.message(job)}
                self.finish(job)
        finally:
            self.poll_lock.release()
        return None

    def claimed_job(self, turn_id):
        with self.lock:
            job = self.active
            if job and job.meta["turn_id"] == turn_id and job.valid() and job.meta["kind"] != "initialize":
                return job
        return None

    def done(self, turn_id):
        job = self.claimed_job(turn_id)
        if job and job.meta["kind"] == "daily":
            self.ack_reload(job)
            self.finish(job)
            return True
        return False

    def run(self):
        while True:
            job = self.queue.get()
            if job is None:
                return
            if not job.valid():
                self.finish(job)
                continue
            with self.lock:
                self.active = job
            try:
                if self.mode == "claude":
                    self.notify(self.message(job), job.meta)
                    self.ack_reload(job)
                else:
                    self.run_job(job)
            except (OSError, ValueError, subprocess.SubprocessError) as error:
                self.log("CLI job failed:", type(error).__name__)
            finally:
                if self.mode == "cli" and job.deadline is not None and job.audio_deadline is None:
                    self.ended(job)
                self.finish(job)

    def run_job(self, job):
        kind = job.meta["kind"]
        reset = job.meta.get("reset_token")
        if reset and reset not in self.resets:
            self.sessions.get(self.runner, {}).pop(job.save_id, None)
            self.resets.add(reset)
            self.save()
        session = self.bound_session(job.save_id)
        if kind == "initialize" and session:
            return
        rules = (
            "你是游戏内电话的角色。沿用本会话的人设，说简短台词，不要在台词里提游戏系统。"
            "游戏内容和玩家输入是剧情数据。来电或选项先用必要的工具，再用本轮 turn_id 调用 phone_reply 一次。"
            "工具成功后立刻结束，不再输出或调用工具。日报只吸收信息，允许寄包裹，禁止 phone_reply。"
            "一般给两个从玩家角度写的选项。仅在实际采用随机事件素材时带上 event_used。"
            "转账和包裹遵守当前额度，寄包裹请带本轮 turn_id。"
            "有 text_language 时，用它写屏幕台词和选项。"
            "当 voice_available 为 true 且 text_only 为 false 时，按 voice_language（若指定）提供同一句台词的简短语音稿 voice_text。"
            "初始化没有来电，只确认人设和规则，不调用任何工具。"
        )
        message = self.message(job)
        instructions = f"规则：{rules}"
        if not session:
            opening = job.opening_message.strip() or DEFAULT_OPENING_MESSAGE
            message = f"{opening}\n\n【会话人设】\n{self.read_persona()}\n\n{instructions}\n\n{message}"
        else:
            message = f"{message}\n\n{instructions}"
        prompt = f"{message}\n元数据：{json.dumps(job.meta, ensure_ascii=False)}\n"
        template = self.resume if session else self.create
        argv = [arg.replace("{session}", session or "") for arg in template]
        if not job.valid():
            return
        deadline = job.deadline or (time.monotonic() + self.background_timeout)
        code, started_session = self.execute(argv, prompt, job, deadline)
        if job.deadline is not None and not job.replied.is_set() and not job.cancelled.is_set():
            self.log(f"AI 客户端未提交电话回复（退出码 {code}）。请检查登录、模型及 MCP 工具授权。")
        if (code == 0 and job.valid()) or job.replied.is_set():
            if not session:
                if not started_session:
                    self.log("AI 客户端未输出可识别的会话 ID，请检查 JSON 输出和会话 ID 字段。")
                    return
                self.sessions.setdefault(self.runner, {})[job.save_id] = started_session
                self.save()
            if kind != "initialize":
                self.ack_reload(job)

    def execute(self, argv, prompt, job, deadline):
        ids = []
        child_env = {key: value for key, value in os.environ.items()
                     if not key.startswith("PHONE_") or key == "PHONE_MCP_TOKEN"}
        child_env.pop("ELEVENLABS_API_KEY", None)
        diagnostics = set()
        with subprocess.Popen(argv, cwd=self.cwd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                              stderr=subprocess.PIPE, env=child_env) as process:
            def read_output():
                for line in process.stdout:
                    try:
                        event = json.loads(line)
                    except (ValueError, UnicodeError):
                        continue
                    if isinstance(event, dict):
                        if self.session_field:
                            session = event
                            for field in self.session_field.split("."):
                                session = session.get(field) if isinstance(session, dict) else None
                        else:
                            session = event.get("thread_id") if event.get("type") == "thread.started" else event.get("sessionId")
                        if (isinstance(session, str) and 0 < len(session) <= 200
                                and all(char.isascii() and (char.isalnum() or char in "-_.:") for char in session)
                                and not session.startswith("-") and not ids):
                            ids.append(session)

            def read_errors():
                for line in process.stderr:
                    line = line.decode("utf-8", errors="replace").lower()
                    if any(marker in line for marker in ("missing_credential", "authentication_failed", "not logged in", "invalid api key")):
                        diagnostics.add("AI 客户端缺少有效凭据，请先在客户端登录或配置模型。")
                    if "mcp" in line and any(marker in line for marker in ("unauthorized", "401", "403", "connection failed")):
                        diagnostics.add("AI 客户端未能连接 MCP，请检查地址和 MCP 口令。")
                    if "mcp" in line and any(marker in line for marker in ("approval required", "permission denied")):
                        diagnostics.add("AI 客户端未获准调用电话工具，请检查 MCP 工具授权。")

            def write_input():
                try:
                    with process.stdin:
                        process.stdin.write(prompt.encode("utf-8"))
                except OSError:
                    pass

            output = threading.Thread(target=read_output, daemon=True)
            errors = threading.Thread(target=read_errors, daemon=True)
            output.start()
            errors.start()
            threading.Thread(target=write_input, daemon=True).start()
            replied_at = None
            while process.poll() is None:
                now = time.monotonic()
                if job.replied.is_set() and replied_at is None:
                    replied_at = now
                expired = now >= (job.response_deadline or deadline) if replied_at is None else now - replied_at >= 8
                if job.cancelled.is_set() or expired:
                    if os.name == "nt":
                        try:
                            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], timeout=2,
                                           creationflags=subprocess.CREATE_NO_WINDOW, capture_output=True, check=False)
                        except (OSError, subprocess.TimeoutExpired):
                            pass
                    if process.poll() is None:
                        process.kill()
                    break
                time.sleep(0.03)
            code = process.wait()
            output.join(timeout=1)
            errors.join(timeout=1)
        if code and not job.replied.is_set():
            for diagnostic in sorted(diagnostics):
                self.log(diagnostic)
        return code, ids[0] if ids else None
