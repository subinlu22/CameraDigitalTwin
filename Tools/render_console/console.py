"""렌더 룩 콘솔 — 유니티 장면의 재질·조명 분위기를 따로 뜬 창에서 실시간으로 조절.

실행:  python Tools/render_console/console.py        (Edge 앱 창이 뜸)
       python Tools/render_console/console.py --no-open  (브라우저에서 http://127.0.0.1:8770)
흐름:
  1) 이 PC 안에서만 보이는 작은 웹 서버(127.0.0.1:8770)를 띄우고 Edge 앱 창으로 화면(web/)을 연다
  2) 유니티가 UDP 5071로 보내는 상태(부위·프리셋 목록, 지금 값)를 받아 두었다가 화면에 넘겨준다
  3) 화면에서 고르거나 슬라이더를 움직이면 UDP 5070으로 유니티에 명령을 보낸다
  4) 1초마다 "ping"을 보내서 유니티가 켜지는 순간 바로 연결된다 (콘솔이 켜져 있을 때만 유니티가 렌더 뷰를 보냄)
  5) 유니티가 TCP 5072로 보내는 렌더 뷰(JPG)를 받아 화면 가운데에 영상(MJPEG)으로 보여 준다
유니티 쪽은 Assets/RenderLookConsole (장면에 안 넣어도 플레이하면 자동으로 생김). 메시지 형식: docs/render_look_messages.md
파이썬 표준 라이브러리만 씀 (설치할 것 없음)
"""
import argparse
import json
import os
import shutil
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HOST, PORT = "127.0.0.1", 8770
UNITY_PORT, LISTEN_PORT = 5070, 5071       # 콘솔 → 유니티 / 유니티 → 콘솔
CRLF = bytes([13, 10])
FRAME_PORT = 5072                         # 유니티 → 콘솔 렌더 뷰 (TCP, [길이 4바이트][JPG])
ALIVE_S = 3.0                              # 이 시간 동안 소식이 없으면 "연결 끊김"
WEB = Path(__file__).resolve().parent / "web"
TYPES = {".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8",
         ".css": "text/css; charset=utf-8", ".svg": "image/svg+xml", ".ico": "image/x-icon", ".ttf": "font/ttf"}


class UnityLink:
    """유니티와 UDP로 주고받기 (받기는 별도 스레드)"""

    def __init__(self):
        self.state = None
        self.last = 0.0
        self.lock = threading.Lock()
        self.tx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.rx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.rx.bind((HOST, LISTEN_PORT))
        self.rx.settimeout(0.5)
        self.running = True
        self.frame, self.frame_id = None, 0
        self.frame_cv = threading.Condition()
        self.fsrv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.fsrv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.fsrv.bind((HOST, FRAME_PORT))
        self.fsrv.listen(1)
        threading.Thread(target=self._receive, daemon=True).start()
        threading.Thread(target=self._hello, daemon=True).start()
        threading.Thread(target=self._frames, daemon=True).start()

    def _receive(self):
        while self.running:
            try:
                data, _ = self.rx.recvfrom(65535)
            except socket.timeout:
                continue
            except OSError:
                return
            try:
                msg = json.loads(data.decode("utf-8"))
            except ValueError:
                continue
            if msg.get("type") == "render_state":
                with self.lock:
                    self.state, self.last = msg, time.time()

    def _hello(self):
        while self.running:
            self.send({"cmd": "ping" if self.connected() else "hello"})
            time.sleep(1)

    def _frames(self):
        """유니티 렌더 뷰 받기: 연결 하나씩, 끊기면 다시 기다림"""
        while self.running:
            try:
                conn, _ = self.fsrv.accept()
            except OSError:
                return
            with conn:
                while self.running:
                    head = self._read(conn, 4)
                    if head is None:
                        break
                    jpg = self._read(conn, int.from_bytes(head, "big"))
                    if jpg is None:
                        break
                    with self.frame_cv:
                        self.frame, self.frame_id = jpg, self.frame_id + 1
                        self.frame_cv.notify_all()

    @staticmethod
    def _read(conn, n):
        buf = b""
        while len(buf) < n:
            chunk = conn.recv(n - len(buf))
            if not chunk:
                return None
            buf += chunk
        return buf

    def wait_frame(self, last_id: int, timeout: float = 2.0):
        with self.frame_cv:
            self.frame_cv.wait_for(lambda: self.frame_id != last_id or not self.running, timeout)
            return self.frame, self.frame_id

    def connected(self) -> bool:
        return time.time() - self.last < ALIVE_S

    def send(self, cmd: dict) -> None:
        cmd = dict(cmd, type="render_cmd")
        self.tx.sendto(json.dumps(cmd, ensure_ascii=False).encode("utf-8"), (HOST, UNITY_PORT))

    def snapshot(self) -> dict:
        with self.lock:
            return {"connected": self.connected(), "state": self.state}

    def close(self):
        self.running = False
        self.rx.close()
        self.tx.close()
        self.fsrv.close()
        with self.frame_cv:
            self.frame_cv.notify_all()


