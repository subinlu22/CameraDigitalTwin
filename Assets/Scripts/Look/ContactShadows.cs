using System.Collections.Generic;
using UnityEngine;

// =============================================================
// [접지 그림자] 로봇 받침·벨트·스탠드·뚜껑이 밑판에 닿는 자리 바로 아래에 부드러운 그림자 판을 깔아 줌
//
// 왜: 실시간 그림자(빛)로는 물체가 바닥에 닿는 아주 얇은 부분의 진한 그림자를 못 그림
//     → 물체가 둥 떠 보임. 제품 렌더·게임에서 흔히 쓰는 "접지(컨택트) 그림자"로 채움
// 흐름: 켜질 때 대상마다 바닥에 닿는 모양(가로·세로 크기)을 재서 얇은 판을 밑판 바로 위에 깔고,
//       움직이는 뚜껑은 매 프레임 따라가며 바닥에서 뜬 만큼 옅어짐
// - 공장 룸(FactoryRoom) 안에 있어서 L 키로 룩을 끄면 같이 꺼짐
// =============================================================
public class ContactShadows : MonoBehaviour
{
    [System.Serializable]
    public class Target
    {
        public string objectName;          // 씬에서 찾을 이름 (예: base_so101_v2)
        [Range(0, 1)] public float opacity = 0.65f;
        public float spread = 0.03f;       // 물체 바닥 크기보다 사방으로 이만큼(m) 더 넓게
        [Range(0.05f, 1)] public float soft = 0.7f;
    }

    public string tableRendererName = "09_panel";   // 밑판 (윗면 높이를 여기서 읽음)
    public Material shadowMaterial;                 // SO101/ContactShadow
    public Target[] targets =
    {
        new Target { objectName = "base_so101_v2", opacity = 0.75f, spread = 0.035f, soft = 0.75f },
        new Target { objectName = "Conveyer_Belt1", opacity = 0.6f, spread = 0.04f, soft = 0.6f },
        new Target { objectName = "pCube1", opacity = 0.5f, spread = 0.03f, soft = 0.6f },
    };
    [Header("뚜껑 (움직이는 물체) — 이 이름 아래 Rigidbody마다 그림자")]
    public string movingParent = "number";
    [Range(0, 1)] public float movingOpacity = 0.7f;
    public float movingFadeHeight = 0.05f;          // 바닥에서 이만큼(m) 뜨면 그림자 0

    [Header("로봇 — 관절(링크)마다 바닥에 가까우면 그림자")]
    public string robotRoot = "so101_new_calib_camera";
    [Range(0, 1)] public float robotOpacity = 0.75f;
    public float robotFadeHeight = 0.06f;

    float tableY;
    readonly List<(Renderer[] parts, Transform blob, MaterialPropertyBlock mpb, Renderer rend)> links = new List<(Renderer[], Transform, MaterialPropertyBlock, Renderer)>();
    readonly List<GameObject> made = new List<GameObject>();
    readonly List<(Rigidbody rb, Transform blob, MaterialPropertyBlock mpb, Renderer rend)> moving = new List<(Rigidbody, Transform, MaterialPropertyBlock, Renderer)>();
    static readonly int OpacityId = Shader.PropertyToID("_Opacity"), SoftId = Shader.PropertyToID("_Soft");

