# -*- coding: utf-8 -*-
"""
브리지 — 리더·흰 팔·유니티를 잇는 본체 (화면 없이도 돌아감)

30Hz 루프 한 바퀴:
  1) 리더 읽기 → 각도 → (미러링 켜짐) 유니티로 보내기(5060) → 리더 소스에 넣기
  2) 흰 팔 읽기 (화면 표시·정지할 때 "지금 위치"로 씀)
  3) (연동 중) 소스 각도 → 안전 범위로 자르기 → 속도 제한 → 흰 팔 목표 쓰기
     소스 값이 watchdog_s 넘게 안 오면 자동 정지
  4) 가끔 상태를 UDP(5062)로 보내기

패널 버튼과 UDP 명령(5061)은 둘 다 아래 public 함수(connect, start_follow, estop …)를 부른다.
서보 통신은 lock 하나로 보호 → 버튼을 누른 순간에도 루프와 엉키지 않음.
"""
from __future__ import annotations

import collections
import json
import threading
import time
from pathlib import Path

from . import calibration as cal
from .net import UdpIn, UdpOut
from .safety import Safety
from .servo_bus import SERVO_IDS, ServoBus, list_ports
from .sources import LeaderSource, OffSource, UnitySource

ROOT = Path(__file__).resolve().parent.parent
CONFIG_PATH = ROOT / "config" / "so101_config.json"


