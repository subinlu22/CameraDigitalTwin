#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SO-ARM101 컨트롤 패널 (v1) — 수빈이 직접 연결·해제·제어하고 값을 보는 화면

흐름: 1) core/bridge.py(로봇·유니티를 잇는 본체)를 켠다
      2) 이 PC 안에서만 보이는 작은 웹 서버(127.0.0.1:8765)를 띄운다
      3) Edge '앱 창'으로 web/index.html을 연다 (주소창 없는 프로그램 창처럼 보임)
      4) 화면은 0.1초마다 /api/status로 값을 받아 다시 그리고,
         버튼은 /api/cmd로 UDP 명령(5061)과 똑같은 명령을 보낸다 → bridge.handle()
화면은 HTML/CSS라 디자인만 바꾸기 쉽고, 로직은 core에 그대로 있음

실행: python panel.py            (Tools/so101_bridge 폴더에서)
      python panel.py --no-open  (창을 자동으로 안 열기 → 브라우저에서 http://127.0.0.1:8765)
끄기: 화면의 [패널 종료] 또는 이 터미널에서 Ctrl+C (연동 중이면 흰 팔은 그 자리에서 정지)
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import threading
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from core import calibration as cal
from core.bridge import Bridge
from core.servo_bus import list_ports

WEB = Path(__file__).resolve().parent / "web"
# 3D 화면용: 유니티와 같은 URDF·STL 메시를 복사하지 않고 그대로 보냄 (/urdf/...)
URDF_DIR = Path(__file__).resolve().parents[2] / "Assets" / "URDF" / "SO101"
HOST, PORT = "127.0.0.1", 8765
TYPES = {".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8",
         ".css": "text/css; charset=utf-8", ".svg": "image/svg+xml",
         ".urdf": "application/xml; charset=utf-8", ".stl": "model/stl"}

bridge: Bridge | None = None
server: ThreadingHTTPServer | None = None


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):          # 요청마다 터미널에 찍지 않기
        pass

    def _send(self, code: int, body: bytes, ctype: str) -> None:
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _json(self, obj, code: int = 200) -> None:
        self._send(code, json.dumps(obj, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8")

    def do_GET(self):
        path = self.path.split("?")[0]
        if path == "/api/status":
            since = int(self.path.split("since=")[1]) if "since=" in self.path else 0
            logs = list(bridge.logs)
            self._json({"status": bridge.status(), "logs": logs[since:] if since <= len(logs) else logs,
                        "log_count": len(logs)})
        elif path == "/api/info":
            self._json({"joints": [{"name": j[0], "ko": j[1], "limit": list(bridge.safety.joint_limit(i))}
                                   for i, j in enumerate(cal.JOINTS)],
                        "watchdog_s": bridge.cfg.get("watchdog_s"),
                        "angles_out": bridge.cfg["udp"]["angles_out"],
                        "ports": [{"port": p, "desc": d} for p, d in list_ports()]})
        else:
            base = URDF_DIR if path.startswith("/urdf/") else WEB
            rel = path[len("/urdf/"):] if base is URDF_DIR else ("index.html" if path == "/" else path.lstrip("/"))
            f = (base / rel).resolve()
            if base not in f.parents or not f.is_file():
                self._send(404, b"not found", "text/plain")
            else:
                self._send(200, f.read_bytes(), TYPES.get(f.suffix, "application/octet-stream"))

    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        try:
            msg = json.loads(self.rfile.read(n) or b"{}")
        except ValueError:
            return self._json({"ok": False, "msg": "JSON이 아님"}, 400)
        if self.path == "/api/cmd":
            self._json(bridge.handle(msg))
        elif self.path == "/api/shutdown":
            self._json({"ok": True, "msg": "패널 종료"})
            threading.Thread(target=server.shutdown, daemon=True).start()
        else:
            self._json({"ok": False, "msg": "모르는 주소"}, 404)


def open_window(url: str) -> None:
    """Edge 앱 창으로 열기 (없으면 기본 브라우저)"""
    edge = shutil.which("msedge") or next((p for p in (
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe") if os.path.exists(p)), None)
    if edge:
        subprocess.Popen([edge, f"--app={url}", "--window-size=1340,940"])
    else:
        webbrowser.open(url)


def main() -> None:
    global bridge, server
    for s in (sys.stdout, sys.stderr):
        if hasattr(s, "reconfigure"):
            s.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description="SO-ARM101 컨트롤 패널")
    ap.add_argument("--no-open", action="store_true", help="창 자동으로 안 열기")
    a = ap.parse_args()

    bridge = Bridge()
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    url = f"http://{HOST}:{PORT}/"
    print(f"[패널] {url}  (끄기: 화면의 [패널 종료] 또는 Ctrl+C)", flush=True)
    if not a.no_open:
        open_window(url)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        bridge.shutdown()
        server.server_close()
        print("[패널] 종료 — 연결 해제됨", flush=True)


if __name__ == "__main__":
    main()
