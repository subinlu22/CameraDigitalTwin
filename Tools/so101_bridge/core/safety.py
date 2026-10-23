# -*- coding: utf-8 -*-
"""
안전 규칙 — "관절 한계"와 "안전 범위"를 따로 관리

  관절 한계 (URDF + 실측으로 넓힌 값): 유니티로 보내는 각도를 자를 때. 유니티 관절 하한과 같아야 함
  안전 범위 (관절 한계보다 margin° 안쪽 + 예외): 흰 팔에 보내는 목표를 자를 때
  속도 제한: 흰 팔 명령이 한 번에 max_speed/hz 도까지만 움직임
"""
from __future__ import annotations

from .calibration import JOINTS


class Safety:
    def __init__(self, cfg: dict):
        self.cfg = cfg

    def joint_limit(self, i: int) -> tuple[float, float]:
        name, _, lo, hi = JOINTS[i]
        o = self.cfg.get("joint_limit_override", {}).get(name)
        return (o[0], o[1]) if o else (lo, hi)

    def safe_range(self, i: int) -> tuple[float, float]:
        name = JOINTS[i][0]
        o = self.cfg.get("safe_range_override", {}).get(name)
        if o:
            return o[0], o[1]
        lo, hi = self.joint_limit(i)
        m = float(self.cfg.get("safety_margin_deg", 5.0))
        return lo + m, hi - m

    def clamp_for_unity(self, i: int, deg: float) -> float:
        lo, hi = self.joint_limit(i)
        return max(lo, min(hi, deg))

    @staticmethod
    def step(cmd: float, target: float, max_step: float) -> float:
        """cmd를 target 쪽으로 최대 max_step만큼 이동 (속도 제한)"""
        return cmd + max(-max_step, min(max_step, target - cmd))
