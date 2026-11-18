using System;

// =============================================================
// 렌더 룩 프리셋 JSON 형식 (ProductRenderStudio 렌더툴과 같은 형식 → 두 툴이 같은 파일을 씀)
// 파일 위치: StreamingAssets/RenderTool/
//   materials/*.json  재질 (색·금속·거칠기·결)
//   looks/*.json      조명 분위기 (HDRI·추가 조명·소프트박스·반사·후처리)
//   parts/<씬 이름>.json  (선택) 부위 이름표: 재질 이름 여러 개를 "로봇 몸체"처럼 보기 좋은 이름으로 묶음
// 렌더툴이 모르는 값(예: sceneLightScale)은 렌더툴이 무시하므로 같은 파일을 그대로 써도 됨
// =============================================================
namespace RenderLook
{
    // ---------------------------------------------------------------- 재질
    [Serializable]
    public class MaterialPresetData
    {
        public string id;                 // 파일 이름과 같게 (예: "satin_stainless")
        public string displayName;
        public string category;
        public string note;
        public string recipe;
        public string mapping = "uv";
        public MaterialValues @base = new MaterialValues();   // JSON에서는 "base"
        public MaterialRanges ranges = new MaterialRanges();
        public string[] matchNames = new string[0];
        public string thumbnail;
    }

    [Serializable]
    public class MaterialValues
    {
        public float[] color = { 0.8f, 0.8f, 0.8f };   // RGB (감마 값)
        public float metallic;
        public float roughness = 0.5f;    // Standard 셰이더 매끈함 = 1 - 거칠기
        public string normalMap;          // 디테일 노멀 (예: "textures/detail/detail_polish.png")
        public float normalStrength;
        public float roughnessVariationTiles;   // 렌더툴(glTF)만 씀
        public float textureScale = 1f;
        // 실사 스캔 재질 (콘솔 [실사 라이브러리]로 받은 것, 없으면 비움). 경로는 RenderTool 폴더 기준
        public string albedoMap;          // 색 사진
        public string surfaceNormalMap;   // 표면 노멀 (디테일 노멀 normalMap과 따로)
        public string metalGlossMap;      // R = 금속, A = 매끈함 (예전 형식. 있으면 금속성 슬라이더가 안 먹음)
        public bool albedoAlphaIsGloss;   // 색 사진의 A = 매끈함 (콘솔이 거칠기 맵을 넣음) → 금속성은 슬라이더 값
    }

    [Serializable]
    public class MaterialRanges
    {
        public float[] metallic = new float[0];
        public float[] roughness = new float[0];
        public float[] normalStrength = new float[0];
    }

    // ---------------------------------------------------------------- 룩 (조명 분위기)
    // 위치(m)는 초점 중심 기준이고 referenceSize 크기에 맞춘 값 → (초점 물체 길이 / referenceSize)를 곱해서 씀
    [Serializable]
    public class LookPresetData
    {
        public string id;
        public string displayName;
        public string note;
        public int order = 10;
        public float referenceSize = 0.3f;
        public bool cleanSlate = true;         // 빈 무대에서 시작: 장면의 원래 조명·구운 조명·후처리·반사 프로브·반사판을 끄고 이 룩만으로 다시 조명
        public float sceneLightScale = 1f;     // 콘솔 추가: 장면에 원래 있던 조명 밝기 배율 (빈 무대면 0에서 시작)
        public float nearLightScale = -1f;     // 초점 근처(NearRadius m 안) 원래 조명 배율 (−1 = sceneLightScale과 같게). 0 = 끔 → 레시피 조명만 비춤
        public string[] hideObjects = new string[0];   // 이 룩일 때 숨길 장면 물체 이름 (예: 작업대 위 반사판 TopReflection)
        public LookEnvironment environment = new LookEnvironment();
        public LookLight[] lights = new LookLight[0];
        public LookSoftbox[] softboxes = new LookSoftbox[0];
        public LookProbe reflectionProbe = new LookProbe();
        public LookPost post = new LookPost();
        public LookCamera camera = new LookCamera();   // 렌더툴 촬영용. 콘솔은 배경 흐림(depthOfField)의 초점 거리·조리개로 씀
        public PartRecipe[] parts = new PartRecipe[0]; // 레시피: 이 룩을 고르면 부위마다 재질도 같이 입힘 (레퍼런스 한 장 = 룩 하나)
        public string thumbnail;
    }

