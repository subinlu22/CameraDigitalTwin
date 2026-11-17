using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using RenderPresets;

// =============================================================
// [F6 룩 프리셋 창] 렌더툴(ProductRenderStudio) 프리셋을 플레이 중에 바로 입혀 보기
//
// 왜: 재질·조명을 매번 손(또는 AI)으로 맞추지 않고, 검증된 프리셋을 골라 비교만 하면 됨
// 흐름: StreamingAssets/RenderTool의 JSON을 읽음 → 창에서 ◀ ▶로 고름 → 바로 적용
//   ① 조명 분위기(룩): HDRI 환경광·반사 + 추가 조명 + 반사에만 보이는 소프트박스 + 후처리. 공장 룸·카메라는 그대로
//   ② 재질: 부위(로봇 몸체·서보·벨트 프레임·벨트 천·작업대)마다 재질 프리셋을 복제본으로 입힘 (원본 재질 파일은 안 바꿈)
//      방식 두 가지: 전부 바꾸기(프리셋 색·질감) / 텍스처 유지·수치만(원래 색·텍스처에 금속·매끈함·결만)
//      조명만 바꾸고 싶으면 재질은 '원래 재질'로 두면 됨
//   ③ [모두 원래대로] = 바꾸기 전 상태로
// L 키로 기존 룩(공장/스튜디오)을 바꿔도 고른 프리셋은 그 위에 다시 입힘
// 씬에 안 넣어도 됨: 플레이를 시작하면 자동으로 하나 만들어짐 (빌드 exe도 같음)
// =============================================================
public class RenderPresetPanel : MonoBehaviour
{
    // 바뀌면 안 되는 값은 상수 (씬에 저장된 public 값이 코드 기본값을 덮는 일이 없게)
    const Key ToggleKey = Key.F6;
    const string RobotRoot = "so101_new_calib_camera";
    const string SoftboxLayerHint = "반사에만 보이는 소프트박스";

    // 재질을 입힐 부위: 화면 이름, 찾을 부모 오브젝트 이름, 바꿀 원래 재질 이름들
    struct Part { public string label, root; public string[] materials; }
    static readonly Part[] Parts =
    {
        new Part { label = "로봇 몸체",   root = RobotRoot,     materials = new[] { "3d_printed", "RobotYellow_Photo", "Robot_Yellow_Satin" } },
        new Part { label = "로봇 서보",   root = RobotRoot,     materials = new[] { "sts3215" } },
        new Part { label = "벨트 프레임", root = "cam_s_merge", materials = new[] { "ConveyorFrame_Photo" } },
        new Part { label = "벨트 천",     root = "cam_s_merge", materials = new[] { "Belt_Cloth", "ConveyorBelt_Photo" } },
        new Part { label = "작업대",      root = "Line_09",     materials = new[] { "Workbench_Photo" } },
    };

    bool open;
    List<LookPresetData> looks = new List<LookPresetData>();
    List<MaterialPresetData> mats = new List<MaterialPresetData>();
    int lookIndex = -1;                              // -1 = 끔
    readonly int[] partChoice = { -1, -1, -1, -1, -1 };   // 부위별 재질 번호 (-1 = 원래 재질)
    // 재질 적용 방식: false = 전부 바꾸기 (프리셋 색·질감으로 교체, 사진 텍스처 뺌)
    //                true  = 텍스처 유지 · 수치만 (원래 색·텍스처 맵은 그대로, 금속·매끈함·결만 프리셋 값으로)
    bool keepTextures;
    readonly bool[] partFound = new bool[5];
    string status = "";
    float statusUntil;

    LookPresetApplier applier;
    LookQualitySwitch quality;
    int lastApplierIndex = int.MinValue;

    // ---------- 재질: 복제본 ↔ 원본
    readonly Dictionary<(Material, string), Material> clones = new Dictionary<(Material, string), Material>();
    readonly Dictionary<Material, Material> sourceOf = new Dictionary<Material, Material>();
    readonly HashSet<Renderer> touched = new HashSet<Renderer>();

