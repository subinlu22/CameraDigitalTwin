#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
리더암 허브 (SO-ARM101) — 리더암 6관절 각도를 읽어서 나눠주는 프로그램

지금 하는 일: 리더암 서보 6개의 현재 위치(틱)를 30Hz로 읽어 각도(°)로 바꿔
             출력하고, UDP로 유니티(기본 127.0.0.1:5060)에 보내기
나중에 붙일 일: 학생 PC 여러 대로 보내기 (--udp를 여러 번 쓰면 지금도 가능)

안전: 서보에 "읽기" 명령만 보냅니다. 쓰기(토크·목표 위치) 명령은 이 파일에 없어서
      로봇이 움직이지 않습니다.

사용법
  python leader_hub.py --scan                       # 포트마다 서보 1~6이 응답하는지 확인
  python leader_hub.py --port COM5                  # 30Hz로 읽어서 출력 + UDP 5060으로 보내기
  python leader_hub.py --port COM5 --no-udp         # 출력만
  python leader_hub.py --port COM5 --capture-zero   # 지금 자세("ㄱ"자)를 영점으로 저장
  python leader_hub.py --port COM5 --capture-gripper-closed  # 리더 집게를 꽉 오므린 위치 저장

각도 계산은 교수님 웹 시뮬레이터(assets/js/feetech-webserial.js)의 '원시 매핑'과 같습니다.
  각도 = (틱 - 영점 틱) × 360/4096 + (관절 한계의 가운데 값)
그래서 같은 영점을 쓰면 웹 시뮬레이터 화면의 각도와 같은 숫자가 나옵니다.

예외 — 그리퍼: 위 계산이면 리더 집게를 꽉 오므려도 약 15°까지만 내려가서 유니티 집게가
  다물지 않습니다. 그래서 '집게 닫힘 틱'을 저장해 두었으면 그리퍼만 이렇게 계산합니다.
  그리퍼 각도 = (틱 - 닫힘 틱) × 360/4096 + GRIPPER_CLOSED_DEG(-12°)
  → 리더를 꽉 오므리면 -12° = 유니티 집게 끝이 맞닿는 각도 (10/22 Scene 화면으로 맞춤)
  -12°는 URDF 한계(-10°)보다 2° 바깥이라, 유니티 그리퍼 하한도 -12°로 넓혀 둠
