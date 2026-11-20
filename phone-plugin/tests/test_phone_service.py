import concurrent.futures
import json
import os
from pathlib import Path
import runpy
import secrets
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


HERE = Path(__file__).resolve()
SERVER = HERE.parents[1] / "server.py"
HTTP = urllib.request.build_opener(urllib.request.ProxyHandler({}))


class Client:
    def __init__(self, port, token, mcp_token):
        self.url = f"http://127.0.0.1:{port}"
        self.token = token
        self.mcp_token = mcp_token

    def post(self, path, body):
        headers = {"Content-Type": "application/json", "X-Phone-Token": self.token}
        if path == "/mcp":
            headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream",
                       "Authorization": "Bearer " + self.mcp_token, "MCP-Protocol-Version": "2025-06-18"}
        request = urllib.request.Request(self.url + path, json.dumps(body).encode(), headers)
        with HTTP.open(request, timeout=50) as response:
            return json.loads(response.read() or "null")

    def rpc(self, method, params=None):
        return self.post("/mcp", {"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}})["result"]

    def tool(self, name, **args):
        return self.rpc("tools/call", {"name": name, "arguments": args})


class ServiceTests(unittest.TestCase):
    def start_service(self, mode, cli=None, timeout=4000, extra_env=None, synthesize_after=None, stub_persona_editor=False):
        self.state = Path(self.enterContext(tempfile.TemporaryDirectory()))
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        token, mcp_token = secrets.token_urlsafe(16), secrets.token_urlsafe(16)
        env = {key: value for key, value in os.environ.items() if not key.startswith(("PHONE_", "ELEVENLABS_"))}
        env.update(PHONE_STATE_DIR=str(self.state), PHONE_TOKEN=token, PHONE_MCP_TOKEN=mcp_token,
                   PHONE_PORT=str(port), PHONE_WAKE_MODE=mode, PHONE_TIMEOUT_MS=str(timeout),
                   PHONE_CLI_CWD=str(self.state))
        if cli in ("fake", "fake-dsh", "fake-custom"):
            command = [sys.executable, str(HERE), "--fake", str(port), cli]
            cli = command, command + ["{session}"]
        elif callable(cli):
            cli = cli(port)
        if cli:
            env.update(PHONE_CLI_CREATE=json.dumps(cli[0]), PHONE_CLI_RESUME=json.dumps(cli[1]))
        if mode == "claude":
            env["PHONE_AUTO_OPEN"] = "1"
        env.update(extra_env or {})
        stdio = subprocess.PIPE if mode == "claude" else subprocess.DEVNULL
        command = [sys.executable, str(SERVER)]
        if synthesize_after is not None:
            command = [sys.executable, str(HERE), "--fake-tts-server", str(synthesize_after)]
        if stub_persona_editor:
            command = [sys.executable, str(HERE), "--fake-persona-editor"]
        process = subprocess.Popen(command, env=env, stdin=stdio, stdout=stdio)
        self.addCleanup(process.wait)
        self.addCleanup(process.terminate)
        self.addCleanup(time.sleep, 0.25)
        if mode == "claude":
            self.addCleanup(process.stdin.close)
            self.addCleanup(process.stdout.close)
        self.process = process
        self.client = Client(port, token, mcp_token)
        for _ in range(100):
            try:
                with HTTP.open(self.client.url + "/phone/health", timeout=1):
                    break
            except urllib.error.URLError:
                if process.poll() is not None:
                    self.fail(f"Service exited with {process.returncode}")
                time.sleep(0.03)
        else:
            self.fail("Service did not start")

    def test_cli_serializes_replies_and_binds_by_save(self):
        self.start_service("cli", "fake")
        self.exercise_cli_binding()

    def test_alternate_cli_transcript_uses_the_same_tools(self):
        self.start_service("cli", "fake-dsh")
        self.exercise_cli_binding()

    def test_configured_nested_session_id_resumes_the_same_chat(self):
        self.start_service("cli", "fake-custom", extra_env={
            "PHONE_CLI_SESSION_FIELD": "session.id", "PHONE_CLI_PROFILE": "fixture-custom",
        })
        self.exercise_cli_binding()
        bindings = json.loads((self.state / "sessions.json").read_text(encoding="utf-8"))
        self.assertIn("slot-A", bindings["sessions"]["fixture-custom"])

    def exercise_cli_binding(self):
        client = self.client
        first = {"saveId": "slot-A", "timeline": "run-1", "callToken": "call-1", "day": 1, "textOnly": True}
        opening = "小狗，我要去开店啦。\n等会儿的电话是我。"
        client.post("/phone/session", dict(first, reason="new", openingMessage=opening))
        reply = client.post("/phone/call", first)
        self.assertTrue(reply["connected"])
        self.assertEqual((self.state / "fixture-opening.txt").read_text(encoding="utf-8"), opening)
        choice = client.post("/phone/choice", dict(first, index=0))
        self.assertTrue(choice["connected"])
        first_session = reply["text"]
        self.assertEqual(choice["text"], first_session)
        client.post("/phone/daily", first)
        self.assertEqual(client.post("/phone/call", first)["text"], first_session)
        time.sleep(0.25)
        second = dict(first, saveId="slot-B", timeline="run-2")
        client.post("/phone/session", dict(second, reason="new"))
        self.assertNotEqual(client.post("/phone/call", second)["text"], first_session)
        time.sleep(0.25)
        loaded = dict(first, timeline="run-3", day=2)
        client.post("/phone/session", dict(loaded, reason="load"))
        self.assertEqual(client.post("/phone/call", loaded)["text"], first_session + " rollback")
        reset = dict(loaded, sessionReset="reset-a")
        fresh_session = client.post("/phone/call", reset)["text"]
        self.assertNotEqual(fresh_session, first_session)
        self.assertEqual(client.post("/phone/call", reset)["text"], fresh_session)

    def test_call_budget_includes_background_queue(self):
        self.start_service("cli", "fake", timeout=300)
        body = {"saveId": "slot-A", "day": 1, "textOnly": True}
        self.client.post("/phone/daily", body)
        self.assertFalse(self.client.post("/phone/call", body)["connected"])
        time.sleep(0.6)
        self.assertTrue(self.client.post("/phone/call", body)["connected"])

    def test_poll_rejects_late_replies_and_delivers_daily(self):
        self.start_service("poll")
        client = self.client
        self.assertIn("## 你是谁", client.rpc("initialize")["instructions"])
        self.assertIn("phone_wait", {tool["name"] for tool in client.rpc("tools/list")["tools"]})
        body = {"saveId": "slot-A", "timeline": "initial", "callToken": "one", "day": 1, "textOnly": True}
        client.post("/phone/session", dict(body, reason="new"))
        with concurrent.futures.ThreadPoolExecutor() as threads:
            first = threads.submit(client.post, "/phone/call", body)
            old = json.loads(client.tool("phone_wait")["content"][0]["text"])["meta"]["turn_id"]
            client.post("/phone/hangup", body)
            self.assertFalse(first.result()["connected"])
            loaded = dict(body, timeline="reloaded", callToken="two")
            client.post("/phone/session", dict(loaded, reason="load"))
            second = threads.submit(client.post, "/phone/call", loaded)
            event = json.loads(client.tool("phone_wait")["content"][0]["text"])
            self.assertTrue(event["content"].startswith("读档回到第1天，之后的事作废。"))
            self.assertNotIn("会话人设", event["content"])
            self.assertTrue(client.tool("phone_reply", turn_id=old, text="late")["isError"])
            self.assertFalse(client.tool("phone_reply", turn_id=event["meta"]["turn_id"], text="hello")["isError"])
            self.assertEqual(second.result()["text"], "hello")
        client.post("/phone/daily", loaded)
        report = json.loads(client.tool("phone_wait")["content"][0]["text"])["meta"]
        self.assertEqual(report["kind"], "daily")
        self.assertFalse(client.tool("phone_done", turn_id=report["turn_id"])["isError"])

    def test_resync_with_final_save_id_allows_new_game_call(self):
        self.start_service("poll")
        client = self.client
        draft = {"saveId": "-1::0", "timeline": "new-game", "day": 1, "reason": "new"}
        final = dict(draft, saveId="0:run:13", textOnly=True)
        client.post("/phone/session", draft)
        self.assertFalse(client.post("/phone/call", final)["connected"])
        client.post("/phone/session", final)
        with concurrent.futures.ThreadPoolExecutor() as threads:
            reply = threads.submit(client.post, "/phone/call", final)
            event = json.loads(client.tool("phone_wait")["content"][0]["text"])
            self.assertEqual(event["meta"]["save_id"], final["saveId"])
            self.assertFalse(client.tool("phone_reply", turn_id=event["meta"]["turn_id"], text="接通了")["isError"])
            self.assertTrue(reply.result()["connected"])

    def test_call_and_choice_use_separate_voice_and_screen_languages(self):
        self.start_service("poll", extra_env={"PHONE_VOICE_LANGUAGE": "English", "PHONE_TEXT_LANGUAGE": "Simplified Chinese"})
        client = self.client
        reply_tool = next(tool for tool in client.rpc("tools/list")["tools"] if tool["name"] == "phone_reply")
        props = reply_tool["inputSchema"]["properties"]
        self.assertIn("voice_language", props["voice_text"]["description"])
        self.assertIn("text_language", props["text"]["description"])
        self.assertIn("text_language", props["choices"]["description"])
        body = {"saveId": "slot-A", "day": 1, "textOnly": True}
        turns = (
            ("/phone/call", {}, "English", "Simplified Chinese"),
            ("/phone/choice", {"voiceLanguage": " 日本語 ", "textLanguage": "English"}, "日本語", "English"),
            ("/phone/choice", {"voiceLanguage": "", "textLanguage": 12}, "English", "Simplified Chinese"),
        )
        with concurrent.futures.ThreadPoolExecutor() as threads:
            for path, language_settings, expected_voice, expected_text in turns:
                response = threads.submit(client.post, path, dict(body, **language_settings))
                event = json.loads(client.tool("phone_wait")["content"][0]["text"])['meta']
                self.assertEqual(event["voice_language"], expected_voice)
                self.assertEqual(event["text_language"], expected_text)
                self.assertEqual(event["text_only"], "true")
                result = client.tool("phone_reply", turn_id=event["turn_id"], text="凛，我在。")
                self.assertFalse(result["isError"])
                self.assertEqual(response.result()["text"], "凛，我在。")

    def test_openai_compatible_voice_and_text_fallback(self):
        requests = []
        mp3 = b"ID3" + b"fake-test-audio"

        class Speech(BaseHTTPRequestHandler):
            def do_POST(self):
                payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                requests.append((self.path, self.headers.get("Authorization"), payload))
                body = mp3 if len(requests) == 1 else b'{"error":"no audio"}'
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *_):
                pass

        speech = self.enterContext(ThreadingHTTPServer(("127.0.0.1", 0), Speech))
        threading.Thread(target=speech.serve_forever, daemon=True).start()
        self.addCleanup(speech.shutdown)
        self.start_service("cli", "fake", extra_env={
            "PHONE_TTS_PROVIDER": "openai", "PHONE_TTS_URL": f"http://127.0.0.1:{speech.server_port}/v1/audio/speech",
            "PHONE_TTS_MODEL": "fixture-model", "PHONE_TTS_VOICE": "fixture-voice", "PHONE_TTS_API_KEY": "fixture-voice-key",
            "PHONE_AUDIO_FORMAT": "mp3",
        })
        call = {"callToken": "fixture-call", "textOnly": False}
        reply = self.client.post("/phone/call", call)
        self.assertEqual(requests[0], ("/v1/audio/speech", "Bearer fixture-voice-key", {
            "input": "Hello", "model": "fixture-model", "voice": "fixture-voice", "response_format": "mp3",
        }))
        self.assertEqual(reply["audio"]["format"], "mp3")
        audio = urllib.request.Request(f"{self.client.url}/phone/audio/{reply['audio']['id']}",
                                       headers={"X-Phone-Token": self.client.token})
        with HTTP.open(audio, timeout=3) as response:
            self.assertEqual(response.read(), mp3)
        fallback = self.client.post("/phone/choice", dict(call, index=0))
        self.assertTrue(fallback["connected"])
        self.assertEqual(fallback["text"], reply["text"])
        self.assertIsNone(fallback.get("audio"))

    def test_cli_allows_voice_to_finish_after_the_reply_deadline(self):
        self.start_service("cli", "fake", timeout=800, synthesize_after=1)
        reply = self.client.post("/phone/call", {"saveId": "slot-A", "day": 1, "textOnly": False})
        self.assertTrue(reply["connected"])
        self.assertEqual(reply["audio"]["format"], "wav")

    def test_voice_grace_keeps_the_text_fallback_and_requires_a_timely_reply(self):
        self.start_service("poll", timeout=400, synthesize_after=1.5)
        client = self.client
        body = {"saveId": "slot-A", "day": 1, "textOnly": False}
        with concurrent.futures.ThreadPoolExecutor() as threads:
            unanswered = threads.submit(client.post, "/phone/call", body)
            turn_id = json.loads(client.tool("phone_wait")["content"][0]["text"])["meta"]["turn_id"]
            self.assertFalse(unanswered.result(timeout=1)["connected"])
            self.assertTrue(client.tool("phone_reply", turn_id=turn_id, text="晚了", voice_text="Late")["isError"])

            answered = threads.submit(client.post, "/phone/call", body)
            turn_id = json.loads(client.tool("phone_wait")["content"][0]["text"])["meta"]["turn_id"]
            self.assertFalse(client.tool("phone_reply", turn_id=turn_id, text="接通了", voice_text="Hello")["isError"])
            reply = answered.result()
            self.assertTrue(reply["connected"])
            self.assertEqual(reply["text"], "接通了")
            self.assertIsNone(reply["audio"])

    @unittest.skipUnless(os.name == "nt", "Local persona editor is a Windows launcher feature")
    def test_local_persona_editor_copies_template_and_preserves_player_file(self):
        self.start_service("poll", stub_persona_editor=True, extra_env={"PHONE_LOCAL_PERSONA_EDITOR": "1"})
        persona = self.state / "persona.md"
        opened = self.state / "editor-target.txt"
        with self.assertRaises(urllib.error.HTTPError) as unauthenticated:
            Client(self.client.url.rsplit(":", 1)[1], "wrong-token", "").post("/phone/persona/open", {})
        self.assertEqual(unauthenticated.exception.code, 401)
        unauthenticated.exception.close()
        self.assertFalse(persona.exists())
        self.client.post("/phone/persona/open", {"path": "ignored.md"})
        defaults = runpy.run_path(str(SERVER.with_name("dispatch.py")))["DEFAULT_PERSONA"]
        self.assertEqual(persona.read_text(encoding="utf-8"), defaults)
        self.assertEqual(Path(opened.read_text(encoding="utf-8")), persona)
        persona.write_text("玩家的人设", encoding="utf-8")
        self.client.post("/phone/persona/open", {})
        self.assertEqual(persona.read_text(encoding="utf-8"), "玩家的人设")

    def test_persona_is_sent_only_when_creating_a_session(self):
        temporary = Path(self.enterContext(tempfile.TemporaryDirectory()))
        config = {"PHONE_PERSONA_DIR": str(temporary / "config"),
                  "PHONE_CLI_CREATE": '["create"]', "PHONE_CLI_RESUME": '["resume", "{session}"]'}
        (temporary / "config").mkdir()
        namespace = runpy.run_path(str(SERVER.with_name("dispatch.py")))
        dispatcher = namespace["Dispatcher"]("cli", temporary, config.get, None, lambda *args: None, None)
        self.addCleanup(dispatcher.close)
        prompts = []

        def execute(argv, prompt, job, deadline):
            prompts.append((argv, prompt))
            return 0, f"session-{len(prompts)}"

        dispatcher.execute = execute

        def run(kind, opening="", **meta):
            dispatcher.run_job(namespace["Job"]("slot-A", "one", "游戏台词", {"kind": kind, **meta},
                                                opening_message=opening))
            return prompts[-1]

        argv, prompt = run("initialize", opening=" \n ")
        self.assertEqual(argv, ["create"])
        self.assertTrue(prompt.startswith(namespace["DEFAULT_OPENING_MESSAGE"]))
        self.assertIn("## 你是谁", prompt)

        persona = temporary / "config" / "persona.md"
        persona.write_text("# 小吴\n\n第一段\n\n" + "甲" * 2000 + "\n<!-- 作者备注 -->", encoding="utf-8")
        for kind in ("call", "choice", "daily"):
            argv, prompt = run(kind, opening="不应重复发送的留言")
            self.assertEqual(argv, ["resume", "session-1"])
            self.assertNotIn("会话人设", prompt)
            self.assertNotIn("不应重复发送的留言", prompt)

        opening = "小狗，我是凛。\n等会儿电话里见。"
        argv, prompt = run("call", opening=opening, reset_token="new-persona")
        self.assertEqual(argv, ["create"])
        self.assertTrue(prompt.startswith(opening + "\n\n【会话人设】"))
        self.assertLess(prompt.index("规则："), prompt.index("【本轮游戏内容】"))
        self.assertIn("# 小吴\n\n第一段\n\n" + "甲" * 2000, prompt)
        self.assertNotIn("作者备注", prompt)
        self.assertNotIn("## 你是谁", prompt)
        persona.write_text("<!-- 暂时留空 -->", encoding="utf-8")
        self.assertIn("## 你是谁", run("call", reset_token="example-persona")[1])

    def test_claude_stdio_channel_still_accepts_legacy_game_requests(self):
        self.start_service("claude")
        client = self.client
        with concurrent.futures.ThreadPoolExecutor() as threads:
            call = threads.submit(client.post, "/phone/call", {"state": {"day": 1}, "textOnly": True})
            notification = json.loads(self.process.stdout.readline())
            self.assertEqual(notification["method"], "notifications/claude/channel")
            meta = notification["params"]["meta"]
            reply = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {
                "name": "phone_reply", "arguments": {"turn_id": meta["turn_id"], "text": "hello"}}}
            self.process.stdin.write(json.dumps(reply).encode() + b"\n")
            self.process.stdin.flush()
            result = json.loads(self.process.stdout.readline())["result"]
            self.assertFalse(result["isError"])
            self.assertEqual(call.result()["text"], "hello")


