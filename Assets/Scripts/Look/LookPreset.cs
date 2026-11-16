using UnityEngine;
using UnityEngine.Rendering;

// 조명·환경·카메라 프리셋 한 벌 (에셋 파일). LookPresetApplier가 플레이 때 씬에 적용함.
// 예: "실사 공장 룸", 나중에 "흰 배경 스튜디오"
[CreateAssetMenu(menuName = "SO101/Look Preset", fileName = "LookPreset")]
public class LookPreset : ScriptableObject
{
    [Header("환경 (하늘·주변광·반사)")]
    public Material skybox;                       // HDRI 스카이박스
    public AmbientMode ambientMode = AmbientMode.Skybox;
    [Range(0, 3)] public float ambientIntensity = 1f;
    public Color ambientColor = new Color(0.8f, 0.8f, 0.8f);   // ambientMode가 Flat일 때
    [Range(0, 2)] public float reflectionIntensity = 1f;

    [Header("안개 (깊이감)")]
    public bool fog = false;
    public Color fogColor = new Color(0.5f, 0.5f, 0.5f);
    public float fogDensity = 0.02f;

    [Header("기존 씬 조명 밝기 배율 (룸 밖 조명만)")]
    [Range(0, 2)] public float sceneLightScale = 1f;

    [Header("조명별 추가 배율 (이름으로 찾음) 예: 벨트 위 Spot Light만 줄이기")]
    public LightScale[] lightScales;

    [System.Serializable]
    public struct LightScale
    {
        public string lightName;   // 씬 조명 오브젝트 이름 (예: Spot Light)
        [Range(0, 2)] public float scale;
    }

    [Header("배경색 (하늘 대신 단색, 스튜디오용)")]
    public bool solidBackground = false;
    public Color backgroundColor = Color.white;

    [Header("데모 카메라 (Main Camera를 이 위치로 옮김)")]
    public bool moveCamera = true;
    public Vector3 cameraPosition;
    public Vector3 cameraEuler;
    public float cameraFov = 35f;

    [Header("반사 (켤 때만 실시간 반사 프로브를 만들어 금속이 주변을 비추게)")]
    public bool reflectionProbe = false;
    public Vector3 probePosition;
    public Vector3 probeSize = new Vector3(12, 6, 12);
    [Range(0, 2)] public float probeIntensity = 1f;   // 낮추면 테이블 같은 반짝이는 면이 덜 하얗게 뜸

    [Header("숨길 오브젝트 (켤 때만, 끄면 다시 보임) 예: flow 작업대")]
    public string[] hideObjects;

    [Header("재질 바꾸기 (켤 때만, 끄면 원래대로)")]
    public MaterialSwap[] materialSwaps;

    [System.Serializable]
    public struct MaterialSwap
    {
        public string objectName;  // 씬 오브젝트 이름 (예: flow)
        public Material material;
    }

    // 가까이 찍을 때 그림자 정밀하게: 켜면 룩 ON 동안 픽셀 조명 수·그림자 거리·해상도(VeryHigh)를 바꾸고,
    // 조명을 모두 ForcePixel로. 룩 끄면 원래 값으로 돌림
    [Header("정밀 조명 (가까이 촬영용)")]
    public bool precisionLighting;
    [Min(1)] public int pixelLights = 8;
    [Min(0.1f)] public float shadowDistance = 12f;
    // rootName 오브젝트 아래에서 source 재질을 쓰는 칸만 replacement로 잠깐 바꿈 (재질 파일은 안 건드림, 끄면 원복)
    [Header("특정 오브젝트 아래 재질만 바꾸기")]
    public SourceMaterialSwap[] sourceMaterialSwaps;
    [System.Serializable]
    public struct SourceMaterialSwap
    {
        public string rootName;
        public Material source;
        public Material replacement;
    }

    // 여기 적은 오브젝트는 룩 ON 동안 "미리 구운 빛(라이트맵)"을 빼고 실시간 조명만 받음
    // 왜: 밑판에 구운 빛이 깔려 있으면 실시간 그림자가 묻혀서 거의 안 보임 → 빼면 그림자가 또렷해짐 (끄면 원복)
    [Header("구운 빛 빼고 실시간 조명만 받을 오브젝트 (예: 밑판 09_panel)")]
    public string[] realtimeOnlyObjects;
}
