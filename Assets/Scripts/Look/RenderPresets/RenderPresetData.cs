using System;

// =============================================================
// 렌더툴(ProductRenderStudio) 프리셋 JSON과 같은 형식 (그쪽 MaterialPresetData·LookPresetData를 그대로 옮김)
// 파일 위치: StreamingAssets/RenderTool/materials/*.json, looks/*.json → 빌드 exe에서도 파일만 넣으면 늘어남
// 로봇팔 장면에서 더 쓰는 값은 "로봇팔 추가" 주석을 단 것뿐 (렌더툴은 모르는 값이라 무시함 → 두 툴이 같은 파일을 씀)
// =============================================================
namespace RenderPresets
{
    // ---------------------------------------------------------------- 재질
    [Serializable]
    public class MaterialPresetData
    {
        public string id;                 // 파일 이름과 같게 (예: "satin_stainless")
        public string displayName;        // 화면에 보일 이름
        public string category;
        public string note;               // 한 줄 설명
        public MaterialValues @base = new MaterialValues();   // JSON에서는 "base"
        public MaterialRanges ranges = new MaterialRanges();
    }

    [Serializable]
    public class MaterialValues
    {
        public float[] color = { 0.8f, 0.8f, 0.8f };   // RGB (감마 값)
        public float metallic;
        public float roughness = 0.5f;    // 거칠기. Standard 셰이더는 매끈함 = 1 - 거칠기
        public string normalMap;          // 디테일 노멀 (예: "textures/detail/detail_polish.png")
        public float normalStrength;
        public float roughnessVariationTiles;   // 렌더툴(glTF 셰이더)만 씀. Standard는 맵 형식이 달라 안 씀
        public float textureScale = 1f;
    }

    [Serializable]
    public class MaterialRanges
    {
        public float[] metallic = new float[0];
        public float[] roughness = new float[0];
        public float[] normalStrength = new float[0];
    }

    // ---------------------------------------------------------------- 룩 (조명 분위기)
    // 위치(m)는 모델 중심 기준이고 referenceSize 크기 모델에 맞춘 값 → (로봇 길이 / referenceSize)를 곱해서 씀
    [Serializable]
    public class LookPresetData
    {
        public string id;
        public string displayName;
        public string note;
        public int order = 10;
        public float referenceSize = 0.3f;
        public float sceneLightScale = 1f;     // 로봇팔 추가: 장면에 원래 있던 조명 밝기 배율 (1 = 그대로)
        public LookEnvironment environment = new LookEnvironment();
        public LookLight[] lights = new LookLight[0];
        public LookSoftbox[] softboxes = new LookSoftbox[0];
        public LookProbe reflectionProbe = new LookProbe();
        public LookPost post = new LookPost();
        // backdrop·camera·contactShadow·output은 렌더툴 촬영용 → 로봇팔 장면에선 안 씀 (공장 룸·데모 카메라를 그대로 둠)
    }

    [Serializable]
    public class LookEnvironment
    {
        public string hdri;               // StreamingAssets/RenderTool 기준
        public float hdriRotation;
        public float ambientIntensity = 1f;
        public float reflectionIntensity = 1f;
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

    // 빛나는 판: 화면엔 안 보이고 금속 반사에만 맺힘
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
        public bool grain;
    }

    [Serializable] public class LookAO { public bool enabled; public float intensity = 1f; public float thickness = 1f; public float[] color = { 0, 0, 0 }; }
    [Serializable] public class LookColorGrading { public string tonemapper = "aces"; public float postExposure; public float contrast; public float saturation; }
    [Serializable] public class LookBloom { public bool enabled; public float threshold = 1f; public float intensity; public float softKnee = 0.5f; }
    [Serializable] public class LookVignette { public bool enabled; public float intensity; public float smoothness = 0.2f; }
}
