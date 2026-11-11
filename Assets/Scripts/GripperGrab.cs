using System.Collections.Generic;
using UnityEngine;

// =============================================================
// [집게로 집기] 유니티 로봇 집게가 닫힐 때 집게 사이에 뚜껑이 있으면 집게에 붙이고, 열면 떨어뜨림
//
// 흐름: 물리 단계마다 → 집게 손가락 사이 상자 안에 뚜껑(Rigidbody)이 있는지 봄
//       → 집게 목표 각도가 closeDeg보다 작아지면(닫는 중) 그 뚜껑을 집게에 붙임 (물리 끄고 집게를 따라다님)
//       → 집게 목표 각도가 openDeg보다 커지면(여는 중) 반듯하게 세워서 바로 아래 면(벨트·탁자)에 올려놓음 → 벨트가 옮김
// ※ 실제 손가락 마찰로 잡으면 유니티 물리에서 미끄러지거나 튀어서, 학습용 시뮬레이터에서 흔히 쓰는 "닫으면 붙이기" 방식
// ※ 집게 충돌 모양(볼록 덩어리)이 손가락 사이를 막아서 뚜껑을 밀어내므로, 뚜껑과 집게 손가락은 서로 안 부딪히게 함
// ※ 잡는 동안 움직이는 집게는 뚜껑 폭에서 멈추게 (관절 하한을 잠깐 그 각도로)
// 각도·좌표 규칙: 집게 링크 기준, URDF 방향(x 앞 / y 왼쪽 / z 위)을 유니티로 바꾸면 (−y, z, x)
// =============================================================
public class GripperGrab : MonoBehaviour
{
    [Header("링크 이름")]
    public string gripperLink = "gripper_link";
    public string movingJawLink = "moving_jaw_so101_v1_link";

    [Header("잡는 곳 (집게 링크 기준, URDF 방향 m) — 손가락 사이")]
    public Vector3 zoneCenterRos = new Vector3(-0.008f, 0f, -0.105f);
    public Vector3 zoneHalfRos = new Vector3(0.035f, 0.04f, 0.04f);   // 11/11 넓힘 (리더로 맞추기 쉽게)

    [Header("집게 각도 (도)")]
    public float closeDeg = 10f;   // 목표 각도가 이보다 작아지면 잡기 (11/11: 5→10, 끝까지 안 쥐어도 잡힘)
    public float openDeg = 18f;    // 목표 각도가 이보다 커지면 놓기
    public float dropSearch = 0.15f;  // 놓을 때 이 거리(m) 안에 아래 면이 있으면 그 위에 반듯이 올려놓음

    [Header("상태 (보기용)")]
    public string holding = "";

    Transform grip;
    ArticulationBody jaw;
    Collider[] fingerColliders;
    Rigidbody held;
    Vector3 heldLocalPos;          // 잡은 순간 집게 기준 위치·방향 → 이후 계속 이 자리에 붙어 다님
    Quaternion heldLocalRot;
    float jawLowerLimit;
    readonly HashSet<Collider> ignored = new HashSet<Collider>();
    readonly Collider[] hits = new Collider[16];

