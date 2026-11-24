# -*- coding: utf-8 -*-
"""
흰 팔 안전장치 테스트 — 실물 없이 "가짜 서보"로 브리지 안전 규칙을 확인

흐름: 1) 설정·영점 파일을 임시 폴더에 복사 (진짜 파일은 안 바뀜)
      2) 브리지를 켜고 30Hz 루프는 바로 멈춤 → 테스트가 _follow_step()을 한 칸씩 직접 돌림
      3) 리더·흰 팔 자리에 FakeBus(가짜 서보)를 꽂음. 흰 팔에 쓴 목표 틱·힘 켜기를 기록만 함
확인하는 것: 안전 범위 자르기 · 속도 제한 · 신호 끊김 자동 정지 · 비상 정지 · 연습 모드 · 힘 빼기 확인 ·
            자동 자세(어깨 먼저 돌리기) · 벨트 충돌 방지

실행: Tools/so101_bridge 폴더에서  python -m unittest -v
"""
from __future__ import annotations

import json
import shutil
import sys
import tempfile
import time
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from core.bridge import Bridge          # noqa: E402
from core.safety import Safety          # noqa: E402

PERIOD = 1.0 / 30                       # 브리지 루프 한 칸 (30Hz)


class FakeBus:
    """가짜 서보 6개. 목표를 쓰면 바로 그 위치로 감 (완벽한 서보)"""

    def __init__(self, port: str, ticks: list[int]):
        self.port = port
        self.ticks = list(ticks)
        self.goals: list[tuple[list[int], list[int]]] = []
        self.torque: dict[int, bool] = {}
        self.torque_limit: dict[int, int] = {}
        self.closed = False

    def read_ticks(self):
        return list(self.ticks)

    def write_goal(self, ids, ticks):
        self.goals.append((list(ids), list(ticks)))
        for sid, t in zip(ids, ticks):
            self.ticks[sid - 1] = t

    def set_torque(self, ids, on):
        for sid in ids:
            self.torque[sid] = bool(on)

    def set_torque_limit(self, ids, value):
        for sid in ids:
            self.torque_limit[sid] = int(value)

    def close(self):
        self.closed = True


