using System.Collections.Generic;
using UnityEngine;

// =============================================================
// [안전하게 자세 옮기기] 유니티 로봇팔을 목표 자세(6관절 각도)로 "벨트에 안 닿는 길"로 옮김
//
// 왜: 6관절을 한꺼번에 움직이면 도중에 집게가 벨트·뚜껑을 밀어냄 (11/16 확인)
// 길 (크게 움직일 때만, 작은 움직임은 바로):
//   작업 자세끼리 : ① 지금 자리에서 위로 들기(Lift 자세) → ② 높이 든 채 어깨 회전 → ③ 목표 위에서 내려가기
//                   들고 옮기는 동안 집게·손목 회전은 지금 그대로 (뚜껑 안 놓침), 마지막에 목표 값으로
//   쉬는 자세에서 나올 때 : 어깨 +45° → 팔(어깨 들기·팔꿈치) 먼저 펴서 위로 → 손목 → ②③
//   쉬는 자세로 들어갈 때 : ① → 어깨 +45° → 손목·집게 먼저 → 팔 접기 → 어깨 되돌림
//   → 벨트 계산(브리지 workspace.py): 위로 든 자세는 벨트 위 12cm, 작업 자세끼리 길 중간 여유 5cm 넘음
//     쉬는 자세 쪽 길은 패널 [쉬는 자세] 자동 이동(브리지 goto_pose)과 같은 규칙 (실물에서 써 옴)
// 실제 움직임은 RobotJointTester가 초당 maxSpeedDps로 함. 흰 팔은 유니티 목표를 따라가므로 같은 길로 움직임
// =============================================================
public class ArmPoseMover : MonoBehaviour
{
    public float clearPanDeg = 45f;        // 쉬는 자세 드나들 때 어깨 회전을 돌려 둘 각도 (+ = 로봇 오른쪽)
    public float foldThresholdDeg = 10f;   // 이보다 크게 바뀌는 관절(0~4번)이 있으면 돌아서 감
    public float arriveTolDeg = 0.3f;      // 이 안이면 그 단계 도착
    public const float FoldedLiftDeg = -80f;   // 어깨 들기가 이보다 작으면 "접힌 자세" (쉬는 자세 -107.6°)

    public void HoldHere() { Cancel(); tester.StartFromCurrentPose(); }   // 그 자리에서 멈춤 (가던 단계 버림)

    RobotJointTester tester;
    readonly Queue<float[]> steps = new Queue<float[]>();
    int total;

    public bool Moving => steps.Count > 0 || current != null;
    public string Status => Moving ? $"자세 이동 중 {total - steps.Count}/{total}" : "";
    float[] current;                       // 지금 가는 중인 단계

    void Awake() => tester = GetComponent<RobotJointTester>();

    // 목표 자세로 이동 시작 (지금 넣고 있는 목표 각도에서 출발)
    public void MoveTo(float[] goal)
    {
        Cancel();
        var now = new float[6];
        for (int i = 0; i < 6; i++) now[i] = tester.CommandDeg(i);
        foreach (var w in SafePath(now, goal, clearPanDeg, foldThresholdDeg)) steps.Enqueue(w);
        total = steps.Count;
        Next();
    }

    // 위로 든 자세의 어깨 들기·팔꿈치·손목 굽힘 (집게가 아래를 보고 벨트 위 12cm쯤)
    static readonly float[] LiftArm = { -20f, -20f, 80f };

    // x 자세에서 어깨 회전은 그대로 두고 팔만 위로 든 자세. grip/roll을 주면 집게·손목 회전을 그 값으로
    static float[] Up(float[] x, float grip, float roll)
    {
        var y = (float[])x.Clone();
        y[1] = LiftArm[0]; y[2] = LiftArm[1]; y[3] = LiftArm[2];
        y[4] = roll; y[5] = grip;
        return y;
    }

    // [안전 경로] now → goal 사이에 들를 자세 목록 (마지막 = goal). LeaderArmReceiver도 같이 씀
    public static List<float[]> SafePath(float[] now, float[] goal, float clearPan = 45f, float threshold = 10f)
    {
        var path = new List<float[]>();
        bool big = false;
        for (int i = 0; i <= 4; i++) if (Mathf.Abs(goal[i] - now[i]) > threshold) big = true;
        if (big)
        {
            bool fromFolded = now[1] < FoldedLiftDeg, toFolded = goal[1] < FoldedLiftDeg;
            float grip = now[5], roll = now[4];                          // 옮기는 동안 집게·손목 회전은 그대로
            var up = Up(now, grip, roll);
            if (fromFolded)
            {
                var a = (float[])now.Clone(); a[0] = clearPan;           // 어깨 +45°
                var b = (float[])a.Clone(); b[1] = LiftArm[0]; b[2] = LiftArm[1];   // 팔 먼저 펴서 위로
                path.Add(a); path.Add(b);
                up = Up(a, grip, roll);                                  // 손목까지 위 자세 (어깨 45°)
            }
            path.Add(up);                                                // ① 지금 자리에서 위로
            if (toFolded)
            {
                var a = (float[])up.Clone(); a[0] = clearPan;            // 위에서 어깨 +45°
                var b = (float[])a.Clone(); b[3] = goal[3]; b[4] = goal[4]; b[5] = goal[5];   // 손목·집게 먼저
                var c = (float[])goal.Clone(); c[0] = clearPan;          // 팔 접기
                path.Add(a); path.Add(b); path.Add(c);
            }
            else path.Add(Up(goal, grip, roll));                         // ② 높이 든 채 목표 어깨로
        }
        path.Add((float[])goal.Clone());                                 // ③ 목표로 (쉬는 자세면 어깨 되돌림)
        return path;
    }

    // 슬라이더를 직접 움직이거나 정지하면 순서 이동 취소 (그 자리 목표 유지)
    public void Cancel() { steps.Clear(); current = null; }

    void Next()
    {
        current = steps.Count > 0 ? steps.Dequeue() : null;
        if (current != null) for (int i = 0; i < 6; i++) tester.anglesDeg[i] = current[i];
    }

    void Update()
    {
        if (current != null && tester.Reached(arriveTolDeg)) Next();
    }
}
