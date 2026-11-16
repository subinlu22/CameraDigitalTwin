using System.Collections.Generic;
using UnityEngine;

// [관절 테스트] 인스펙터의 슬라이더로 각 관절을 직접 움직여 보는 스크립트
// control 체크 = 슬라이더 각도로 움직임 / 체크 해제 = 다시 축 처짐
public class RobotJointTester : MonoBehaviour
{
    public bool control = false;         // 체크하면 슬라이더 각도를 따라간다
    public float stiffness = 10000f;     // 자세를 버티는 힘 (제어할 때만 사용)
    public float damping = 100f;         // 흔들림을 잡는 저항 (제어할 때만 사용)
    public float forceLimit = 1000f;     // 관절이 낼 수 있는 최대 힘
    public float maxSpeedDps = 10f;      // 슬라이더를 따라가는 최대 속도 (도/초)

    [Range(-180f, 180f)]
    public float[] anglesDeg;            // 관절별 목표 각도(도). 순서는 콘솔 로그에 찍힘

    List<ArticulationBody> joints = new List<ArticulationBody>();   // 회전 관절 목록
    List<float> droopDamping = new List<float>();                    // 처짐 상태의 원래 저항 값
    bool wasControlling = false;                                     // 지난 프레임의 control 상태
    float[] commandDeg;                                              // 지금 관절에 넣고 있는 목표 (슬라이더 쪽으로 천천히 다가감)

    // Start()는 첫 프레임 직전에 한 번 불리는 함수야 (Awake보다 나중이라 처짐 스크립트가 먼저 끝나 있음)
    void Start()
    {
        foreach (var body in GetComponentsInChildren<ArticulationBody>())
        {
            if (body.jointType != ArticulationJointType.RevoluteJoint) continue;
            joints.Add(body);
            droopDamping.Add(body.xDrive.damping);           // 처짐 상태의 저항을 기억해둔다
        }

        if (anglesDeg == null || anglesDeg.Length != joints.Count)
            anglesDeg = new float[joints.Count];              // 슬라이더 개수를 관절 수에 맞춤
        commandDeg = new float[joints.Count];

        for (int i = 0; i < joints.Count; i++)
            Debug.Log($"[관절 테스트] {i}번 슬라이더 = {joints[i].name}");   // 어느 슬라이더가 어느 관절인지
    }

    // Update()는 Unity가 매 프레임 불러주는 함수야
    void Update()
    {
        // control을 켜거나 끌 때 한 번만: 힘을 넣거나 빼준다
        if (control != wasControlling)
        {
            if (control)
            {
                // [안전] 켜는 순간 지금 목표 각도(리더가 넣어 둔 값 = 흰 팔로 보내던 값)를 슬라이더에 먼저 넣음
                // → 아무것도 안 움직임 (예전엔 0°=ㄱ자로 확 튐)
                for (int i = 0; i < joints.Count; i++)
                    anglesDeg[i] = commandDeg[i] = joints[i].xDrive.target;
                if (FindFirstObjectByType<LeaderArmReceiver>() is LeaderArmReceiver r && r.enabled)
                    Debug.LogWarning("[관절 테스트] LeaderArmReceiver가 켜져 있어요 → 리더 각도와 슬라이더가 서로 덮어씀. 먼저 끄세요");
                Debug.Log($"[관절 테스트] 슬라이더 켬: 지금 자세에서 시작, 초당 {maxSpeedDps}°까지만 움직임");
            }
            for (int i = 0; i < joints.Count; i++)
            {
                ArticulationDrive drive = joints[i].xDrive;
                drive.stiffness = control ? stiffness : 0f;                    // 켜면 힘, 끄면 처짐
                drive.damping = control ? damping : droopDamping[i];
                drive.forceLimit = control ? forceLimit : drive.forceLimit;
                joints[i].xDrive = drive;
            }
            wasControlling = control;
        }

        if (!control) return;

        // 슬라이더 각도 쪽으로 초당 maxSpeedDps도씩만 다가가서 관절 목표 각도로 넣는다
        // (슬라이더를 확 끌어도 목표는 천천히 따라감 → 유니티→흰 팔 시험 때 흰 팔도 천천히)
        for (int i = 0; i < joints.Count; i++)
        {
            ArticulationDrive drive = joints[i].xDrive;
            float target = anglesDeg[i];
            if (drive.upperLimit > drive.lowerLimit)                           // 제한 범위가 설정돼 있으면 그 안으로 자름
                target = Mathf.Clamp(target, drive.lowerLimit, drive.upperLimit);
            commandDeg[i] = Mathf.MoveTowards(commandDeg[i], target, maxSpeedDps * Time.deltaTime);
            drive.target = commandDeg[i];
            joints[i].xDrive = drive;
        }
    }

    // [지금 자세에서 시작] 슬라이더·명령을 지금 목표 각도로 맞춤 (UnityToFollowerGUI가 "유니티로 조종" 누를 때 부름)
    public void StartFromCurrentPose()
    {
        if (commandDeg == null || joints.Count == 0) return;
        for (int i = 0; i < joints.Count; i++)
            anglesDeg[i] = commandDeg[i] = joints[i].xDrive.target;
    }

    // 지금 관절에 넣고 있는 목표 (천천히 따라가는 중인 값)
    public float CommandDeg(int i) => commandDeg != null && i < commandDeg.Length ? commandDeg[i] : joints[i].xDrive.target;

    // [도착했나] 지금 넣고 있는 목표가 슬라이더 각도(관절 한계로 자른 값)에 tol° 안으로 다 왔으면 true
    public bool Reached(float tol)
    {
        if (commandDeg == null) return true;
        for (int i = 0; i < joints.Count; i++)
        {
            var d = joints[i].xDrive;
            float target = d.upperLimit > d.lowerLimit ? Mathf.Clamp(anglesDeg[i], d.lowerLimit, d.upperLimit) : anglesDeg[i];
            if (Mathf.Abs(commandDeg[i] - target) > tol) return false;
        }
        return true;
    }

    // [각도 초기화] 컴포넌트 오른쪽 ⋮ 메뉴에 "각도 0으로 초기화" 항목을 만들어준다
    [ContextMenu("각도 0으로 초기화")]
    void ResetAngles()
    {
        if (anglesDeg == null) return;           // 슬라이더가 아직 없으면 아무것도 안 함
        for (int i = 0; i < anglesDeg.Length; i++)
            anglesDeg[i] = 0f;                   // 모든 슬라이더를 0도로
    }
}