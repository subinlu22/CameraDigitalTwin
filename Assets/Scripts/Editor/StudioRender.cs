using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// =============================================================
// [스튜디오 렌더] 메뉴: Tools > 스튜디오 렌더 > 가로 4K / 세로
//
// 포트폴리오용 "스튜디오 사진" 찍기. Scene 창에서 보이는 각도 그대로,
// 밝은 회색 배경(사이클로라마) + 스튜디오 조명 3개로 장비만 깔끔하게 렌더해서 PNG로 저장한다.
//
// - 원래 씬은 전혀 안 건드림: 장비(cam_s_merge, RingLight_Glow)를 "보이지 않는 임시 씬"에 복사해서 거기서 찍고 지움
//   → 로봇팔·바닥·기존 조명은 사진에 안 나옴, 씬이 수정됨(*) 표시도 안 생김
// - 링라이트가 있으면 빛 번짐(블룸)도 자동으로 입혀줌
// - 저장 위치: 프로젝트 폴더/Renders/studio_날짜_시간.png (저장 후 탐색기가 열림)
//
// 사용법: Scene 창에서 마우스로 원하는 각도를 잡는다 → 메뉴 클릭 → 몇 초 기다리기
//   (Scene 창 각도 잡기: 우클릭 드래그 = 둘러보기, 휠 = 확대/축소, 휠 클릭 드래그 = 옆으로 이동,
//    Alt + 좌클릭 드래그 = 물체 중심으로 돌기, 오브젝트 선택 후 F = 그 물체로 화면 맞추기)
// =============================================================
public static class StudioRender
{
    static readonly string[] Subjects = { "cam_s_merge", "RingLight_Glow" }; // 사진에 넣을 오브젝트 (Hierarchy 맨 위 이름)
    const string GlowMatPath = "Assets/Materials/RingLight_Glow.mat";        // 빛 번짐을 계산할 발광 재질

    // ---- 사진 느낌 조절값 ----
    static readonly Color BackdropColor = new Color(0.80f, 0.79f, 0.80f); // 배경(바닥+벽) 색
    const float KeyIntensity = 0.95f;   // 주 조명(그림자 만드는 빛) 세기
    const float FillIntensity = 0.35f;  // 그림자 쪽을 살짝 밝혀주는 빛
    const float RimIntensity = 0.45f;   // 뒤에서 윤곽을 살려주는 빛
    const float BloomStrength = 0.85f;  // 링라이트 빛 번짐 세기 (0이면 끔)
    const float Vignette = 0.12f;       // 가장자리 살짝 어둡게 (0이면 끔)

    [MenuItem("Tools/스튜디오 렌더/가로 4K (3840x2160)")]
    static void RenderWide() => Render(3840, 2160);

    [MenuItem("Tools/스튜디오 렌더/세로 (1728x2160)")]
    static void RenderTall() => Render(1728, 2160);

