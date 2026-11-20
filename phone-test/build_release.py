"""Package a built Windows player release without local settings or credentials."""
import argparse
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, default=ROOT / "mod/bin/Release/netstandard2.1/AiPhone.dll")
    parser.add_argument("--output", type=Path, default=ROOT / "dist/DefinitelyYours-Windows.zip")
    args = parser.parse_args()
    if not args.dll.is_file():
        parser.error("找不到 Release DLL，请先构建或用 --dll 指定文件。")
    files = [
        "启动电话.bat", "恢复原配置.bat", "README.md", "LICENSE", "EVENTS_GUIDE.md",
        "mod/manifest.xml", "mod/config.example.json", "mod/events.starter.txt",
        "phone-plugin/server.py", "phone-plugin/dispatch.py",
        "phone-plugin/CONNECT.md", "phone-plugin/AGENTS.md", "phone-plugin/.env.example",
        "phone-plugin/deploy/nginx-phone.conf", "phone-test/launcher.py", "phone-test/player_setup.py",
        "phone-test/DEEPSEEK.md", "phone-test/dsh-mcp.example.yml", "phone-test/.env.example",
        "phone-test/test-config.example.json",
        "docs/title.gif", "docs/how-it-works.png",
    ]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(args.output, "w", compression=ZIP_DEFLATED) as archive:
        archive.write(args.dll, "mod/AiPhone.dll")
        for name in files:
            source = ROOT / name
            if source.suffix.lower() == ".bat":
                # cmd.exe requires Windows line endings even when Git checked out LF.
                data = source.read_bytes().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
                archive.writestr(name, data)
            else:
                archive.write(source, name)
        for texture in sorted((ROOT / "mod/Textures/Items").glob("*.png")):
            archive.write(texture, texture.relative_to(ROOT).as_posix())
    print(f"已生成玩家安装包：{args.output}")


if __name__ == "__main__":
    main()
