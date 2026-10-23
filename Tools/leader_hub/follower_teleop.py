#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
리더 → 흰 팔(팔로워) 연동 (SO-ARM101)  ※ 이 파일은 실물 흰 팔을 움직입니다

흐름: 리더(COM5) 6관절 틱 읽기 → 리더 영점 기준 각도로 바꾸기 → 안전 범위로 자르기
      → 흰 팔 영점 기준 틱으로 바꾸기 → 한 번에 조금씩만 다가가게(속도 제한) → 흰 팔(COM6)에 목표 위치 쓰기
      → 같은 각도를 유니티(UDP 5060)에도 보내기 (유니티·리더·흰 팔이 같이 움직임)

안전장치
  - 관절 범위: URDF 한계에서 MARGIN_DEG(5°) 안쪽으로 자름
  - 속도: 한 번에 --max-speed(기본 45°/초) 이상 움직이지 않음. 시작할 때 흰 팔의 "지금 위치"에서 출발 → 갑자기 튀지 않음
  - 그리퍼: 기본은 안 움직임 (집게 닫힘 값을 아직 모름 → 꽉 쥐어 서보 무리할 수 있음). --with-gripper 로 켬
  - 리더를 못 읽으면 그 관절은 직전 목표에서 멈춤
  - --dry-run: 흰 팔에 아무것도 안 쓰고 보낼 값만 출력 (처음엔 이걸로 확인)
  - Ctrl+C: 보내기를 멈춤 → 흰 팔은 마지막 목표 위치에서 힘을 준 채 멈춰 있음 (힘을 빼면 팔이 떨어지므로)
            힘을 빼려면 팔을 손으로 받친 상태에서 --release

흰 팔 영점(follower_zero.json) 만드는 법 (둘 다 읽기만 함, 흰 팔 안 움직임)
  --capture-zero-from-rest : 리더와 흰 팔을 둘 다 "접힌 쉬는 자세"로 두고 실행
                             흰 팔 영점 = 리더 영점 + (흰 팔 쉬는 틱 − 리더 쉬는 틱)
                             (쉬는 자세가 두 팔에서 똑같다고 가정 → 몇 도 오차 가능)
  --capture-zero           : 흰 팔을 손으로 "ㄱ"자로 잡고 실행 (더 정확)

사용법
  python follower_teleop.py --capture-zero-from-rest        # 1) 영점 저장
  python follower_teleop.py --dry-run --seconds 20          # 2) 값만 확인
  python follower_teleop.py --seconds 60                    # 3) 실제 연동 (흰 팔 움직임)
  python follower_teleop.py --release                       # 흰 팔 힘 빼기 (팔을 받치고!)