    static void Render(int W, int H)
    {
        SceneView sv = SceneView.lastActiveSceneView;
        if (sv == null || sv.camera == null)
        {
            EditorUtility.DisplayDialog("스튜디오 렌더", "Scene 창을 한 번 클릭해서 열어둔 뒤 다시 눌러주세요.", "확인");
            return;
        }

        Scene stage = EditorSceneManager.NewPreviewScene();   // 눈에 안 보이는 임시 씬
        float oldShadowDist = QualitySettings.shadowDistance;
        ShadowResolution oldShadowRes = QualitySettings.shadowResolution;
        Material glowMat = AssetDatabase.LoadAssetAtPath<Material>(GlowMatPath);
        Color oldEmission = glowMat != null ? glowMat.GetColor("_EmissionColor") : Color.black;
        var temps = new List<UnityEngine.Object>();
        bool lightingOverridden = false;

        try
        {
            EditorUtility.DisplayProgressBar("스튜디오 렌더", "장면 준비 중...", 0.1f);

            // [1] 장비 복사 → 임시 씬으로
            Bounds bounds = new Bounds();
            bool hasBounds = false;
            foreach (string name in Subjects)
            {
                GameObject src = GameObject.Find(name);
                if (src == null) continue;
                GameObject copy = UnityEngine.Object.Instantiate(src, src.transform.position, src.transform.rotation);
                copy.transform.localScale = src.transform.localScale;
                SceneManager.MoveGameObjectToScene(copy, stage);
                foreach (Renderer r in copy.GetComponentsInChildren<Renderer>())
                {
                    if (!hasBounds) { bounds = r.bounds; hasBounds = true; } else bounds.Encapsulate(r.bounds);
                }
            }
            if (!hasBounds) throw new Exception("사진 찍을 오브젝트(cam_s_merge)를 못 찾았어요.");

            // [2] 카메라: Scene 창 각도 그대로
            // Scene 창이 화면에 안 그려진 상태여도 맞게: 중심점(pivot)·회전·거리로 카메라 위치를 직접 계산
            Quaternion svRot = sv.rotation;
            Vector3 svPos = sv.pivot - svRot * Vector3.forward * sv.cameraDistance;
            var camGo = new GameObject("StudioCam");
            SceneManager.MoveGameObjectToScene(camGo, stage);
            Camera cam = camGo.AddComponent<Camera>();
            cam.scene = stage;                                   // 임시 씬만 찍음
            cam.transform.SetPositionAndRotation(svPos, svRot);
            cam.fieldOfView = sv.cameraSettings.fieldOfView;
            cam.nearClipPlane = 0.01f; cam.farClipPlane = 100f;
            cam.allowHDR = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = BackdropColor * 0.8f;

            // [3] 배경 + 조명 (카메라가 보는 방향 쪽에 벽이 오도록 돌림)
            float yaw = svRot.eulerAngles.y;
            var studio = new GameObject("Studio");
            SceneManager.MoveGameObjectToScene(studio, stage);
            studio.transform.SetPositionAndRotation(new Vector3(bounds.center.x, bounds.min.y, bounds.center.z), Quaternion.Euler(0f, yaw, 0f));
            Mesh cyc = MakeCyclorama();
            temps.Add(cyc);
            var cycGo = new GameObject("Backdrop");
            cycGo.transform.SetParent(studio.transform, false);
            cycGo.transform.localPosition = new Vector3(0f, -0.001f, 1.0f);
            cycGo.AddComponent<MeshFilter>().sharedMesh = cyc;
            var cycMat = new Material(Shader.Find("Standard")) { color = BackdropColor };
            cycMat.SetFloat("_Glossiness", 0.12f);
            temps.Add(cycMat);
            var cycR = cycGo.AddComponent<MeshRenderer>();
            cycR.sharedMaterial = cycMat;
            cycR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            AddLight(studio, "Key", KeyIntensity, new Color(1f, 0.97f, 0.92f), new Vector3(52, 140, 0), true);
            AddLight(studio, "Fill", FillIntensity, new Color(0.85f, 0.9f, 1f), new Vector3(25, -60, 0), false);
            AddLight(studio, "Rim", RimIntensity, Color.white, new Vector3(20, 330, 0), false);

            // [4] 임시 씬 전용 조명 환경 (원래 씬 설정은 그대로)
            Unsupported.SetOverrideLightingSettings(stage);
            lightingOverridden = true;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.64f, 0.68f);
            RenderSettings.ambientEquatorColor = new Color(0.52f, 0.52f, 0.54f);
            RenderSettings.ambientGroundColor = new Color(0.32f, 0.31f, 0.31f);
            RenderSettings.fog = false;
            QualitySettings.shadowDistance = 4f;                 // 그림자를 선명하게 (끝나면 원래대로)
            QualitySettings.shadowResolution = ShadowResolution.VeryHigh;

            // [5] 본 렌더: 2배 크기로 찍고 줄여서 계단현상 제거
            EditorUtility.DisplayProgressBar("스튜디오 렌더", "렌더링 중...", 0.4f);
            Color[] img = RenderPixels(cam, W, H, 2, 8);

            // [6] 빛 번짐: 작은 크기로 "링 켬 / 링 끔" 두 번 찍어서 차이(= 빛나는 부분)만 흐리게 해서 더함
            if (glowMat != null && BloomStrength > 0f)
            {
                EditorUtility.DisplayProgressBar("스튜디오 렌더", "빛 번짐 계산 중...", 0.7f);
                int lw = W / 8, lh = H / 8;
                Color[] on = RenderPixels(cam, lw, lh, 2, 4);
                glowMat.SetColor("_EmissionColor", Color.black);
                Color[] off = RenderPixels(cam, lw, lh, 2, 4);
                glowMat.SetColor("_EmissionColor", oldEmission);
                var glow = new Color[on.Length];
                for (int i = 0; i < on.Length; i++)
                    glow[i] = new Color(Mathf.Max(0, on[i].r - off[i].r), Mathf.Max(0, on[i].g - off[i].g), Mathf.Max(0, on[i].b - off[i].b));
                Color[] small = Blur(glow, lw, lh, 2);
                Color[] wide = Blur(glow, lw, lh, 9);
                for (int i = 0; i < glow.Length; i++) glow[i] = small[i] * 1.0f + wide[i] * 1.6f;
                Composite(img, W, H, glow, lw, lh, BloomStrength);
            }

            // [7] 가장자리 살짝 어둡게
            if (Vignette > 0f) ApplyVignette(img, W, H, Vignette);

            // [8] 저장
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.SetPixels(img);
            tex.Apply();
            string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Renders");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"studio_{DateTime.Now:yyyyMMdd_HHmmss}_{W}x{H}.png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            Debug.Log($"[스튜디오 렌더] 저장: {path}");
            EditorUtility.RevealInFinder(path);
        }
        catch (Exception e)
        {
            Debug.LogError("[스튜디오 렌더] 실패: " + e.Message);
        }
        finally
        {
            if (glowMat != null) glowMat.SetColor("_EmissionColor", oldEmission);
            if (lightingOverridden) Unsupported.RestoreOverrideLightingSettings();
            QualitySettings.shadowDistance = oldShadowDist;
            QualitySettings.shadowResolution = oldShadowRes;
            EditorSceneManager.ClosePreviewScene(stage);
            foreach (var o in temps) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            EditorUtility.ClearProgressBar();
        }
    }

    // 카메라로 (W*ss)x(H*ss) 크기로 찍은 뒤 WxH로 줄여서 픽셀 배열로 돌려줌
    static Color[] RenderPixels(Camera cam, int W, int H, int ss, int msaa)
    {
        var big = new RenderTexture(W * ss, H * ss, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = msaa };
        var rt = new RenderTexture(W, H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var prev = RenderTexture.active;
        cam.targetTexture = big;
        cam.Render();
        cam.targetTexture = null;
        Graphics.Blit(big, rt);
        RenderTexture.active = rt;
        var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        Color[] px = tex.GetPixels();
        UnityEngine.Object.DestroyImmediate(tex);
        big.Release(); rt.Release();
        UnityEngine.Object.DestroyImmediate(big); UnityEngine.Object.DestroyImmediate(rt);
        return px;
    }

    // 상자 흐림 3번 = 가우시안 흐림과 비슷
    static Color[] Blur(Color[] src, int w, int h, int radius)
    {
        Color[] a = (Color[])src.Clone(), b = new Color[src.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxLine(a, b, w, h, radius, true);
            BoxLine(b, a, w, h, radius, false);
        }
        return a;
    }

    static void BoxLine(Color[] src, Color[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        float inv = 1f / (2 * r + 1);
        for (int l = 0; l < lines; l++)
        {
            for (int i = 0; i < len; i++)
            {
                Color sum = Color.clear;
                for (int k = -r; k <= r; k++)
                {
                    int j = Mathf.Clamp(i + k, 0, len - 1);
                    sum += horizontal ? src[l * w + j] : src[j * w + l];
                }
                if (horizontal) dst[l * w + i] = sum * inv; else dst[i * w + l] = sum * inv;
            }
        }
    }

    // 작은 빛 번짐 이미지를 크게 늘려서 "스크린" 방식으로 겹침 (밝은 곳은 더 밝게, 하얗게 날아가지는 않게)
    static void Composite(Color[] img, int W, int H, Color[] glow, int lw, int lh, float strength)
    {
        for (int y = 0; y < H; y++)
        {
            float gy = Mathf.Clamp((y + 0.5f) * lh / H - 0.5f, 0, lh - 1);
            int y0 = (int)gy, y1 = Mathf.Min(y0 + 1, lh - 1);
            float ty = gy - y0;
            for (int x = 0; x < W; x++)
            {
                float gx = Mathf.Clamp((x + 0.5f) * lw / W - 0.5f, 0, lw - 1);
                int x0 = (int)gx, x1 = Mathf.Min(x0 + 1, lw - 1);
                float tx = gx - x0;
                Color g = Color.Lerp(Color.Lerp(glow[y0 * lw + x0], glow[y0 * lw + x1], tx),
                                     Color.Lerp(glow[y1 * lw + x0], glow[y1 * lw + x1], tx), ty) * strength;
                Color c = img[y * W + x];
                img[y * W + x] = new Color(1 - (1 - c.r) * (1 - Mathf.Clamp01(g.r)),
                                           1 - (1 - c.g) * (1 - Mathf.Clamp01(g.g)),
                                           1 - (1 - c.b) * (1 - Mathf.Clamp01(g.b)), 1);
            }
        }
    }

    static void ApplyVignette(Color[] img, int W, int H, float amount)
    {
        float cx = W * 0.5f, cy = H * 0.5f, maxD = Mathf.Sqrt(cx * cx + cy * cy);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxD;
                float k = 1f - amount * d * d;
                Color c = img[y * W + x];
                img[y * W + x] = new Color(c.r * k, c.g * k, c.b * k, 1);
            }
    }

    static void AddLight(GameObject parent, string name, float intensity, Color color, Vector3 euler, bool shadows)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        go.transform.localRotation = Quaternion.Euler(euler);
        var l = go.AddComponent<Light>();
        l.type = LightType.Directional;
        l.intensity = intensity;
        l.color = color;
        l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
        l.shadowStrength = 0.55f;
        l.shadowBias = 0.01f;
        l.shadowNormalBias = 0.1f;
    }

    // 바닥 → 둥글게 휘어 → 벽으로 이어지는 배경판 (사진관 배경지처럼 경계선이 안 보임)
    static Mesh MakeCyclorama()
    {
        const float W = 12f, D = 6f, R = 1.2f, Hgt = 5f;
        const int arc = 24;
        var prof = new List<Vector3> { new Vector3(0, 0, -D), Vector3.zero };
        var pn = new List<Vector3> { Vector3.up, Vector3.up };
        for (int i = 1; i <= arc; i++)
        {
            float a = Mathf.PI * 0.5f * i / arc;
            prof.Add(new Vector3(0, R - R * Mathf.Cos(a), R * Mathf.Sin(a)));
            pn.Add(new Vector3(0, Mathf.Cos(a), -Mathf.Sin(a)));
        }
        prof.Add(new Vector3(0, Hgt, R)); pn.Add(Vector3.back);
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var tris = new List<int>();
        for (int s = 0; s <= 2; s++)
            for (int p = 0; p < prof.Count; p++) { verts.Add(new Vector3(-W / 2 + W * s / 2, prof[p].y, prof[p].z)); norms.Add(pn[p]); }
        int n = prof.Count;
        for (int s = 0; s < 2; s++)
            for (int p = 0; p < n - 1; p++)
            {
                int a = s * n + p, b = (s + 1) * n + p;
                tris.AddRange(new[] { a, a + 1, b, b, a + 1, b + 1 });
            }
        var m = new Mesh();
        m.SetVertices(verts); m.SetNormals(norms); m.SetTriangles(tris, 0); m.RecalculateBounds();
        return m;
    }
}
