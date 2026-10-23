# -*- coding: utf-8 -*-
"""UDP 보내기·받기 (메시지는 전부 JSON 한 덩어리). 규격: docs/so101_messages.md"""
from __future__ import annotations

import json
import socket
import threading
from typing import Callable


def parse_target(s: str) -> tuple[str, int]:
    host, port = s.rsplit(":", 1)
    return host, int(port)


class UdpOut:
    def __init__(self, targets: list[str]):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.targets = [parse_target(t) for t in targets]

    def send(self, msg: dict) -> None:
        data = json.dumps(msg, ensure_ascii=False).encode("utf-8")
        for t in self.targets:
            try:
                self.sock.sendto(data, t)
            except OSError:
                pass


class UdpIn:
    """포트로 오는 JSON을 받을 때마다 callback(msg, (ip, port)) — 별도 스레드"""

    def __init__(self, port: int, callback: Callable[[dict, tuple], None], host: str = "127.0.0.1"):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind((host, port))
        self.sock.settimeout(0.5)
        self.callback = callback
        self.running = True
        self.thread = threading.Thread(target=self._loop, daemon=True)
        self.thread.start()

    def _loop(self) -> None:
        while self.running:
            try:
                data, addr = self.sock.recvfrom(65536)
            except socket.timeout:
                continue
            except OSError:
                break
            try:
                msg = json.loads(data.decode("utf-8"))
            except (ValueError, UnicodeDecodeError):
                continue
            if isinstance(msg, dict):
                self.callback(msg, addr)

    def close(self) -> None:
        self.running = False
        self.sock.close()
