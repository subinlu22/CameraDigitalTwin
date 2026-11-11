@echo off
rem SO-ARM101 컨트롤 패널 켜기 (더블클릭). 이미 켜져 있으면 창만 열림
rem 바탕화면에 바로가기로 두고 쓰면 됨. 끄기: 패널 화면의 [패널 종료]
chcp 65001 >nul
cd /d "%~dp0"
start "SO101 panel" /min cmd /k python panel.py