    void OnEnable()
    {
        var table = GameObject.Find(tableRendererName);
        if (table == null || shadowMaterial == null) { Debug.LogWarning("[접지 그림자] 밑판이나 재질이 없음"); return; }
        tableY = table.GetComponent<Renderer>().bounds.max.y;

        foreach (var t in targets)
        {
            var go = GameObject.Find(t.objectName);
            if (go == null) continue;
            if (!TryFootprint(go, out Bounds b)) continue;
            var blob = MakeBlob($"_Shadow_{t.objectName}", out var rend);
            Place(blob, b, t.spread, 0f);
            var mpb = new MaterialPropertyBlock(); mpb.SetFloat(OpacityId, t.opacity); mpb.SetFloat(SoftId, t.soft); rend.SetPropertyBlock(mpb);
        }

        var robot = GameObject.Find(robotRoot);
        if (robot)
            foreach (var body in robot.GetComponentsInChildren<ArticulationBody>())
            {
                var parts = new List<Renderer>();
                foreach (var r in body.GetComponentsInChildren<Renderer>()) if (r.GetComponentInParent<ArticulationBody>() == body) parts.Add(r);
                if (parts.Count == 0) continue;
                var blob = MakeBlob($"_Shadow_{body.name}", out var rend);
                var mpb = new MaterialPropertyBlock(); mpb.SetFloat(SoftId, 0.8f);
                links.Add((parts.ToArray(), blob, mpb, rend));
            }

        foreach (var rb in FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
        {
            if (rb.transform.parent == null || rb.transform.parent.name != movingParent) continue;
            var blob = MakeBlob($"_Shadow_{rb.name}", out var rend);
            var mpb = new MaterialPropertyBlock(); mpb.SetFloat(SoftId, 0.8f);
            moving.Add((rb, blob, mpb, rend));
        }
        LateUpdate();
    }

    void OnDisable()
    {
        foreach (var g in made) if (g) Destroy(g);
        made.Clear(); moving.Clear(); links.Clear();
    }

    void LateUpdate()
    {
        // 로봇 링크: 바닥에 가까울수록 진하게 (집게가 바닥에 닿으면 진한 그림자)
        foreach (var l in links)
        {
            Bounds b = l.parts[0].bounds;
            for (int i = 1; i < l.parts.Length; i++) b.Encapsulate(l.parts[i].bounds);
            float h = b.min.y - tableY;
            float a = robotOpacity * Mathf.Clamp01(1f - h / robotFadeHeight);
            bool show = a > 0.01f;
            l.blob.gameObject.SetActive(show);
            if (!show) continue;
            Place(l.blob, b, 0.012f, 0f);
            l.mpb.SetFloat(OpacityId, a); l.rend.SetPropertyBlock(l.mpb);
        }

        foreach (var m in moving)
        {
            if (m.rb == null) { if (m.blob) m.blob.gameObject.SetActive(false); continue; }
            var col = m.rb.GetComponentInChildren<Collider>();
            Bounds b = col && col.enabled ? col.bounds : new Bounds(m.rb.position, Vector3.one * 0.04f);
            float h = b.min.y - tableY;
            float a = movingOpacity * Mathf.Clamp01(1f - h / movingFadeHeight);
            bool show = a > 0.01f && h > -0.01f;
            m.blob.gameObject.SetActive(show);
            if (!show) continue;
            Place(m.blob, b, 0.012f, 0f);
            m.mpb.SetFloat(OpacityId, a); m.rend.SetPropertyBlock(m.mpb);
        }
    }

    // 대상 아래 모든 렌더러를 합친 크기 (바닥 그림자 모양)
    static bool TryFootprint(GameObject go, out Bounds b)
    {
        b = default; bool any = false;
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
        }
        return any;
    }

    Transform MakeBlob(string name, out Renderer rend)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = name; go.hideFlags = HideFlags.DontSave;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(transform, true);
        go.transform.rotation = Quaternion.Euler(90, 0, 0);
        go.layer = 4;   // Water: 거울 반사 카메라에 안 찍히게
        rend = go.GetComponent<MeshRenderer>();
        rend.sharedMaterial = shadowMaterial;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; rend.receiveShadows = false;
        rend.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; rend.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        made.Add(go);
        return go.transform;
    }

    void Place(Transform blob, Bounds b, float spread, float yaw)
    {
        blob.position = new Vector3(b.center.x, tableY + 0.0005f, b.center.z);
        blob.rotation = Quaternion.Euler(90, yaw, 0);
        Vector3 s = blob.parent ? blob.parent.lossyScale : Vector3.one;
        blob.localScale = new Vector3((b.size.x + spread * 2) / s.x, (b.size.z + spread * 2) / s.z, 1);
    }
}