    void Start()
    {
        foreach (var t in GetComponentsInChildren<Transform>())
        {
            if (t.name == gripperLink) grip = t;
            if (t.name == movingJawLink) jaw = t.GetComponent<ArticulationBody>();
        }
        if (grip == null || jaw == null) { Debug.LogWarning("[집기] 집게 링크를 못 찾음"); enabled = false; return; }
        var list = new List<Collider>(grip.GetComponentsInChildren<Collider>(false));  // 손가락 = 그리퍼 + 움직이는 집게 (켜져 있는 것만)
        fingerColliders = list.ToArray();
        jawLowerLimit = jaw.xDrive.lowerLimit;
        // 처음부터 씬의 뚜껑들(로봇 밖 Rigidbody)은 손가락과 안 부딪히게 (다가가다 튕겨 나가지 않게)
        foreach (var rb in FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
            if (rb.GetComponentInParent<ArticulationBody>() == null)
                foreach (var col in rb.GetComponentsInChildren<Collider>()) IgnoreFingers(col);
    }

    // 유니티 방향으로 바꾸기: URDF (x, y, z) → 유니티 (−y, z, x)
    static Vector3 RosToUnity(Vector3 r) => new Vector3(-r.y, r.z, r.x);

    void FixedUpdate()
    {
        float target = jaw.xDrive.target;   // 리더(또는 슬라이더)가 넣은 집게 목표 각도

        // [잡고 있으면] 뚜껑을 집게에 붙은 자리로 옮김 (물리 꺼진 상태라 MovePosition으로)
        if (held != null)
        {
            held.MovePosition(grip.TransformPoint(heldLocalPos));
            held.MoveRotation(grip.rotation * heldLocalRot);
        }

        // [잡는 곳 안의 뚜껑 찾기] 손가락 사이 상자 (조금 크게 봐서 다가올 때부터 손가락과 안 부딪히게)
        Vector3 c = grip.TransformPoint(RosToUnity(zoneCenterRos));
        Vector3 half = Abs(RosToUnity(zoneHalfRos));
        int n = Physics.OverlapBoxNonAlloc(c, half * 1.6f, hits, grip.rotation, ~0, QueryTriggerInteraction.Ignore);
        Rigidbody candidate = null;
        for (int i = 0; i < n; i++)
        {
            var rb = hits[i].attachedRigidbody;
            if (rb == null || rb == held || hits[i].GetComponentInParent<ArticulationBody>() != null) continue;
            IgnoreFingers(hits[i]);
            // 진짜 잡는 곳(작은 상자) 안인지
            Vector3 local = grip.InverseTransformPoint(rb.worldCenterOfMass) - RosToUnity(zoneCenterRos);
            if (Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z) candidate = rb;
        }

        if (held == null && candidate != null && target < closeDeg) Grab(candidate);
        else if (held != null && target > openDeg) Release();
    }

    void Grab(Rigidbody rb)
    {
        held = rb;
        foreach (var lc in rb.GetComponentsInChildren<Collider>()) lc.enabled = false;   // 잡는 동안 아무것과도 안 부딪힘 (손목 등과 겹쳐 튕기지 않게)
        rb.isKinematic = true;                        // 물리 끄고 집게를 따라다니게 (로봇 관절 아래 자식으로 넣으면 관절 물리가 꼬여서 안 넣음)
        heldLocalPos = grip.InverseTransformPoint(rb.position);
        heldLocalRot = Quaternion.Inverse(grip.rotation) * rb.rotation;
        // 움직이는 집게는 지금 각도에서 멈춤 (뚜껑을 뚫고 더 닫히지 않게)
        var d = jaw.xDrive; d.lowerLimit = jaw.jointPosition[0] * Mathf.Rad2Deg; jaw.xDrive = d;
        holding = rb.name;
        Debug.Log($"[집기] {rb.name} 잡음");
    }

    void Release()
    {
        var rb = held;
        // 반듯하게 세워서 놓기: 기울어진 만큼만 되돌림 (돌린 방향은 그대로)
        Quaternion upright = Quaternion.FromToRotation(rb.transform.up, Vector3.up) * rb.rotation;
        rb.transform.rotation = upright;
        rb.rotation = upright;
        // 바로 아래(벨트·탁자)를 찾아서 그 면 위에 반듯이 올려놓음 → 떨어지며 모서리에 부딪혀 뒤집히지 않게
        Physics.SyncTransforms();
        float halfH = 0.008f;   // 뚜껑 두께 1.6cm의 절반 (콜라이더가 꺼져 있어 bounds를 못 씀)
        if (rb.TryGetComponent<MeshFilter>(out var mf)) halfH = mf.sharedMesh.bounds.extents.y * rb.transform.lossyScale.y;
        Vector3 from = rb.position + Vector3.up * 0.02f;
        var hitsDown = Physics.RaycastAll(from, Vector3.down, dropSearch + 0.02f, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue; Vector3 land = Vector3.zero; bool found = false;
        foreach (var h in hitsDown)
        {
            if (h.rigidbody == rb || h.collider.GetComponentInParent<ArticulationBody>() != null) continue;
            if (h.distance < best) { best = h.distance; land = h.point; found = true; }
        }
        if (found) rb.position = rb.transform.position = new Vector3(rb.position.x, land.y + halfH + 0.001f, rb.position.z);
        rb.isKinematic = false;
        foreach (var lc in rb.GetComponentsInChildren<Collider>()) lc.enabled = true;    // 면 위에 놓은 뒤 다시 켬
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        var d = jaw.xDrive; d.lowerLimit = jawLowerLimit; jaw.xDrive = d;
        Debug.Log($"[집기] {rb.name} 놓음");
        held = null;
        holding = "";
    }

    void IgnoreFingers(Collider other)
    {
        if (!ignored.Add(other)) return;
        foreach (var f in fingerColliders) Physics.IgnoreCollision(f, other, true);
    }

    static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    // 씬 화면에 잡는 곳 상자 그리기 (Gizmos 켜면 보임)
    void OnDrawGizmosSelected()
    {
        Transform g = grip;
        if (g == null) foreach (var t in GetComponentsInChildren<Transform>()) if (t.name == gripperLink) g = t;
        if (g == null) return;
        Gizmos.color = held ? Color.green : Color.yellow;
        Gizmos.matrix = Matrix4x4.TRS(g.TransformPoint(RosToUnity(zoneCenterRos)), g.rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, Abs(RosToUnity(zoneHalfRos)) * 2f);
    }
}
