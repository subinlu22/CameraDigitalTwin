using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SceneManagement;

// =============================================================
// [렌더 룩 콘솔 — 유니티 쪽 본체] 어느 장면에 넣어도 동작 (Built-in RP + Standard 셰이더 + Post Processing v2)
//
// 하는 일
//   ① 부위 찾기: 장면에 쓰인 재질 이름이 그대로 부위가 됨 (자동).
//      parts/<씬 이름>.json 이 있으면 재질 여러 개를 "로봇 몸체"처럼 보기 좋은 이름으로 묶어 맨 위에 보여 줌
//   ② 조명 분위기(룩): HDRI 환경광·반사 + 추가 조명 + 반사에만 보이는 소프트박스 + 후처리. 세부 수치(환경광·노출 등) 조절
//   ③ 재질: 부위마다 원래 재질 / 전부 바꾸기 / 텍스처 유지·수치만. 금속·거칠기·결 세기·결 크기·색 조절
//      원본 재질 파일은 절대 안 바꿈 — 부위마다 복제본을 만들어 입히고, 되돌리면 원본을 다시 끼움
//   ④ 원래대로 / 지금 수치를 새 프리셋 JSON으로 저장
//   ⑤ 다른 스크립트(예: 장면 룩 전환)가 하늘·재질을 덮어쓰면 0.5초 안에 고른 값을 다시 입힘
// 화면: 따로 뜨는 콘솔 창(RenderLookLink, UDP) 또는 플레이 화면의 F6 창(RenderLookPanel). 둘 다 이 본체만 부름
// 씬에 안 넣어도 됨: 플레이를 시작하면 자동으로 만들어짐 (빌드 exe도 같음)
// =============================================================
namespace RenderLook
{
    // 부위 하나의 지금 상태 (콘솔과 주고받는 값)
    [Serializable]
    public class PartState
    {
        public string id;
        public string mode = "original";   // "original" | "replace"(전부 바꾸기) | "keep"(텍스처 유지·수치만)
        public string preset;              // 재질 프리셋 id
        public float metallic, roughness = 0.5f, normalStrength, tiling = 1f;
        public float[] color = { 0.8f, 0.8f, 0.8f };
    }

    // 조명 분위기 세부 수치
    [Serializable]
    public class LookParams
    {
        public float ambient = 1f, reflection = 1f, sceneLight = 1f, exposure, contrast, saturation, bloom;
        public float temperature, tint;    // 색온도(+ 따뜻하게) · 색조(+ 자홍, − 초록)
        // 어두운 곳(lift)·중간(gamma)·밝은 곳(gain) 색: RGB + 밝기(w). 기본 (1,1,1,0). 레퍼런스 색 맞추기가 씀
        public float[] lift = { 1, 1, 1, 0 }, gamma = { 1, 1, 1, 0 }, gain = { 1, 1, 1, 0 };
    }

    public class Part
    {
        public string id, label, root;
        public bool auto;                  // true = 재질 이름 그대로 (이름표 없음)
        public HashSet<string> materials = new HashSet<string>();
        public int count;                  // 이 부위를 쓰는 렌더러 칸 수
        public bool found;                 // 지금 화면에 보이는 오브젝트가 있음
        public float[] color = { 0.8f, 0.8f, 0.8f };   // 원래 재질 색·금속·매끈함 (콘솔의 "원본" 공 그림용)
        public float metallic, smoothness = 0.5f;
    }

    public class RenderLookCore : MonoBehaviour
    {
        const string RigPrefix = "_RenderLookRig";
        const float WatchEvery = 0.5f;
        const float AeLow = 50f, AeHigh = 95f, AeRange = 4f;   // 기준 밝기(자동 노출): 볼 밝기 구간(%) · 최대 보정(EV)

        public static RenderLookCore Instance { get; private set; }
        public event Action Changed;       // 상태가 바뀌면 (콘솔에 다시 보냄)
        public string Message { get; private set; } = "";

        public List<LookPresetData> Looks { get; private set; } = new List<LookPresetData>();
        public List<MaterialPresetData> Materials { get; private set; } = new List<MaterialPresetData>();
        public List<Part> Parts { get; } = new List<Part>();
        public string LookId => look != null ? look.id : null;
        public LookParams Params { get; private set; } = new LookParams();
        public LookParams DefaultParams => look != null ? ParamsFrom(look) : null;   // 고른 룩의 원래 수치 (↺ 되돌리기용)
        public readonly Dictionary<string, PartState> States = new Dictionary<string, PartState>();