    // ---------- 룩: 되돌리기용 (값, 내가 넣은 값) — 내가 넣은 값이 그대로일 때만 되돌림 (L 키가 바꾼 건 건드리지 않음)
    GameObject rig; Material sky; PostProcessProfile profile;
    Material savedSky; AmbientMode savedAmbMode; float savedAmbInt, savedReflInt;
    float setAmbInt, setReflInt;
    readonly Dictionary<Light, (float original, float set)> savedLights = new Dictionary<Light, (float, float)>();
    PostProcessLayer postLayer; bool savedPostOn, postTurnedOn;
    Camera cam; int savedCull, setCull; int softboxLayer = -1;

    GUIStyle title, label, small, button, nameBox;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoCreate()
    {
        if (FindFirstObjectByType<RenderPresetPanel>()) return;
        new GameObject("RenderPresetPanel").AddComponent<RenderPresetPanel>();
    }

    void Start()
    {
        looks = RenderPresetFiles.LoadLooks();
        mats = RenderPresetFiles.LoadMaterials();
        applier = FindFirstObjectByType<LookPresetApplier>();
        quality = FindFirstObjectByType<LookQualitySwitch>();
        lastApplierIndex = applier ? applier.Index : int.MinValue;
        Debug.Log($"[렌더 프리셋] 룩 {looks.Count}개, 재질 {mats.Count}개 읽음 (F6 창)");
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb[ToggleKey].wasPressedThisFrame) open = !open;
    }

    void LateUpdate()
    {
        // L 키로 기존 룩이 바뀜 → 고른 룩·재질을 새 상태 위에 다시 입힘
        int idx = applier ? applier.Index : int.MinValue;
        if (idx == lastApplierIndex) return;
        lastApplierIndex = idx;
        if (lookIndex >= 0) { RemoveLook(); ApplyLook(looks[lookIndex]); }
        RefreshMaterials();
    }

    void OnDisable() { ResetAll(); }

    // ================================================================ 재질
    void RefreshMaterials()
    {
        for (int p = 0; p < Parts.Length; p++) partFound[p] = false;
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var arr = r.sharedMaterials;
            bool changed = false;
            for (int s = 0; s < arr.Length; s++)
            {
                var cur = arr[s];
                if (!cur) continue;
                var src = sourceOf.TryGetValue(cur, out var o) ? o : cur;   // 지금 내 복제본이면 원본으로
                Material want = src;
                for (int p = 0; p < Parts.Length; p++)
                {
                    if (System.Array.IndexOf(Parts[p].materials, src.name) < 0 || !UnderRoot(r.transform, Parts[p].root)) continue;
                    if (r.gameObject.activeInHierarchy) partFound[p] = true;
                    if (partChoice[p] >= 0) want = CloneFor(src, mats[partChoice[p]]);
                    break;
                }
                if (want != cur) { arr[s] = want; changed = true; }
            }
            if (changed) { r.sharedMaterials = arr; touched.Add(r); }
        }
    }

    static bool UnderRoot(Transform t, string root)
    {
        for (; t; t = t.parent) if (t.name == root) return true;
        return false;
    }

    Material CloneFor(Material src, MaterialPresetData p)
    {
        string key = p.id + (keepTextures ? "|keep" : "");
        if (clones.TryGetValue((src, key), out var m) && m) return m;
        var standard = Shader.Find("Standard");
        m = src.shader == standard ? new Material(src) : new Material(standard);
        m.name = src.name + " → " + p.id;
        var b = p.@base;
        float metal = RenderPresetFiles.Clamp(b.metallic, p.ranges?.metallic);
        float rough = RenderPresetFiles.Clamp(b.roughness, p.ranges?.roughness);
        float normal = RenderPresetFiles.Clamp(b.normalStrength, p.ranges?.normalStrength);

        // 금속·매끈함 (Standard 매끈함 = 1 - 거칠기)
        m.SetFloat("_Metallic", metal);
        m.SetFloat("_Glossiness", 1f - rough);
        if (keepTextures)
        {
            // 텍스처 유지: 색·사진·금속 맵은 그대로. 금속 맵이 있으면 Standard는 _Metallic 대신 맵을 읽음 → 매끈함 배율만 맞춤
            m.SetFloat("_GlossMapScale", 1f - rough);
        }
        else
        {
            // 전부 바꾸기: 사진 텍스처·금속 맵을 빼서 프리셋 색이 그대로 보이게
            m.SetColor("_Color", RenderPresetFiles.ToColor(b.color));
            m.SetTexture("_MainTex", null);
            m.SetTexture("_MetallicGlossMap", null);
            m.DisableKeyword("_METALLICGLOSSMAP");
            m.SetFloat("_SmoothnessTextureChannel", 0);
            m.DisableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
        }

        // 디테일 노멀 (원래 노멀맵은 그대로 두고 그 위에 결만 더함). 반복 4×4 (렌더툴과 같음)
        var n = RenderPresetFiles.LoadNormal(b.normalMap);
        if (n && normal > 0f)
        {
            m.SetTexture("_DetailNormalMap", n);
            m.SetFloat("_DetailNormalMapScale", normal);
            m.SetTexture("_DetailAlbedoMap", null);   // 비우면 회색 = 색 안 바뀜
            m.SetTextureScale("_DetailAlbedoMap", Vector2.one * 4f * (b.textureScale > 0 ? b.textureScale : 1f));
            m.SetFloat("_UVSec", 0);
            m.EnableKeyword("_NORMALMAP"); m.EnableKeyword("_DETAIL_MULX2");
        }
        clones[(src, key)] = m; sourceOf[m] = src;
        return m;
    }

    void StepPart(int p, int dir)
    {
        if (mats.Count == 0) return;
        int v = partChoice[p] + dir;                // -1(원래) → 0 → … → 마지막 → -1
        if (v >= mats.Count) v = -1; else if (v < -1) v = mats.Count - 1;
        partChoice[p] = v;
        RefreshMaterials();
        Say(partFound[p] ? $"{Parts[p].label}: {PartName(p)}" : $"{Parts[p].label} — 지금 장면에 없어요 (L 키로 공장 룸 켜기)");
    }

    string PartName(int p) => partChoice[p] < 0 ? "원래 재질" : mats[partChoice[p]].displayName;

    // ================================================================ 룩 (조명 분위기)
    void StepLook(int dir)
    {
        if (looks.Count == 0) return;
        int v = lookIndex + dir;
        if (v >= looks.Count) v = -1; else if (v < -1) v = looks.Count - 1;
        RemoveLook();
        lookIndex = v;
        if (v >= 0) ApplyLook(looks[v]);
        Say(v < 0 ? "조명 분위기: 원래대로" : "조명 분위기: " + looks[v].displayName);
    }

    void ApplyLook(LookPresetData look)
    {
        cam = Camera.main;
        rig = new GameObject("_RenderLookRig (" + look.displayName + ")") { hideFlags = HideFlags.DontSave };

        // 로봇 중심·크기 → 프리셋 위치를 로봇 크기에 맞춤
        var bounds = RobotBounds();
        Vector3 c = bounds.center;
        float longest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        float s = look.referenceSize > 0 && longest > 0 ? longest / look.referenceSize : 1f;
        var env = look.environment ?? new LookEnvironment();

        // 1) 환경: HDRI를 환경광으로 (공장 룸 안이라 배경으로는 거의 안 보임)
        savedSky = RenderSettings.skybox; savedAmbMode = RenderSettings.ambientMode;
        savedAmbInt = RenderSettings.ambientIntensity; savedReflInt = RenderSettings.reflectionIntensity;
        var hdri = RenderPresetFiles.LoadHdr(env.hdri);
        if (hdri)
        {
            sky = new Material(Shader.Find("Skybox/Panoramic")) { name = "Sky (" + hdri.name + ")" };
            sky.SetTexture("_MainTex", hdri);
            sky.SetFloat("_Mapping", 1); sky.EnableKeyword("_MAPPING_LATITUDE_LONGITUDE_LAYOUT");
            sky.SetFloat("_Rotation", env.hdriRotation);
            RenderSettings.skybox = sky;
            RenderSettings.ambientMode = AmbientMode.Skybox;
        }
        RenderSettings.ambientIntensity = setAmbInt = env.ambientIntensity;
        RenderSettings.reflectionIntensity = setReflInt = env.reflectionIntensity;
        DynamicGI.UpdateEnvironment();

        // 2) 원래 조명 밝기 배율
        savedLights.Clear();
        if (!Mathf.Approximately(look.sceneLightScale, 1f))
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                float set = l.intensity * look.sceneLightScale;
                savedLights[l] = (l.intensity, set);
                l.intensity = set;
            }

        // 3) 추가 조명
        if (look.lights != null)
            foreach (var d in look.lights)
            {
                var pos = RenderPresetFiles.ToVector3(d.position) * s;
                var go = new GameObject(d.name);
                go.transform.SetParent(rig.transform, false);
                go.transform.position = c + pos;
                go.transform.rotation = d.aimAtCenter && pos.sqrMagnitude > 0 ? Quaternion.LookRotation(-pos) : Quaternion.Euler(RenderPresetFiles.ToVector3(d.euler));
                var l = go.AddComponent<Light>();
                l.type = d.type == "point" ? LightType.Point : d.type == "directional" ? LightType.Directional : LightType.Spot;
                l.color = RenderPresetFiles.ToColor(d.color); l.intensity = d.intensity;
                l.range = (d.range > 0 ? d.range : 5) * s; l.spotAngle = d.spotAngle > 0 ? d.spotAngle : 40;
                l.shadows = d.shadows ? LightShadows.Soft : LightShadows.None;
                l.shadowBias = 0.01f; l.shadowNormalBias = 0.1f;
                l.renderMode = LightRenderMode.ForcePixel;
            }

        // 4) 소프트박스: 카메라엔 안 보이는 레이어에 두고, 반사 프로브만 찍음 → 금속에 네모난 빛이 맺힘
        softboxLayer = FreeLayer();
        bool hasSoftbox = look.softboxes != null && look.softboxes.Length > 0 && softboxLayer >= 0;
        if (hasSoftbox)
        {
            var standard = Shader.Find("Standard");
            foreach (var sb in look.softboxes)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                q.name = sb.name; q.layer = softboxLayer;
                Destroy(q.GetComponent<Collider>());
                q.transform.SetParent(rig.transform, false);
                var p = c + RenderPresetFiles.ToVector3(sb.position) * s;
                q.transform.position = p;
                q.transform.rotation = Quaternion.LookRotation(p - c);   // 쿼드 앞면(-Z 쪽)이 로봇을 봄
                var size = RenderPresetFiles.ToVector2(sb.size) * s;
                q.transform.localScale = new Vector3(size.x, size.y, 1);
                var m = new Material(standard) { name = "Softbox" };
                m.SetColor("_Color", Color.black);
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", RenderPresetFiles.ToColor(sb.emission));   // 1보다 큰 값 그대로 = HDR 빛
                var mr = q.GetComponent<MeshRenderer>();
                mr.sharedMaterial = m; mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
            }
            if (cam) { savedCull = cam.cullingMask; cam.cullingMask = setCull = savedCull & ~(1 << softboxLayer); }
        }

        // 5) 반사: 로봇 중심에서 HDRI 하늘 + 소프트박스만 찍는 프로브 (공장 룸 반사 대신 스튜디오 반사)
        var probe = look.reflectionProbe;
        if (probe != null && probe.enabled)
        {
            var go = new GameObject("ReflectionProbe");
            go.transform.SetParent(rig.transform, false);
            go.transform.position = c;
            var rp = go.AddComponent<ReflectionProbe>();
            rp.mode = ReflectionProbeMode.Realtime;
            rp.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            rp.clearFlags = ReflectionProbeClearFlags.Skybox;
            rp.cullingMask = hasSoftbox ? 1 << softboxLayer : 0;
            rp.size = RenderPresetFiles.ToVector3(probe.size) * s;
            rp.intensity = probe.intensity; rp.importance = 20; rp.boxProjection = true; rp.hdr = true;
            rp.resolution = Mathf.Clamp(Mathf.ClosestPowerOfTwo(probe.resolution > 0 ? probe.resolution : 256), 64, 512);
            rp.RenderProbe();
        }

        // 6) 후처리: 우선순위 높은 전역 볼륨 (공장 룸 볼륨 위에 덮어씀)
        if (look.post != null)
        {
            profile = BuildPost(look.post);
            var vgo = new GameObject("PostVolume") { layer = 0 };
            vgo.transform.SetParent(rig.transform, false);
            var vol = vgo.AddComponent<PostProcessVolume>();
            vol.isGlobal = true; vol.priority = 10; vol.sharedProfile = profile;
            postLayer = cam ? cam.GetComponent<PostProcessLayer>() : null;
            postTurnedOn = false;
            if (postLayer && !postLayer.enabled && !(quality && quality.IsLight))   // F5 가벼운 룩이면 후처리는 그대로 꺼 둠
            { savedPostOn = false; postLayer.enabled = true; postTurnedOn = true; }
        }
    }

    void RemoveLook()
    {
        if (!rig) return;
        // 내가 넣은 값이 그대로 남아 있을 때만 되돌림 (L 키가 그 사이에 바꾼 건 그대로 둠)
        if (sky && RenderSettings.skybox == sky) { RenderSettings.skybox = savedSky; RenderSettings.ambientMode = savedAmbMode; }
        if (Mathf.Approximately(RenderSettings.ambientIntensity, setAmbInt)) RenderSettings.ambientIntensity = savedAmbInt;
        if (Mathf.Approximately(RenderSettings.reflectionIntensity, setReflInt)) RenderSettings.reflectionIntensity = savedReflInt;
        DynamicGI.UpdateEnvironment();
        foreach (var kv in savedLights)
            if (kv.Key && Mathf.Approximately(kv.Key.intensity, kv.Value.set)) kv.Key.intensity = kv.Value.original;
        savedLights.Clear();
        if (cam && softboxLayer >= 0 && cam.cullingMask == setCull) cam.cullingMask = savedCull;
        if (postTurnedOn && postLayer) postLayer.enabled = savedPostOn;
        postTurnedOn = false;

        foreach (var r in rig.GetComponentsInChildren<Renderer>()) if (r.sharedMaterial) Destroy(r.sharedMaterial);
        Destroy(rig); rig = null;
        if (sky) { Destroy(sky); sky = null; }
        if (profile) { Destroy(profile); profile = null; }
    }

    static PostProcessProfile BuildPost(LookPost p)
    {
        var prof = ScriptableObject.CreateInstance<PostProcessProfile>();
        prof.name = "Post (render preset)";
        if (p.ambientOcclusion != null && p.ambientOcclusion.enabled)
        {
            var ao = prof.AddSettings<AmbientOcclusion>(); ao.enabled.Override(true);
            ao.mode.Override(AmbientOcclusionMode.MultiScaleVolumetricObscurance);
            ao.intensity.Override(p.ambientOcclusion.intensity); ao.thicknessModifier.Override(p.ambientOcclusion.thickness);
            ao.color.Override(RenderPresetFiles.ToColor(p.ambientOcclusion.color));
        }
        var g = p.colorGrading ?? new LookColorGrading();
        var cg = prof.AddSettings<ColorGrading>(); cg.enabled.Override(true);
        cg.tonemapper.Override(g.tonemapper == "neutral" ? Tonemapper.Neutral : g.tonemapper == "none" ? Tonemapper.None : Tonemapper.ACES);
        cg.postExposure.Override(g.postExposure); cg.contrast.Override(g.contrast); cg.saturation.Override(g.saturation);
        if (p.bloom != null && p.bloom.enabled)
        {
            var bl = prof.AddSettings<Bloom>(); bl.enabled.Override(true);
            bl.threshold.Override(p.bloom.threshold); bl.intensity.Override(p.bloom.intensity); bl.softKnee.Override(p.bloom.softKnee);
        }
        if (p.vignette != null && p.vignette.enabled)
        {
            var vg = prof.AddSettings<Vignette>(); vg.enabled.Override(true);
            vg.intensity.Override(p.vignette.intensity); vg.smoothness.Override(p.vignette.smoothness);
        }
        if (p.grain) { var gr = prof.AddSettings<Grain>(); gr.enabled.Override(true); gr.intensity.Override(0.06f); gr.size.Override(0.6f); gr.colored.Override(false); }
        return prof;
    }

    static Bounds RobotBounds()
    {
        var root = GameObject.Find(RobotRoot);
        var b = new Bounds(Vector3.zero, Vector3.one * 0.3f);
        if (!root) return b;
        bool first = true;
        foreach (var r in root.GetComponentsInChildren<Renderer>())
        {
            if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds);
        }
        return b;
    }

    // 이름 없는 레이어 하나 (8~31 뒤쪽부터) — 소프트박스 전용
    static int FreeLayer()
    {
        for (int i = 31; i >= 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
        Debug.LogWarning("[렌더 프리셋] 빈 레이어가 없어 " + SoftboxLayerHint + "는 뺌");
        return -1;
    }

    // ================================================================ 모두 원래대로
    void ResetAll()
    {
        RemoveLook(); lookIndex = -1;
        for (int p = 0; p < partChoice.Length; p++) partChoice[p] = -1;
        if (touched.Count > 0)
        {
            foreach (var r in touched)
            {
                if (!r) continue;
                var arr = r.sharedMaterials;
                for (int s = 0; s < arr.Length; s++) if (arr[s] && sourceOf.TryGetValue(arr[s], out var o)) arr[s] = o;
                r.sharedMaterials = arr;
            }
            touched.Clear();
        }
        foreach (var m in sourceOf.Keys) if (m) Destroy(m);
        clones.Clear(); sourceOf.Clear();
    }

    void Say(string text) { status = text; statusUntil = Time.unscaledTime + 4f; Debug.Log("[렌더 프리셋] " + text); }

    // ================================================================ 화면 (IMGUI, 왼쪽 아래)
    static readonly Color Ink = new Color32(0xF5, 0xF4, 0xF3, 255), Muted = new Color32(0xC2, 0xC2, 0xC8, 255);
    static readonly Color Blue = new Color32(0x78, 0xAD, 0xFC, 255);
    static readonly Color Fill = new Color(.13f, .12f, .118f, .88f), Card = new Color(.28f, .26f, .25f, .45f);
    static Texture2D white;

    void Styles()
    {
        if (title != null) return;
        white = Texture2D.whiteTexture;
        title = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, normal = { textColor = Ink } };
        label = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = Ink }, alignment = TextAnchor.MiddleLeft };
        small = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = Muted }, wordWrap = true };
        button = new GUIStyle(GUI.skin.button) { fontSize = 13 };
        nameBox = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, normal = { textColor = Ink }, clipping = TextClipping.Clip };
        small2 = new GUIStyle(nameBox) { normal = { textColor = Muted } };
    }

    void Rect(Rect r, Color c) { var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, white); GUI.color = old; }

    void OnGUI()
    {
        Styles();
        float k = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1));
        float h = Screen.height / k;

        if (!open)
        {
            var pill = new Rect(12, h - 44, 150, 32);
            if (GUI.Button(pill, "룩 프리셋  (F6)", button)) open = true;
            return;
        }

        const float W = 340, Row = 30;
        float inner = W - 28;
        string note = lookIndex < 0 ? (looks.Count == 0 ? "looks 폴더에 프리셋이 없어요" : $"프리셋 {looks.Count}개 · ◀ ▶ 로 넘기기") : looks[lookIndex].note;
        float noteH = Mathf.Max(16, small.CalcHeight(new GUIContent(note), inner));
        float H = 12 + 30 + 20 + Row + 4 + noteH + 10 + 22 + 34 + Parts.Length * (Row + 6) + 4 + 34 + 22;
        var box = new Rect(12, h - H - 12, W, H);
        Rect(box, Fill);
        float x = box.x + 14, y = box.y + 12;

        GUI.Label(new Rect(x, y, inner, 22), "룩 프리셋", title);
        GUI.Label(new Rect(x + 90, y + 3, inner - 90, 18), "렌더툴 프리셋을 골라 바로 비교", small);
        y += 30;

        // 조명 분위기
        GUI.Label(new Rect(x, y, inner, 18), "조명 분위기", small); y += 20;
        string lookName = lookIndex < 0 ? "원래대로" : looks[lookIndex].displayName;
        if (Stepper(new Rect(x, y, inner, Row), lookName, lookIndex >= 0, out int d)) StepLook(d);
        y += Row + 4;
        GUI.Label(new Rect(x, y, inner, noteH), note, small); y += noteH + 10;

        // 재질
        GUI.Label(new Rect(x, y, inner, 18), "재질 (부위별) · 조명만 바꾸려면 '원래 재질'로 두기", small); y += 22;
        // 적용 방식 두 칸: 전부 바꾸기 / 텍스처 유지·수치만
        float half = (inner - 6) / 2;
        if (Segment(new Rect(x, y, half, 26), "전부 바꾸기", !keepTextures)) SetKeepTextures(false);
        if (Segment(new Rect(x + half + 6, y, half, 26), "텍스처 유지 · 수치만", keepTextures)) SetKeepTextures(true);
        y += 34;
        for (int p = 0; p < Parts.Length; p++)
        {
            GUI.Label(new Rect(x, y, 84, Row), Parts[p].label, label);
            if (Stepper(new Rect(x + 86, y, inner - 86, Row), PartName(p), partChoice[p] >= 0, out int pd)) StepPart(p, pd);
            y += Row + 6;
        }

        // 아래 줄
        y += 4;
        if (GUI.Button(new Rect(x, y, inner * 0.62f, 30), "모두 원래대로", button)) { ResetAll(); Say("모두 원래대로 돌림"); }
        if (GUI.Button(new Rect(x + inner * 0.62f + 8, y, inner * 0.38f - 8, 30), "닫기 (F6)", button)) open = false;
        y += 34;
        GUI.Label(new Rect(x, y, inner, 18), Time.unscaledTime < statusUntil ? status : "원본 재질 파일은 안 바뀌어요 · 끄면 그대로 돌아와요", small);
    }

    // 고르는 칸 하나 (켜진 쪽은 파란 바탕). 눌렀으면 true
    bool Segment(Rect r, string text, bool on)
    {
        Rect(r, on ? new Color(Blue.r, Blue.g, Blue.b, .35f) : Card);
        return GUI.Button(r, text, on ? nameBox : small2);
    }
    GUIStyle small2;

    void SetKeepTextures(bool keep)
    {
        if (keep == keepTextures) return;
        keepTextures = keep;
        RefreshMaterials();   // 고른 재질을 새 방식으로 다시 입힘 (복제본은 방식별로 따로 만들어 둠)
        Say(keep ? "텍스처 유지 · 수치만: 원래 색·텍스처에 금속·매끈함·결만 바꿈" : "전부 바꾸기: 프리셋 색·질감으로 교체");
    }

    // [◀] 이름 [▶] — 눌렀으면 true, dir = -1 / +1
    bool Stepper(Rect r, string text, bool active, out int dir)
    {
        dir = 0;
        Rect(new Rect(r.x + 34, r.y, r.width - 68, r.height), active ? new Color(Blue.r, Blue.g, Blue.b, .28f) : Card);
        if (GUI.Button(new Rect(r.x, r.y, 30, r.height), "◀", button)) dir = -1;
        GUI.Label(new Rect(r.x + 36, r.y, r.width - 72, r.height), text, nameBox);
        if (GUI.Button(new Rect(r.xMax - 30, r.y, 30, r.height), "▶", button)) dir = 1;
        return dir != 0;
    }
}
