@echo off
setlocal
chcp 65001 >nul
set PYTHONUTF8=1
if not exist "%~dp0phone-test\launcher.py" goto incomplete
if not exist "%~dp0phone-test\player_setup.py" goto incomplete
pushd "%~dp0" || goto unavailable

python.exe -c "import sys; raise SystemExit(sys.version_info < (3, 10))" >nul 2>nul
if not errorlevel 1 goto python
py.exe -3 -c "import sys; raise SystemExit(sys.version_info < (3, 10))" >nul 2>nul
if not errorlevel 1 goto py

echo 找不到 Python 3.10 或更新版本。请安装 Windows 版 Python，然后重新双击本脚本。
goto failed

:python
python.exe "%~dp0phone-test\launcher.py" setup
if errorlevel 1 goto failed
exit /b 0

:py
py.exe -3 "%~dp0phone-test\launcher.py" setup
if errorlevel 1 goto failed
exit /b 0

:incomplete
echo 找不到启动文件。请先将 ZIP 完整解压，再打开解压目录里的「启动电话.bat」。
goto failed

:unavailable
echo 无法打开安装目录。请将 ZIP 解压到本机可写的文件夹，再重新启动。

:failed
pause
exit /b 1
