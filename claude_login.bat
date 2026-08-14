@echo off
chcp 65001 >nul
rem ASCII-only launcher. Logic lives in claude_login.ps1 (UTF-8).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0claude_login.ps1"
