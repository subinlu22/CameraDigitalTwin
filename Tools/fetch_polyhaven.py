"""Poly Haven(CC0) 에셋 받기 — 실사 공장 룸 프리셋용.

받은 파일은 git에 안 올림(.gitignore). 다른 PC에서는 이 스크립트를 한 번 실행:
    python Tools/fetch_polyhaven.py
흐름: Poly Haven API에서 파일 주소를 물어봄 → Assets/ThirdParty/PolyHaven/ 아래에 저장
(이미 있으면 건너뜀).
"""
import json
import os
import urllib.request

ROOT = os.path.join(os.path.dirname(__file__), "..", "Assets", "ThirdParty", "PolyHaven")
UA = {"User-Agent": "so101-sim/1.0"}  # User-Agent 없으면 403

HDRI = [("empty_warehouse_01", "1k"), ("meadow_2", "2k")]  # meadow_2 = 공장 창밖 자연 풍경
TEXTURES = [  # (이름, 해상도) — 카메라에 보이는 면만
    ("concrete_wall_008", "2k"),      # 뒤쪽 벽
    ("hangar_concrete_floor", "1k"),  # 바닥 (조금만 보임)
]
MODELS = ["worn_metal_rack", "wooden_crate_01", "cardboard_box_01", "barrel_03"]
MAPS = ["Diffuse", "nor_gl", "Rough"]


def api(asset):
    req = urllib.request.Request("https://api.polyhaven.com/files/" + asset, headers=UA)
    return json.load(urllib.request.urlopen(req))


def save(url, folder):
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, url.split("/")[-1])
    if os.path.exists(path):
        return 0
    req = urllib.request.Request(url, headers=UA)
    data = urllib.request.urlopen(req).read()
    with open(path, "wb") as f:
        f.write(data)
    print("  ", os.path.relpath(path, ROOT), round(len(data) / 1e6, 2), "MB")
    return len(data)


def main():
    total = 0
    for name, res in HDRI:
        total += save(api(name)["hdri"][res]["hdr"]["url"], os.path.join(ROOT, "HDRI"))
    for name, res in TEXTURES:
        files = api(name)
        for m in MAPS:
            total += save(files[m][res]["jpg"]["url"], os.path.join(ROOT, "Textures", name))
    for name in MODELS:
        files = api(name)
        folder = os.path.join(ROOT, "Models", name)
        total += save(files["fbx"]["1k"]["fbx"]["url"], folder)
        for m in MAPS:  # 모델 텍스처도 jpg로 (exr보다 작음)
            total += save(files[m]["1k"]["jpg"]["url"], folder)
    print("받은 용량", round(total / 1e6, 1), "MB")


if __name__ == "__main__":
    main()
