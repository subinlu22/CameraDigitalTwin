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

    [Range(-180f, 180f)]
    public float[] anglesDeg;            // 관절별 목표 각도(도). 순서는 콘솔 로그에 찍힘

    List<ArticulationBody> joints = new List<ArticulationBody>();   // 회전 관절 목록
    List<float> droopDamping = new List<float>();                    // 처짐 상태의 원래 저항 값
    bool wasControlling = false;                                     // 지난 프레임의 control 상태

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

        for (int i = 0; i < joints.Count; i++)
            Debug.Log($"[관절 테스트] {i}번 슬라이더 = {joints[i].name}");   // 어느 슬라이더가 어느 관절인지
    }

    // Update()는 Unity가 매 프레임 불러주는 함수야
    void Update()
    {
        // control을 켜거나 끌 때 한 번만: 힘을 넣거나 빼준다
        if (control != wasControlling)
        {
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

        // 슬라이더 각도를 관절의 목표 각도로 넣는다
        for (int i = 0; i < joints.Count; i++)
        {
            ArticulationDrive drive = joints[i].xDrive;
            float target = anglesDeg[i];
            if (drive.upperLimit > drive.lowerLimit)                           // 제한 범위가 설정돼 있으면 그 안으로 자름
                target = Mathf.Clamp(target, drive.lowerLimit, drive.upperLimit);
            drive.target = target;
            joints[i].xDrive = drive;
        }
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