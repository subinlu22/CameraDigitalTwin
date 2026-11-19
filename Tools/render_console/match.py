"""레퍼런스 부위 대조 — 레퍼런스 사진에서 찍은 부위 색과 유니티 화면의 같은 부위 색을 비교해, 맞을 때까지 재질 색을 고침.

흐름:
  1) 화면에서 사용자가 레퍼런스 사진의 부위(작업대·벨트 천·프레임·로봇…)를 눌러 색을 찍음 → samples {부위 id: [r,g,b]}
  2) 유니티에 "measure" → 유니티가 부위 마스크로 그 부위 픽셀만 골라 평균 색을 잼 (RenderLookMeasure)
  3) 부위마다 ΔE(사람 눈 기준 색 차이, Lab 거리) 계산 → 차이만큼 그 부위 재질 색(알베도)을 고침
     빛의 양 비율로 고침: 새 색 = 지금 색 × (레퍼런스 / 화면)^0.8 (선형 RGB)
  4) 다시 재기 → 모든 부위 ΔE < OK 이거나 STEPS번까지 반복. 부위마다 결과표(맞음/안 맞음)를 화면에 보여 줌
ΔE 기준: 2 이하 = 거의 같음, 5 이하 = 가까움(맞음), 10 이상 = 눈에 띄게 다름
"""
import math
import time

STEPS = 8
OK = 5.0


def lin(c):
    return [((v + 0.055) / 1.055) ** 2.4 if v > 0.04045 else v / 12.92 for v in c]


def srgb(c):
    return [1.055 * v ** (1 / 2.4) - 0.055 if v > 0.0031308 else v * 12.92 for v in c]


def lab(c):
    r, g, b = lin(c)
    x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047
    y = 0.2126 * r + 0.7152 * g + 0.0722 * b
    z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883
    f = lambda t: t ** (1 / 3) if t > 0.008856 else 7.787 * t + 16 / 116
    fx, fy, fz = f(x), f(y), f(z)
    return 116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)


def delta_e(a, b):
    return math.dist(lab(a), lab(b))


def _measure(link, timeout=4.0):
    state = (link.snapshot().get("state") or {})
    seq = state.get("measureSeq", 0)
    link.send({"cmd": "measure"})
    end = time.time() + timeout
    while time.time() < end:
        time.sleep(0.2)
        st = link.snapshot().get("state") or {}
        if st.get("measureSeq", 0) != seq:
            return st
    return None


def fit(link, samples: dict, report, done) -> None:
    """samples = {부위 id: [r,g,b] 0~1}. report(글자) = 진행, done(표) = 부위별 결과"""
    st = link.snapshot().get("state") or {}
    if not st:
        return report("유니티가 연결돼야 대조할 수 있어요")
    for i in range(STEPS):
        st = _measure(link)
        if not st:
            return report("유니티가 부위 색을 안 보내요 (플레이 중인지 확인)")
        meas = {m["id"]: m["rgb"] for m in st.get("measures", []) if m.get("coverage", 0) > 0.0005}
        states = {s["id"]: s for s in st.get("states", [])}
        table, worst = [], 0.0
        for pid, ref in samples.items():
            cur = meas.get(pid)
            if cur is None:
                table.append({"id": pid, "ref": ref, "cur": None, "de": None, "ok": False, "note": "화면에 안 보임"})
                continue
            de = delta_e(ref, cur)
            worst = max(worst, de)
            table.append({"id": pid, "ref": ref, "cur": cur, "de": round(de, 1), "ok": de <= OK})
            s = states.get(pid)
            if de <= OK / 2 or not s or s.get("mode") != "replace":
                continue   # 원래 재질·텍스처 유지 부위는 색을 못 바꿈 (재질을 먼저 골라야 함)
            col = lin(s["color"])
            k = [max(0.25, min(4.0, (r + 1e-3) / (c + 1e-3))) ** 0.8 for r, c in zip(lin(ref), lin(cur))]
            s = dict(s, color=[round(min(1.0, max(0.005, v)), 4) for v in srgb([max(1e-4, min(1.0, a * b)) for a, b in zip(col, k)])])
            link.send({"cmd": "part", "part": s, "pick": False})
        done(table)
        report(f"대조 {i + 1}/{STEPS} · 가장 큰 차이 ΔE {worst:.1f} (5 이하 = 맞음)")
        if worst <= OK:
            return report(f"대조 끝 · 모든 부위 맞음 (가장 큰 ΔE {worst:.1f}) · 마음에 들면 [룩 저장]")
        time.sleep(0.6)   # 바뀐 재질로 다시 그릴 시간
    report(f"대조 {STEPS}번 끝 · 아직 차이 있는 부위는 표에서 ✖ (조명·노출을 먼저 맞춘 뒤 다시)")
