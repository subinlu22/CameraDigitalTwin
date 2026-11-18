"""레퍼런스 색 맞추기 — 사진 한 장의 색감에 지금 유니티 화면을 맞춤 (Claude 없이, 이 PC에서만).

흐름 (Reinhard 색 전이를 '조명 세부' 값으로 옮긴 것, 3구간):
  1) 레퍼런스 사진과 지금 렌더 뷰를 같은 방법으로 잼
     - 밝기: 중간값(노출) · 5%~95% 폭(대비) · 아래 5%(검은 끝)
     - 색: 밝기로 세 구간(어두운 곳 · 중간 · 밝은 곳)을 나눠 구간마다 R:G:B 비율
     - 채도: Lab 색 퍼짐
  2) 차이만큼 값을 조금씩 움직임
     노출 · 대비 · 채도 · lift(어두운 곳 색 + 검은 끝) · gamma(중간 색) · gain(밝은 곳 색)
  3) 유니티가 새로 그린 화면을 다시 재고 → 차이가 작아질 때까지 반복 (최대 STEPS번)
그림 내용이 달라도 '분위기 숫자'만 맞추는 방식이라 결과는 근사값. 재질·배경 흐림은 레시피 룩이 맡음
Pillow 필요 (pip install pillow)
"""
import io
import math

STEPS = 8
RANGES = {"exposure": (-2, 2), "contrast": (-50, 50), "saturation": (-60, 60)}
TONE_LIMIT = 0.25          # lift·gamma·gain RGB가 1에서 벗어날 수 있는 최대 폭


def stats(img_bytes: bytes) -> dict:
    from PIL import Image, ImageStat
    im = Image.open(io.BytesIO(img_bytes)).convert("RGB")
    im.thumbnail((256, 256))
    px = list(im.getdata())
    lum = sorted(0.2126 * r + 0.7152 * g + 0.0722 * b for r, g, b in px)
    n = len(lum)
    p = lambda q: lum[min(n - 1, int(q * n))] / 255
    lo_cut, hi_cut = lum[n // 3], lum[2 * n // 3]
    bands = [[0.0, 0.0, 0.0, 0] for _ in range(3)]
    for r, g, b in px:
        y = 0.2126 * r + 0.7152 * g + 0.0722 * b
        k = 0 if y <= lo_cut else 2 if y >= hi_cut else 1
        bands[k][0] += r; bands[k][1] += g; bands[k][2] += b; bands[k][3] += 1
    ratio = []
    for r, g, b, c in bands:   # 구간 색 = 평균 RGB를 그 평균 밝기로 나눔 (1,1,1 = 회색)
        c = max(c, 1)
        avg = max((r + g + b) / (3 * c), 1)
        ratio.append([r / c / avg, g / c / avg, b / c / avg])
    _, as_, bs = ImageStat.Stat(im.convert("LAB")).stddev
    lin = lambda v: v ** 2.2
    return {"mid": max(lin(p(0.5)), 1e-4), "spread": max(p(0.95) - p(0.05), 1e-3), "black": p(0.05),
            "chroma": max(math.hypot(as_, bs), 1e-3), "bands": ratio}


def _clamp(k, v):
    lo, hi = RANGES[k]
    return max(lo, min(hi, v))


def _tone(v, d, w=None):
    v = list(v or [1, 1, 1, 0])
    for i in range(3):
        v[i] = max(1 - TONE_LIMIT, min(1 + TONE_LIMIT, v[i] + d[i]))
    if w is not None:
        v[3] = max(-0.3, min(0.3, v[3] + w))
    return v


def step(params: dict, ref: dict, cur: dict) -> tuple[dict, float]:
    """한 번 움직일 양. 돌려주는 err = 남은 차이 (작을수록 비슷)"""
    p = dict(params)
    ev = math.log2(ref["mid"] / cur["mid"])
    dc = ref["spread"] / cur["spread"] - 1
    ds = ref["chroma"] / cur["chroma"] - 1
    dk = ref["black"] - cur["black"]
    p["exposure"] = _clamp("exposure", p.get("exposure", 0) + max(-0.6, min(0.6, ev * 0.8)))
    p["contrast"] = _clamp("contrast", p.get("contrast", 0) + max(-12, min(12, dc * 50)))
    p["saturation"] = _clamp("saturation", p.get("saturation", 0) + max(-12, min(12, ds * 50)))
    d = [[(ref["bands"][k][i] - cur["bands"][k][i]) * 0.5 for i in range(3)] for k in range(3)]
    p["lift"] = _tone(p.get("lift"), [x * 0.6 for x in d[0]], dk * 0.5)
    p["gamma"] = _tone(p.get("gamma"), d[1])
    p["gain"] = _tone(p.get("gain"), d[2])
    color_err = sum(abs(x) for band in d for x in band)
    err = abs(ev) + abs(dc) + abs(ds) + color_err + abs(dk) * 2
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
        return report("먼저 룩을 하나 고르세요 (색 맞추기는 룩의 세부 값을 움직여요)")
    params = dict(state.get("lookParams") or {})
    _, fid = link.wait_frame(-1, 0.1)
    for i in range(STEPS):
        for _ in range(4):   # 자동 노출이 따라올 시간 + 바뀐 값으로 그린 그림이 올 때까지 몇 장 넘김
            _, fid = link.wait_frame(fid, 1.0)
        jpg, fid = link.wait_frame(fid, 2.0)
        if jpg is None:
            return report("렌더 뷰가 안 와요 (유니티 플레이 중인지 확인)")
        params, err = step(params, ref, stats(jpg))
        link.send({"cmd": "look_params", "look": params})
        report(f"색 맞추는 중 {i + 1}/{STEPS} · 남은 차이 {err:.2f}")
        if err < 0.1:
            break
    report("색 맞추기 끝 · 마음에 들면 [룩 저장], 아니면 [되돌리기]")
