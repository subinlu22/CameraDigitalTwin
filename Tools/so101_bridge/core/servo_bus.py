# -*- coding: utf-8 -*-
"""
서보 통신 (SO-ARM101 = Feetech STS3215 서보 6개, 1Mbps)

패킷 모양: FF FF  ID  길이  명령  파라미터…  체크섬
  - SYNC READ  (0x82): 서보 여러 개에 "위치 알려줘"를 한 번에
  - READ       (0x02): 서보 하나에 묻기 (SYNC READ가 안 될 때)
  - SYNC WRITE (0x83): 서보 여러 개에 같은 레지스터를 한 번에 쓰기 (목표 위치, 힘 켜기/끄기)
읽기만 하는 리더도, 쓰기도 하는 흰 팔도 이 클래스 하나로 다룬다.
"""
from __future__ import annotations

import time

import serial
import serial.tools.list_ports

BAUD = 1_000_000
SERVO_IDS = [1, 2, 3, 4, 5, 6]
ADDR_TORQUE_ENABLE = 40      # 1바이트: 1 = 힘 줌, 0 = 힘 뺌
ADDR_GOAL_POSITION = 42      # 2바이트: 목표 위치(틱)
ADDR_PRESENT_POSITION = 56   # 2바이트: 현재 위치(틱)


def checksum(body: list[int]) -> int:
    return (~sum(body)) & 0xFF


def build_sync_read(addr: int, length: int, ids: list[int]) -> bytes:
    body = [0xFE, len(ids) + 4, 0x82, addr, length] + ids
    return bytes([0xFF, 0xFF] + body + [checksum(body)])


def build_read(servo_id: int, addr: int, length: int) -> bytes:
    body = [servo_id, 4, 0x02, addr, length]
    return bytes([0xFF, 0xFF] + body + [checksum(body)])


def build_sync_write(addr: int, data_len: int, items: list[tuple[int, list[int]]]) -> bytes:
    params = [addr, data_len]
    for sid, data in items:
        params += [sid] + data
    body = [0xFE, len(params) + 2, 0x83] + params
    return bytes([0xFF, 0xFF] + body + [checksum(body)])


def parse_packets(buf: bytearray) -> dict[int, int]:
    """받은 바이트에서 응답 패킷을 찾아 {서보ID: 2바이트 값}"""
    got: dict[int, int] = {}
    i = 0
    while i + 5 < len(buf):
        if buf[i] != 0xFF or buf[i + 1] != 0xFF:
            i += 1
            continue
        sid, length = buf[i + 2], buf[i + 3]
        end = i + 4 + length
        if length < 2 or end > len(buf):
            break
        body = list(buf[i + 2:end - 1])
        if checksum(body) == buf[end - 1] and length >= 4:
            got[sid] = buf[i + 5] | (buf[i + 6] << 8)
            i = end
        else:
            i += 2
    return got


class ServoBus:
    def __init__(self, port: str):
        self.port = port
        self.ser = serial.Serial(port, BAUD, timeout=0)
        self.use_sync = True
        self.sync_fails = 0

    def close(self) -> None:
        try:
            self.ser.close()
        except Exception:
            pass

    def _ask(self, packet: bytes, want: int, timeout_s: float) -> dict[int, int]:
        self.ser.reset_input_buffer()
        self.ser.write(packet)
        buf = bytearray()
        deadline = time.perf_counter() + timeout_s
        got: dict[int, int] = {}
        while time.perf_counter() < deadline:
            buf += self.ser.read(256)
            got = parse_packets(buf)
            if len(got) >= want:
                break
        return got

    def read_ticks(self) -> list[int | None]:
        """6관절 현재 위치(틱). 응답 없는 관절은 None"""
        if self.use_sync:
            got = self._ask(build_sync_read(ADDR_PRESENT_POSITION, 2, SERVO_IDS), len(SERVO_IDS), 0.030)
            if len(got) == len(SERVO_IDS):
                self.sync_fails = 0
                return [got[i] for i in SERVO_IDS]
            self.sync_fails += 1
            if self.sync_fails >= 3:
                self.use_sync = False
        return [self._ask(build_read(sid, ADDR_PRESENT_POSITION, 2), 1, 0.020).get(sid) for sid in SERVO_IDS]

    def write_goal(self, ids: list[int], ticks: list[int]) -> None:
        items = [(sid, [t & 0xFF, (t >> 8) & 0xFF]) for sid, t in zip(ids, ticks)]
        self.ser.write(build_sync_write(ADDR_GOAL_POSITION, 2, items))

    def set_torque(self, ids: list[int], on: bool) -> None:
        self.ser.write(build_sync_write(ADDR_TORQUE_ENABLE, 1, [(sid, [1 if on else 0]) for sid in ids]))


def list_ports() -> list[tuple[str, str]]:
    """USB 시리얼 포트 목록 [(COM5, 설명)] — 블루투스·기본 포트 제외"""
    return [(p.device, p.description) for p in serial.tools.list_ports.comports() if p.vid is not None]
