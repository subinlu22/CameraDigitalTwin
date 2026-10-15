using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// =============================================================
// [벨트 천 UV 다시 만들기] 메뉴: Tools > 벨트 > 천 메시(UV) 다시 만들기
//
// 벨트 천(Conveyer_Belt2)이 BeltScroll로 "돌아가 보이려면", 천 무늬가 벨트 둘레를 따라
// 한 바퀴 이어지는 UV가 필요하다. FBX 원본 천 메시는 다른 부품과 텍스처 한 장(아틀라스)을 같이 써서
// 그대로는 무늬를 흘릴 수 없음 → 원본 메시를 복사해서 UV만 새로 계산한 메시를 만든다.
//
// - 원본 FBX(Assets/Models/cam_s_merge.fbx)는 안 건드림
// - 결과: Assets/Models/Conveyer_Belt2_Loop.asset 을 다시 만들고, 씬의 Conveyer_Belt2에 연결
// - 언제 쓰나: FBX를 다시 내보내서 벨트 천 모양이 바뀌었을 때 / Loop 메시가 망가졌을 때
// - TileLength 값을 바꾸면 BeltScroll 컴포넌트의 Tile Length도 같은 값으로 바꿔야 속도가 맞음
// =============================================================
public static class BeltLoopBuilder
{
    const string FbxPath = "Assets/Models/cam_s_merge.fbx";
    const string SourceMeshName = "Conveyer_Belt2";                 // FBX 안의 천 메시 이름
    const string ScenePath = "cam_s_merge/Conveyer_Belt2";          // 씬의 천 오브젝트
    const string OutPath = "Assets/Models/Conveyer_Belt2_Loop.asset";
    const float TileLength = 0.05f;                                 // 천 무늬 한 장이 차지하는 길이 (m)

    [MenuItem("Tools/벨트/천 메시(UV) 다시 만들기")]
    static void Build()
    {
        Mesh src = null;
        foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(FbxPath))
            if (o is Mesh m && m.name == SourceMeshName) { src = m; break; }
        GameObject target = GameObject.Find(ScenePath);
        if (src == null || target == null)
        {
            EditorUtility.DisplayDialog("벨트", $"'{SourceMeshName}' 메시나 '{ScenePath}' 오브젝트를 못 찾았어요.", "확인");
            return;
        }

        // 벨트는 옆에서 보면 "운동장 트랙" 모양: 위 직선 → 앞쪽 반원 → 아래 직선 → 뒤쪽 반원
        // 각 점이 트랙을 따라 몇 m 지점에 있는지(s)를 계산해서 UV의 세로값으로 씀
        Vector3[] v = src.vertices; Vector3[] nrm = src.normals; int[] tris = src.triangles;
        float R = src.bounds.extents.y, L = src.bounds.extents.z, W = src.bounds.size.x;
        float zc = L - R;                              // 반원 중심까지 거리
        float P = 4f * zc + 2f * Mathf.PI * R;         // 한 바퀴 길이

        System.Func<Vector3, float> S = p =>
        {
            float s;
            if (p.z > zc) s = zc + Mathf.Atan2(p.z - zc, p.y) * R;                                   // 앞쪽 반원
            else if (p.z < -zc) s = 3f * zc + Mathf.PI * R + Mathf.Atan2(-(p.z + zc), -p.y) * R;     // 뒤쪽 반원
            else if (p.y >= 0) s = p.z;                                                              // 윗면
            else s = zc + Mathf.PI * R + (zc - p.z);                                                 // 아랫면
            if (s > 2f * zc + Mathf.PI * R) s -= P;     // 이음매를 안 보이는 아랫면 가운데로
            return s;
        };

        // 삼각형마다 점을 따로 만들어서(이음매 걸친 삼각형만 한 바퀴 더해줌) UV가 늘어지지 않게
        var nv = new Vector3[tris.Length]; var nn = new Vector3[tris.Length]; var nuv = new Vector2[tris.Length]; var nt = new int[tris.Length];
        for (int i = 0; i < tris.Length; i += 3)
        {
            float[] ss = { S(v[tris[i]]), S(v[tris[i + 1]]), S(v[tris[i + 2]]) };
            float mx = Mathf.Max(ss[0], Mathf.Max(ss[1], ss[2]));
            for (int k = 0; k < 3; k++)
            {
                if (mx - ss[k] > P * 0.5f) ss[k] += P;
                Vector3 p = v[tris[i + k]];
                nv[i + k] = p; nn[i + k] = nrm[tris[i + k]];
                nuv[i + k] = new Vector2(p.x / W + 0.5f, ss[k] / TileLength);
                nt[i + k] = i + k;
            }
        }
        var mesh = new Mesh { name = "Conveyer_Belt2_Loop", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.vertices = nv; mesh.normals = nn; mesh.uv = nuv; mesh.triangles = nt;
        mesh.RecalculateBounds(); mesh.RecalculateTangents();

        // 기존 에셋이 있으면 내용만 바꿔서 GUID(연결)를 유지
        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(OutPath);
        if (existing != null) { EditorUtility.CopySerialized(mesh, existing); mesh = existing; EditorUtility.SetDirty(existing); }
        else AssetDatabase.CreateAsset(mesh, OutPath);
        AssetDatabase.SaveAssets();

        var mf = target.GetComponent<MeshFilter>();
        Undo.RecordObject(mf, "벨트 천 메시 연결");
        mf.sharedMesh = mesh;
        EditorSceneManager.MarkSceneDirty(target.scene);
        Debug.Log($"[벨트] 천 메시 UV 다시 만듦 (한 바퀴 {P:F3} m, 무늬 한 장 {TileLength} m) → Ctrl+S로 씬 저장하세요");
    }
}
