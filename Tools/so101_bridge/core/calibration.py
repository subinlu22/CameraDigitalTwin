# -*- coding: utf-8 -*-
"""
틱 ↔ 각도, 영점 파일

각도 계산은 교수님 웹 시뮬레이터 '원시 매핑'과 같다 (10/22 확인):
  각도 = (틱 − 영점 틱) × 360/4096 + (URDF 한계의 가운데 값)
리더 그리퍼만 예외: 닫힘 틱이 있으면  각도 = (틱 − 닫힘 틱) × 360/4096 − 12°
"""
from __future__ import annotations

import json
import time
from pathlib import Path

TICKS_PER_REV = 4096
GRIPPER_CLOSED_DEG = -12.0

# (이름, 한국어, URDF 최소°, URDF 최대°) — CLAUDE.md 표와 같음
JOINTS = [
    ("shoulder_pan", "어깨 회전", -110.0, 110.0),
    ("shoulder_lift", "어깨 들기", -100.0, 100.0),
    ("elbow_flex", "팔꿈치", -96.8, 96.8),
    ("wrist_flex", "손목 굽힘", -95.0, 95.0),
    ("wrist_roll", "손목 회전", -157.2, 162.8),
    ("gripper", "그리퍼", -10.0, 100.0),
]
NAMES = [j[0] for j in JOINTS]
GRIPPER = 5


def mid(i: int) -> float:
    return (JOINTS[i][2] + JOINTS[i][3]) / 2


class ArmCalib:
    """한 팔의 영점. use_gripper_closed=True면 리더식 그리퍼 계산"""

    def __init__(self, path: Path, use_gripper_closed: bool):
        self.path = path
        self.use_gripper_closed = use_gripper_closed
        self.zero: list[int] | None = None
        self.gripper_closed: int | None = None
        self.load()

    def load(self) -> None:
        if not self.path.exists():
            self.zero, self.gripper_closed = None, None
            return
        data = json.loads(self.path.read_text(encoding="utf-8"))
        self.zero = [int(data["zero_ticks"][n]) for n in NAMES]
        self.gripper_closed = data.get("gripper_closed_tick")

    @property
    def ready(self) -> bool:
        return self.zero is not None

    def deg(self, i: int, tick: int) -> float:
        """자르지 않은 각도"""
        if i == GRIPPER and self.use_gripper_closed and self.gripper_closed is not None:
            return (tick - self.gripper_closed) * 360.0 / TICKS_PER_REV + GRIPPER_CLOSED_DEG
        return (tick - self.zero[i]) * 360.0 / TICKS_PER_REV + mid(i)

    def tick(self, i: int, deg: float) -> int:
        """deg()의 반대 (흰 팔 목표 틱 만들 때)"""
        if i == GRIPPER and self.use_gripper_closed and self.gripper_closed is not None:
            return round(self.gripper_closed + (deg - GRIPPER_CLOSED_DEG) * TICKS_PER_REV / 360.0)
        return round(self.zero[i] + (deg - mid(i)) * TICKS_PER_REV / 360.0)

    def save_zero(self, zero: list[int], note: str, port: str) -> None:
        """영점 저장. 그리퍼 닫힘 값 등 다른 칸은 그대로 둠"""
        data = json.loads(self.path.read_text(encoding="utf-8")) if self.path.exists() else {}
        data.update({
            "note": note,
            "saved_at": time.strftime("%Y-%m-%d %H:%M:%S"),
            "port": port,
            "zero_ticks": {n: int(zero[i]) for i, n in enumerate(NAMES)},
        })
        self.path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        self.load()