def web_version() -> int:
    return int(max((f.stat().st_mtime for f in WEB.iterdir() if f.is_file()), default=0))


link: UnityLink | None = None
server: ThreadingHTTPServer | None = None


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
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
        if path == "/api/state":
            # web 폴더 파일이 바뀌면 version이 바뀜 → 화면이 알아서 새로고침 (창을 다시 열 필요 없음)
            return self._json(dict(link.snapshot(), version=web_version()))
        if path == "/stream":
            return self._stream()
        if path.startswith("/assets/"):
            return self._asset(path[len("/assets/"):])
        f = (WEB / ("index.html" if path == "/" else path.lstrip("/"))).resolve()
        if WEB not in f.parents or not f.is_file():
            return self._send(404, b"not found", "text/plain")
        self._send(200, f.read_bytes(), TYPES.get(f.suffix, "application/octet-stream"))

    def _stream(self):
        """렌더 뷰 영상 (MJPEG): <img src="/stream">가 새 그림을 계속 받음"""
        self.send_response(200)
        self.send_header("Content-Type", "multipart/x-mixed-replace; boundary=frame")
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        last = -1
        try:
            while link.running:
                jpg, last = link.wait_frame(last)
                if jpg is None:
                    continue
                head = b"--frame" + CRLF + b"Content-Type: image/jpeg" + CRLF + b"Content-Length: %d" % len(jpg) + CRLF + CRLF
                self.wfile.write(head + jpg + CRLF)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, OSError):
            pass

    def _asset(self, rel: str):
        """썸네일 등: 유니티가 알려 준 프리셋 폴더(StreamingAssets/RenderTool) 안의 파일만"""
        state = link.snapshot()["state"]
        if not state or not state.get("root"):
            return self._send(404, b"no unity", "text/plain")
        base = Path(state["root"]).resolve()
        f = (base / urllib.request.url2pathname(rel)).resolve()
        if base not in f.parents or not f.is_file() or f.suffix.lower() not in (".png", ".jpg", ".jpeg"):
            return self._send(404, b"not found", "text/plain")
        self._send(200, f.read_bytes(), "image/png" if f.suffix.lower() == ".png" else "image/jpeg")

    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        try:
            msg = json.loads(self.rfile.read(n) or b"{}")
        except ValueError:
            return self._json({"ok": False, "msg": "JSON이 아님"}, 400)
        if self.path == "/api/cmd":
            link.send(msg)
            return self._json({"ok": True})
        if self.path == "/api/shutdown":
            self._json({"ok": True})
            threading.Thread(target=server.shutdown, daemon=True).start()
            return
        self._json({"ok": False, "msg": "모르는 주소"}, 404)


def open_window(url: str) -> None:
    """Edge 앱 창으로 열기 (없으면 기본 브라우저)"""
    edge = shutil.which("msedge") or next((p for p in (
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe") if os.path.exists(p)), None)
    if edge:
        subprocess.Popen([edge, f"--app={url}", "--window-size=1600,940"])
    else:
        webbrowser.open(url)


def main() -> None:
    global link, server
    for s in (sys.stdout, sys.stderr):
        if hasattr(s, "reconfigure"):
            s.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description="렌더 룩 콘솔")
    ap.add_argument("--no-open", action="store_true", help="창 자동으로 안 열기")
    a = ap.parse_args()

    url = f"http://{HOST}:{PORT}/"
    try:   # 이미 켜져 있으면 창만 열기
        urllib.request.urlopen(url + "api/state", timeout=1)
        print("[렌더 콘솔] 이미 켜져 있어요 → 창만 엽니다", flush=True)
        if not a.no_open:
            open_window(url)
        return
    except (urllib.error.URLError, OSError):
        pass

    link = UnityLink()
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    print(f"[렌더 콘솔] {url}  유니티 UDP {UNITY_PORT}↔{LISTEN_PORT}  (끄기: 화면의 [콘솔 종료] 또는 Ctrl+C)", flush=True)
    if not a.no_open:
        open_window(url)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        link.close()
        server.server_close()
        print("[렌더 콘솔] 종료", flush=True)


if __name__ == "__main__":
    main()
