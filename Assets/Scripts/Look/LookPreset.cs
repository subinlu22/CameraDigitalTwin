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
    [Range(0, 2)] public float reflectionIntensity = 1f;

    [Header("안개 (깊이감)")]
    public bool fog = false;
    public Color fogColor = new Color(0.5f, 0.5f, 0.5f);
    public float fogDensity = 0.02f;

    [Header("기존 씬 조명 밝기 배율 (룸 밖 조명만)")]
    [Range(0, 2)] public float sceneLightScale = 1f;

    [Header("데모 카메라 (Main Camera를 이 위치로 옮김)")]
    public bool moveCamera = true;
    public Vector3 cameraPosition;
    public Vector3 cameraEuler;
    public float cameraFov = 35f;

    [Header("재질 바꾸기 (켤 때만, 끄면 원래대로)")]
    public MaterialSwap[] materialSwaps;

    [System.Serializable]
    public struct MaterialSwap
    {
        public string objectName;  // 씬 오브젝트 이름 (예: flow)
        public Material material;
    }
}
