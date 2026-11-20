import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import unittest


ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(sys.platform == "win32", "Windows launcher tests")
class LauncherTests(unittest.TestCase):
    def setUp(self):
        game = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.repo = game / "Modding" / "DefinitelyYours"
        self.mod = game / "Mods" / "AiPhone"
        self.mod.mkdir(parents=True)
        self.dll = self.mod / "AiPhone.dll"
        self.config = self.mod / "config.json"
        self.original = b'{"baseUrl":"https://example.invalid","token":"fixture"}'
        self.dll.write_bytes(b"original")
        self.config.write_bytes(self.original)
        (self.mod / "manifest.xml").write_text("<Manifest />")
        release = self.repo / "mod/bin/Release/netstandard2.1"
        release.mkdir(parents=True)
        (release / "AiPhone.dll").write_bytes(b"test build")
        for folder, names in (("phone-test", ("launcher.py",)), ("phone-plugin", ("server.py", "dispatch.py"))):
            (self.repo / folder).mkdir()
            for name in names:
                shutil.copyfile(ROOT / folder / name, self.repo / folder / name)
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            self.port = listener.getsockname()[1]
        (self.repo / "phone-test/test-config.json").write_text(json.dumps({
            "port": self.port, "wakeMode": "poll", "gameDirectory": "../..",
        }))
        self.runtime = self.repo / ".phone-test-state"
        self.env = dict(os.environ, PYTHONUTF8="1", PHONE_MCP_TOKEN="fixture")

    def launch(self, action, **kwargs):
        return subprocess.Popen([sys.executable, "-u", str(self.repo / "phone-test/launcher.py"), action],
                                env=self.env, **kwargs)

    def wait_for(self, condition):
        deadline = time.monotonic() + 12
        while time.monotonic() < deadline:
            if condition():
                return
            time.sleep(0.1)
        log = next(self.runtime.glob("test-*.log"), None)
        self.fail(log.read_text(encoding="utf-8") if log else "Launcher did not reach the expected state")

    def ready(self):
        return any("可以开游戏了，拨 10000" in log.read_text(encoding="utf-8", errors="replace")
                   for log in self.runtime.glob("test-*.log"))

    def restored(self):
        return self.dll.read_bytes() == b"original" and not (self.runtime / "pending.json").exists()

    def test_closing_launcher_restores_both_files(self):
        process = self.launch("start", stdout=subprocess.DEVNULL)
        try:
            self.wait_for(self.ready)
            self.assertEqual(self.dll.read_bytes(), b"test build")
            self.assertEqual(json.loads(self.config.read_bytes())["baseUrl"], f"http://127.0.0.1:{self.port}")
        finally:
            process.terminate()
            process.wait(timeout=5)
        self.wait_for(self.restored)
        self.assertEqual(self.config.read_bytes(), self.original)
        logfile = next(self.runtime.glob("test-*.log"))

        def guardian_exited():
            try:
                logfile.rename(logfile.with_suffix(".finished.log"))
                return True
            except PermissionError:
                return False

        self.wait_for(guardian_exited)

    def test_graceful_stop_restores_absent_config(self):
        self.config.unlink()
        process = self.launch("watch", stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        try:
            self.wait_for(lambda: self.config.exists() or process.poll() is not None)
            self.assertIsNone(process.poll())
        finally:
            process.stdin.close()
        self.assertEqual(process.wait(timeout=12), 0)
        self.assertIn("原 DLL 和配置已还原", process.stdout.read().decode())
        process.stdout.close()
        self.assertTrue(self.restored())
        self.assertFalse(self.config.exists())

    def test_manual_fallback_restores_after_guardian_failure(self):
        backup = self.runtime / "backup"
        backup.mkdir(parents=True)
        shutil.copyfile(self.dll, backup / self.dll.name)
        shutil.copyfile(self.config, backup / self.config.name)
        (self.runtime / "pending.json").write_text(json.dumps({
            "modDirectory": str(self.mod), "originalFiles": [self.dll.name, self.config.name],
        }))
        self.dll.write_bytes(b"test build")
        self.config.write_text("{}")
        with self.launch("restore", stdout=subprocess.PIPE) as process:
            output, _ = process.communicate(timeout=10)
        self.assertEqual(process.returncode, 0, output.decode())
        self.assertTrue(self.restored())
        self.assertEqual(self.config.read_bytes(), self.original)
