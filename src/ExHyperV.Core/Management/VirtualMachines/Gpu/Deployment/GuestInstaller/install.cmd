@echo off
setlocal
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0_payload\install.ps1"
set "EXIT_CODE=%errorlevel%"
echo.
if %EXIT_CODE% neq 0 (
    echo [ExHyperV] Driver installation finished with errors ^(exit code %EXIT_CODE%^).
) else (
    echo [ExHyperV] Driver installation completed successfully.
)
pause
exit /b %EXIT_CODE%
