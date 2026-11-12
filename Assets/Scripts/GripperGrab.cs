using UnityEngine;

// =============================================================
// [집게로 집기 v2] 두 손가락이 뚜껑에 닿은 채로 더 쥐면 잡고, 벌리면 놓음
//
// 흐름: 물리 단계마다
//   ① 손가락 두 개(FingerBox)가 모두 닿아 있는 뚜껑(Rigidbody)을 찾음
//   ② 그 상태에서 집게 목표 각도가 지금 각도보다 squeezeDeg 넘게 작으면(= 더 쥐는 중, 뚜껑에 막혀 멈춤) → 잡음
//      잡을 때 뚜껑을 고정 손가락에 붙이고 움직이는 손가락도 닿을 때까지 오므린 뒤 멈춤, 뚜껑은 충돌을 끄고 집게에 붙어 다님(물리 끔)
//   ③ 목표 각도가 잡은 각도보다 releaseDeg 넘게 커지면(= 벌림) → 물리에 맡겨 놓음 (집게가 움직이던 속도로 떨어짐)
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
    public float betweenDist = 0.03f;   // 두 손가락이 모두 뚜껑 중심에서 이 거리(m) 안이어야 "사이에 있음" (뚜껑 반지름 2cm + 여유)
    public float releaseClearSec = 1.0f; // 놓은 뒤 손가락 충돌을 최대 이만큼(초) 꺼 둠 (뚜껑이 손가락에서 떨어지면 바로 다시 켬)
    public float fingerFriction = 0.1f; // 손가락 마찰 (낮을수록 바닥·뚜껑에 덜 붙잡힘)
    public float betweenDot = 0.8f;     // 뚜껑 중심에서 본 두 손가락 방향이 이 값보다 작아야 "사이" (가장자리를 집어도 잡히게 넉넉히, 거의 같은 쪽(0.8 넘음)만 아님)

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

        // 손가락을 미끄럽게: 바닥·뚜껑에 눌려도 마찰에 붙잡혀 집게가 안 움직이는 일 없게
        // (뚜껑을 드는 건 마찰이 아니라 Grab()이 붙여서 하므로 미끄러워도 괜찮음)
        var slippery = new PhysicsMaterial("손가락_미끄럼")
        {
            dynamicFriction = fingerFriction, staticFriction = fingerFriction,
            frictionCombine = PhysicsMaterialCombine.Minimum, bounceCombine = PhysicsMaterialCombine.Minimum
        };
        fingerA.sharedMaterial = slippery;
        foreach (var c in jaw.GetComponentsInChildren<BoxCollider>()) c.sharedMaterial = slippery;
    }

    void FixedUpdate()
    {
        float target = jaw.xDrive.target;                       // 리더(또는 슬라이더)가 넣은 집게 목표 각도
        float angle = jaw.jointPosition[0] * Mathf.Rad2Deg;     // 실제 집게 각도 (뚜껑에 막히면 목표보다 큼)
        CheckFallGuard();
        CheckFingersClear();

        if (held != null)
        {
            Vector3 to = grip.TransformPoint(heldLocalPos);
            heldVel = (to - prevHeldPos) / Time.fixedDeltaTime;     // 놓을 때 이 속도 그대로 던져짐
            prevHeldPos = to;
            held.MovePosition(to);                                  // 집게에 붙은 자리로
            held.MoveRotation(grip.rotation * heldLocalRot);
            if (target > heldAngle + releaseDeg) Release();
            return;
        }

        if (fingersOff) return;                                 // 놓은 직후 손가락 비켜 주는 동안엔 새로 잡지 않음
        LogIfStuck(target, angle);

        // ① 두 손가락 모두 닿은 뚜껑 찾기
        Rigidbody both = TouchedByBoth();
        touching = both ? both.name : "";
        // ② 쥐는 중 + 뚜껑이 두 손가락 사이에 있음 → 잡기 (닿자마자 잡아서 뚜껑이 끼어 튀어 나가기 전에)
        if (both == null) return;
        bool squeezing = target < angle - squeezeDeg, between = Between(both);
        if (squeezing && between) { Grab(both, angle); return; }
        // 두 손가락이 닿았는데 못 잡을 때 이유 남기기 (0.5초에 한 번)
        if (Time.time - lastWhyLog > 0.5f)
        {
            lastWhyLog = Time.time;
            Debug.Log($"[집기] {both.name} 두 손가락 닿음, 못 잡음: 쥐는 중={squeezing}(목표 {target:F1}° / 지금 {angle:F1}°) 사이={between}(고정 손가락 {lastA * 100:F1}cm, 움직이는 손가락 {lastB * 100:F1}cm, 방향 {lastDot:F2})");
        }
    }
    float lastWhyLog = -1f;

    // [진단] 집게가 목표 각도를 0.3초 넘게 못 따라가면(= 뭔가에 막힘) 움직이는 집게 상자에 닿은 것들을 로그로 남김
    float stuckSince = -1f, lastStuckLog = -1f;
    readonly Collider[] hitsStuck = new Collider[16];
    void LogIfStuck(float target, float angle)
    {
        if (Mathf.Abs(target - angle) < 5f) { stuckSince = -1f; return; }
        if (stuckSince < 0f) { stuckSince = Time.time; return; }
        if (Time.time - stuckSince < 0.3f || Time.time - lastStuckLog < 1f) return;
        lastStuckLog = Time.time;
        var names = new System.Collections.Generic.List<string>();
        foreach (var box in jaw.GetComponentsInChildren<BoxCollider>())
        {
            int n = Overlap(box, hitsStuck);
            for (int i = 0; i < n; i++)
            {
                var c = hitsStuck[i];
                if (c.transform.IsChildOf(jaw.transform)) continue;   // 자기 자신
                string who = c.attachedArticulationBody ? c.attachedArticulationBody.name : (c.attachedRigidbody ? c.attachedRigidbody.name : c.name);
                string s = $"{box.name}↔{who}({c.GetType().Name})";
                if (!names.Contains(s)) names.Add(s);
            }
        }
        Debug.Log($"[집기 진단] 집게 막힘: 목표 {target:F1}° / 지금 {angle:F1}° · 닿은 것: {(names.Count > 0 ? string.Join(", ", names) : "없음")}");
    }

    // 뚜껑 중심이 두 손가락 사이에 있는지 — 뚜껑 중심에서 본 두 손가락이 서로 반대쪽이면 "사이"
    // (예전엔 두 손가락 상자 '가운데'를 잇는 선 근처만 봐서, 손가락 끝으로 집으면 사이가 아니라고 나왔음)
    // 닫힌 집게로 위에서 누르면 두 손가락이 같은 쪽 → 아님
    // 가장자리만 집으면 중심에서 본 두 손가락이 비슷한 쪽이라 기준을 넉넉히 둠 (집게가 막혀 있는지(쥐는 중)도 같이 봄)
    bool Between(Rigidbody rb)
    {
        Vector3 p = rb.worldCenterOfMass;
        Vector3 va = fingerA.ClosestPoint(p) - p;
        Vector3 vb = fingerB.ClosestPoint(p) - p;
        lastA = va.magnitude; lastB = vb.magnitude;
        lastDot = (va.sqrMagnitude < 1e-8f || vb.sqrMagnitude < 1e-8f) ? -1f : Vector3.Dot(va.normalized, vb.normalized);
        if (lastA > betweenDist || lastB > betweenDist) return false;   // 손가락이 뚜껑 중심에서 너무 멂
        return lastDot < betweenDot;                                    // 두 손가락이 뚜껑 중심을 사이에 두고 반대쪽
    }
    float lastA, lastB, lastDot;

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
        var lid = rb.GetComponentInChildren<Collider>();
        float lockAngle = angle;
        if (lid != null)
        {
            // ⓐ 고정 손가락과 뚜껑 사이 틈만큼 뚜껑을 고정 손가락 쪽으로 붙임 (1cm 넘으면 안 옮김)
            Vector3 gapA = Gap(fingerA, lid);
            if (gapA.magnitude < 0.01f) { rb.position += gapA; rb.transform.position = rb.position; Physics.SyncTransforms(); }
            // ⓑ 움직이는 손가락과 뚜껑 사이 남은 틈만큼 집게를 더 오므린 각도에서 멈춤 → 한쪽이 뜨지 않게
            Vector3 gapB = Gap(fingerB, lid, out Vector3 touchB);
            Vector3 pivot = jaw.transform.TransformPoint(jaw.anchorPosition);
            Vector3 axis = jaw.transform.rotation * jaw.anchorRotation * Vector3.right;
            float r = Vector3.ProjectOnPlane(touchB - pivot, axis).magnitude;      // 회전축에서 닿는 점까지 거리
            if (r > 0.005f && gapB.magnitude < 0.01f)
                lockAngle = Mathf.Max(jawLowerLimit, angle - gapB.magnitude / r * Mathf.Rad2Deg);
        }
        held = rb;
        heldAngle = lockAngle;
        rb.detectCollisions = false;                                                    // 잡는 동안 아무것과도 안 부딪힘
        rb.isKinematic = true;                                                          // 물리 끄고 집게를 따라다님
        guardRb = null;                                                                 // 다른 뚜껑 떨어짐 지킴이는 끝
        heldLocalPos = grip.InverseTransformPoint(rb.position);
        heldLocalRot = Quaternion.Inverse(grip.rotation) * rb.rotation;
        prevHeldPos = rb.position; heldVel = Vector3.zero;
        var d = jaw.xDrive; d.lowerLimit = lockAngle; jaw.xDrive = d;                   // 집게는 뚜껑 폭에서 멈춤
        holding = rb.name;
        Debug.Log($"[집기] {rb.name} 잡음 (집게 {angle:F1}° → {lockAngle:F1}°까지 오므림)");
    }

    // 손가락 상자 → 뚜껑 표면까지 가장 짧은 벡터 (닿거나 겹치면 0). touch = 손가락 쪽 가장 가까운 점
    Vector3 Gap(BoxCollider finger, Collider lid) => Gap(finger, lid, out _);
    Vector3 Gap(BoxCollider finger, Collider lid, out Vector3 touch)
    {
        Vector3 q = finger.ClosestPoint(lid.bounds.center), p = q;
        for (int i = 0; i < 6; i++) { p = lid.ClosestPoint(q); q = finger.ClosestPoint(p); }   // 서로 가장 가까운 점 찾기
        touch = q;
        return q - p;
    }

    // 놓기: 순간이동·자세 고치기 없이 물리에 맡김 → 집게가 움직이던 속도 그대로 떨어지고 튕기고 구름
    void Release()
    {
        var rb = held;
        held = null;
        holding = "";
        var d = jaw.xDrive; d.lowerLimit = jawLowerLimit; jaw.xDrive = d;
        Vector3 at = grip.TransformPoint(heldLocalPos);          // 지금 집게에 붙은 자리로 확실히 맞춰 놓고 시작
        rb.position = rb.transform.position = at;
        rb.rotation = rb.transform.rotation = grip.rotation * heldLocalRot;
        // 순서 중요: 먼저 물리를 켠(움직이는 물체) 다음에 충돌 모양을 새로 붙여야 함
        // (물리를 끈 채 충돌을 켜면, 물리 엔진이 "벨트·탁자와는 안 부딪힘"으로 기억해 둔 게 남아서 벨트를 뚫고 떨어졌음)
        rb.isKinematic = false;
        rb.detectCollisions = true;
        foreach (var lc in rb.GetComponentsInChildren<Collider>()) { lc.enabled = false; lc.enabled = true; }
        Physics.SyncTransforms();
        LiftOutOfSurface(rb);                                    // 벨트·탁자 속으로 눌러 넣은 채 놓으면 위로 꺼냄 (안 하면 아래로 빠짐)
        StartFallGuard(rb);
        // 놓은 직후 손가락 충돌 잠깐 끄기: 팔이 벨트를 누르고 있으면 손가락이 뚜껑을 벨트 속으로 밀어 넣었음
        // → 뚜껑이 손가락에서 떨어지면(또는 최대 releaseClearSec초 뒤) 다시 켬
        SetFingersSolid(false);
        clearLid = rb.GetComponentInChildren<Collider>(); clearUntil = Time.time + releaseClearSec;
        rb.linearVelocity = Vector3.ClampMagnitude(heldVel, 1.5f);
        rb.angularVelocity = Vector3.zero;
        Debug.Log($"[집기] {rb.name} 놓음 (속도 {rb.linearVelocity.magnitude * 100:F0}cm/초)");
    }
    Vector3 prevHeldPos, heldVel;

    // [떨어짐 지킴이] 놓은 뒤 1.5초 동안 뚜껑이 벨트·탁자(움직이지 않는 면)에 2mm 넘게 박히면 박힌 만큼 밖으로 밀어냄
    // (잡았다 놓은 뚜껑이 기울어진 채 떨어지면 물리 엔진이 벨트 속 1.5cm에 박힌 채 멈추거나 뚫고 빠지는 일이 있었음.
    //  정상으로 떨어지면 아무 일도 안 함)
    Rigidbody guardRb; float guardUntil, guardTop, guardHalfH; bool guardLogged;
    readonly Collider[] hitsGuard = new Collider[16];

    // [놓은 직후 집게 비켜 주기] 집게 충돌 상자(손가락·뿌리·몸통)를 껐다가, 뚜껑과 5mm 넘게 떨어지면 다시 켬
    Collider clearLid; float clearUntil; bool fingersOff;
    void SetFingersSolid(bool on)
    {
        foreach (var c in grip.GetComponentsInChildren<BoxCollider>()) c.enabled = on;   // 손가락·집게 뿌리·몸통 상자 (그리퍼 아래 전부)
        fingersOff = !on;
    }
    void CheckFingersClear()
    {
        if (!fingersOff) return;
        bool timeUp = Time.time > clearUntil || clearLid == null;
        bool apart = true;
        if (!timeUp)
        {
            Bounds lb = clearLid.bounds; lb.Expand(0.01f);   // 5mm 여유 (양쪽 합 1cm)
            foreach (var c in grip.GetComponentsInChildren<BoxCollider>()) if (lb.Intersects(BoxBounds(c))) apart = false;
        }
        if (timeUp || apart) { SetFingersSolid(true); clearLid = null; }
    }
    // 꺼진 콜라이더는 bounds가 비어서, 상자 크기로 직접 계산
    static Bounds BoxBounds(BoxCollider b)
    {
        Transform t = b.transform;
        Vector3 half = Vector3.Scale(b.size * 0.5f, t.lossyScale);
        Vector3 ex = new Vector3(
            Mathf.Abs(t.right.x * half.x) + Mathf.Abs(t.up.x * half.y) + Mathf.Abs(t.forward.x * half.z),
            Mathf.Abs(t.right.y * half.x) + Mathf.Abs(t.up.y * half.y) + Mathf.Abs(t.forward.y * half.z),
            Mathf.Abs(t.right.z * half.x) + Mathf.Abs(t.up.z * half.y) + Mathf.Abs(t.forward.z * half.z));
        return new Bounds(t.TransformPoint(b.center), ex * 2f);
    }
    void StartFallGuard(Rigidbody rb)
    {
        guardRb = null;
        var lid = rb.GetComponentInChildren<Collider>();
        if (lid == null) return;
        guardTop = float.NegativeInfinity;    // 놓을 때 바로 아래 면 높이 (완전히 뚫고 빠졌을 때 되돌릴 곳)
        float best = float.MaxValue;
        foreach (var h in Physics.RaycastAll(rb.position + Vector3.up * 0.01f, Vector3.down, 0.5f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (!IsStaticSurface(h.collider, rb)) continue;
            if (h.distance < best) { best = h.distance; guardTop = h.point.y; }
        }
        guardHalfH = lid.bounds.extents.y;
        guardRb = rb; guardUntil = Time.time + 1.5f; guardLogged = false;
    }
    // 움직이지 않는 면(벨트·탁자)인지: 물리 몸(Rigidbody)도 로봇도 아닌 것
    static bool IsStaticSurface(Collider c, Rigidbody self) =>
        c.attachedRigidbody == null && c.attachedArticulationBody == null && c.GetComponentInParent<ArticulationBody>() == null;

    void CheckFallGuard()
    {
        if (guardRb == null) return;
        if (Time.time > guardUntil || guardRb.isKinematic) { guardRb = null; return; }
        var lid = guardRb.GetComponentInChildren<Collider>();
        if (lid == null) { guardRb = null; return; }

        // ① 면에 박혔으면 박힌 만큼 밀어내고, 면 쪽으로 가던 속도는 없앰
        Bounds b = lid.bounds;
        int n = Physics.OverlapBoxNonAlloc(b.center, b.extents, hitsGuard, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var c = hitsGuard[i];
            if (c == lid || !IsStaticSurface(c, guardRb)) continue;
            if (!Physics.ComputePenetration(lid, lid.transform.position, lid.transform.rotation, c, c.transform.position, c.transform.rotation, out Vector3 dir, out float depth)) continue;
            if (depth < 0.002f) continue;
            guardRb.position = guardRb.transform.position = guardRb.position + dir * depth;
            float into = Vector3.Dot(guardRb.linearVelocity, dir);
            if (into < 0f) guardRb.linearVelocity -= dir * into;
            if (!guardLogged) { guardLogged = true; Debug.Log($"[집기] {guardRb.name} {c.name}에 {depth * 1000:F0}mm 박혀서 밀어냄"); }
        }
        // ② 완전히 뚫고 아래로 빠졌으면 놓을 때 아래 있던 면 위로 되돌림
        if (guardRb.position.y < guardTop - guardHalfH - 0.005f)
        {
            Vector3 p = guardRb.position; p.y = guardTop + guardHalfH + 0.001f;
            guardRb.position = guardRb.transform.position = p;
            guardRb.linearVelocity = Vector3.zero; guardRb.angularVelocity = Vector3.zero;
            Debug.Log($"[집기] {guardRb.name} 벨트·탁자를 뚫고 빠져서 위로 되돌림");
        }
    }

    // 뚜껑이 로봇이 아닌 것(벨트·탁자)과 겹쳐 있으면 1mm씩 올려서 안 겹치는 높이로 (최대 6cm)
    readonly Collider[] hitsLift = new Collider[16];
    void LiftOutOfSurface(Rigidbody rb)
    {
        var lid = rb.GetComponentInChildren<Collider>();
        if (lid == null) return;
        Transform t = lid.transform;
        for (int step = 0; step <= 60; step++)
        {
            Vector3 up = Vector3.up * (0.001f * step);
            bool overlap = false;
            Bounds b = lid.bounds;
            int n = Physics.OverlapBoxNonAlloc(b.center + up, b.extents, hitsLift, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n && !overlap; i++)
            {
                var c = hitsLift[i];
                if (c.attachedRigidbody == rb || c.GetComponentInParent<ArticulationBody>() != null) continue;   // 자기 자신·로봇은 빼고
                overlap = Physics.ComputePenetration(lid, t.position + up, t.rotation, c, c.transform.position, c.transform.rotation, out _, out _);
            }
            if (!overlap)
            {
                if (step > 0)
                {
                    rb.position = rb.transform.position = rb.position + up;
                    Physics.SyncTransforms();
                    Debug.Log($"[집기] {rb.name} 벨트·탁자 속에서 {step}mm 올려서 놓음");
                }
                return;
            }
        }
    }
}