class Bridge:
    def __init__(self, config_path: Path = CONFIG_PATH, enable_udp_in: bool = True):
        self.config_path = config_path
        self.cfg = json.loads(config_path.read_text(encoding="utf-8"))
        cdir = config_path.parent
        self.calib = {
            "leader": cal.ArmCalib((cdir / self.cfg["leader_zero_file"]).resolve(), use_gripper_closed=True),
            "follower": cal.ArmCalib((cdir / self.cfg["follower_zero_file"]).resolve(), use_gripper_closed=False),
        }
        self._derive_follower_gripper()
        self.safety = Safety(self.cfg)
        self.lock = threading.RLock()
        self.bus: dict[str, ServoBus | None] = {"leader": None, "follower": None}
        self.sources = {"leader": LeaderSource(), "unity": UnitySource(), "off": OffSource()}
        self.source = "leader"
        self.mirror = False
        self.follow = False
        self.dry_run = False
        self.estopped = False
        self.torque_on = False
        self.cmd: list[float] | None = None          # 흰 팔에 보내는 명령 각도 (속도 제한 걸린 값)
        self.motion: list[list[float]] = []           # 자세 이동 중이면 남은 경유 자세들 (ㄱ자 / 쉬는 자세)
        self.motion_name: str | None = None
        self.motion_total = 0
        self.follow_lo_hi: list[tuple[float, float]] = []
        self.clipped = [False] * 6
        self.view = {r: {"ticks": [None] * 6, "deg": [None] * 6, "hz": 0.0, "n": 0, "t0": time.perf_counter()}
                     for r in ("leader", "follower")}
        self.sent = 0
        self.last_error = ""
        self.logs: collections.deque[str] = collections.deque(maxlen=300)
        self.angles_out = UdpOut(self.cfg["udp"]["angles_out"])
        self.status_out = UdpOut(self.cfg["udp"]["status_out"])
        self.seq = 0
        self.running = True
        self.udp_in: list[UdpIn] = []
        if enable_udp_in:
            try:
                self.udp_in.append(UdpIn(self.cfg["udp"]["cmd_in_port"], self._on_cmd))
                self.udp_in.append(UdpIn(self.cfg["udp"]["target_in_port"], self._on_target))
            except OSError as e:
                self.log(f"UDP 명령 포트를 못 열었어요 (다른 패널이 켜져 있나요?): {e}")
        self.thread = threading.Thread(target=self._loop, daemon=True)
        self.thread.start()
        self.log("브리지 시작 — 설정: " + str(config_path.name))

    def _derive_follower_gripper(self) -> None:
        """흰 팔 집게도 리더처럼 '닫힘 틱 = -12°'로 계산 (따로 잰 값이 없을 때)
        흰 팔 닫힘 틱 = 리더 닫힘 틱 + (흰 팔 영점 − 리더 영점)  ※ 흰 팔 집게 실제 닫힘 위치는 아직 안 잼"""
        lc, fc = self.calib["leader"], self.calib["follower"]
        fc.use_gripper_closed = True
        if fc.ready and lc.ready and lc.gripper_closed is not None and fc.gripper_closed is None:
            fc.gripper_closed = lc.gripper_closed + (fc.zero[cal.GRIPPER] - lc.zero[cal.GRIPPER])

    # ------------------------------------------------------------------ 기록
    def log(self, text: str) -> None:
        self.logs.append(time.strftime("%H:%M:%S ") + text)

    def _err(self, text: str) -> dict:
        self.last_error = text
        self.log("[오류] " + text)
        return {"ok": False, "msg": text}

    def _ok(self, text: str = "") -> dict:
        if text:
            self.log(text)
        return {"ok": True, "msg": text}

    def _save_cfg(self) -> None:
        self.config_path.write_text(json.dumps(self.cfg, ensure_ascii=False, indent=2), encoding="utf-8")

    # ------------------------------------------------------------------ 연결
    def scan_ports(self) -> dict:
        out = []
        with self.lock:
            busy = {b.port: r for r, b in self.bus.items() if b}
            for dev, desc in list_ports():
                if dev in busy:
                    out.append({"port": dev, "desc": desc, "state": f"{busy[dev]} 연결됨"})
                    continue
                try:
                    b = ServoBus(dev)
                except Exception as e:
                    out.append({"port": dev, "desc": desc, "state": f"열 수 없음 ({e.__class__.__name__})"})
                    continue
                try:
                    n = sum(t is not None for t in b.read_ticks())
                finally:
                    b.close()
                out.append({"port": dev, "desc": desc, "state": f"서보 응답 {n}/6"})
        self.log("포트 찾기: " + ", ".join(f"{p['port']} {p['state']}" for p in out) if out else "포트 찾기: USB 포트 없음")
        return {"ok": True, "msg": "", "ports": out}

    def connect(self, role: str, port: str) -> dict:
        if role not in self.bus:
            return self._err(f"모르는 역할: {role}")
        with self.lock:
            other = "follower" if role == "leader" else "leader"
            if self.bus[other] and self.bus[other].port == port:
                return self._err(f"{port}는 이미 {other}로 연결돼 있어요")
            self.disconnect(role)
            try:
                b = ServoBus(port)
            except Exception as e:
                return self._err(f"{role} {port} 못 엶 — 웹 시뮬레이터 등 다른 프로그램이 쓰는 중일 수 있어요 ({e})")
            ticks = b.read_ticks()
            if all(t is None for t in ticks):
                b.close()
                return self._err(f"{role} {port}: 서보가 응답하지 않아요 (전원·케이블 확인)")
            self.bus[role] = b
            self.cfg[f"{role}_port"] = port
            self._save_cfg()
        return self._ok(f"{'리더' if role == 'leader' else '흰 팔'} {port} 연결 (응답 {sum(t is not None for t in ticks)}/6)")

    def disconnect(self, role: str) -> dict:
        with self.lock:
            if role == "follower" and self.follow:
                self.stop("흰 팔 연결 해제")
            if role == "leader" and self.follow and self.source == "leader":
                self.stop("리더 연결 해제")
            b = self.bus.get(role)
            if b:
                b.close()
                self.bus[role] = None
                self.view[role]["ticks"] = [None] * 6
                self.view[role]["deg"] = [None] * 6
                self.log(f"{role} {b.port} 연결 해제")
        return {"ok": True, "msg": ""}

    # ------------------------------------------------------------------ 동작
    def set_mirror(self, on: bool) -> dict:
        self.mirror = bool(on)
        return self._ok(f"유니티 미러링 {'켬' if on else '끔'} → {', '.join(self.cfg['udp']['angles_out'])}")

    def set_source(self, name: str) -> dict:
        if name not in self.sources:
            return self._err(f"모르는 입력 소스: {name}")
        if self.follow:
            self.stop("입력 소스 바꿈")
        self.source = name
        return self._ok(f"입력 소스 = {name}")

    def _begin(self, dry_run: bool, need_source: bool) -> dict | None:
        """흰 팔을 움직이기 전 공통 준비. 문제가 있으면 오류 dict, 괜찮으면 None"""
        if self.estopped:
            return self._err("비상 정지 상태예요. 먼저 [해제]")
        if self.follow:
            return self._err("이미 움직이는 중이에요. 먼저 [정지]")
        fb = self.bus["follower"]
        if not fb:
            return self._err("흰 팔이 연결돼 있지 않아요")
        if need_source:
            if self.source == "off":
                return self._err("입력 소스가 '끔'이에요")
            if self.source == "leader" and not self.bus["leader"]:
                return self._err("리더가 연결돼 있지 않아요")
        fc = self.calib["follower"]
        if not fc.ready:
            return self._err("흰 팔 영점이 없어요 (영점 카드에서 먼저 저장)")
        now = fb.read_ticks()
        ids = self._follow_ids()
        if any(now[sid - 1] is None for sid in ids):
            return self._err(f"흰 팔 위치를 못 읽었어요: {now}")
        # 흰 팔 "지금 각도"에서 출발 → 갑자기 튀지 않음
        self.cmd = [fc.deg(i, now[i]) if now[i] is not None else 0.0 for i in range(6)]
        # 시작 자세가 안전 범위 밖(예: 접힌 쉬는 자세)이어도 거기까지는 허용
        self.follow_lo_hi = []
        for i in range(6):
            lo, hi = self.safety.safe_range(i)
            self.follow_lo_hi.append((min(lo, self.cmd[i]), max(hi, self.cmd[i])))
        self.dry_run = bool(dry_run)
        if not self.dry_run:
            fb.write_goal(ids, [fc.tick(i - 1, self.cmd[i - 1]) for i in ids])
            fb.set_torque(ids, True)           # 목표 = 지금 위치를 먼저 넣고 힘을 켬
            self.torque_on = True
        return None

    def start_follow(self, dry_run: bool = False) -> dict:
        with self.lock:
            err = self._begin(dry_run, need_source=True)
            if err:
                return err
            self.motion, self.motion_name = [], None
            self.follow = True
        return self._ok(f"흰 팔 연동 시작 — 소스 {self.source}, {'dry-run(안 움직임)' if dry_run else '실제'}, "
                        f"최대 {self.cfg['max_speed_dps']:.0f}°/초, 그리퍼 {'포함' if self.cfg.get('follow_gripper') else '제외'}")

    def goto_pose(self, pose: str, dry_run: bool = False) -> dict:
        """흰 팔을 ㄱ자(l_pose, 모두 0°) 또는 쉬는 자세(rest)로 자동 이동
        벨트에 안 닿게: 어깨를 옆으로 clear_pan_deg 돌림 → 자세 바꿈 → 어깨를 목표 각도로 (3단계)"""
        if pose == "l_pose":
            goal, name = [0.0] * 6, "ㄱ자"
        elif pose == "rest":
            goal, name = [float(v) for v in self.cfg.get("rest_pose_deg", [0, -107.6, 87.2, 71.1, 0, 0])], "쉬는 자세"
        else:
            return self._err(f"모르는 자세: {pose}")
        with self.lock:
            err = self._begin(dry_run, need_source=False)
            if err:
                return err
            pan = float(self.cfg.get("clear_pan_deg", 45.0))
            cur = list(self.cmd)
            w1 = list(cur); w1[0] = pan                       # 1) 어깨만 옆으로
            w2 = list(goal); w2[0] = pan                      # 2) 나머지 관절을 목표 자세로
            w3 = list(goal)                                   # 3) 어깨를 목표 각도로
            for i in range(6):                                # 목표 자세(쉬는 자세 -107.6° 등)까지는 범위 허용
                lo, hi = self.follow_lo_hi[i]
                extra = [goal[i]] + ([pan] if i == 0 else [])
                self.follow_lo_hi[i] = (min([lo] + extra), max([hi] + extra))
            self.motion = [w1, w2, w3]
            self.motion_total = 3
            self.motion_name = name
            self.follow = True
        return self._ok(f"{name}로 이동 시작 — 어깨 {pan:.0f}° 돌리기 → 자세 바꾸기 → 어깨 되돌리기, "
                        f"{float(self.cfg.get('pose_speed_dps', 20)):.0f}°/초, {'dry-run(안 움직임)' if dry_run else '실제'}")

    def _hold_here(self) -> None:
        """흰 팔 '지금 실제 위치'를 목표로 써서 그 자리에 멈춤 (힘 켜져 있을 때만)"""
        fb = self.bus["follower"]
        if not fb or not self.torque_on:
            return
        now = fb.read_ticks()
        ids = [sid for sid in SERVO_IDS if now[sid - 1] is not None]
        if ids:
            fb.write_goal(ids, [now[sid - 1] for sid in ids])

    def stop(self, reason: str = "정지 버튼") -> dict:
        with self.lock:
            was = self.follow
            self.follow = False
            self.motion, self.motion_name = [], None
            if not self.dry_run:
                self._hold_here()
        return self._ok(f"정지 ({reason}) — 흰 팔은 그 자리에서 힘을 준 채 멈춤" if was and not self.dry_run
                        else f"정지 ({reason})")

    def estop(self) -> dict:
        with self.lock:
            self.follow = False
            self.motion, self.motion_name = [], None
            self.estopped = True
            self._hold_here()
        return self._ok("■ 비상 정지 — 해제 전까지 연동을 시작할 수 없어요")

    def estop_reset(self) -> dict:
        self.estopped = False
        return self._ok("비상 정지 해제")

    def release(self, confirm: bool = False) -> dict:
        if not confirm:
            return self._err("힘 빼기는 confirm=true가 필요해요 (팔을 손으로 받친 뒤)")
        with self.lock:
            self.follow = False
            fb = self.bus["follower"]
            if not fb:
                return self._err("흰 팔이 연결돼 있지 않아요")
            fb.set_torque(SERVO_IDS, False)
            self.torque_on = False
        return self._ok("흰 팔 힘 뺌")

    # ------------------------------------------------------------------ 영점
    def _avg_ticks(self, role: str, n: int = 20) -> list[int] | None:
        b = self.bus[role]
        if not b:
            return None
        samples = []
        for _ in range(n):
            t = b.read_ticks()
            if all(v is not None for v in t):
                samples.append(t)
            time.sleep(0.02)
        if not samples:
            return None
        return [round(sum(s[i] for s in samples) / len(samples)) for i in range(6)]

    def capture_zero(self, role: str, method: str) -> dict:
        """role=leader|follower, method=l_pose(지금 ㄱ자) | rest(두 팔 쉬는 자세 차이, 흰 팔만)"""
        if self.follow:
            return self._err("연동 중에는 영점을 저장할 수 없어요")
        with self.lock:
            if method == "l_pose":
                avg = self._avg_ticks(role)
                if avg is None:
                    return self._err(f"{role} 연결/응답 확인")
                self.calib[role].save_zero(avg, f"{role}를 ㄱ자(0자세)로 놓고 저장", self.bus[role].port)
                self._derive_follower_gripper()
                return self._ok(f"{role} ㄱ자 영점 저장: {avg}")
            if method == "rest" and role == "follower":
                lc = self.calib["leader"]
                if not lc.ready:
                    return self._err("리더 영점이 없어요")
                lr, fr = self._avg_ticks("leader"), self._avg_ticks("follower")
                if lr is None or fr is None:
                    return self._err("리더·흰 팔 둘 다 연결돼 있어야 해요")
                zero = [z + (f - l) for z, f, l in zip(lc.zero, fr, lr)]
                self.calib["follower"].save_zero(zero, "두 팔 쉬는 자세 틱 차이로 계산", self.bus["follower"].port)
                self._derive_follower_gripper()
                return self._ok(f"흰 팔 영점(쉬는 자세 차이) 저장: {zero}")
        return self._err(f"모르는 방법: {role}/{method}")

    def _default_zero(self) -> list[int] | None:
        d = self.cfg.get("follower_zero_default")
        return [int(d[n]) for n in cal.NAMES] if d else None

    def reset_zero_default(self) -> dict:
        """흰 팔 영점을 설정 파일의 기본값(10/23 쉬는 자세 기준)으로 되돌림"""
        if self.follow:
            return self._err("움직이는 중에는 영점을 바꿀 수 없어요")
        z = self._default_zero()
        if z is None:
            return self._err("설정 파일에 follower_zero_default가 없어요")
        port = self.bus["follower"].port if self.bus["follower"] else self.cfg.get("follower_port", "")
        self.calib["follower"].save_zero(z, "기본값 (10/23 두 팔 쉬는 자세 차이로 계산, 4634fb7)", port)
        self._derive_follower_gripper()
        return self._ok(f"흰 팔 영점을 기본값으로 되돌림: {z}")

    # ------------------------------------------------------------------ 루프
    def _follow_ids(self) -> list[int]:
        return SERVO_IDS if self.cfg.get("follow_gripper") else SERVO_IDS[:cal.GRIPPER]

    def _read(self, role: str) -> list[int | None] | None:
        b = self.bus[role]
        if not b:
            return None
        try:
            ticks = b.read_ticks()
        except Exception as e:
            self._err(f"{role} 읽기 실패 → 연결 해제: {e}")
            self.disconnect(role)
            return None
        v = self.view[role]
        v["ticks"] = ticks
        c = self.calib[role]
        if c.ready:
            v["deg"] = [c.deg(i, t) if t is not None else None for i, t in enumerate(ticks)]
        if any(t is not None for t in ticks):
            v["n"] += 1
        return ticks

    def _loop(self) -> None:
        period = 1.0 / float(self.cfg.get("hz", 30))
        t_next = time.perf_counter()
        t_status = 0.0
        while self.running:
            with self.lock:
                lt = self._read("leader")
                ld = self.view["leader"]["deg"]
                if lt is not None and self.calib["leader"].ready and all(d is not None for d in ld):
                    if self.mirror:
                        self.seq += 1
                        self.angles_out.send({"type": "so101_leader", "seq": self.seq, "t": round(time.time(), 3),
                                              "deg": [round(self.safety.clamp_for_unity(i, d), 2) for i, d in enumerate(ld)]})
                        self.sent += 1
                    self.sources["leader"].put(ld)   # 흰 팔에는 자르기 전 각도
                self._read("follower")
                if self.follow:
                    self._follow_step(period)
            now = time.perf_counter()
            for v in self.view.values():               # 1초마다 Hz 계산
                if now - v["t0"] >= 1.0:
                    v["hz"] = v["n"] / (now - v["t0"])
                    v["n"], v["t0"] = 0, now
            if now - t_status >= 1.0 / float(self.cfg.get("status_hz", 5)):
                self.status_out.send(self.status())
                t_status = now
            t_next += period
            wait = t_next - time.perf_counter()
            if wait > 0:
                time.sleep(wait)
            else:
                t_next = time.perf_counter()

    def _follow_step(self, period: float) -> None:
        ids = self._follow_ids()
        clamp = lambda i, v: min(self.follow_lo_hi[i][1], max(self.follow_lo_hi[i][0], v))
        if self.motion:                                   # 자세 이동: 경유 자세를 차례로
            speed = float(self.cfg.get("pose_speed_dps", 20.0))
            if all(abs(self.cmd[sid - 1] - clamp(sid - 1, self.motion[0][sid - 1])) < 0.3 for sid in ids):
                self.motion.pop(0)
                if not self.motion:
                    name = self.motion_name
                    self.follow, self.motion_name = False, None
                    self.log(f"{name} 도착 (dry-run)" if self.dry_run else f"{name} 도착 — 흰 팔은 그 자세에서 힘을 준 채 멈춤")
                    return
            deg = self.motion[0]
        else:                                             # 연동: 입력 소스를 따라감
            deg, age = self.sources[self.source].latest()
            if deg is None or age > float(self.cfg.get("watchdog_s", 0.3)):
                self.stop(f"신호 끊김 — {self.source} 값이 {self.cfg.get('watchdog_s', 0.3)}초 넘게 안 옴")
                return
            speed = float(self.cfg["max_speed_dps"])
        max_step = speed * period
        for sid in ids:
            i = sid - 1
            want = clamp(i, deg[i])
            self.clipped[i] = want != deg[i]
            self.cmd[i] = Safety.step(self.cmd[i], want, max_step)
        if not self.dry_run:
            fc = self.calib["follower"]
            self.bus["follower"].write_goal(ids, [fc.tick(sid - 1, self.cmd[sid - 1]) for sid in ids])

    # ------------------------------------------------------------------ 상태·명령
    def status(self) -> dict:
        def arm(role):
            v, b = self.view[role], self.bus[role]
            return {"connected": b is not None, "port": b.port if b else self.cfg.get(f"{role}_port"),
                    "hz": round(v["hz"], 1), "ticks": v["ticks"],
                    "deg": [None if d is None else round(d, 2) for d in v["deg"]],
                    "zero_ready": self.calib[role].ready,
                    "zero_default": (role == "follower" and self.calib[role].zero == self._default_zero())}
        return {"type": "so101_status", "t": round(time.time(), 3),
                "leader": arm("leader"), "follower": arm("follower"),
                "mirror": self.mirror, "sent": self.sent, "source": self.source,
                "follow": self.follow, "dry_run": self.dry_run, "estop": self.estopped, "torque": self.torque_on,
                "cmd_deg": None if self.cmd is None else [round(c, 2) for c in self.cmd],
                "clipped": self.clipped, "follow_ids": self._follow_ids(),
                "safe_range": [list(self.safety.safe_range(i)) for i in range(6)],
                "max_speed_dps": self.cfg["max_speed_dps"], "last_error": self.last_error,
                "motion": self.motion_name,
                "motion_step": (self.motion_total - len(self.motion) + 1) if self.motion_name else 0,
                "motion_total": self.motion_total}

    def handle(self, msg: dict) -> dict:
        """UDP 명령·패널 공용 입구"""
        c = msg.get("cmd")
        try:
            if c == "scan_ports":
                return self.scan_ports()
            if c == "connect":
                return self.connect(msg["role"], msg["port"])
            if c == "disconnect":
                return self.disconnect(msg["role"])
            if c == "mirror":
                return self.set_mirror(bool(msg.get("on")))
            if c == "set_source":
                return self.set_source(msg["source"])
            if c == "follow":
                return self.start_follow(bool(msg.get("dry_run"))) if msg.get("on") else self.stop("follow off 명령")
            if c == "goto_pose":
                return self.goto_pose(msg["pose"], bool(msg.get("dry_run")))
            if c == "stop":
                return self.stop("stop 명령")
            if c == "estop":
                return self.estop()
            if c == "estop_reset":
                return self.estop_reset()
            if c == "release":
                return self.release(bool(msg.get("confirm")))
            if c == "capture_zero":
                return self.capture_zero(msg["role"], msg["method"])
            if c == "reset_zero":
                return self.reset_zero_default()
            if c == "get_status":
                return {"ok": True, "msg": "", "status": self.status()}
        except KeyError as e:
            return self._err(f"{c}: 빠진 값 {e}")
        return self._err(f"모르는 명령: {c}")

    def _on_cmd(self, msg: dict, addr: tuple) -> None:
        if msg.get("type") != "so101_cmd":
            return
        self.log(f"UDP 명령 {msg.get('cmd')} ← {addr[0]}:{addr[1]}")
        ack = self.handle(msg)
        self.status_out.send({"type": "so101_ack", "id": msg.get("id"), **{k: v for k, v in ack.items() if k != "status"}})

    def _on_target(self, msg: dict, addr: tuple) -> None:
        if msg.get("type") == "so101_target" and isinstance(msg.get("deg"), list) and len(msg["deg"]) == 6:
            self.sources["unity"].put([float(d) for d in msg["deg"]])

    def shutdown(self) -> None:
        if self.follow:
            self.stop("프로그램 종료")
        self.running = False
        self.thread.join(timeout=1.0)
        for u in self.udp_in:
            u.close()
        for r in ("leader", "follower"):
            b = self.bus[r]
            if b:
                b.close()
        self.log("브리지 종료")