class BridgeTest(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        cfg = json.loads((ROOT / "config" / "so101_config.json").read_text(encoding="utf-8"))
        for key in ("leader_zero_file", "follower_zero_file"):      # 영점 파일도 임시 복사본으로
            src = (ROOT / "config" / cfg[key]).resolve()
            dst = self.tmp / src.name
            shutil.copy(src, dst)
            cfg[key] = str(dst)
        cfg["udp"] = {"angles_out": [], "status_out": [], "cmd_in_port": 0, "target_in_port": 0}
        cfg_path = self.tmp / "so101_config.json"
        cfg_path.write_text(json.dumps(cfg, ensure_ascii=False), encoding="utf-8")
        self.b = Bridge(cfg_path, enable_udp_in=False)
        self.b.running = False                  # 30Hz 루프 멈춤 → 테스트가 직접 한 칸씩
        self.b.thread.join(timeout=2)
        self.b.guard.enabled = False            # 벨트 테스트에서만 켬
        self.fc = self.b.calib["follower"]
        self.lc = self.b.calib["leader"]

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    # ---- 도우미
    def plug(self, follower_deg=(0, 0, 0, 0, 0, 0), leader_deg=None):
        """흰 팔(필요하면 리더)을 가짜 서보로 연결. 각도로 넣으면 틱으로 바꿔 둠"""
        self.fbus = FakeBus("FAKE_F", [self.fc.tick(i, d) for i, d in enumerate(follower_deg)])
        self.b.bus["follower"] = self.fbus
        if leader_deg is not None:
            self.b.bus["leader"] = FakeBus("FAKE_L", [self.lc.tick(i, d) for i, d in enumerate(leader_deg)])

    def leader_says(self, deg):
        self.b.sources["leader"].put(list(deg))

    def run_steps(self, n, deg=None):
        for _ in range(n):
            if deg is not None:
                self.leader_says(deg)
            if not self.b.follow:
                break
            self.b._follow_step(PERIOD)

    def follower_deg(self):
        return [self.fc.deg(i, t) for i, t in enumerate(self.fbus.ticks)]

    # ---- 1. 범위
    def test_safe_range_is_inside_joint_limit(self):
        s = self.b.safety
        self.assertEqual(s.safe_range(0), (-105.0, 105.0))        # 한계 ±110에서 5° 안쪽
        self.assertEqual(s.safe_range(1), (-108.0, 95.0))         # 어깨 들기: 쉬는 자세 -107.6° 허용 (예외)
        self.assertEqual(s.safe_range(5), (-12.0, 95.0))          # 그리퍼: 꽉 닫힘 -12°까지
        for i in range(6):
            lo, hi = s.safe_range(i)
            jlo, jhi = s.joint_limit(i)
            if i != 5:                                            # 그리퍼 -12°는 일부러 URDF -10° 바깥
                self.assertGreaterEqual(lo, jlo)
            self.assertLessEqual(hi, jhi)

    def test_unity_angles_clamped_to_joint_limit(self):
        self.assertEqual(self.b.safety.clamp_for_unity(1, -130.0), -110.0)
        self.assertEqual(self.b.safety.clamp_for_unity(0, 200.0), 110.0)
        self.assertEqual(self.b.safety.clamp_for_unity(2, 10.0), 10.0)

    def test_follow_never_leaves_safe_range(self):
        self.plug(leader_deg=(0, 0, 0, 0, 0, 0))
        self.leader_says([0, 0, 0, 0, 0, 0])
        self.assertTrue(self.b.start_follow(dry_run=True)["ok"])
        self.run_steps(200, deg=[150, 0, 0, 0, 0, 0])            # 리더가 한계 밖을 요구
        self.assertAlmostEqual(self.b.cmd[0], 105.0, places=3)
        self.assertTrue(self.b.clipped[0])

    # ---- 2. 속도
    def test_speed_limit_per_step(self):
        self.assertEqual(Safety.step(0.0, 100.0, 3.0), 3.0)
        self.assertEqual(Safety.step(0.0, -100.0, 3.0), -3.0)
        self.assertEqual(Safety.step(0.0, 1.0, 3.0), 1.0)

    def test_follow_moves_at_most_max_speed(self):
        self.plug(leader_deg=(0, 0, 0, 0, 0, 0))
        self.leader_says([0, 0, 0, 0, 0, 0])
        self.b.start_follow(dry_run=True)
        max_step = self.b.cfg["max_speed_dps"] * PERIOD           # 90°/초 × 1/30초 = 3°
        prev = list(self.b.cmd)
        for _ in range(20):
            self.run_steps(1, deg=[90, -90, 90, -90, 90, 90])
            for a, b in zip(prev, self.b.cmd):
                self.assertLessEqual(abs(b - a), max_step + 1e-9)
            prev = list(self.b.cmd)

    def test_start_from_current_position_no_jump(self):
        start = (20, -50, 40, 10, 0, 5)
        self.plug(follower_deg=start, leader_deg=(0, 0, 0, 0, 0, 0))
        self.leader_says([0, 0, 0, 0, 0, 0])
        self.b.start_follow(dry_run=True)
        for c, s in zip(self.b.cmd, start):
            self.assertAlmostEqual(c, s, delta=0.1)               # 틱 반올림 오차만

    # ---- 3. 신호 끊김
    def test_watchdog_stops_when_signal_lost(self):
        self.plug(leader_deg=(0, 0, 0, 0, 0, 0))
        self.leader_says([0, 0, 0, 0, 0, 0])
        self.b.start_follow(dry_run=True)
        self.b.sources["leader"].t = time.perf_counter() - 0.31   # 마지막 값이 0.31초 전
        self.b._follow_step(PERIOD)
        self.assertFalse(self.b.follow)

    def test_watchdog_keeps_going_with_fresh_signal(self):
        self.plug(leader_deg=(0, 0, 0, 0, 0, 0))
        self.leader_says([0, 0, 0, 0, 0, 0])
        self.b.start_follow(dry_run=True)
        self.run_steps(30, deg=[10, 0, 0, 0, 0, 0])
        self.assertTrue(self.b.follow)

    # ---- 4. 비상 정지
    def test_estop_blocks_start_until_reset(self):
        self.plug(leader_deg=(0, 0, 0, 0, 0, 0))
        self.leader_says([0, 0, 0, 0, 0, 0])
        self.b.start_follow(dry_run=True)
        self.b.estop()
        self.assertFalse(self.b.follow)
        self.assertFalse(self.b.start_follow(dry_run=True)["ok"])
        self.assertFalse(self.b.goto_pose("l_pose", dry_run=True)["ok"])
        self.b.estop_reset()
        self.assertTrue(self.b.start_follow(dry_run=True)["ok"])

    def test_estop_holds_current_position_when_torque_on(self):
        self.plug(follower_deg=(10, 0, 0, 0, 0, 0), leader_deg=(0, 0, 0, 0, 0, 0))
        self.leader_says([0, 0, 0, 0, 0, 0])
        self.b.start_follow(dry_run=False)
        self.run_steps(5, deg=[60, 0, 0, 0, 0, 0])
        here = list(self.fbus.ticks)
        self.b.estop()
        ids, ticks = self.fbus.goals[-1]
        self.assertEqual(ticks, [here[sid - 1] for sid in ids])   # 마지막 목표 = 지금 위치

    # ---- 5. 연습 모드 / 실제
    def test_dry_run_never_writes_to_servo(self):
        self.plug(leader_deg=(0, 0, 0, 0, 0, 0))
        self.leader_says([0, 0, 0, 0, 0, 0])
        self.b.start_follow(dry_run=True)
        self.run_steps(30, deg=[30, 0, 0, 0, 0, 0])
        self.b.stop()
        self.assertEqual(self.fbus.goals, [])
        self.assertEqual(self.fbus.torque, {})

    def test_real_mode_sets_goal_before_torque_and_limits_gripper(self):
        self.plug(follower_deg=(5, 0, 0, 0, 0, 0), leader_deg=(0, 0, 0, 0, 0, 0))
        self.leader_says([0, 0, 0, 0, 0, 0])
        self.b.start_follow(dry_run=False)
        first_ids, first_ticks = self.fbus.goals[0]
        self.assertEqual(first_ticks, [self.fbus.ticks[sid - 1] for sid in first_ids])   # 첫 목표 = 지금 위치
        self.assertTrue(all(self.fbus.torque[sid] for sid in first_ids))
        self.assertEqual(self.fbus.torque_limit.get(6), self.b.cfg["gripper_torque_limit"])

    def test_release_needs_confirm(self):
        self.plug()
        self.assertFalse(self.b.release(confirm=False)["ok"])
        self.assertTrue(self.b.release(confirm=True)["ok"])
        self.assertFalse(any(self.fbus.torque.values()))

    def test_refuses_without_follower_or_source(self):
        self.assertFalse(self.b.start_follow(dry_run=True)["ok"])  # 흰 팔 없음
        self.plug()                                                # 리더 없음
        self.assertFalse(self.b.start_follow(dry_run=True)["ok"])
        self.b.set_source("off")
        self.assertFalse(self.b.start_follow(dry_run=True)["ok"])

    # ---- 5-1. USB 빠짐 (흰 팔 쓰기 오류)
    def test_write_error_stops_follow_and_disconnects(self):
        self.plug(leader_deg=(0, 0, 0, 0, 0, 0))
        self.leader_says([0, 0, 0, 0, 0, 0])
        self.b.start_follow(dry_run=False)
        def broken(*a):
            raise OSError("USB 빠짐")
        self.fbus.write_goal = broken
        self.b._follow_step(PERIOD)
        self.assertFalse(self.b.follow)
        self.assertIsNone(self.b.bus["follower"])
        self.assertIn("쓰기 실패", self.b.last_error)

    def test_loop_survives_errors(self):
        """30Hz 루프 한 바퀴에서 오류가 나도 루프 스레드는 살아 있고, 연동은 멈춤"""
        b = Bridge(self.b.config_path, enable_udp_in=False)
        try:
            b.bus["follower"] = FakeBus("FAKE_F", [self.fc.tick(i, 0) for i in range(6)])
            b.follow = True
            def boom(period):
                raise RuntimeError("테스트 오류")
            b._follow_step = boom
            time.sleep(0.3)
            self.assertTrue(b.thread.is_alive())
            self.assertFalse(b.follow)
            self.assertIn("루프 오류", b.last_error)
        finally:
            b.running = False
            b.thread.join(timeout=2)

    # ---- 6. 자동 자세: 어깨 먼저 돌리기
    def test_goto_rest_turns_shoulder_first(self):
        self.plug(follower_deg=(0, 0, 0, 0, 0, 0))
        self.assertTrue(self.b.goto_pose("rest", dry_run=True)["ok"])
        w1, w2, w3 = self.b.motion
        self.assertEqual(w1[1:5], self.b.cmd[1:5])                 # 1단계: 어깨 회전만 바뀜
        self.assertEqual(w1[0], self.b.cfg["clear_pan_deg"])
        self.assertEqual(w2[0], self.b.cfg["clear_pan_deg"])       # 2단계: 어깨는 옆으로 둔 채 자세 바꿈
        rest = self.b.cfg["rest_pose_deg"]
        # 1단계 동안 다른 관절은 안 움직임
        while self.b.follow and len(self.b.motion) == 3:
            before = list(self.b.cmd)
            self.b._follow_step(PERIOD)
            if len(self.b.motion) == 3:                            # (어깨가 도착한 칸은 바로 2단계로 넘어가서 제외)
                self.assertEqual(before[1:5], self.b.cmd[1:5])
        self.run_steps(3000)
        self.assertFalse(self.b.follow)                            # 도착하면 스스로 멈춤
        for i in range(5):
            self.assertAlmostEqual(self.b.cmd[i], rest[i], delta=0.3)

    # ---- 7. 벨트 충돌 방지
    def test_belt_guard_keeps_clearance_while_following(self):
        g = self.b.guard
        if g.kin is None:
            self.skipTest("벨트 충돌 방지 설정(URDF)을 못 읽음")
        g.enabled = True
        self.plug(leader_deg=(0, 0, 0, 0, 0, 0))                   # ㄱ자: 벨트에서 약 10cm 위
        self.leader_says([0, 0, 0, 0, 0, 0])
        self.b.start_follow(dry_run=True)
        target = [0, 30, 30, 60, 0, 0]                             # 벨트 속으로 들어가는 자세 (계산상 -11.8cm)
        self.assertLess(g.clearance(target)[0], 0)
        for _ in range(300):
            self.run_steps(1, deg=target)
            self.assertGreaterEqual(g.clearance(self.b.cmd)[0], 0)  # 한 번도 벨트 안으로 안 들어감
        self.assertIsNotNone(self.b.belt_block)

    def test_belt_guard_still_allows_turning_away(self):
        g = self.b.guard
        if g.kin is None:
            self.skipTest("벨트 충돌 방지 설정(URDF)을 못 읽음")
        g.enabled = True
        self.plug(leader_deg=(0, 0, 0, 0, 0, 0))
        self.leader_says([0, 0, 0, 0, 0, 0])
        self.b.start_follow(dry_run=True)
        self.run_steps(300, deg=[0, 30, 30, 60, 0, 0])             # 벨트 앞에서 막힘
        pan_before = self.b.cmd[0]
        self.run_steps(30, deg=[90, 30, 30, 60, 0, 0])             # 어깨를 돌려 비켜나는 건 됨
        self.assertGreater(self.b.cmd[0], pan_before + 10)


if __name__ == "__main__":
    unittest.main()
