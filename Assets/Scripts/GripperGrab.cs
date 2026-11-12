using UnityEngine;

// =============================================================
// [집게로 집기 v2] 두 손가락이 뚜껑에 닿은 채로 더 쥐면 잡고, 벌리면 놓음
//
// 흐름: 물리 단계마다
//   ① 손가락 두 개(FingerBox)가 모두 닿아 있는 뚜껑(Rigidbody)을 찾음
//   ② 그 상태에서 집게 목표 각도가 지금 각도보다 squeezeDeg 넘게 작으면(= 더 쥐는 중, 뚜껑에 막혀 멈춤) → 잡음
//      잡으면 뚜껑 충돌을 끄고 집게에 붙어 다님(물리 끔), 움직이는 집게는 그 각도에서 멈춤
//   ③ 목표 각도가 잡은 각도보다 releaseDeg 넘게 커지면(= 벌림) → 반듯이 세워 바로 아래 면(벨트·탁자)에 놓음
// ※ 손가락 충돌 모양: 원래 볼록 덩어리(DroopRobotPose가 플레이 때 자동으로 붙임)가 손가락 사이를 막아서
//   그건 끄고, 손가락 크기의 얇은 상자(FingerBox)로 바꿈 → 뚜껑이 손가락에 자연스럽게 막히고 밀림
// ※ 이미 닫힌 집게로 뚜껑을 건드리면 안 잡힘 (손가락 둘 다 닿아야 하고, 뚜껑 중심이 두 손가락 사이에 있어야 함)
// =============================================================
public class GripperGrab : MonoBehaviour
{
    [Header("링크 이름")]
    public string gripperLink = "gripper_link";
    public string movingJawLink = "moving_jaw_so101_v1_link";
    public string fingerBoxName = "FingerBox";

    [Header("잡기·놓기 (도)")]
    public float squeezeDeg = 2f;     // 목표가 지금 각도보다 이만큼 더 닫혀 있으면 "쥐는 중"
    public float releaseDeg = 4f;     // 목표가 잡은 각도보다 이만큼 더 열리면 놓기
    public float touchMargin = 0.004f;  // 손가락이 이 거리(m) 안이면 닿은 걸로 봄
    public float betweenDist = 0.015f;  // 뚜껑 중심이 두 손가락을 잇는 선에서 이 거리(m) 안이어야 "사이에 있음"
    public float dropSearch = 0.15f;  // 놓을 때 이 거리(m) 안에 아래 면이 있으면 그 위에 반듯이 올려놓음

    [Header("상태 (보기용)")]
    public string holding = "";
    public string touching = "";

    Transform grip;
    ArticulationBody jaw;
    BoxCollider fingerA, fingerB;
    Rigidbody held;
    Vector3 heldLocalPos;
    Quaternion heldLocalRot;
    float heldAngle, jawLowerLimit;
    readonly Collider[] hitsA = new Collider[16], hitsB = new Collider[16];

    void Start()
    {
        foreach (var t in GetComponentsInChildren<Transform>())
        {
            if (t.name == gripperLink) grip = t;
            if (t.name == movingJawLink) jaw = t.GetComponent<ArticulationBody>();
        }
        if (grip == null || jaw == null) { Debug.LogWarning("[집기] 집게 링크를 못 찾음"); enabled = false; return; }
        var a = grip.Find(fingerBoxName); var b = jaw.transform.Find(fingerBoxName);
        fingerA = a ? a.GetComponent<BoxCollider>() : null;
        fingerB = b ? b.GetComponent<BoxCollider>() : null;
        if (fingerA == null || fingerB == null) { Debug.LogWarning("[집기] 손가락 상자(FingerBox)가 없음"); enabled = false; return; }

        // 손가락 사이를 막는 볼록 충돌 모양 끄기 (DroopRobotPose가 Awake에서 붙인 것)
        foreach (var c in grip.GetComponentsInChildren<MeshCollider>(true))
            if (c.name.StartsWith("wrist_roll_follower") || c.name.StartsWith("moving_jaw")) c.enabled = false;
        jawLowerLimit = jaw.xDrive.lowerLimit;
    }

    void FixedUpdate()
    {
        float target = jaw.xDrive.target;                       // 리더(또는 슬라이더)가 넣은 집게 목표 각도
        float angle = jaw.jointPosition[0] * Mathf.Rad2Deg;     // 실제 집게 각도 (뚜껑에 막히면 목표보다 큼)

        if (held != null)
        {
            held.MovePosition(grip.TransformPoint(heldLocalPos));   // 집게에 붙은 자리로
            held.MoveRotation(grip.rotation * heldLocalRot);
            if (target > heldAngle + releaseDeg) Release();
            return;
        }

        // ① 두 손가락 모두 닿은 뚜껑 찾기
        Rigidbody both = TouchedByBoth();
        touching = both ? both.name : "";
        // ② 쥐는 중 + 뚜껑이 두 손가락 사이에 있음 → 잡기 (닿자마자 잡아서 뚜껑이 끼어 튀어 나가기 전에)
        if (both != null && target < angle - squeezeDeg && Between(both)) Grab(both, angle);
    }

