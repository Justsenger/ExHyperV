@echo off
setlocal
chcp 65001 >nul
title ExHyperV Core Tests
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0Scripts\Inspect-Resources.ps1"
set "launchExit=%errorlevel%"
if not "%launchExit%"=="0" (
    echo.
    echo 测试页面启动失败，请查看上方提示。
    pause
)
exit /b %launchExit%
