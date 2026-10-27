using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.PostProcessing;

// 프리셋 켜기/끄기 (플레이 중, 빌드 exe에서도 동작)
// 흐름: 켤 때 지금 상태(하늘·주변광·조명 밝기·카메라·재질)를 기억 → 프리셋 값 적용 + 룸 보이기
//       끌 때 기억한 값으로 되돌리고 룸 숨기기.
// L 키: 프리셋 순서대로 넘김 (공장 → 스튜디오 → 끄기 → 공장 ...)
public class LookPresetApplier : MonoBehaviour
{
    [System.Serializable]
    public class Look { public LookPreset preset; public GameObject roomRoot; }
    public Look[] looks;                    // 프리셋 + 그 프리셋의 룸 (씬에는 꺼진 채 저장)

    [Header("지금 적용 중인 것 (looks에서 자동으로 채움)")]
    public LookPreset preset;
    public GameObject roomRoot;
    public PostProcessLayer postLayer;      // Main Camera에 붙은 것 (꺼진 채 저장)
    public bool applyOnStart = true;
    public Key toggleKey = Key.L;

    public bool IsOn { get; private set; }
    public int Index { get; private set; } = -1;  // -1 = 꺼짐

    // 되돌리기용 원래 값
    Material _sky; UnityEngine.Rendering.AmbientMode _ambMode; float _ambInt, _reflInt; Color _ambColor;
    bool _fog; Color _fogColor; float _fogDensity; FogMode _fogMode;
    Vector3 _camPos; Quaternion _camRot; float _camFov; CameraClearFlags _camClear; Color _camBg;
    readonly Dictionary<Light, float> _lights = new Dictionary<Light, float>();
    readonly Dictionary<Renderer, Material[]> _mats = new Dictionary<Renderer, Material[]>();
    readonly List<Renderer> _hidden = new List<Renderer>();
    GameObject _probe;

    void Start()
    {
        if (looks != null) foreach (var l in looks) if (l.roomRoot) l.roomRoot.SetActive(false);
        if (applyOnStart) Select(0);
    }

    // i번 프리셋으로 바꾸기 (-1이면 끄기)
    public void Select(int i)
    {
        if (IsOn) SetOn(false);
        Index = (looks != null && i >= 0 && i < looks.Length) ? i : -1;
        if (Index < 0) return;
        preset = looks[Index].preset; roomRoot = looks[Index].roomRoot;
        SetOn(true);
    }

    public void Next() { Select(Index + 1 < (looks?.Length ?? 0) ? Index + 1 : -1); }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb[toggleKey].wasPressedThisFrame) Next();
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

        _ambColor = RenderSettings.ambientLight;
        RenderSettings.skybox = preset.skybox ? preset.skybox : _sky;
        RenderSettings.ambientLight = preset.ambientColor;
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
            if (preset.lightScales != null)
                foreach (var ls in preset.lightScales)
                    if (ls.lightName == l.name) l.intensity *= ls.scale;
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

        _hidden.Clear();
        if (preset.hideObjects != null)
            foreach (var n in preset.hideObjects)
            {
                var go = GameObject.Find(n);
                if (go == null) continue;
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                    if (r.enabled) { r.enabled = false; _hidden.Add(r); }
            }

        var cam = Camera.main;
        if (cam)
        {
            _camPos = cam.transform.position; _camRot = cam.transform.rotation; _camFov = cam.fieldOfView;
            _camClear = cam.clearFlags; _camBg = cam.backgroundColor;
            if (preset.solidBackground) { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = preset.backgroundColor; }
            if (preset.moveCamera)
            {
                cam.transform.SetPositionAndRotation(preset.cameraPosition, Quaternion.Euler(preset.cameraEuler));
                cam.fieldOfView = preset.cameraFov;
            }
        }

        if (roomRoot) roomRoot.SetActive(true);
        if (postLayer) postLayer.enabled = true;

        // 반사 프로브는 씬에 저장하지 않고 켤 때만 만듦 (라이트맵 굽기와 안 섞이게)
        if (preset.reflectionProbe)
        {
            _probe = new GameObject("_LookReflectionProbe") { hideFlags = HideFlags.DontSave };
            _probe.transform.position = preset.probePosition;
            var rp = _probe.AddComponent<ReflectionProbe>();
            rp.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
            rp.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
            rp.timeSlicingMode = UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.NoTimeSlicing;
            rp.size = preset.probeSize; rp.resolution = 256; rp.hdr = true; rp.importance = 10;
            rp.intensity = preset.probeIntensity;
            rp.RenderProbe();
        }
    }

    void Restore()
    {
        RenderSettings.skybox = _sky; RenderSettings.ambientMode = _ambMode; RenderSettings.ambientLight = _ambColor;
        RenderSettings.ambientIntensity = _ambInt; RenderSettings.reflectionIntensity = _reflInt;
        RenderSettings.fog = _fog; RenderSettings.fogColor = _fogColor;
        RenderSettings.fogDensity = _fogDensity; RenderSettings.fogMode = _fogMode;
        DynamicGI.UpdateEnvironment();

        foreach (var kv in _lights) if (kv.Key) kv.Key.intensity = kv.Value;
        foreach (var kv in _mats) if (kv.Key) kv.Key.sharedMaterials = kv.Value;
        foreach (var r in _hidden) if (r) r.enabled = true;

        var cam = Camera.main;
        if (cam) { cam.clearFlags = _camClear; cam.backgroundColor = _camBg; }
        if (cam && preset.moveCamera)
        {
            cam.transform.SetPositionAndRotation(_camPos, _camRot);
            cam.fieldOfView = _camFov;
        }

        if (roomRoot) roomRoot.SetActive(false);
        if (postLayer) postLayer.enabled = false;
        if (_probe) { if (Application.isPlaying) Destroy(_probe); else DestroyImmediate(_probe); _probe = null; }
    }
}