        PartsFile partsFile;
        float nextWatch;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (FindFirstObjectByType<RenderLookCore>()) return;
            var go = new GameObject("RenderLookConsole");
            go.AddComponent<RenderLookCore>();
            go.AddComponent<RenderLookLink>();
            go.AddComponent<RenderLookPanel>();
            go.AddComponent<RenderLookStream>();
        }

        void Awake() { Instance = this; }

        void Start() { Reload(); }

        public void Reload()
        {
            Looks = RenderLookFiles.LoadLooks();
            Materials = RenderLookFiles.LoadMaterials();
            partsFile = RenderLookFiles.LoadParts(SceneManager.GetActiveScene().name);
            FindParts();
            Debug.Log($"[렌더 룩] 룩 {Looks.Count} · 재질 {Materials.Count} · 부위 {Parts.Count} (이름표 {(partsFile != null ? partsFile.parts.Length : 0)})");
            Notify("");
        }

        void Notify(string msg)
        {
            if (!string.IsNullOrEmpty(msg)) { Message = msg; Debug.Log("[렌더 룩] " + msg); }
            Changed?.Invoke();
        }

        void LateUpdate()
        {
            if (Time.unscaledTime < nextWatch) return;
            nextWatch = Time.unscaledTime + WatchEvery;
            // 다른 스크립트가 하늘을 바꿨으면 고른 룩을 그 위에 다시 입힘
            if (look != null && sky && RenderSettings.skybox != sky) { RemoveLook(); BuildLook(look); ApplyParams(); }
            if (HasMaterialChoice()) RefreshMaterials();
        }

        void OnDisable() { ResetAll(); }

        // ================================================================ 부위 찾기
        static bool IsOurs(Renderer r) => r.transform.root.name.StartsWith(RigPrefix);
        static bool UnderRoot(Transform t, string root)
        {
            if (string.IsNullOrEmpty(root)) return true;
            for (; t; t = t.parent) if (t.name == root) return true;
            return false;
        }

