@echo off
chcp 65001 >nul
set PYTHONUTF8=1
where python.exe >nul 2>nul
if errorlevel 1 (
    py -3 "%~dp0phone-test\launcher.py" restore
) else (
    python "%~dp0phone-test\launcher.py" restore
)
pause
