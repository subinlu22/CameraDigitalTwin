"""실사 라이브러리 — Poly Haven HDRI · ambientCG 스캔 재질을 받아 렌더 룩 프리셋(JSON)으로 만듦.

둘 다 CC0 (무료, 출처 표시 의무 없음). 받은 파일은 이 PC에만 둠 (.gitignore)
흐름:
  목록  : 사이트 API에서 이름·썸네일 주소만 받음 (한 번 받으면 1시간 기억)
  받기  : HDRI   → hdri/<id>_2k.hdr + looks/lib_hdri_<id>.json (조명은 HDRI만, 후처리 기본)
          재질   → 1K zip을 메모리에서 열어 색·노멀·금속·거칠기 사진만 꺼냄
                   → textures/library/<id>/ + materials/lib_<id>.json
                   거칠기는 색 사진의 A(알파)에 매끈함으로 넣음 → 금속성은 슬라이더로 조절 (Pillow 필요)
                   금속 맵은 평균값만 재서 금속성 기본값으로 씀
경로는 모두 유니티가 알려 준 RenderTool 폴더(StreamingAssets/RenderTool) 기준
"""
import io
import json
import time
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path

UA = {"User-Agent": "render-look-console/1.0"}   # User-Agent 없으면 403
HDRI_RES = "2k"
MAT_RES = "1K-JPG"
LIST_TTL = 3600
_cache: dict = {}


def _get(url: str, timeout: float = 30):
    return urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=timeout).read()


def _json(url: str):
    return json.loads(_get(url))


def _cached(key, make):
    hit = _cache.get(key)
    if hit and time.time() - hit[0] < LIST_TTL:
        return hit[1]
    val = make()
    _cache[key] = (time.time(), val)
    return val


# ---------------------------------------------------------------- 목록
def list_hdris():
    def make():
        data = _json("https://api.polyhaven.com/assets?t=hdris")
        items = [{"id": k, "name": v.get("name", k), "cats": v.get("categories", []),
                  "thumb": f"https://cdn.polyhaven.com/asset_img/thumbs/{k}.png?width=320&height=160",
                  "n": v.get("download_count", 0)} for k, v in data.items()]
        items.sort(key=lambda x: -x["n"])
        return items
    return _cached("hdri", make)


def list_materials(q: str = "", offset: int = 0):
    def make():
        qs = urllib.parse.urlencode({"type": "Material", "limit": 60, "offset": offset, "q": q,
                                     "include": "imageData", "sort": "Popular"})
        data = _json("https://ambientcg.com/api/v2/full_json?" + qs)
        return {"total": data.get("numberOfResults", 0),
                "items": [{"id": a["assetId"], "name": a.get("displayName", a["assetId"]),
                           "cats": [a.get("displayCategory") or a.get("category") or ""],
                           "thumb": (a.get("previewImage") or {}).get("256-PNG", "")}
                          for a in data.get("foundAssets", [])]}
    return _cached(("mat", q, offset), make)


# ---------------------------------------------------------------- 받기
def _write(path: Path, data: bytes):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def _safe_id(s: str) -> str:
    return "".join(c for c in s if c.isalnum() or c in "_-")


def fetch_hdri(root: Path, hid: str, report) -> str:
    hid = _safe_id(hid)
    info = next((h for h in list_hdris() if h["id"] == hid), {"name": hid})
    rel = f"hdri/{hid}_{HDRI_RES}.hdr"
    if not (root / rel).exists():
        report(f"HDRI 받는 중: {info['name']} ({HDRI_RES})")
        url = _json("https://api.polyhaven.com/files/" + hid)["hdri"][HDRI_RES]["hdr"]["url"]
        _write(root / rel, _get(url, 120))
    thumb = f"thumbnails/library/hdri_{hid}.png"
    if not (root / thumb).exists():
        _write(root / thumb, _get(f"https://cdn.polyhaven.com/asset_img/thumbs/{hid}.png?width=512&height=256"))
    look_id = "lib_hdri_" + hid
    look = {
        "id": look_id, "displayName": info["name"], "order": 50, "referenceSize": 0.46, "sceneLightScale": 0.6,
        "note": "Poly Haven 실사 HDRI (CC0) · 조명은 HDRI만. 세부 슬라이더로 다듬기",
        "environment": {"hdri": rel, "hdriRotation": 0, "ambientIntensity": 1, "reflectionIntensity": 1},
        "lights": [], "softboxes": [],
        "reflectionProbe": {"enabled": True, "size": [2.4, 1.6, 2.4], "intensity": 1, "resolution": 256},
        "post": {
            "ambientOcclusion": {"enabled": True, "intensity": 0.8, "thickness": 1, "color": [0, 0, 0]},
            "colorGrading": {"tonemapper": "aces", "postExposure": 0, "contrast": 0, "saturation": 0, "temperature": 0, "tint": 0},
            "bloom": {"enabled": False, "threshold": 1.1, "intensity": 0, "softKnee": 0.5},
            "vignette": {"enabled": True, "intensity": 0.12, "smoothness": 0.4}, "grain": False,
        },
        "thumbnail": thumb,
    }
    _write(root / "looks" / f"{look_id}.json", json.dumps(look, ensure_ascii=False, indent=2).encode("utf-8"))
    return look_id


