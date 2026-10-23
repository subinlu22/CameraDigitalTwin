# -*- coding: utf-8 -*-
"""
흰 팔 입력 소스 — 흰 팔이 "누구의 자세"를 따라갈지

  leader : 리더암 각도 (v1 구현)
  unity  : 유니티가 UDP 5063으로 보낸 목표 자세 (자리만 만들어 둠, v2에서 실물 테스트)
  off    : 아무것도 안 따라감
각 소스는 latest() → (6관절 각도 리스트 또는 None, 받은 지 몇 초)
"""
from __future__ import annotations

import time


class Source:
    name = "off"

    def __init__(self):
        self.deg: list[float] | None = None
        self.t = 0.0

    def put(self, deg: list[float]) -> None:
        self.deg = list(deg)
        self.t = time.perf_counter()

    def latest(self) -> tuple[list[float] | None, float]:
        if self.deg is None:
            return None, 1e9
        return self.deg, time.perf_counter() - self.t


class LeaderSource(Source):
    name = "leader"


class UnitySource(Source):
    name = "unity"


class OffSource(Source):
    name = "off"

    def latest(self):
        return None, 1e9
