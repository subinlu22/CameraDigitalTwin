# -*- coding: utf-8 -*-
"""
벨트 충돌 방지 (작업공간 제한) — 관절 각도로 팔 끝 위치를 계산해서 벨트에 너무 가까우면 막기

  1) URDF 관절 정보(위치·회전축)로 "관절 각도 → 링크 위치"를 계산 (정기구학, FK)
  2) 팔 끝 쪽 링크(팔뚝·손목·그리퍼·움직이는 집게)마다 실제 모양의 표면 점들을 미리 뽑아 둠
     (유니티 메시에서 6mm마다 1개씩: config/so101_collision_points.json)
  3) 그 점들이 벨트 위 "금지 높이"보다 낮으면 위반
       - 벨트 테두리(옆 레일) 위·바깥 둘레: 레일 윗면 + rail_margin (11/11 결정: 2cm)
         바깥 둘레 폭 = side_margin (11/11: 0.5cm — 2cm면 쉬는 자세 집게가 이미 걸려서 줄임)
       - 벨트 안쪽(뚜껑 놓이는 곳):        벨트 면   + belt_margin (11/11 결정: 0.5cm)
  4) 여유(clearance) = 점 높이 − 금지 높이 중 가장 작은 값. 음수면 위반
     브리지는 "다음 명령이 위반이고, 지금보다 여유가 줄어들면" 움직이지 않고 멈춤 (여유가 늘어나는 쪽은 허용)

좌표: 로봇 받침(base_link) 기준, URDF와 같음 — x = 로봇 앞(벨트 쪽), y = 로봇 왼쪽, z = 위. 단위 m
"""
from __future__ import annotations

import json
import math
import xml.etree.ElementTree as ET
from pathlib import Path

import numpy as np

from .calibration import NAMES

# 팔 끝까지 이어지는 관절 순서 (부모 → 자식)
CHAIN = ["shoulder_pan", "shoulder_lift", "elbow_flex", "wrist_flex", "wrist_roll"]


def _rot(rpy):
    r, p, y = rpy
    cr, sr, cp, sp, cy, sy = math.cos(r), math.sin(r), math.cos(p), math.sin(p), math.cos(y), math.sin(y)
    return np.array([[cy * cp, cy * sp * sr - sy * cr, cy * sp * cr + sy * sr],
                     [sy * cp, sy * sp * sr + cy * cr, sy * sp * cr - cy * sr],
                     [-sp, cp * sr, cp * cr]])


def _tf(xyz, R):
    T = np.eye(4)
    T[:3, :3] = R
    T[:3, 3] = xyz
    return T


def _axis_rot(axis, angle):
    a = np.asarray(axis, float)
    a = a / np.linalg.norm(a)
    x, y, z = a
    c, s, C = math.cos(angle), math.sin(angle), 1 - math.cos(angle)
    return np.array([[c + x * x * C, x * y * C - z * s, x * z * C + y * s],
                     [y * x * C + z * s, c + y * y * C, y * z * C - x * s],
                     [z * x * C - y * s, z * y * C + x * s, c + z * z * C]])


class Kinematics:
    """URDF를 읽어서 관절 각도(도) → 각 링크의 위치·방향(4x4)을 계산"""

    def __init__(self, urdf_path: Path):
        root = ET.parse(urdf_path).getroot()
        self.joints = {}
        for j in root.findall("joint"):
            o = j.find("origin")
            xyz = [float(v) for v in (o.get("xyz") or "0 0 0").split()]
            rpy = [float(v) for v in (o.get("rpy") or "0 0 0").split()]
            a = j.find("axis")
            self.joints[j.get("name")] = {
                "parent": j.find("parent").get("link"), "child": j.find("child").get("link"),
                "T": _tf(xyz, _rot(rpy)), "axis": [float(v) for v in a.get("xyz").split()] if a is not None else None,
                "type": j.get("type"),
            }

    def links(self, deg: list[float]) -> dict[str, np.ndarray]:
        """6관절 각도(도, 순서 = NAMES) → {링크 이름: 받침 기준 4x4}"""
        out = {"base_link": np.eye(4)}
        T = np.eye(4)
        for name in CHAIN:
            j = self.joints[name]
            T = T @ j["T"] @ _tf([0, 0, 0], _axis_rot(j["axis"], math.radians(deg[NAMES.index(name)])))
            out[j["child"]] = T
        g = self.joints["gripper"]   # 움직이는 집게
        out[g["child"]] = T @ g["T"] @ _tf([0, 0, 0], _axis_rot(g["axis"], math.radians(deg[NAMES.index("gripper")])))
        f = self.joints.get("gripper_frame_joint")   # 집게 끝 기준점 (고정)
        if f:
            out[f["child"]] = T @ f["T"]
        return out