    // 뚜껑 중심이 두 손가락 사이(손가락을 잇는 선 위, 양 끝 사이)에 있는지 — 닫힌 집게로 위에서 누르면 아님
    bool Between(Rigidbody rb)
    {
        Vector3 a = fingerA.transform.TransformPoint(fingerA.center);
        Vector3 b = fingerB.transform.TransformPoint(fingerB.center);
        Vector3 ab = b - a;
        if (ab.sqrMagnitude < 1e-6f) return false;
        Vector3 p = rb.worldCenterOfMass;
        float t = Vector3.Dot(p - a, ab) / ab.sqrMagnitude;
        float dist = Vector3.Distance(p, a + ab * Mathf.Clamp01(t));
        return t > 0.1f && t < 0.9f && dist < betweenDist;
    }

    Rigidbody TouchedByBoth()
    {
        int na = Overlap(fingerA, hitsA), nb = Overlap(fingerB, hitsB);
        for (int i = 0; i < na; i++)
        {
            var rb = hitsA[i].attachedRigidbody;
            if (rb == null || rb.isKinematic || rb.GetComponentInParent<ArticulationBody>() != null) continue;
            for (int k = 0; k < nb; k++) if (hitsB[k].attachedRigidbody == rb) return rb;
        }
        return null;
    }

    // 손가락 상자를 touchMargin만큼 키워서 겹치는 것 찾기
    int Overlap(BoxCollider box, Collider[] buf)
    {
        Transform t = box.transform;
        Vector3 c = t.TransformPoint(box.center);
        Vector3 half = Vector3.Scale(box.size * 0.5f, t.lossyScale) + Vector3.one * touchMargin;
        return Physics.OverlapBoxNonAlloc(c, half, buf, t.rotation, ~0, QueryTriggerInteraction.Ignore);
    }

    void Grab(Rigidbody rb, float angle)
    {
        held = rb;
        heldAngle = angle;
        foreach (var lc in rb.GetComponentsInChildren<Collider>()) lc.enabled = false;   // 잡는 동안 아무것과도 안 부딪힘
        rb.isKinematic = true;                                                          // 물리 끄고 집게를 따라다님
        heldLocalPos = grip.InverseTransformPoint(rb.position);
        heldLocalRot = Quaternion.Inverse(grip.rotation) * rb.rotation;
        var d = jaw.xDrive; d.lowerLimit = angle; jaw.xDrive = d;                       // 집게는 뚜껑 폭에서 멈춤
        holding = rb.name;
        Debug.Log($"[집기] {rb.name} 잡음 (집게 {angle:F1}°)");
    }

    void Release()
    {
        var rb = held;
        held = null;
        holding = "";
        var d = jaw.xDrive; d.lowerLimit = jawLowerLimit; jaw.xDrive = d;

        // 반듯이 세우기 (기울어진 만큼만 되돌리고 돌린 방향은 그대로)
        Quaternion upright = Quaternion.FromToRotation(rb.transform.up, Vector3.up) * rb.rotation;
        rb.transform.rotation = upright;
        rb.rotation = upright;
        // 바로 아래 면(벨트·탁자) 위에 올려놓기 → 떨어지며 모서리에 부딪혀 뒤집히지 않게
        float halfH = 0.008f;
        if (rb.TryGetComponent<MeshFilter>(out var mf)) halfH = mf.sharedMesh.bounds.extents.y * rb.transform.lossyScale.y;
        Vector3 from = rb.position + Vector3.up * 0.02f;
        float best = float.MaxValue; Vector3 land = Vector3.zero; bool found = false;
        foreach (var h in Physics.RaycastAll(from, Vector3.down, dropSearch + 0.02f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (h.rigidbody == rb || h.collider.GetComponentInParent<ArticulationBody>() != null) continue;
            if (h.distance < best) { best = h.distance; land = h.point; found = true; }
        }
        if (found) rb.position = rb.transform.position = new Vector3(rb.position.x, land.y + halfH + 0.001f, rb.position.z);
        foreach (var lc in rb.GetComponentsInChildren<Collider>()) lc.enabled = true;
        rb.isKinematic = false;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        Debug.Log($"[집기] {rb.name} 놓음");
    }
}
