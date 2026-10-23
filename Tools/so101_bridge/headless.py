#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
화면 없이 브리지만 켜기 — UDP 명령(5061)으로 조작 (나중에 유니티 exe가 패널 대신 조작할 때)

  python headless.py                      # 켜기만 (명령 대기)
  python headless.py --connect --mirror   # 설정 파일의 포트로 리더·흰 팔 연결 + 유니티 미러링 켬
흰 팔은 'follow' 명령이 와야만 움직임. Ctrl+C로 종료 (연동 중이면 그 자리에서 정지)
"""
from __future__ import annotations

import argparse
import sys
import time

from core.bridge import Bridge


def main() -> None:
    for s in (sys.stdout, sys.stderr):
        if hasattr(s, "reconfigure"):
            s.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description="SO-ARM101 브리지 (화면 없음)")
    ap.add_argument("--connect", action="store_true", help="설정 파일의 포트로 리더·흰 팔 연결")
    ap.add_argument("--mirror", action="store_true", help="유니티 미러링 켜기")
    a = ap.parse_args()

    br = Bridge()
    if a.connect:
        br.connect("leader", br.cfg["leader_port"])
        br.connect("follower", br.cfg["follower_port"])
    if a.mirror:
        br.set_mirror(True)
    shown = 0
    try:
        while True:
            logs = list(br.logs)
            for line in logs[shown:]:
                print(line, flush=True)
            shown = len(logs)
            time.sleep(0.2)
    except KeyboardInterrupt:
        pass
    finally:
        br.shutdown()
        for line in list(br.logs)[shown:]:
            print(line)


if __name__ == "__main__":
    main()
