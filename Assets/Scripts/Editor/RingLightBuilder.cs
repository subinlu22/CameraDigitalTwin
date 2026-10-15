using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// =============================================================
// [링라이트 발광 만들기] 메뉴: Tools > 링라이트 > 발광 링 만들기 (다시 만들기)
//
// 링라이트 모델(cam_s_merge/cam/pTorus1)의 안쪽 아래에 "빛나는 띠"를 붙이고,
// 그 아래로 벨트를 비추는 Spot Light를 하나 달아준다. (실제 장비의 링 조명처럼)
//
// - 링 모델의 위치/크기를 메시에서 직접 읽어서 맞추기 때문에, 링을 옮기거나 FBX를 바꿔도 메뉴만 다시 누르면 됨
// - 이미 있으면 지우고 새로 만든다 (Ctrl+Z로 되돌릴 수 있음)
// - 만들어지는 것:
//     Hierarchy  : RingLight_Glow (빛나는 띠 GlowBand + 아래 비추는 조명 RingSpot)
//     Assets     : Models/RingLight_GlowBand.asset (띠 모양), Materials/RingLight_Glow.mat (빛나는 재질)
// - 밝기 조절은 만들어진 뒤 Inspector에서 직접:
//     띠 밝기   → Materials/RingLight_Glow.mat 의 Emission 색 / 세기
//     벨트 조명 → RingLight_Glow/RingSpot 의 Light > Intensity
// =============================================================
public static class RingLightBuilder
{
    const string RingPath = "cam_s_merge/cam/pTorus1";               // 링라이트 모델 경로 (Hierarchy 기준)
    const string RootName = "RingLight_Glow";                        // 만들어질 오브젝트 이름
    const string MeshPath = "Assets/Models/RingLight_GlowBand.asset";
    const string MatPath = "Assets/Materials/RingLight_Glow.mat";

    const float BandHeight = 0.017f;         // 안쪽 벽에서 빛나는 띠 높이 (m)
    const float EmissionIntensity = 2.2f;    // 띠 밝기 (1 = 기본, 클수록 밝게)
    const float SpotIntensity = 5f;          // 벨트를 비추는 조명 세기
    const float SpotAngle = 80f;             // 조명 퍼지는 각도

    [MenuItem("Tools/링라이트/발광 링 만들기 (다시 만들기)")]
    static void Build()
    {
        GameObject ring = GameObject.Find(RingPath);
        if (ring == null)
        {
            EditorUtility.DisplayDialog("링라이트", $"'{RingPath}' 를 못 찾았어요. 링 모델 이름/위치가 바뀌었는지 확인하세요.", "확인");
            return;
        }

        // [1] 링 모델 메시에서 중심 / 안쪽 반지름 / 바닥 높이 / 바닥 바깥 반지름 읽기 (월드 좌표)
        Mesh ringMesh = ring.GetComponent<MeshFilter>().sharedMesh;
        Vector3 center = ring.GetComponent<Renderer>().bounds.center;
        float innerR = float.MaxValue, bottomY = float.MaxValue;
        var world = new List<Vector3>();
        foreach (Vector3 v in ringMesh.vertices)
        {
            Vector3 w = ring.transform.TransformPoint(v);
            world.Add(w);
            innerR = Mathf.Min(innerR, Flat(w - center).magnitude);
            bottomY = Mathf.Min(bottomY, w.y);
        }
        // 바닥면(가장 낮은 높이 근처) 점들 중 가장 안쪽 반지름 = 아래쪽 경사면이 끝나는 곳
        float bottomInnerR = float.MaxValue;
        foreach (Vector3 w in world)
            if (w.y < bottomY + 0.001f) bottomInnerR = Mathf.Min(bottomInnerR, Flat(w - center).magnitude);
        float wallBottomY = bottomY + (bottomInnerR - innerR); // 45도 경사라고 보고 안쪽 벽이 시작되는 높이

        // [2] 기존 것 지우기 (있으면)
        GameObject old = GameObject.Find(RootName);
        if (old != null) Undo.DestroyObjectImmediate(old);

        // [3] 빛나는 띠 메시 만들기: 안쪽 벽 아래 띠 + 아래쪽 경사면 (양면)
        float r0 = innerR - 0.0007f;                      // 링 벽에 딱 붙으면 깜빡거려서 아주 살짝 안쪽
        float[] pr = { r0, r0, r0, r0 + 0.001f, (r0 + bottomInnerR) * 0.5f, bottomInnerR };
        float[] py = { wallBottomY + BandHeight, wallBottomY + BandHeight * 0.5f, wallBottomY, wallBottomY - 0.0007f,
                       (wallBottomY + bottomY) * 0.5f, bottomY - 0.0005f };
        Mesh band = MakeBand(pr, py, 128);
        band.name = "RingLight_GlowBand";
        AssetDatabase.DeleteAsset(MeshPath);
        AssetDatabase.CreateAsset(band, MeshPath);

        // [4] 빛나는 재질 (없을 때만 새로 - 있으면 사용자가 조절한 값을 그대로 둠)
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Standard")) { name = "RingLight_Glow" };
            mat.color = new Color(0.95f, 0.97f, 1f);
            mat.SetFloat("_Glossiness", 0f);
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", new Color(0.95f, 0.98f, 1f) * EmissionIntensity);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            AssetDatabase.CreateAsset(mat, MatPath);
        }