def fake_agent(port, dialect, session=None):
    assert "PHONE_TTS_API_KEY" not in os.environ
    prompt = sys.stdin.buffer.read().decode()
    meta = json.loads(prompt.splitlines()[-1].removeprefix("元数据："))
    session = session or secrets.token_hex(8)
    event = {
        "fake-dsh": {"sessionId": session},
        "fake-custom": {"session": {"id": session}},
    }.get(dialect, {"type": "thread.started", "thread_id": session})
    print(json.dumps(event), flush=True)
    with Path("worker.lock").open("x"):
        pass
    try:
        if meta["kind"] == "initialize":
            Path("fixture-opening.txt").write_text(prompt.split("\n\n【会话人设】", 1)[0], encoding="utf-8")
        elif meta["kind"] in {"call", "choice"}:
            client = Client(int(port), "", os.environ["PHONE_MCP_TOKEN"])
            text = session + (" rollback" if "reload_day" in meta else "")
            args = {"voice_text": "Hello"} if meta.get("voice_available") == "true" and meta.get("text_only") == "false" else {}
            if client.tool("phone_reply", turn_id=meta["turn_id"], text=text, choices=["Next"], **args)["isError"]:
                return 1
            time.sleep(0.15)
        elif meta["kind"] == "daily":
            time.sleep(0.5)
    finally:
        Path("worker.lock").unlink()
    return 0


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--fake":
        sys.exit(fake_agent(*sys.argv[2:]))
    if len(sys.argv) > 1 and sys.argv[1] == "--fake-persona-editor":
        sys.path.insert(0, str(SERVER.parent))
        import server

        def open_fixture_editor(path):
            (path.parent / "editor-target.txt").write_text(str(path), encoding="utf-8")

        server.open_persona_editor = open_fixture_editor
        server.main()
        sys.exit(0)
    if len(sys.argv) > 1 and sys.argv[1] == "--fake-tts-server":
        sys.path.insert(0, str(SERVER.parent))
        import server

        def synthesize(_):
            time.sleep(float(sys.argv[2]))
            return {"id": "0000000000000000.wav", "format": "wav"}

        server.VOICE_ON = True
        server.VOICE_GRACE = 0.85
        server.synthesize = synthesize
        server.main()
        sys.exit(0)
    unittest.main()
