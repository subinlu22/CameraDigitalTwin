@echo off
rem Render Look Console (double click). If already running, only the window opens.
chcp 65001 >nul
cd /d "%~dp0"
start "Render Look Console" /min cmd /k python console.py