        // [5] 오브젝트 만들기: RingLight_Glow (링 중심, 높이 0) ─ GlowBand / RingSpot
        var root = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(root, "링라이트 발광 만들기");
        root.transform.position = new Vector3(center.x, 0f, center.z);

        var glow = new GameObject("GlowBand");
        glow.transform.SetParent(root.transform, false);
        glow.AddComponent<MeshFilter>().sharedMesh = band;
        var mr = glow.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        var spotObj = new GameObject("RingSpot");
        spotObj.transform.SetParent(root.transform, false);
        spotObj.transform.localPosition = new Vector3(0f, bottomY + 0.004f, 0f);
        spotObj.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);   // 아래를 보게
        var spot = spotObj.AddComponent<Light>();
        spot.type = LightType.Spot;
        spot.spotAngle = SpotAngle;
        spot.innerSpotAngle = 30f;
        spot.range = 0.5f;
        spot.intensity = SpotIntensity;
        spot.color = new Color(0.93f, 0.97f, 1f);
        spot.shadows = LightShadows.Soft;
        spot.shadowStrength = 0.6f;
        spot.shadowNearPlane = 0.02f;

        Selection.activeGameObject = root;
        EditorSceneManager.MarkSceneDirty(root.scene);
        AssetDatabase.SaveAssets();
        Debug.Log($"[링라이트] 발광 링 생성 완료 (안쪽 반지름 {innerR:F4} m, 바닥 높이 {bottomY:F4}) → Ctrl+S로 씬 저장하세요");
    }

    [MenuItem("Tools/링라이트/발광 링 지우기")]
    static void Remove()
    {
        GameObject old = GameObject.Find(RootName);
        if (old == null) { Debug.Log("[링라이트] 지울 게 없어요"); return; }
        Undo.DestroyObjectImmediate(old);
        Debug.Log("[링라이트] 발광 링 삭제 (에셋 파일은 남아 있음) → Ctrl+S로 씬 저장하세요");
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    // 반지름/높이 목록(pr, py)을 한 바퀴 돌려서 띠 모양 메시를 만듦. 안쪽/바깥쪽 양면.
    static Mesh MakeBand(float[] pr, float[] py, int seg)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var tris = new List<int>();
        int p = pr.Length;
        for (int i = 0; i <= seg; i++)
        {
            float a = 2f * Mathf.PI * i / seg;
            var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            for (int k = 0; k < p; k++)
            {
                verts.Add(d * pr[k] + Vector3.up * py[k]);
                normals.Add((-d + Vector3.down * (k >= 3 ? 1.2f : 0.1f)).normalized); // 안쪽+아래를 향함
            }
        }
        for (int i = 0; i < seg; i++)
            for (int k = 0; k < p - 1; k++)
            {
                int a = i * p + k, b = (i + 1) * p + k;
                tris.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });   // 앞면
                tris.AddRange(new[] { a, a + 1, b, a + 1, b + 1, b });   // 뒷면
            }
        var m = new Mesh();
        m.SetVertices(verts);
        m.SetNormals(normals);
        m.SetTriangles(tris, 0);
        m.RecalculateBounds();
        return m;
    }
}