        public void FindParts()
        {
            Parts.Clear();
            var labeled = new List<Part>();
            if (partsFile?.parts != null)
                foreach (var l in partsFile.parts)
                {
                    if (string.IsNullOrEmpty(l.id)) continue;
                    var p = new Part { id = l.id, label = string.IsNullOrEmpty(l.label) ? l.id : l.label, root = l.root, auto = false };
                    foreach (var m in l.materials ?? new string[0]) p.materials.Add(m);
                    labeled.Add(p);
                }
            var auto = new Dictionary<string, Part>();
            var standard = Shader.Find("Standard");
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (IsOurs(r)) continue;
                foreach (var cur in r.sharedMaterials)
                {
                    if (!cur) continue;
                    var src = sourceOf.TryGetValue(cur, out var o) ? o : cur;
                    var part = labeled.Find(p => p.materials.Contains(src.name) && UnderRoot(r.transform, p.root));
                    if (part == null)
                    {
                        if (src.shader != standard) continue;   // 값을 쓸 수 있는 Standard 재질만
                        if (!auto.TryGetValue(src.name, out part))
                            auto[src.name] = part = new Part { id = "mat:" + src.name, label = src.name, auto = true, materials = { src.name } };
                    }
                    if (part.count == 0 && src.HasProperty("_Color"))
                    {
                        var col = src.color; part.color = new[] { col.r, col.g, col.b };
                        if (src.HasProperty("_Metallic")) part.metallic = src.GetFloat("_Metallic");
                        if (src.HasProperty("_Glossiness")) part.smoothness = src.GetFloat("_Glossiness");
                    }
                    part.count++;
                    if (r.gameObject.activeInHierarchy) part.found = true;
                }
            }
            Parts.AddRange(labeled);
            var autos = new List<Part>(auto.Values);
            autos.Sort((a, b) => a.found != b.found ? (a.found ? -1 : 1) : b.count != a.count ? b.count - a.count : string.CompareOrdinal(a.label, b.label));
            Parts.AddRange(autos);
        }

        Part PartFor(Renderer r, Material src)
        {
            foreach (var p in Parts)
                if (p.materials.Contains(src.name) && (p.auto || UnderRoot(r.transform, p.root))) return p;
            return null;
        }

        // ================================================================ 재질
        readonly Dictionary<(Material, string), Material> clones = new Dictionary<(Material, string), Material>();
        readonly Dictionary<Material, Material> sourceOf = new Dictionary<Material, Material>();
        readonly HashSet<Renderer> touched = new HashSet<Renderer>();

        bool HasMaterialChoice()
        {
            foreach (var s in States.Values) if (s.mode != "original") return true;
            return false;
        }

        public MaterialPresetData FindMaterial(string id) => Materials.Find(m => m.id == id);

        // 부위 바꾸기. pick = 프리셋을 새로 고름 → 수치를 프리셋 기본값으로 채움
        public void SetPart(PartState s, bool pick)
        {
            var part = Parts.Find(p => p.id == s.id);
            if (part == null) { Notify($"부위 '{s.id}'를 못 찾음"); return; }
            if (s.mode != "original" && !string.IsNullOrEmpty(s.preset) && FindMaterial(s.preset) == null) { Notify($"모르는 재질 '{s.preset}' — 무시함"); return; }
            if (s.mode != "original" && string.IsNullOrEmpty(s.preset) && Materials.Count > 0) { s.preset = Materials[0].id; pick = true; }
            var preset = FindMaterial(s.preset);
            if (pick && preset != null)
            {
                var b = preset.@base;
                s.metallic = b.metallic; s.roughness = b.roughness; s.normalStrength = b.normalStrength;
                s.tiling = b.textureScale > 0 ? b.textureScale : 1f; s.color = (float[])b.color.Clone();
            }
            States[s.id] = s;
            // 이 부위의 복제본 값을 새로 씀 (슬라이더를 움직일 때마다 새 재질을 만들지 않고 같은 복제본을 고침)
            foreach (var kv in clones) if (kv.Key.Item2 == s.id && kv.Value) Write(kv.Value, kv.Key.Item1, s, preset);
            RefreshMaterials();
            Notify(s.mode == "original" ? $"{part.label}: 원래 재질" :
                   $"{part.label}: {(preset != null ? preset.displayName : s.preset)} · {(s.mode == "keep" ? "텍스처 유지·수치만" : "전부 바꾸기")}");
        }

        void RefreshMaterials()
        {
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (IsOurs(r)) continue;
                var arr = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    var cur = arr[i];
                    if (!cur) continue;
                    var src = sourceOf.TryGetValue(cur, out var o) ? o : cur;   // 내 복제본이면 원본으로
                    Material want = src;
                    var part = PartFor(r, src);
                    if (part != null && States.TryGetValue(part.id, out var s) && s.mode != "original") want = CloneFor(src, s);
                    if (want != cur) { arr[i] = want; changed = true; }
                }
                if (changed) { r.sharedMaterials = arr; touched.Add(r); }
            }
        }

        Material CloneFor(Material src, PartState s)
        {
            if (clones.TryGetValue((src, s.id), out var m) && m) return m;
            m = new Material(src) { name = src.name + " (렌더 룩)" };
            Write(m, src, s, FindMaterial(s.preset));
            clones[(src, s.id)] = m; sourceOf[m] = src;
            return m;
        }

        static void Write(Material m, Material src, PartState s, MaterialPresetData preset)
        {
            var standard = Shader.Find("Standard");
            if (src.shader == standard) { m.shader = standard; m.CopyPropertiesFromMaterial(src); }
            else m.shader = standard;   // Standard가 아닌 재질은 Standard로 (이름표 부위에서만 생김)

            float metal = Mathf.Clamp01(s.metallic), rough = Mathf.Clamp01(s.roughness);
            m.SetFloat("_Metallic", metal);
            m.SetFloat("_Glossiness", 1f - rough);
            if (s.mode == "keep")
            {
                // 텍스처 유지: 색·사진·금속 맵 그대로. 금속 맵이 있으면 Standard는 맵을 읽으므로 매끈함 배율도 맞춤
                m.SetFloat("_GlossMapScale", 1f - rough);
            }
            else
            {
                m.SetColor("_Color", RenderLookFiles.ToColor(s.color));
                m.SetTexture("_MainTex", null);
                m.SetTexture("_MetallicGlossMap", null);
                m.DisableKeyword("_METALLICGLOSSMAP");
                m.SetFloat("_SmoothnessTextureChannel", 0);
                m.DisableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
                if (preset != null) WriteScan(m, preset.@base, s);
            }

            // 결 (디테일 노멀): 원래 노멀맵은 그대로, 그 위에 더함. 반복 4×4 × 결 크기 (렌더툴과 같음)
            var n = preset != null ? RenderLookFiles.LoadNormal(preset.@base.normalMap) : null;
            if (n && s.normalStrength > 0f)
            {
                m.SetTexture("_DetailNormalMap", n);
                m.SetFloat("_DetailNormalMapScale", s.normalStrength);
                m.SetTexture("_DetailAlbedoMap", null);   // 비우면 회색 = 색 안 바뀜
                m.SetTextureScale("_DetailAlbedoMap", Vector2.one * 4f * Mathf.Max(0.05f, s.tiling));
                m.SetFloat("_UVSec", 0);
                m.EnableKeyword("_NORMALMAP"); m.EnableKeyword("_DETAIL_MULX2");
            }
            else { m.SetTexture("_DetailNormalMap", null); m.DisableKeyword("_DETAIL_MULX2"); }
        }

        // 실사 스캔 재질: 색 사진·표면 노멀·금속/매끈함 맵을 입힘 (맵이 없는 프리셋은 그대로)
        // 거칠기 슬라이더 0.5 = 맵 그대로, 낮추면 더 매끈 / 높이면 더 거칠게. 결 크기 = 사진 반복 크기
        static void WriteScan(Material m, MaterialValues b, PartState s)
        {
            var albedo = RenderLookFiles.LoadImage(b.albedoMap, false);
            var normal = RenderLookFiles.LoadNormal(b.surfaceNormalMap);
            var metalGloss = RenderLookFiles.LoadImage(b.metalGlossMap, true);
            if (!albedo && !normal && !metalGloss) return;
            var tile = Vector2.one * Mathf.Max(0.05f, s.tiling);
            float glossScale = Mathf.Clamp01((1f - Mathf.Clamp01(s.roughness)) * 2f);
            if (albedo) { m.SetTexture("_MainTex", albedo); m.SetTextureScale("_MainTex", tile); }
            if (albedo && b.albedoAlphaIsGloss)
            {
                // 매끈함 = 색 사진 A × 배율, 금속 = 금속성 슬라이더 (Standard 셰이더 '알베도 알파' 방식)
                m.SetFloat("_SmoothnessTextureChannel", 1);
                m.EnableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
                m.SetFloat("_GlossMapScale", glossScale);
            }
            // 결 세기 = 표면 노멀 세기 (스캔 재질엔 디테일 결이 없어서)
            if (normal) { m.SetTexture("_BumpMap", normal); m.SetFloat("_BumpScale", s.normalStrength); m.EnableKeyword("_NORMALMAP"); }
            if (metalGloss)
            {
                m.SetTexture("_MetallicGlossMap", metalGloss);
                m.EnableKeyword("_METALLICGLOSSMAP");
                m.SetFloat("_GlossMapScale", glossScale);
            }
        }

        void RestoreMaterials()
        {
            foreach (var r in touched)
            {
                if (!r) continue;
                var arr = r.sharedMaterials;
                for (int i = 0; i < arr.Length; i++) if (arr[i] && sourceOf.TryGetValue(arr[i], out var o)) arr[i] = o;
                r.sharedMaterials = arr;
            }
            touched.Clear();
            foreach (var m in sourceOf.Keys) if (m) Destroy(m);
            clones.Clear(); sourceOf.Clear();
        }

        // ================================================================ 룩 (조명 분위기)
        LookPresetData look;
        GameObject rig; Material sky; PostProcessProfile profile; ColorGrading grading; Bloom bloomFx;
        Material savedSky; AmbientMode savedAmbMode; float savedAmbInt, savedReflInt, setAmbInt, setReflInt;
        readonly Dictionary<Light, (float original, float set)> savedLights = new Dictionary<Light, (float, float)>();
        PostProcessLayer postLayer; bool postTurnedOn;
        Camera cam; int savedCull, setCull; int softboxLayer = -1;

        public void SetLook(string id)
        {
            RemoveLook();
            look = string.IsNullOrEmpty(id) ? null : Looks.Find(l => l.id == id);
            if (look == null) { Notify("조명 분위기: 원래대로"); return; }
            Params = ParamsFrom(look);
            BuildLook(look);
            ApplyParams();
            ApplyRecipe(look);
            Notify("조명 분위기: " + look.displayName);
        }

        // 레시피 룩: 부위마다 재질을 같이 입힘 (값을 -1로 둔 칸은 재질 프리셋 값)
        void ApplyRecipe(LookPresetData l)
        {
            if (l.parts == null) return;
            foreach (var r in l.parts)
            {
                if (string.IsNullOrEmpty(r.part) || FindMaterial(r.material) == null || Parts.Find(p => p.id == r.part) == null) continue;
                var st = new PartState { id = r.part, mode = string.IsNullOrEmpty(r.mode) ? "replace" : r.mode, preset = r.material };
                SetPart(st, true);   // 프리셋 값으로 채운 뒤
                if (r.metallic >= 0) st.metallic = r.metallic;
                if (r.roughness >= 0) st.roughness = r.roughness;
                if (r.normalStrength >= 0) st.normalStrength = r.normalStrength;
                if (r.tiling > 0) st.tiling = r.tiling;
                if (r.color != null && r.color.Length >= 3) st.color = (float[])r.color.Clone();
                SetPart(st, false);  // 레시피 값으로 덮음
            }
        }

        public void SetParams(LookParams p)
        {
            if (look == null || p == null) return;
            Params = p;
            ApplyParams();
            Notify("");
        }

        static LookParams ParamsFrom(LookPresetData l)
        {
            var env = l.environment ?? new LookEnvironment();
            var g = l.post?.colorGrading ?? new LookColorGrading();
            return new LookParams
            {
                ambient = env.ambientIntensity, reflection = env.reflectionIntensity, sceneLight = l.sceneLightScale,
                exposure = g.postExposure, contrast = g.contrast, saturation = g.saturation, temperature = g.temperature, tint = g.tint,
                lift = Tone(g.lift), gamma = Tone(g.gamma), gain = Tone(g.gain),
                bloom = l.post?.bloom != null && l.post.bloom.enabled ? l.post.bloom.intensity : 0f,
            };
        }

        static float[] Tone(float[] v) => v != null && v.Length == 4 ? (float[])v.Clone() : new float[] { 1, 1, 1, 0 };
        static Vector4 V4(float[] v) { v = Tone(v); return new Vector4(v[0], v[1], v[2], v[3]); }

        Bounds FocusBounds()
        {
            var b = new Bounds(); bool first = true;
            void Add(Renderer r) { if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds); }
            var focus = !string.IsNullOrEmpty(partsFile?.focus) ? GameObject.Find(partsFile.focus) : null;
            if (focus) foreach (var r in focus.GetComponentsInChildren<Renderer>()) Add(r);
            if (first)
                foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    if (IsOurs(r)) continue;
                    foreach (var m in r.sharedMaterials)
                        if (m && PartFor(r, sourceOf.TryGetValue(m, out var o) ? o : m) is Part p && !p.auto) { Add(r); break; }
                }
            if (first)   // 이름표가 없으면 카메라 앞 2m
            {
                var c = Camera.main;
                b = new Bounds(c ? c.transform.position + c.transform.forward * 2f : Vector3.zero, Vector3.one * 0.5f);
            }
            return b;
        }

        void BuildLook(LookPresetData l)
        {
            cam = Camera.main;
            rig = new GameObject(RigPrefix + " (" + l.displayName + ")") { hideFlags = HideFlags.DontSave };
            var bounds = FocusBounds();
            Vector3 c = bounds.center;
            float longest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            float s = l.referenceSize > 0 && longest > 0 ? longest / l.referenceSize : 1f;
            var env = l.environment ?? new LookEnvironment();

            // 1) 환경: HDRI → 환경광·반사
            savedSky = RenderSettings.skybox; savedAmbMode = RenderSettings.ambientMode;
            savedAmbInt = RenderSettings.ambientIntensity; savedReflInt = RenderSettings.reflectionIntensity;
            var hdri = RenderLookFiles.LoadHdr(env.hdri);
            if (hdri)
            {
                sky = new Material(Shader.Find("Skybox/Panoramic")) { name = "Sky (" + hdri.name + ")" };
                sky.SetTexture("_MainTex", hdri);
                sky.SetFloat("_Mapping", 1); sky.EnableKeyword("_MAPPING_LATITUDE_LONGITUDE_LAYOUT");
                sky.SetFloat("_Rotation", env.hdriRotation);
                RenderSettings.skybox = sky;
                RenderSettings.ambientMode = AmbientMode.Skybox;
            }

            // 2) 원래 조명 밝기 기억 (배율은 ApplyParams에서)
            savedLights.Clear();
            foreach (var li in FindObjectsByType<Light>(FindObjectsSortMode.None)) savedLights[li] = (li.intensity, li.intensity);

            // 3) 추가 조명
            if (l.lights != null)
                foreach (var d in l.lights)
                {
                    var pos = RenderLookFiles.ToVector3(d.position) * s;
                    var go = new GameObject(d.name);
                    go.transform.SetParent(rig.transform, false);
                    go.transform.position = c + pos;
                    go.transform.rotation = d.aimAtCenter && pos.sqrMagnitude > 0 ? Quaternion.LookRotation(-pos) : Quaternion.Euler(RenderLookFiles.ToVector3(d.euler));
                    var li = go.AddComponent<Light>();
                    li.type = d.type == "point" ? LightType.Point : d.type == "directional" ? LightType.Directional : LightType.Spot;
                    li.color = RenderLookFiles.ToColor(d.color); li.intensity = d.intensity;
                    li.range = (d.range > 0 ? d.range : 5) * s; li.spotAngle = d.spotAngle > 0 ? d.spotAngle : 40;
                    li.shadows = d.shadows ? LightShadows.Soft : LightShadows.None;
                    li.shadowBias = 0.01f; li.shadowNormalBias = 0.1f;
                    li.renderMode = LightRenderMode.ForcePixel;
                }

            // 4) 소프트박스: 카메라엔 안 보이는 빈 레이어 → 반사 프로브만 찍음 → 금속에 네모난 빛이 맺힘
            softboxLayer = FreeLayer();
            bool hasSoftbox = l.softboxes != null && l.softboxes.Length > 0 && softboxLayer >= 0;
            if (hasSoftbox)
            {
                var standard = Shader.Find("Standard");
                foreach (var sb in l.softboxes)
                {
                    var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    q.name = sb.name; q.layer = softboxLayer;
                    Destroy(q.GetComponent<Collider>());
                    q.transform.SetParent(rig.transform, false);
                    var p = c + RenderLookFiles.ToVector3(sb.position) * s;
                    q.transform.position = p;
                    q.transform.rotation = Quaternion.LookRotation(p - c);   // 쿼드 앞면이 초점 쪽
                    var size = RenderLookFiles.ToVector2(sb.size) * s;
                    q.transform.localScale = new Vector3(size.x, size.y, 1);
                    var m = new Material(standard) { name = "Softbox" };
                    m.SetColor("_Color", Color.black);
                    m.EnableKeyword("_EMISSION");
                    m.SetColor("_EmissionColor", RenderLookFiles.ToColor(sb.emission));   // 1보다 큰 값 = HDR 빛
                    var mr = q.GetComponent<MeshRenderer>();
                    mr.sharedMaterial = m; mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
                }
                if (cam) { savedCull = cam.cullingMask; cam.cullingMask = setCull = savedCull & ~(1 << softboxLayer); }
            }

            // 5) 반사: 초점 중심에서 HDRI 하늘 + 소프트박스만 찍는 프로브
            var probe = l.reflectionProbe;
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
                rp.size = RenderLookFiles.ToVector3(probe.size) * s;
                rp.intensity = probe.intensity; rp.importance = 20; rp.boxProjection = true; rp.hdr = true;
                rp.resolution = Mathf.Clamp(Mathf.ClosestPowerOfTwo(probe.resolution > 0 ? probe.resolution : 256), 64, 512);
                rp.RenderProbe();
            }

            // 6) 후처리: 우선순위 높은 전역 볼륨 (장면 볼륨 위에 덮어씀)
            profile = BuildPost(l.post ?? new LookPost());
            if (l.post != null && l.post.depthOfField && cam)
            {
                // 배경 흐림: 초점 = 카메라에서 초점 물체까지 거리, 조리개·렌즈는 룩의 camera 값 (작은 조리개 수 = 더 흐림)
                var dof = profile.AddSettings<DepthOfField>(); dof.enabled.Override(true);
                dof.focusDistance.Override(Mathf.Max(0.1f, Vector3.Distance(cam.transform.position, c)));
                dof.aperture.Override(Mathf.Clamp(l.camera != null ? l.camera.aperture : 2.8f, 0.5f, 32f));
                dof.focalLength.Override(Mathf.Clamp(l.camera != null ? l.camera.focalLength : 50f, 10f, 300f));
                dof.kernelSize.Override(KernelSize.Medium);
            }
            grading = profile.GetSetting<ColorGrading>();
            bloomFx = profile.GetSetting<Bloom>();
            var vgo = new GameObject("PostVolume") { layer = 0 };
            vgo.transform.SetParent(rig.transform, false);
            var vol = vgo.AddComponent<PostProcessVolume>();
            vol.isGlobal = true; vol.priority = 10; vol.sharedProfile = profile;
            postLayer = cam ? cam.GetComponent<PostProcessLayer>() : null;
            postTurnedOn = false;
            if (postLayer && !postLayer.enabled) { postLayer.enabled = true; postTurnedOn = true; }
        }

        // 세부 수치만 바꿈 (조명·하늘은 다시 안 만듦 → 슬라이더가 가벼움)
        void ApplyParams()
        {
            if (rig == null) return;
            var p = Params;
            RenderSettings.ambientIntensity = setAmbInt = p.ambient;
            RenderSettings.reflectionIntensity = setReflInt = p.reflection;
            DynamicGI.UpdateEnvironment();
            var keys = new List<Light>(savedLights.Keys);
            foreach (var li in keys)
            {
                if (!li) continue;
                var (orig, set) = savedLights[li];
                if (!Mathf.Approximately(li.intensity, set)) continue;   // 다른 스크립트가 바꾼 조명은 건드리지 않음
                float v = orig * p.sceneLight;
                li.intensity = v; savedLights[li] = (orig, v);
            }
            if (grading)
            {
                grading.postExposure.Override(p.exposure); grading.contrast.Override(p.contrast); grading.saturation.Override(p.saturation);
                grading.temperature.Override(p.temperature); grading.tint.Override(p.tint);
                grading.lift.Override(V4(p.lift)); grading.gamma.Override(V4(p.gamma)); grading.gain.Override(V4(p.gain));
            }
            if (bloomFx) { bloomFx.enabled.Override(p.bloom > 0f); bloomFx.intensity.Override(p.bloom); }
        }

        void RemoveLook()
        {
            if (!rig) return;
            // 내가 넣은 값이 그대로일 때만 되돌림 (그 사이 다른 스크립트가 바꾼 건 그대로 둠)
            if (sky && RenderSettings.skybox == sky) { RenderSettings.skybox = savedSky; RenderSettings.ambientMode = savedAmbMode; }
            if (Mathf.Approximately(RenderSettings.ambientIntensity, setAmbInt)) RenderSettings.ambientIntensity = savedAmbInt;
            if (Mathf.Approximately(RenderSettings.reflectionIntensity, setReflInt)) RenderSettings.reflectionIntensity = savedReflInt;
            DynamicGI.UpdateEnvironment();
            foreach (var kv in savedLights)
                if (kv.Key && Mathf.Approximately(kv.Key.intensity, kv.Value.set)) kv.Key.intensity = kv.Value.original;
            savedLights.Clear();
            if (cam && softboxLayer >= 0 && cam.cullingMask == setCull) cam.cullingMask = savedCull;
            if (postTurnedOn && postLayer) postLayer.enabled = false;
            postTurnedOn = false;

            foreach (var r in rig.GetComponentsInChildren<Renderer>()) if (r.sharedMaterial) Destroy(r.sharedMaterial);
            Destroy(rig); rig = null;
            if (sky) { Destroy(sky); sky = null; }
            if (profile) { Destroy(profile); profile = null; }
            grading = null; bloomFx = null;
        }

        static PostProcessProfile BuildPost(LookPost p)
        {
            var prof = ScriptableObject.CreateInstance<PostProcessProfile>();
            prof.name = "Post (render look)";
            if (p.ambientOcclusion != null && p.ambientOcclusion.enabled)
            {
                var ao = prof.AddSettings<AmbientOcclusion>(); ao.enabled.Override(true);
                ao.mode.Override(AmbientOcclusionMode.MultiScaleVolumetricObscurance);
                ao.intensity.Override(p.ambientOcclusion.intensity); ao.thicknessModifier.Override(p.ambientOcclusion.thickness);
                ao.color.Override(RenderLookFiles.ToColor(p.ambientOcclusion.color));
            }
            if (p.autoExposure)
            {
                // 기준 밝기: 밝기 분포 50~95% 구간의 평균을 18% 회색(keyValue 1)에 맞춤 → 룩마다 하얗게 뜨거나 칙칙해지지 않음
                var ae = prof.AddSettings<AutoExposure>(); ae.enabled.Override(true);
                ae.filtering.Override(new Vector2(AeLow, AeHigh));
                ae.minLuminance.Override(-AeRange); ae.maxLuminance.Override(AeRange);
                ae.keyValue.Override(1f);
                ae.eyeAdaptation.Override(EyeAdaptation.Progressive);
                ae.speedUp.Override(3f); ae.speedDown.Override(2f);
            }
            var g = p.colorGrading ?? new LookColorGrading();
            var cg = prof.AddSettings<ColorGrading>(); cg.enabled.Override(true);
            cg.tonemapper.Override(g.tonemapper == "neutral" ? Tonemapper.Neutral : g.tonemapper == "none" ? Tonemapper.None : Tonemapper.ACES);
            var bl = prof.AddSettings<Bloom>();   // 슬라이더로 켜고 끔
            bl.enabled.Override(p.bloom != null && p.bloom.enabled);
            if (p.bloom != null) { bl.threshold.Override(p.bloom.threshold); bl.softKnee.Override(p.bloom.softKnee); }
            if (p.vignette != null && p.vignette.enabled)
            {
                var vg = prof.AddSettings<Vignette>(); vg.enabled.Override(true);
                vg.intensity.Override(p.vignette.intensity); vg.smoothness.Override(p.vignette.smoothness);
            }
            if (p.grain) { var gr = prof.AddSettings<Grain>(); gr.enabled.Override(true); gr.intensity.Override(0.06f); gr.size.Override(0.6f); gr.colored.Override(false); }
            return prof;
        }

        static int FreeLayer()
        {
            for (int i = 31; i >= 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
            Debug.LogWarning("[렌더 룩] 빈 레이어가 없어 소프트박스는 뺌");
            return -1;
        }

        // ================================================================ 원래대로 · 저장
        public void ResetAll()
        {
            RemoveLook(); look = null;
            States.Clear();
            RestoreMaterials();
            Notify("모두 원래대로");
        }

        // 부위의 지금 수치를 새 재질 프리셋으로 저장
        public void SaveMaterial(string partId, string name)
        {
            if (!States.TryGetValue(partId ?? "", out var s) || s.mode == "original") { Notify("저장할 재질이 없어요 (부위에 재질을 먼저 고르기)"); return; }
            var src = FindMaterial(s.preset);
            var data = new MaterialPresetData
            {
                displayName = string.IsNullOrEmpty(name) ? (src != null ? src.displayName + " (조절)" : "새 재질") : name,
                category = src?.category, note = "렌더 룩 콘솔에서 저장" + (src != null ? $" · {src.displayName} 기반" : ""),
                recipe = src?.recipe, ranges = src?.ranges ?? new MaterialRanges(), matchNames = src?.matchNames ?? new string[0],
                @base = new MaterialValues
                {
                    color = (float[])s.color.Clone(), metallic = s.metallic, roughness = s.roughness,
                    normalMap = src?.@base.normalMap, normalStrength = s.normalStrength, textureScale = s.tiling,
                    albedoMap = src?.@base.albedoMap, surfaceNormalMap = src?.@base.surfaceNormalMap, metalGlossMap = src?.@base.metalGlossMap,
                    albedoAlphaIsGloss = src != null && src.@base.albedoAlphaIsGloss,
                },
            };
            data.ranges = new MaterialRanges();   // 내가 고른 값이 범위에 잘리지 않게 (범위는 비움 = 제한 없음)
            string id = RenderLookFiles.Save("materials", src != null ? src.id + "_custom" : "custom",
                                             fid => { data.id = fid; return JsonUtility.ToJson(data, true); });
            Materials = RenderLookFiles.LoadMaterials();
            s.preset = id; States[partId] = s;
            Notify($"재질 저장: materials/{id}.json");
        }

        // 지금 조명 분위기 + 세부 수치를 새 룩 프리셋으로 저장
        public void SaveLook(string name)
        {
            if (look == null) { Notify("저장할 조명 분위기가 없어요 (먼저 고르기)"); return; }
            var data = JsonUtility.FromJson<LookPresetData>(JsonUtility.ToJson(look));   // 깊은 복사
            var p = Params;
            data.displayName = string.IsNullOrEmpty(name) ? look.displayName + " (조절)" : name;
            data.note = "렌더 룩 콘솔에서 저장 · " + look.displayName + " 기반";
            data.order = look.order + 1;
            data.environment.ambientIntensity = p.ambient; data.environment.reflectionIntensity = p.reflection;
            data.sceneLightScale = p.sceneLight;
            data.post.colorGrading.postExposure = p.exposure; data.post.colorGrading.contrast = p.contrast;
            data.post.colorGrading.saturation = p.saturation;
            data.post.colorGrading.temperature = p.temperature; data.post.colorGrading.tint = p.tint;
            data.post.colorGrading.lift = Tone(p.lift); data.post.colorGrading.gamma = Tone(p.gamma); data.post.colorGrading.gain = Tone(p.gain);
            data.post.bloom.enabled = p.bloom > 0f; data.post.bloom.intensity = p.bloom;
            string id = RenderLookFiles.Save("looks", look.id + "_custom", fid => { data.id = fid; return JsonUtility.ToJson(data, true); });
            Looks = RenderLookFiles.LoadLooks();
            look = Looks.Find(l => l.id == id) ?? look;
            Notify($"룩 저장: looks/{id}.json");
        }
    }
}