"""
from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import leader_hub as hub            # 같은 폴더의 리더 허브 (읽기·각도 계산·UDP를 그대로 씀)

FOLLOWER_ZERO_FILE = Path(__file__).with_name("follower_zero.json")
MARGIN_DEG = 5.0                    # URDF 한계에서 이만큼 안쪽까지만 보냄
ADDR_TORQUE_ENABLE = 40             # 1 = 힘 줌, 0 = 힘 뺌 (1바이트)
ADDR_GOAL_POSITION = 42             # 목표 위치 (2바이트)
GRIPPER = 5                         # JOINTS에서 그리퍼 번호


# ---------------------------------------------------------------------------
# 서보에 쓰기 — SYNC WRITE(0x83): 여러 서보에 한 패킷으로 같은 레지스터를 씀
#   패킷: FF FF FE 길이 83 주소 데이터길이 [ID 데이터…]… 체크섬
# ---------------------------------------------------------------------------
def build_sync_write(addr: int, data_len: int, items: list[tuple[int, list[int]]]) -> bytes:
    params = [addr, data_len]
    for sid, data in items:
        params += [sid] + data
    body = [0xFE, len(params) + 2, 0x83] + params
    return bytes([0xFF, 0xFF] + body + [hub.checksum(body)])


class FollowerBus(hub.LeaderBus):
    """흰 팔 서보 버스. 읽기는 리더와 같고, 쓰기(목표 위치·힘)가 더 있음"""

    def write_goal_ticks(self, ids: list[int], ticks: list[int]) -> None:
        items = [(sid, [t & 0xFF, (t >> 8) & 0xFF]) for sid, t in zip(ids, ticks)]
        self.ser.write(build_sync_write(ADDR_GOAL_POSITION, 2, items))

    def set_torque(self, ids: list[int], on: bool) -> None:
        self.ser.write(build_sync_write(ADDR_TORQUE_ENABLE, 1, [(sid, [1 if on else 0]) for sid in ids]))


# ---------------------------------------------------------------------------
# 영점
# ---------------------------------------------------------------------------
def load_follower_zero() -> list[int] | None:
    if not FOLLOWER_ZERO_FILE.exists():
        return None
    data = json.loads(FOLLOWER_ZERO_FILE.read_text(encoding="utf-8"))
    return [int(data["zero_ticks"][name]) for name, _, _ in hub.JOINTS]


def save_follower_zero(zero: list[int], how: str, port: str) -> None:
    data = {
        "note": "흰 팔(팔로워) 영점 틱 — 리더 영점(leader_zero.json)과 같은 자세(ㄱ자)일 때의 틱",
        "how": how,
        "saved_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        "port": port,
        "zero_ticks": {name: zero[i] for i, (name, _, _) in enumerate(hub.JOINTS)},
    }
    FOLLOWER_ZERO_FILE.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[저장] {FOLLOWER_ZERO_FILE.name}: {data['zero_ticks']}")


def capture_zero(f_port: str) -> None:
    """흰 팔을 손으로 ㄱ자로 잡은 상태에서: 지금 틱 = 영점"""
    avg, n = hub.read_avg_ticks(f_port)
    if avg is None:
        print("[실패] 흰 팔 응답 없음")
        return
    save_follower_zero(avg, "ㄱ자에서 직접 읽음", f_port)


def capture_zero_from_rest(l_port: str, f_port: str) -> None:
    """두 팔 다 쉬는 자세에서: 흰 팔 영점 = 리더 영점 + (흰 팔 틱 − 리더 틱)"""
    l_zero, has_file, _ = hub.load_zero()
    if not has_file:
        print("[실패] 리더 영점 파일(leader_zero.json)이 없습니다")
        return
    l_rest, _ = hub.read_avg_ticks(l_port)
    f_rest, _ = hub.read_avg_ticks(f_port)
    if l_rest is None or f_rest is None:
        print("[실패] 리더나 흰 팔 응답 없음")
        return
    diff = [f - l for f, l in zip(f_rest, l_rest)]
    print("쉬는 자세 틱  리더:", l_rest, " 흰 팔:", f_rest)
    print("차이(흰 팔−리더):", diff, " = 각도", [round(d * 360 / hub.TICKS_PER_REV, 1) for d in diff])
    save_follower_zero([z + d for z, d in zip(l_zero, diff)], "두 팔 쉬는 자세 차이로 계산", f_port)


# ---------------------------------------------------------------------------
# 연동
# ---------------------------------------------------------------------------
def safe_limits() -> list[tuple[float, float]]:
    return [(lo + MARGIN_DEG, hi - MARGIN_DEG) for _, lo, hi in hub.JOINTS]


def deg_to_follower_tick(i: int, deg: float, f_zero: list[int]) -> int:
    """리더 허브의 각도 계산을 거꾸로: 틱 = 영점 + (각도 − 한계 가운데) × 4096/360"""
    _, lo, hi = hub.JOINTS[i]
    return round(f_zero[i] + (deg - (lo + hi) / 2) * hub.TICKS_PER_REV / 360.0)


def teleop(l_port: str, f_port: str, hz: float, max_speed: float, seconds: float,
           dry_run: bool, with_gripper: bool, sender: hub.UdpSender | None) -> None:
    f_zero = load_follower_zero()
    if f_zero is None:
        print("[중지] 흰 팔 영점 파일이 없습니다 → --capture-zero-from-rest 먼저")
        return
    l_zero, _, l_gripper_closed = hub.load_zero()
    ids = hub.SERVO_IDS if with_gripper else hub.SERVO_IDS[:GRIPPER]
    n = len(ids)
    limits = safe_limits()
    step_max = max_speed / hz                       # 한 번에 움직일 수 있는 최대 각도

    leader = hub.LeaderBus(l_port)
    follower = FollowerBus(f_port)
    try:
        # [시작] 흰 팔의 지금 위치를 읽어서 그 각도에서 출발
        f_now = follower.read_ticks()
        if any(t is None for t in f_now[:n]):
            print("[중지] 흰 팔 위치를 못 읽었습니다:", f_now)
            return
        cmd = [(f_now[i] - f_zero[i]) * 360.0 / hub.TICKS_PER_REV + (hub.JOINTS[i][1] + hub.JOINTS[i][2]) / 2
               for i in range(n)]                    # 흰 팔 지금 각도 = 처음 명령 각도
        print("흰 팔 지금 각도:", [round(c, 1) for c in cmd])
        # 시작 자세(접힌 쉬는 자세)가 안전 범위 밖이어도 그 자세까지는 허용 → 시작하자마자 끌려 올라가지 않게
        limits = [(min(lo, cmd[i]), max(hi, cmd[i])) for i, (lo, hi) in enumerate(limits[:n])]
        print("모드:", "값만 출력(--dry-run), 흰 팔 안 움직임" if dry_run else f"실제 연동, 최대 {max_speed:.0f}°/초",
              "| 그리퍼", "움직임" if with_gripper else "안 움직임")
        if not dry_run:
            follower.write_goal_ticks(ids, [deg_to_follower_tick(i, cmd[i], f_zero) for i in range(n)])
            follower.set_torque(ids, True)          # 목표 = 지금 위치를 먼저 넣고 힘을 켬 → 안 튐

        last = [0.0] * len(hub.JOINTS)
        period = 1.0 / hz
        t_start = t_report = t_next = time.perf_counter()
        clipped = [False] * n
        while seconds <= 0 or time.perf_counter() - t_start < seconds:
            ticks = leader.read_ticks()
            last = hub.ticks_to_degrees(ticks, l_zero, last, l_gripper_closed)
            if sender:
                sender.send(last)                   # 유니티는 리더 각도 그대로
            for i in range(n):
                if ticks[i] is None:
                    continue                        # 리더를 못 읽은 관절은 멈춤
                # 흰 팔엔 "자르기 전" 리더 각도를 씀. last는 URDF 한계(-100°)로 잘린 값이라
                # 쉬는 자세(-107°)에서 시작하면 바로 7° 들어 올리게 됨 (10/23 dry-run에서 발견)
                _, jlo, jhi = hub.JOINTS[i]
                raw = (ticks[i] - l_zero[i]) * 360.0 / hub.TICKS_PER_REV + (jlo + jhi) / 2
                lo, hi = limits[i]
                want = min(hi, max(lo, raw))        # 안전 범위로 자르기
                clipped[i] = want != raw
                cmd[i] += max(-step_max, min(step_max, want - cmd[i]))   # 속도 제한
            goal = [deg_to_follower_tick(i, cmd[i], f_zero) for i in range(n)]
            if not dry_run:
                follower.write_goal_ticks(ids, goal)

            now = time.perf_counter()
            if now - t_report >= 0.5:
                mark = "".join("!" if c else "." for c in clipped)
                print(f"리더(유니티) {[round(d, 1) for d in last[:n]]} → 흰 팔 {[round(c, 1) for c in cmd]} 틱 {goal}  범위밖 {mark}",
                      flush=True)
                t_report = now
            t_next += period
            wait = t_next - time.perf_counter()
            if wait > 0:
                time.sleep(wait)
            else:
                t_next = time.perf_counter()
    except KeyboardInterrupt:
        pass
    finally:
        leader.close()
        follower.close()
        print("\n종료 — 흰 팔은 마지막 위치에서 힘을 준 채 멈춰 있음 (힘 빼기: 팔을 받치고 --release)"
              if not dry_run else "\n종료 (dry-run, 흰 팔엔 아무것도 안 보냄)")


def release(f_port: str) -> None:
    bus = FollowerBus(f_port)
    try:
        bus.set_torque(hub.SERVO_IDS, False)
    finally:
        bus.close()
    print("흰 팔 힘을 뺐습니다")


def main() -> None:
    hub._utf8_stdout()
    ap = argparse.ArgumentParser(description="리더 → 흰 팔 연동 (흰 팔이 움직임)")
    ap.add_argument("--leader", default="COM5", help="리더 포트 (기본 COM5)")
    ap.add_argument("--follower", default="COM6", help="흰 팔 포트 (기본 COM6)")
    ap.add_argument("--hz", type=float, default=30.0)
    ap.add_argument("--max-speed", type=float, default=45.0, help="최대 속도 °/초 (기본 45)")
    ap.add_argument("--seconds", type=float, default=0, help="이 시간 뒤 자동 종료 (0 = 계속)")
    ap.add_argument("--dry-run", action="store_true", help="흰 팔에 안 쓰고 값만 출력")
    ap.add_argument("--with-gripper", action="store_true", help="그리퍼도 움직이기")
    ap.add_argument("--no-udp", action="store_true", help="유니티로 안 보내기")
    ap.add_argument("--capture-zero", action="store_true", help="흰 팔 ㄱ자 영점 저장 (읽기만)")
    ap.add_argument("--capture-zero-from-rest", action="store_true", help="두 팔 쉬는 자세로 흰 팔 영점 계산 (읽기만)")
    ap.add_argument("--release", action="store_true", help="흰 팔 힘 빼기 (팔이 떨어지니 받치고)")
    a = ap.parse_args()

    if a.leader == a.follower:
        ap.error("리더와 흰 팔 포트가 같습니다")
    if a.capture_zero:
        capture_zero(a.follower)
    elif a.capture_zero_from_rest:
        capture_zero_from_rest(a.leader, a.follower)
    elif a.release:
        release(a.follower)
    else:
        sender = None if a.no_udp else hub.UdpSender([hub.DEFAULT_UDP])
        teleop(a.leader, a.follower, a.hz, a.max_speed, a.seconds, a.dry_run, a.with_gripper, sender)


if __name__ == "__main__":
    main()