class BeltGuard:
    """벨트 테두리·벨트 면 위 금지 높이와 팔 꼭짓점을 비교"""

    def __init__(self, cfg: dict, base_dir: Path):
        g = cfg.get("belt_guard", {})
        self.enabled = bool(g.get("enabled", False))
        self.kin = Kinematics((base_dir / g["urdf_file"]).resolve()) if self.enabled else None
        pts = json.loads((base_dir / g["points_file"]).read_text(encoding="utf-8")) if self.enabled else {}
        # 링크마다 표면 점들 (링크 기준 좌표). "points" = 메시에서 뽑은 점, "boxes" = 감싸는 상자 꼭짓점 8개
        self.points = {}
        for link, pl in pts.get("points", {}).items():
            self.points[link] = np.hstack([np.asarray(pl, float), np.ones((len(pl), 1))]).T
        for link, box in pts.get("boxes", {}).items():
            (x0, y0, z0), (x1, y1, z1) = box
            self.points[link] = np.array([[x, y, z, 1.0] for x in (x0, x1) for y in (y0, y1) for z in (z0, z1)]).T
        self.frame = g.get("frame_box")          # 벨트 테두리 전체 [x0, x1, y0, y1] (받침 기준, m)
        self.inner_x = g.get("inner_x")          # 벨트 안쪽(레일 사이) x 범위 [x0, x1]
        self.rail_top = float(g.get("rail_top", 0))
        self.belt_top = float(g.get("belt_top", 0))
        self.rail_margin = float(g.get("rail_margin", 0.02))
        self.belt_margin = float(g.get("belt_margin", 0.005))
        self.side_margin = float(g.get("side_margin", self.rail_margin))   # 벨트 테두리 옆(수평) 여유

    def floor(self, x: float, y: float) -> float | None:
        """(x, y) 위치에서 넘으면 안 되는 높이. 벨트 근처가 아니면 None (제한 없음)"""
        fx0, fx1, fy0, fy1 = self.frame
        m = self.side_margin
        if not (fx0 - m <= x <= fx1 + m and fy0 - m <= y <= fy1 + m):
            return None
        ix0, ix1 = self.inner_x
        if ix0 < x < ix1 and fy0 <= y <= fy1:
            return self.belt_top + self.belt_margin     # 벨트 안쪽: 뚜껑 집을 수 있게 낮게
        return self.rail_top + self.rail_margin         # 레일 위·바깥 둘레: 높게

    def clearance(self, deg: list[float]) -> tuple[float, str]:
        """(가장 작은 여유 m, 그 꼭짓점의 링크 이름). 벨트 근처 꼭짓점이 없으면 (+inf, '')"""
        if not self.enabled:
            return math.inf, ""
        links = self.kin.links(deg)
        best, who = math.inf, ""
        for link, P in self.points.items():
            if link not in links:
                continue
            W = links[link] @ P
            for x, y, z in W[:3].T:
                f = self.floor(x, y)
                if f is not None and z - f < best:
                    best, who = z - f, link
        return best, who

    def step(self, cur: list[float], nxt: list[float], ids: list[int]) -> tuple[list[float], bool, float, str]:
        """cur → nxt 한 단계. 통째로 안 되면 관절을 하나씩 넣어 보고 되는 것만 움직임
        (예: 팔꿈치는 막혀도 어깨 회전은 따라감 → 벨트에서 스스로 비켜날 수 있음)
        돌려주는 값: (실제로 갈 각도, 일부라도 막혔는지, 여유 m, 막힌 링크)"""
        ok, clear, who = self.allow(cur, nxt)
        if ok:
            return list(nxt), False, clear, who
        out = list(cur)
        for sid in ids:                                   # 어깨 회전부터 순서대로
            i = sid - 1
            if nxt[i] == out[i]:
                continue
            trial = list(out)
            trial[i] = nxt[i]
            if self.allow(out, trial)[0]:
                out = trial
        return out, True, clear, who

    def allow(self, cur: list[float], nxt: list[float]) -> tuple[bool, float, str]:
        """cur → nxt로 움직여도 되는지. 위반이어도 여유가 줄지 않으면 허용 (빠져나오는 동작)"""
        c_next, who = self.clearance(nxt)
        if c_next >= 0:
            return True, c_next, who
        c_cur, _ = self.clearance(cur)
        return c_next >= c_cur - 1e-4, c_next, who