"""
from __future__ import annotations

import argparse
import json
import socket
import sys
import time
from pathlib import Path

import serial                      # pyserial
import serial.tools.list_ports

# ---------------------------------------------------------------------------
# 설정 값
# ---------------------------------------------------------------------------
BAUD = 1_000_000                   # SO-ARM101 서보(STS3215) 통신 속도
SERVO_IDS = [1, 2, 3, 4, 5, 6]     # 관절 순서대로 서보 ID
ADDR_PRESENT_POSITION = 56         # 서보의 "현재 위치" 레지스터 (2바이트)
TICKS_PER_REV = 4096               # 서보 한 바퀴 = 4096틱
CENTER_TICK = 2048                 # 영점 파일이 없을 때 쓰는 기본 영점
GRIPPER_CLOSED_DEG = -12.0         # 리더 집게 꽉 오므림 = 유니티 집게 닫힘 각도 (URDF 하한 -10°보다 2° 더)

# (관절 이름, 최소 °, 최대 °) — URDF 한계, CLAUDE.md 표와 같음
JOINTS = [
    ("shoulder_pan", -110.0, 110.0),
    ("shoulder_lift", -100.0, 100.0),
    ("elbow_flex", -96.8, 96.8),
    ("wrist_flex", -95.0, 95.0),
    ("wrist_roll", -157.2, 162.8),
    ("gripper", -10.0, 100.0),
]

ZERO_FILE = Path(__file__).with_name("leader_zero.json")   # 영점 틱 저장 파일

# UDP 보내는 곳. 5053은 카메라 프로젝트, 5050은 다른 프로그램이 써서 5060 사용
DEFAULT_UDP = "127.0.0.1:5060"


def _utf8_stdout() -> None:
    """윈도우 콘솔에서 한글이 깨지지 않게"""
    for s in (sys.stdout, sys.stderr):
        if hasattr(s, "reconfigure"):
            try:
                s.reconfigure(encoding="utf-8", errors="replace")
            except (ValueError, OSError):
                pass


# ---------------------------------------------------------------------------
# 1) 서보 통신 — 패킷 만들기 / 읽기
#    패킷 모양: FF FF  ID  길이  명령  파라미터…  체크섬
# ---------------------------------------------------------------------------
def checksum(body: list[int]) -> int:
    return (~sum(body)) & 0xFF


def build_sync_read(addr: int, length: int, ids: list[int]) -> bytes:
    """서보 여러 개에 한 번에 '위치 알려줘'라고 묻는 패킷 (SYNC READ, 0x82)"""
    body = [0xFE, len(ids) + 4, 0x82, addr, length] + ids
    return bytes([0xFF, 0xFF] + body + [checksum(body)])


def build_read(servo_id: int, addr: int, length: int) -> bytes:
    """서보 하나에 묻는 패킷 (READ, 0x02) — SYNC READ가 안 될 때 대신 씀"""
    body = [servo_id, 4, 0x02, addr, length]
    return bytes([0xFF, 0xFF] + body + [checksum(body)])


def parse_packets(buf: bytearray) -> dict[int, int]:
    """받은 바이트에서 응답 패킷을 찾아 {서보ID: 틱} 으로 돌려준다"""
    got: dict[int, int] = {}
    i = 0
    while i + 5 < len(buf):
        if buf[i] != 0xFF or buf[i + 1] != 0xFF:
            i += 1
            continue
        sid, length = buf[i + 2], buf[i + 3]
        end = i + 4 + length                      # 체크섬 다음 위치
        if length < 2 or end > len(buf):
            break                                  # 아직 덜 들어옴
        body = list(buf[i + 2:end - 1])
        if checksum(body) == buf[end - 1] and length >= 4:
            got[sid] = buf[i + 5] | (buf[i + 6] << 8)   # 리틀엔디언 2바이트
            i = end
        else:
            i += 2                                 # 깨진 패킷은 건너뛰고 다시 찾기
    return got


class LeaderBus:
    """리더암 서보 버스. 읽기만 한다."""

    def __init__(self, port: str):
        self.ser = serial.Serial(port, BAUD, timeout=0)
        self.use_sync = True
        self.sync_fails = 0

    def close(self) -> None:
        self.ser.close()

    def _ask(self, packet: bytes, want: int, timeout_s: float) -> dict[int, int]:
        """패킷을 보내고, 응답 want개가 오거나 시간이 다 될 때까지 받는다"""
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
        """6관절의 현재 위치(틱). 응답 없는 관절은 None"""
        if self.use_sync:
            got = self._ask(build_sync_read(ADDR_PRESENT_POSITION, 2, SERVO_IDS),
                            len(SERVO_IDS), 0.030)
            if len(got) == len(SERVO_IDS):
                self.sync_fails = 0
                return [got[i] for i in SERVO_IDS]
            self.sync_fails += 1
            if self.sync_fails >= 3:               # 3번 연속 실패하면 하나씩 읽기로
                self.use_sync = False
                print("[알림] SYNC READ 응답이 없어 관절별로 하나씩 읽습니다")
        out: list[int | None] = []
        for sid in SERVO_IDS:
            got = self._ask(build_read(sid, ADDR_PRESENT_POSITION, 2), 1, 0.020)
            out.append(got.get(sid))
        return out


# ---------------------------------------------------------------------------
# 2) 틱 → 각도
# ---------------------------------------------------------------------------
def read_zero_file() -> dict:
    """영점 파일 내용 (없으면 빈 dict)"""
    if ZERO_FILE.exists():
        return json.loads(ZERO_FILE.read_text(encoding="utf-8"))
    return {}


def load_zero() -> tuple[list[int], bool, int | None]:
    """영점 틱, 영점 파일 유무, 그리퍼 닫힘 틱(없으면 None). 영점 파일이 없으면 서보 중앙(2048)"""
    data = read_zero_file()
    closed = data.get("gripper_closed_tick")
    if "zero_ticks" in data:
        return [int(data["zero_ticks"][name]) for name, _, _ in JOINTS], True, closed
    return [CENTER_TICK] * len(JOINTS), False, closed


def ticks_to_degrees(ticks: list[int | None], zero: list[int],
                     last: list[float], gripper_closed: int | None = None) -> list[float]:
    """웹 시뮬레이터 '원시 매핑'과 같은 계산. 못 읽은 관절은 직전 값을 유지
    그리퍼는 닫힘 틱이 있으면 닫힘 틱 = GRIPPER_CLOSED_DEG(-12°)로 계산"""
    out = []
    for i, (name, lo, hi) in enumerate(JOINTS):
        t = ticks[i]
        if t is None:
            out.append(last[i])
            continue
        if name == "gripper" and gripper_closed is not None:
            lo = GRIPPER_CLOSED_DEG                  # 자르는 아래 끝도 -12°
            deg = (t - gripper_closed) * 360.0 / TICKS_PER_REV + lo
        else:
            deg = (t - zero[i]) * 360.0 / TICKS_PER_REV + (lo + hi) / 2
        out.append(max(lo, min(hi, deg)))          # 관절 한계 안으로 자르기
    return out


# ---------------------------------------------------------------------------
# 3) UDP 보내기 — 받는 쪽(유니티 등)이 여러 개여도 같은 메시지를 각각 보낸다
#    메시지 예: {"type": "so101_leader", "seq": 12, "t": 1792634500.12,
#               "deg": [0.0, 1.1, -0.2, -0.7, 2.5, 45.0]}
#    deg 순서 = JOINTS 순서 (shoulder_pan, shoulder_lift, elbow_flex,
#                             wrist_flex, wrist_roll, gripper)
# ---------------------------------------------------------------------------
class UdpSender:
    def __init__(self, targets: list[str]):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.targets = []
        for t in targets:                            # "IP:포트" 글자를 (IP, 포트)로
            host, port = t.rsplit(":", 1)
            self.targets.append((host, int(port)))
        self.seq = 0                                 # 보낸 순서 번호 (빠진 메시지 확인용)

    def send(self, degrees: list[float]) -> None:
        self.seq += 1
        msg = json.dumps({
            "type": "so101_leader",
            "seq": self.seq,
            "t": round(time.time(), 3),
            "deg": [round(d, 2) for d in degrees],
        }).encode("utf-8")
        for target in self.targets:
            self.sock.sendto(msg, target)


# ---------------------------------------------------------------------------
# 4) 실행 모드
# ---------------------------------------------------------------------------
def scan_ports() -> None:
    """USB 시리얼 포트에서 서보 1~6 응답을 확인 (읽기만)"""
    for p in serial.tools.list_ports.comports():
        if p.vid is None:                           # 블루투스·기본 COM 포트는 건너뜀
            continue
        try:
            bus = LeaderBus(p.device)
        except serial.SerialException as e:
            print(f"{p.device:6} 열 수 없음 ({p.description}) — 다른 프로그램이 쓰는 중일 수 있음: {e}")
            continue
        try:
            ticks = bus.read_ticks()
        finally:
            bus.close()
        ok = sum(t is not None for t in ticks)
        print(f"{p.device:6} 응답 {ok}/6  틱={ticks}  ({p.description})")


def read_avg_ticks(port: str) -> tuple[list[int] | None, int]:
    """20번 읽어서 평균 틱 (손 떨림 줄이기). 응답이 없으면 None"""
    bus = LeaderBus(port)
    samples = []
    try:
        for _ in range(20):
            t = bus.read_ticks()
            if all(v is not None for v in t):
                samples.append(t)
            time.sleep(0.02)
    finally:
        bus.close()
    if not samples:
        return None, 0
    return [round(sum(s[i] for s in samples) / len(samples)) for i in range(len(JOINTS))], len(samples)


def capture_zero(port: str) -> None:
    """지금 자세를 영점으로 저장. 리더암을 "ㄱ"자(0자세)로 놓고 실행"""
    avg, n = read_avg_ticks(port)
    if avg is None:
        print("[실패] 서보 응답이 없어 영점을 저장하지 못했습니다")
        return
    old = read_zero_file()
    data = {
        "note": "리더암을 ㄱ자(0자세)로 놓고 --capture-zero로 저장한 틱 값",
        "saved_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        "port": port,
        "zero_ticks": {name: avg[i] for i, (name, _, _) in enumerate(JOINTS)},
    }
    for k in ("gripper_closed_tick", "gripper_closed_saved_at"):   # 집게 닫힘 값은 그대로 둠
        if k in old:
            data[k] = old[k]
    ZERO_FILE.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[저장] {ZERO_FILE.name}: {data['zero_ticks']}  (샘플 {n}개 평균)")


def capture_gripper_closed(port: str) -> None:
    """리더 집게를 꽉 오므린 위치(틱)를 저장. 이 틱이 그리퍼 -12°(GRIPPER_CLOSED_DEG)가 됨"""
    avg, n = read_avg_ticks(port)
    if avg is None:
        print("[실패] 서보 응답이 없어 저장하지 못했습니다")
        return
    data = read_zero_file()
    gi = [name for name, _, _ in JOINTS].index("gripper")
    data["gripper_closed_tick"] = avg[gi]
    data["gripper_closed_saved_at"] = time.strftime("%Y-%m-%d %H:%M:%S")
    ZERO_FILE.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[저장] {ZERO_FILE.name}: gripper_closed_tick={avg[gi]}  (샘플 {n}개 평균)")


def run(port: str, hz: float, print_every: float, seconds: float = 0,
        sender: UdpSender | None = None) -> None:
    """hz 속도로 읽어서 각도를 출력(+UDP로 보내기). Ctrl+C로 종료 (seconds > 0 이면 그 시간 뒤 종료)"""
    zero, has_file, gripper_closed = load_zero()
    print("[그리퍼] " + (f"닫힘 틱 {gripper_closed} = {GRIPPER_CLOSED_DEG}°" if gripper_closed is not None
                       else "닫힘 틱 없음 → 원시 매핑 (--capture-gripper-closed 로 저장 가능)"))
    if not has_file:
        print("[주의] 영점 파일이 없어 서보 중앙(2048)을 영점으로 씁니다. "
              "각도가 웹 시뮬레이터와 다를 수 있어요 → --capture-zero 먼저")
    bus = LeaderBus(port)
    period = 1.0 / hz
    last = [0.0] * len(JOINTS)
    frames, misses = 0, 0
    t_start = t_report = t_next = time.perf_counter()
    names = " ".join(f"{n[:11]:>11}" for n, _, _ in JOINTS)
    print(f"{'Hz':>5} {names}", flush=True)
    try:
        while seconds <= 0 or time.perf_counter() - t_start < seconds:
            ticks = bus.read_ticks()
            misses += sum(t is None for t in ticks)
            last = ticks_to_degrees(ticks, zero, last, gripper_closed)
            if sender:
                sender.send(last)                    # 읽을 때마다 바로 보냄
            frames += 1

            now = time.perf_counter()
            if now - t_report >= print_every:       # 매 프레임 찍으면 너무 많아서 가끔만 출력
                rate = frames / (now - t_report)
                vals = " ".join(f"{d:11.1f}" for d in last)
                print(f"{rate:5.1f} {vals}" + (f"   (못 읽음 {misses})" if misses else ""), flush=True)
                frames, misses, t_report = 0, 0, now

            t_next += period                         # 다음 읽을 시각까지 기다리기
            wait = t_next - time.perf_counter()
            if wait > 0:
                time.sleep(wait)
            else:
                t_next = time.perf_counter()        # 밀렸으면 지금부터 다시
    except KeyboardInterrupt:
        print("\n종료")
    finally:
        bus.close()


def main() -> None:
    _utf8_stdout()
    ap = argparse.ArgumentParser(description="SO-ARM101 리더암 허브 (읽기 전용)")
    ap.add_argument("--port", help="리더암 COM 포트 (예: COM5)")
    ap.add_argument("--hz", type=float, default=30.0, help="읽는 속도 (기본 30)")
    ap.add_argument("--print-every", type=float, default=0.5, help="출력 간격(초)")
    ap.add_argument("--seconds", type=float, default=0, help="이 시간(초) 뒤 자동 종료 (0 = 계속)")
    ap.add_argument("--udp", action="append", metavar="IP:PORT",
                    help=f"각도를 보낼 곳 (여러 번 쓸 수 있음, 기본 {DEFAULT_UDP})")
    ap.add_argument("--no-udp", action="store_true", help="UDP로 보내지 않고 출력만")
    ap.add_argument("--scan", action="store_true", help="포트마다 서보 응답 확인")
    ap.add_argument("--capture-zero", action="store_true", help="지금 자세를 영점으로 저장")
    ap.add_argument("--capture-gripper-closed", action="store_true",
                    help="리더 집게를 꽉 오므린 위치를 저장 (그리퍼 -12°로 맞춤)")
    a = ap.parse_args()

    if a.scan:
        scan_ports()
    elif not a.port:
        ap.error("--port 가 필요합니다 (모르면 --scan 먼저)")
    elif a.capture_zero:
        capture_zero(a.port)
    elif a.capture_gripper_closed:
        capture_gripper_closed(a.port)
    else:
        sender = None
        if not a.no_udp:
            sender = UdpSender(a.udp or [DEFAULT_UDP])
            print(f"[UDP] 보내는 곳: {', '.join(f'{h}:{p}' for h, p in sender.targets)}")
        run(a.port, a.hz, a.print_every, a.seconds, sender)


if __name__ == "__main__":
    main()