    [Serializable]
    public class LookEnvironment
    {
        public string hdri;
        public float hdriRotation;
        public float ambientIntensity = 1f;
        public float reflectionIntensity = 1f;
        public bool showHdriAsBackground;
        public float[] backgroundColor = { 0.12f, 0.12f, 0.12f };
    }

    [Serializable]
    public class LookLight
    {
        public string name;
        public string type = "spot";      // "spot" | "point" | "directional"
        public float[] color = { 1, 1, 1 };
        public float intensity = 1f;
        public float[] position = { 0, 1, 0 };
        public bool aimAtCenter = true;
        public float[] euler = { 50, -30, 0 };
        public float range = 5f;
        public float spotAngle = 40f;
        public bool shadows;
    }

    [Serializable]
    public class LookSoftbox
    {
        public string name;
        public float[] position = { 0, 1, 0 };
        public float[] size = { 0.7f, 0.7f };
        public float[] emission = { 1, 1, 1 };
    }

    [Serializable]
    public class LookProbe
    {
        public bool enabled = true;
        public float[] size = { 3, 2, 2 };
        public float intensity = 1f;
        public int resolution = 512;
    }

    [Serializable]
    public class LookPost
    {
        public LookAO ambientOcclusion = new LookAO();
        public LookColorGrading colorGrading = new LookColorGrading();
        public LookBloom bloom = new LookBloom();
        public LookVignette vignette = new LookVignette();
        public bool depthOfField;
        public bool grain;
        public string antialiasing = "smaa";
        public bool autoExposure;          // 자동 노출 (기본 끔: 켜면 화면이 바뀔 때마다 밝기가 크게 출렁여서 고정 노출이 더 예측 가능)
        public float exposureKey = 0.8f;   // 그 기준 (1 = 18% 회색, 낮추면 전체가 어두워짐)
    }

    [Serializable] public class LookAO { public bool enabled; public float intensity = 1f; public float thickness = 1f; public float[] color = { 0, 0, 0 }; }
    [Serializable] public class LookColorGrading { public string tonemapper = "aces"; public float postExposure; public float contrast; public float saturation; public float temperature; public float tint;
        public float[] lift, gamma, gain; }   // 어두운 곳·중간·밝은 곳 색 (RGB + 밝기). 비우면 기본 (1,1,1,0)
    [Serializable] public class LookBloom { public bool enabled; public float threshold = 1f; public float intensity; public float softKnee = 0.5f; }
    [Serializable] public class LookVignette { public bool enabled; public float intensity; public float smoothness = 0.2f; }
    [Serializable] public class LookCamera { public float focalLength = 50f; public float aperture = 32f; public LookAngle[] angles = { new LookAngle() }; }
    [Serializable] public class LookAngle { public string name = "front"; public float[] direction = { 0, 0.3f, 1 }; public float distance = 1.2f; }

    // 레시피 한 줄: 부위 id + 재질 프리셋 + 값 (값을 비우면 프리셋 값)
    [Serializable]
    public class PartRecipe
    {
        public string part, material, mode = "replace";
        public float metallic = -1, roughness = -1, normalStrength = -1, tiling = -1;
        public float[] color;
    }

    // ---------------------------------------------------------------- 부위 이름표 (선택)
    // 없어도 됨: 그러면 장면에 쓰인 재질 이름이 그대로 부위가 됨
    [Serializable]
    public class PartsFile
    {
        public string focus;              // 조명을 이 물체 중심·크기에 맞춤 (비우면 이름표 부위 전체)
        public PartLabel[] parts = new PartLabel[0];
    }

    [Serializable]
    public class PartLabel
    {
        public string id;                 // 영문 (예: "robot_body")
        public string label;              // 화면 이름 (예: "로봇 몸체")
        public string root;               // 이 오브젝트 아래만 (비우면 장면 전체)
        public string[] materials = new string[0];   // 묶을 원래 재질 이름들
    }
}
