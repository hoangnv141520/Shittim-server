@echo off
cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File "%~dp0autorun.ps1"
