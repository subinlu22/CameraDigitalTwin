using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.PostProcessing;

// 프리셋 켜기/끄기 (플레이 중, 빌드 exe에서도 동작)
// 흐름: 켤 때 지금 상태(하늘·주변광·조명 밝기·카메라·재질)를 기억 → 프리셋 값 적용 + 룸 보이기
//       끌 때 기억한 값으로 되돌리고 룸 숨기기. L 키로 토글.
public class LookPresetApplier : MonoBehaviour
{
    public LookPreset preset;
    public GameObject roomRoot;             // FactoryRoom (씬에는 꺼진 채 저장)
    public PostProcessLayer postLayer;      // Main Camera에 붙은 것 (꺼진 채 저장)
    public bool applyOnStart = true;
    public Key toggleKey = Key.L;

    public bool IsOn { get; private set; }

    // 되돌리기용 원래 값
    Material _sky; UnityEngine.Rendering.AmbientMode _ambMode; float _ambInt, _reflInt;
    bool _fog; Color _fogColor; float _fogDensity; FogMode _fogMode;
    Vector3 _camPos; Quaternion _camRot; float _camFov;
    readonly Dictionary<Light, float> _lights = new Dictionary<Light, float>();
    readonly Dictionary<Renderer, Material[]> _mats = new Dictionary<Renderer, Material[]>();

    void Start()
    {
        if (roomRoot) roomRoot.SetActive(false);
        if (applyOnStart) SetOn(true);
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb[toggleKey].wasPressedThisFrame) SetOn(!IsOn);
    }

    void OnDisable() { if (IsOn) SetOn(false); }

    public void SetOn(bool on)
    {
        if (preset == null || on == IsOn) return;
        if (on) Apply(); else Restore();
        IsOn = on;
    }

    void Apply()
    {
        _sky = RenderSettings.skybox; _ambMode = RenderSettings.ambientMode;
        _ambInt = RenderSettings.ambientIntensity; _reflInt = RenderSettings.reflectionIntensity;
        _fog = RenderSettings.fog; _fogColor = RenderSettings.fogColor;
        _fogDensity = RenderSettings.fogDensity; _fogMode = RenderSettings.fogMode;

        if (preset.skybox) RenderSettings.skybox = preset.skybox;
        RenderSettings.ambientMode = preset.ambientMode;
        RenderSettings.ambientIntensity = preset.ambientIntensity;
        RenderSettings.reflectionIntensity = preset.reflectionIntensity;
        RenderSettings.fog = preset.fog;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = preset.fogColor;
        RenderSettings.fogDensity = preset.fogDensity;
        DynamicGI.UpdateEnvironment();  // 주변광을 새 하늘로 다시 계산

        // 룸 밖 조명만 배율 적용 (룸 안 조명은 프리셋 전용)
        _lights.Clear();
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (roomRoot && l.transform.IsChildOf(roomRoot.transform)) continue;
            _lights[l] = l.intensity;
            l.intensity *= preset.sceneLightScale;
        }

        _mats.Clear();
        if (preset.materialSwaps != null)
            foreach (var s in preset.materialSwaps)
            {
                var go = GameObject.Find(s.objectName);
                if (go == null || s.material == null) continue;
                var r = go.GetComponent<Renderer>();
                if (r == null) continue;
                _mats[r] = r.sharedMaterials;
                var arr = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < arr.Length; i++) arr[i] = s.material;
                r.sharedMaterials = arr;
            }

        var cam = Camera.main;
        if (cam)
        {
            _camPos = cam.transform.position; _camRot = cam.transform.rotation; _camFov = cam.fieldOfView;
            if (preset.moveCamera)
            {
                cam.transform.SetPositionAndRotation(preset.cameraPosition, Quaternion.Euler(preset.cameraEuler));
                cam.fieldOfView = preset.cameraFov;
            }
        }

        if (roomRoot) roomRoot.SetActive(true);
        if (postLayer) postLayer.enabled = true;
    }

    void Restore()
    {
        RenderSettings.skybox = _sky; RenderSettings.ambientMode = _ambMode;
        RenderSettings.ambientIntensity = _ambInt; RenderSettings.reflectionIntensity = _reflInt;
        RenderSettings.fog = _fog; RenderSettings.fogColor = _fogColor;
        RenderSettings.fogDensity = _fogDensity; RenderSettings.fogMode = _fogMode;
        DynamicGI.UpdateEnvironment();

        foreach (var kv in _lights) if (kv.Key) kv.Key.intensity = kv.Value;
        foreach (var kv in _mats) if (kv.Key) kv.Key.sharedMaterials = kv.Value;

        var cam = Camera.main;
        if (cam && preset.moveCamera)
        {
            cam.transform.SetPositionAndRotation(_camPos, _camRot);
            cam.fieldOfView = _camFov;
        }

        if (roomRoot) roomRoot.SetActive(false);
        if (postLayer) postLayer.enabled = false;
    }
}
