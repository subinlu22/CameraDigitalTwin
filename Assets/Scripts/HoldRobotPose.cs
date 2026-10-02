using UnityEngine;

// [로봇팔 자세 유지] 플레이를 시작하면 모든 관절이 자세를 버티도록 힘을 넣어주는 스크립트
public class HoldRobotPose : MonoBehaviour
{
    public float stiffness = 10000f;  // 자세를 유지하려는 힘 (클수록 단단하게 버팀)
    public float damping = 100f;      // 흔들림을 잡아주는 저항 (클수록 덜 흔들림)
    public float forceLimit = 1000f;  // 관절이 낼 수 있는 최대 힘

    // Awake()는 플레이를 시작할 때 Unity가 제일 먼저 한 번 불러주는 함수야
    void Awake()
    {
        // 이 오브젝트 아래에 있는 모든 관절(ArticulationBody)을 하나씩 돈다
        foreach (var body in GetComponentsInChildren<ArticulationBody>())
        {
            body.useGravity = false;                      // 중력 끄기
            if (body.isRoot) body.immovable = true;       // 맨 밑 부품은 제자리에 고정

            // 회전 관절만 힘을 준다 (고정 관절은 건드릴 게 없음)
            if (body.jointType == ArticulationJointType.RevoluteJoint)
            {
                ArticulationDrive drive = body.xDrive;    // 구조체라서 복사본을 꺼내서
                drive.stiffness = stiffness;              // 값을 고치고
                drive.damping = damping;
                drive.forceLimit = forceLimit;
                body.xDrive = drive;                      // 다시 넣어준다
            }
        }
    }
}
