@echo off
setlocal
cd /d "%~dp0"
where node >nul 2>nul
if errorlevel 1 (
  echo Node.js was not found. Install Node.js 24 and add it to PATH.
  pause
  exit /b 1
)
node website\scripts\start.mjs
if errorlevel 1 (
  pause
  exit /b 1
)
