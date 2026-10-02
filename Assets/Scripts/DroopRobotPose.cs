using System.Collections.Generic;
using UnityEngine;

// [로봇팔 축 처지기] 플레이하면 관절이 풀려서 중력으로 처지고, 바닥에 닿으면 멈추는 스크립트
// 관절마다 끝쪽 무게에 비례해서 저항을 넣어, 모든 관절이 비슷한 속도로 처지게 한다
public class DroopRobotPose : MonoBehaviour
{
    // [관절별 직접 지정] 인스펙터에서 링크 이름과 저항 값을 적으면 그 관절만 이 값을 쓴다
    [System.Serializable]
    public class JointDampingOverride
    {
        public string linkName;      // 링크 이름 (예: upper_arm_link)
        public float damping = 5f;   // 이 관절만 쓸 저항 값
    }

    public bool autoByWeight = true;     // 켜면 끝쪽 무게에 비례해서 저항을 자동 계산
    public float baseDamping = 5f;       // 가장 가벼운 관절이 받는 저항 (클수록 천천히 처짐)
    public float jointFriction = 0.5f;   // 관절 마찰
    public List<JointDampingOverride> overrides = new List<JointDampingOverride>();

    // Awake()는 플레이를 시작할 때 Unity가 제일 먼저 한 번 불러주는 함수야
    void Awake()
    {
        ArticulationBody[] bodies = GetComponentsInChildren<ArticulationBody>();

        // 1) 회전 관절 중 "끝쪽 무게"가 제일 가벼운 값을 찾는다 (비율 계산의 기준)
        float lightest = float.MaxValue;
        foreach (var body in bodies)
        {
            if (body.jointType != ArticulationJointType.RevoluteJoint) continue;
            float m = SubtreeMass(body);
            if (m > 0f && m < lightest) lightest = m;
        }
        bool canScale = autoByWeight && lightest != float.MaxValue;   // 무게 정보가 없으면 자동 계산 포기

        // 2) 모든 관절: 중력 켜고, 힘은 빼고, 저항만 관절별로 넣는다
        foreach (var body in bodies)
        {
            body.useGravity = true;                          // 중력 켜기
            if (body.isRoot) body.immovable = true;          // 맨 밑 부품은 제자리에 고정
            body.jointFriction = jointFriction;              // 관절 마찰

            if (body.jointType != ArticulationJointType.RevoluteJoint) continue;

            float damping = baseDamping;
            if (canScale) damping = baseDamping * (SubtreeMass(body) / lightest);   // 무게 비율만큼 저항 키움
            foreach (var o in overrides)                                            // 직접 지정한 관절이면 그 값으로 덮어씀
                if (o.linkName == body.name) damping = o.damping;

            ArticulationDrive drive = body.xDrive;           // 구조체라서 복사본을 꺼내서
            drive.stiffness = 0f;                            // 자세를 버티는 힘은 없애고
            drive.damping = damping;                         // 저항만 넣고
            body.xDrive = drive;                             // 다시 넣어준다

            Debug.Log($"[처짐] {body.name}: 저항 {damping:F1} (끝쪽 무게 {SubtreeMass(body):F3}kg)");   // 값 확인용 로그
        }

        // 3) 모든 보이는 모양에 충돌 모양을 자동으로 붙인다 (바닥에 닿아서 멈추게)
        foreach (var meshFilter in GetComponentsInChildren<MeshFilter>())
        {
            if (meshFilter.GetComponent<Collider>() != null) continue;   // 이미 있으면 건너뜀
            var col = meshFilter.gameObject.AddComponent<MeshCollider>();
            col.sharedMesh = meshFilter.sharedMesh;
            col.convex = true;                               // 물리 계산을 하려면 볼록 모양이어야 해
        }
    }

    // [끝쪽 무게] 이 관절 + 그 아래 달린 모든 부품의 무게를 합친다
    float SubtreeMass(ArticulationBody body)
    {
        float total = 0f;
        foreach (var b in body.GetComponentsInChildren<ArticulationBody>())   // 자기 자신 + 자식 전부
            total += b.mass;
        return total;
    }
}