"""Unity Factory(에셋 스토어 무료, HDRP 전용)에서 배경 부품만 꺼내 가볍게 넣기.

흐름:
1. 에셋 스토어 캐시의 Unity Factory.unitypackage(약 900MB)를 연다 (압축 tar)
2. Background의 모델·프리팹·재질·텍스처만 고른다 (씬·라이트맵·셰이더·HDRP 설정·작업자·카트·왜건은 뺌)
3. 4K TGA 텍스처는 PNG로 줄인다 (바닥·벽·천장 2048, 나머지 1024). .meta(GUID)는 그대로 → 재질 연결 유지
4. Assets/ThirdParty/UnityFactory/ 에 저장 (git 제외, 라이선스상 재배포 금지)
그다음 유니티 메뉴 SO101 > Unity Factory 재질 변환 (HDRP → Built-in Standard)

준비: 에셋 스토어에서 Unity Factory를 내 에셋에 추가 → Package Manager > My Assets > Download
실행: python Tools/import_unity_factory.py
"""
import io
import os
import sys
import tarfile

from PIL import Image

PKG = os.path.join(os.environ.get("APPDATA", ""), "Unity", "Asset Store-5.x", "Unity Technologies Japan",
                   "3D ModelsEnvironmentsIndustrial", "Unity Factory.unitypackage")
SRC_PREFIX = "Assets/UnityFactorySceneHDRP/Scene_Factory/Background/"
DST = os.path.join(os.path.dirname(__file__), "..", "Assets", "ThirdParty", "UnityFactory")
KEEP_DIRS = ("Models/", "Prefabs/", "Materials/", "Textures/")
SKIP_WORDS = ("Wagon", "Cart")          # 카메라에 안 쓰는 큰 소품
BIG = ("Floor", "Wall", "Ceiling")      # 2048로 남길 텍스처


def main():
    if not os.path.exists(PKG):
        sys.exit("패키지 없음: " + PKG + "\nPackage Manager > My Assets 에서 Unity Factory를 Download 하세요")
    tar = tarfile.open(PKG, "r:gz")
    entries = {}  # guid -> {name: TarInfo}
    for m in tar.getmembers():
        parts = m.name.lstrip("./").split("/")
        if len(parts) == 2:
            entries.setdefault(parts[0], {})[parts[1]] = m
    total = 0
    for guid, files in entries.items():
        if "pathname" not in files or "asset" not in files:
            continue
        path = tar.extractfile(files["pathname"]).read().decode("utf-8").splitlines()[0]
        if not path.startswith(SRC_PREFIX):
            continue
        rel = path[len(SRC_PREFIX):]
        if not rel.startswith(KEEP_DIRS) or any(w in rel for w in SKIP_WORDS):
            continue
        data = tar.extractfile(files["asset"]).read()
        meta = tar.extractfile(files["asset.meta"]).read() if "asset.meta" in files else None
        out = os.path.normpath(os.path.join(DST, rel))
        if out.lower().endswith(".tga"):  # 4K TGA → 작은 PNG
            img = Image.open(io.BytesIO(data))
            size = 2048 if any(b in rel for b in BIG) else 1024
            img.thumbnail((size, size), Image.LANCZOS)
            out = out[:-4] + ".png"
            buf = io.BytesIO()
            img.save(buf, "PNG", optimize=True)
            data = buf.getvalue()
        os.makedirs(os.path.dirname(out), exist_ok=True)
        with open(out, "wb") as f:
            f.write(data)
        if meta:
            with open(out + ".meta", "wb") as f:
                f.write(meta)
        total += len(data)
    print("넣은 용량", round(total / 1e6, 1), "MB →", os.path.normpath(DST))


if __name__ == "__main__":
    main()
