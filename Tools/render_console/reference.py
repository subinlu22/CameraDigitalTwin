"""레퍼런스 색 맞추기 — 사진 한 장의 색감에 지금 유니티 화면을 맞춤 (Claude 없이, 이 PC에서만).

흐름 (Reinhard 색 전이를 '조명 세부 슬라이더'로 옮긴 것):
  1) 레퍼런스 사진과 지금 렌더 뷰를 Lab 색 공간으로 바꿔 평균·퍼짐을 잼
     L = 밝기, a = 초록↔자홍, b = 파랑↔노랑
  2) 차이만큼 슬라이더를 조금씩 움직임
     밝기 평균 → 노출 · 밝기 퍼짐 → 대비 · 색 퍼짐 → 채도 · b 평균 → 색온도 · a 평균 → 색조
  3) 유니티가 새로 그린 화면을 다시 재고 → 차이가 작아질 때까지 반복 (최대 STEPS번)
그림 내용이 달라도 '분위기 숫자'만 맞추는 방식이라 결과는 근사값. 마음에 안 들면 슬라이더 ↺로 되돌림
Pillow 필요 (pip install pillow)
"""
import io
import math

STEPS = 6
RANGES = {"exposure": (-2, 2), "contrast": (-50, 50), "saturation": (-60, 60), "temperature": (-60, 60), "tint": (-60, 60)}


def stats(img_bytes: bytes) -> dict:
    from PIL import Image, ImageStat
    im = Image.open(io.BytesIO(img_bytes)).convert("RGB")
    im.thumbnail((320, 320))
    st = ImageStat.Stat(im.convert("LAB"))
    (lm, am, bm), (ls, as_, bs) = st.mean, st.stddev
    lin = ((lm / 255 * 100 + 16) / 116) ** 3          # Lab 밝기 → 빛의 양 (노출은 빛의 양 비율로 맞춤)
    return {"lin": max(lin, 1e-4), "ls": max(ls, 1e-3), "a": am - 128, "b": bm - 128, "c": max(math.hypot(as_, bs), 1e-3)}


def _clamp(k, v):
    lo, hi = RANGES[k]
    return max(lo, min(hi, v))


def step(params: dict, ref: dict, cur: dict) -> tuple[dict, float]:
    """한 번 움직일 양. 돌려주는 err = 남은 차이 (작을수록 비슷)"""
    p = dict(params)
    ev = math.log2(ref["lin"] / cur["lin"])
    dc = ref["ls"] / cur["ls"] - 1
    ds = ref["c"] / cur["c"] - 1
    db, da = ref["b"] - cur["b"], ref["a"] - cur["a"]
    p["exposure"] = _clamp("exposure", p.get("exposure", 0) + max(-0.6, min(0.6, ev * 0.8)))
    p["contrast"] = _clamp("contrast", p.get("contrast", 0) + max(-15, min(15, dc * 60)))
    p["saturation"] = _clamp("saturation", p.get("saturation", 0) + max(-15, min(15, ds * 60)))
    p["temperature"] = _clamp("temperature", p.get("temperature", 0) + max(-12, min(12, db * 1.5)))
    p["tint"] = _clamp("tint", p.get("tint", 0) + max(-12, min(12, da * 1.5)))
    err = abs(ev) + abs(dc) + abs(ds) + (abs(db) + abs(da)) / 20
    return p, err


def fit(link, ref_bytes: bytes, report) -> None:
    """link = console.UnityLink. report(글자) = 화면에 진행 상황"""
    try:
        ref = stats(ref_bytes)
    except ImportError:
        return report("Pillow가 없어요 → pip install pillow 후 콘솔 다시 켜기")
    except Exception as e:
        return report(f"사진을 못 읽었어요: {e}")
    snap = link.snapshot()
    state = snap.get("state") or {}
    if not snap.get("connected") or not state.get("look"):
        return report("먼저 룩을 하나 고르세요 (색 맞추기는 룩의 세부 슬라이더를 움직여요)")
    params = dict(state.get("lookParams") or {})
    _, fid = link.wait_frame(-1, 0.1)
    for i in range(STEPS):
        jpg, fid = link.wait_frame(fid, 2.0)
        if jpg is None:
            return report("렌더 뷰가 안 와요 (유니티 플레이 중인지 확인)")
        params, err = step(params, ref, stats(jpg))
        link.send({"cmd": "look_params", "look": params})
        report(f"색 맞추는 중 {i + 1}/{STEPS} · 남은 차이 {err:.2f}")
        for _ in range(3):   # 바뀐 값으로 그린 그림이 올 때까지 몇 장 넘김
            _, fid = link.wait_frame(fid, 1.0)
        if err < 0.08:
            break
    report("색 맞추기 끝 · 마음에 들면 [룩 저장], 아니면 슬라이더 ↺")
