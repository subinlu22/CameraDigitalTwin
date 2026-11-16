using UnityEngine;

// =============================================================
// [연습용 뚜껑] 로봇 없이 연습할 때, 집게 바로 아래(두 손가락 사이)에 뚜껑 하나를 놓아 줌
//
// 왜: 저장한 자세(흰 팔로 실제 뚜껑을 집으며 저장)를 유니티에서 재생하면, 집는 자리에 유니티 뚜껑이 없어서 빈손으로 움직임
//     → 집게를 닫기 바로 전에 손가락 사이 아래 면(탁자·벨트) 위에 뚜껑을 놓음 → 닫으면 GripperGrab이 잡고, 벌리면 놓음
// 흐름: PoseLibrary 재생이 "다음 자세가 집게를 닫는 자세"일 때 PlaceBetweenFingers()를 부름 (연습 모드에서만, 실물 모드는 실제 뚜껑)
//   ① 두 손가락 상자(FingerBox) 가운데를 구함
//   ② 거기서 아래로 광선을 쏴서 로봇이 아닌 첫 면(탁자·벨트)을 찾음
//   ③ 뚜껑 견본(DigitalTwinReceiver.lidTemplatesParent의 첫 번째)을 복사해서 그 면 위에 올려놓음 (물리 켬, 중력)
//   ④ 연습용 뚜껑은 하나만 — 새로 놓으면 예전 것은 지움
// ※ 손가락 끝이 면에서 뚜껑 두께보다 높으면 뚜껑이 손가락 사이에 안 걸려 못 잡을 수 있음 (그땐 로그로 높이 알려 줌)
// =============================================================
public class PracticeLid : MonoBehaviour
{
    public string gripperLink = "gripper_link";
    public string movingJawLink = "moving_jaw_so101_v1_link";
    public string fingerBoxName = "FingerBox";
    public float maxDropM = 0.3f;          // 이 거리 안에 면이 없으면 안 놓음

    GameObject lid;                        // 지금 놓여 있는 연습용 뚜껑
    BoxCollider fingerA, fingerB;

    public bool Has => lid != null;

    // 손가락 상자 찾기 (플레이 때 만들어지는 상자라 처음 쓸 때 찾음)
    void FindFingers()
    {
        foreach (var t in GetComponentsInChildren<Transform>())
        {
            if (t.name == gripperLink && t.Find(fingerBoxName)) fingerA = t.Find(fingerBoxName).GetComponent<BoxCollider>();
            if (t.name == movingJawLink && t.Find(fingerBoxName)) fingerB = t.Find(fingerBoxName).GetComponent<BoxCollider>();
        }
        if (!fingerA || !fingerB) Debug.LogWarning("[연습 뚜껑] 손가락 상자(FingerBox)를 못 찾음 → 연습 뚜껑 안 놓음");
    }

    // 두 손가락 사이 아래 면 위에 뚜껑 놓기. 성공하면 true
    public bool PlaceBetweenFingers()
    {
        if (!fingerA || !fingerB) FindFingers();
        if (!fingerA || !fingerB) return false;
        var template = Template();
        if (!template) { Debug.LogWarning("[연습 뚜껑] 뚜껑 견본이 없음 (DigitalTwinReceiver의 lidTemplatesParent)"); return false; }

        Vector3 mid = (fingerA.bounds.center + fingerB.bounds.center) * 0.5f;
        float tipY = Mathf.Min(fingerA.bounds.min.y, fingerB.bounds.min.y);   // 손가락 끝 높이
        if (!FindSurface(mid, out float surfaceY)) { Debug.LogWarning("[연습 뚜껑] 집게 아래에 탁자·벨트 면이 없음"); return false; }

        if (lid) Destroy(lid);
        lid = Instantiate(template, new Vector3(mid.x, surfaceY + 0.05f, mid.z), template.transform.rotation);
        lid.transform.localScale = template.transform.lossyScale;
        lid.name = "연습뚜껑";
        lid.SetActive(true);
        foreach (var m in lid.GetComponents<LidMover>()) Destroy(m);         // 벨트 따라 흘러가지 않게

        var col = lid.GetComponentInChildren<Collider>();
        if (!col && lid.GetComponent<MeshFilter>()) col = lid.AddComponent<MeshCollider>();
        if (col is MeshCollider mc) mc.convex = true;                       // 움직이는 물체는 볼록 모양이어야 함
        var rb = lid.GetComponent<Rigidbody>();
        if (!rb) rb = lid.AddComponent<Rigidbody>();

        // 바닥이 면에 딱 닿게 높이 맞춤 (+1mm), 그다음 물리 켬
        Physics.SyncTransforms();
        if (col)
        {
            float bottom = col.bounds.min.y;
            lid.transform.position += Vector3.up * (surfaceY + 0.001f - bottom);
        }
        rb.isKinematic = false;
        rb.useGravity = true;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        Physics.SyncTransforms();

        float lidTop = col ? col.bounds.max.y : surfaceY;
        Debug.Log($"[연습 뚜껑] 손가락 사이에 놓음 (손가락 끝 {(tipY - surfaceY) * 100:F1}cm 위, 뚜껑 높이 {(lidTop - surfaceY) * 100:F1}cm)"
                  + (tipY > lidTop ? " ⚠ 손가락 끝이 뚜껑보다 높아서 못 잡을 수 있음" : ""));
        return true;
    }

    public void Clear() { if (lid) Destroy(lid); lid = null; }

    // 아래로 광선 → 로봇·물리 물체가 아닌 첫 면
    bool FindSurface(Vector3 from, out float y)
    {
        y = 0f;
        float best = float.MaxValue;
        foreach (var h in Physics.RaycastAll(from, Vector3.down, maxDropM, ~0, QueryTriggerInteraction.Ignore))
        {
            var c = h.collider;
            if (c.attachedRigidbody || c.attachedArticulationBody || c.GetComponentInParent<ArticulationBody>()) continue;
            if (h.distance < best) { best = h.distance; y = h.point.y; }
        }
        return best < float.MaxValue;
    }

    static GameObject Template()
    {
        var rx = FindFirstObjectByType<DigitalTwinReceiver>();
        if (!rx || !rx.lidTemplatesParent || rx.lidTemplatesParent.childCount == 0) return null;
        return rx.lidTemplatesParent.GetChild(0).gameObject;
    }
}