MAPS = {"_Color.jpg": "color.jpg", "_NormalGL.jpg": "normal.jpg", "_Roughness.jpg": "rough.jpg", "_Metalness.jpg": "metal.jpg"}


def fetch_material(root: Path, mid: str, report) -> str:
    mid = _safe_id(mid)
    folder = root / "textures" / "library" / mid
    a = _json("https://ambientcg.com/api/v2/full_json?" + urllib.parse.urlencode(
        {"id": mid, "include": "downloadData,imageData"}))["foundAssets"][0]
    name = a.get("displayName", mid)
    if not (folder / "color.jpg").exists():
        dl = a["downloadFolders"]["default"]["downloadFiletypeCategories"]["zip"]["downloads"]
        link = next(d["downloadLink"] for d in dl if d["attribute"] == MAT_RES)
        report(f"재질 받는 중: {name} ({MAT_RES})")
        with zipfile.ZipFile(io.BytesIO(_get(link, 180))) as z:
            for member in z.namelist():   # 알려진 사진만 정해진 이름으로 꺼냄 (zip 안 경로는 안 씀)
                for suffix, out in MAPS.items():
                    if member.endswith(suffix):
                        _write(folder / out, z.read(member))
    base = {"color": [1, 1, 1], "metallic": 0, "roughness": 0.5, "normalStrength": 1, "textureScale": 1,
            "albedoMap": f"textures/library/{mid}/color.jpg"}
    if (folder / "normal.jpg").exists():
        base["surfaceNormalMap"] = f"textures/library/{mid}/normal.jpg"
    packed = _pack_color_gloss(folder)
    if packed:
        base["albedoMap"], base["albedoAlphaIsGloss"] = f"textures/library/{mid}/{packed}", True
    base["metallic"] = _mean_metal(folder)
    thumb = f"thumbnails/library/mat_{mid}.png"
    pv = (a.get("previewImage") or {}).get("256-PNG")
    if pv and not (root / thumb).exists():
        _write(root / thumb, _get(pv))
    mat_id = "lib_" + mid
    mat = {"id": mat_id, "displayName": name, "category": "scan",
           "note": "ambientCG 실사 스캔 (CC0) · 거칠기 0.5 = 사진 그대로, 결 크기 = 사진 반복",
           "recipe": "", "mapping": "uv", "base": base, "ranges": {}, "matchNames": [], "thumbnail": thumb}
    _write(root / "materials" / f"{mat_id}.json", json.dumps(mat, ensure_ascii=False, indent=2).encode("utf-8"))
    return mat_id


def _pack_color_gloss(folder: Path):
    """색(RGB) + 매끈함(A = 1 − 거칠기) 한 장. Pillow가 없거나 거칠기 맵이 없으면 None (그러면 거칠기 슬라이더 값만 씀)"""
    try:
        from PIL import Image, ImageChops
    except ImportError:
        return None
    if not (folder / "rough.jpg").exists():
        return None
    color = Image.open(folder / "color.jpg").convert("RGB")
    gloss = ImageChops.invert(Image.open(folder / "rough.jpg").convert("L").resize(color.size))
    color.putalpha(gloss)
    color.save(folder / "color_gloss.png")
    return "color_gloss.png"


def _mean_metal(folder: Path) -> float:
    try:
        from PIL import Image, ImageStat
        return round(ImageStat.Stat(Image.open(folder / "metal.jpg").convert("L")).mean[0] / 255, 2)
    except Exception:   # 금속 맵 없음 = 금속 아님
        return 0.0
