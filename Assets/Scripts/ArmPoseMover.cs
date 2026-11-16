using System.Collections.Generic;
using UnityEngine;

// =============================================================
// [안전하게 자세 옮기기] 유니티 로봇팔을 목표 자세(6관절 각도)로 "벨트에 안 닿는 순서"로 옮김
//
// 왜: 6관절을 한꺼번에 움직이면 접히는 도중 집게가 벨트에 부딪힘 (11/16 확인, 벨트 여유 -4cm)
// 흐름 (팔을 크게 접거나 펼 때만):
//   ① 어깨 회전만 clearPanDeg(+45°, 로봇 오른쪽)로 돌림 → 팔이 벨트 위에서 비켜남
//   ② 접을 때: 위에서 손목·집게를 먼저 굽힘 / 펼 때: 어깨 들기·팔꿈치를 먼저 폄
//   ③ 나머지 관절도 목표 자세로 (어깨는 45° 그대로)
//   ④ 어깨 회전을 목표 각도로 되돌림
//   → 벨트 계산(브리지 workspace.py)으로 최소 여유 접기 14.9cm / 펴기 12cm. -45°는 닿아서 안 씀
//   작은 움직임(어깨 들기~손목 회전이 foldThresholdDeg 안)은 바로 이동
// 실제 움직임은 RobotJointTester가 초당 maxSpeedDps로 함. 흰 팔은 유니티 목표를 따라가므로 같은 순서로 움직임
// (브리지 goto_pose의 "어깨 먼저 돌리기"와 같은 규칙: so101_config.json clear_pan_deg)
// =============================================================
public class ArmPoseMover : MonoBehaviour
{
    public float clearPanDeg = 45f;        // ① 어깨 회전을 돌려 둘 각도 (+ = 로봇 오른쪽, 벨트 끝 쪽)
    public float foldThresholdDeg = 10f;   // 이보다 크게 바뀌는 관절(1~4번)이 있으면 ①~③ 순서로
    public float arriveTolDeg = 0.3f;      // 이 안이면 그 단계 도착

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

    // [안전 경로] now → goal 사이에 들를 자세 목록 (마지막 = goal). LeaderArmReceiver도 같이 씀
    public static List<float[]> SafePath(float[] now, float[] goal, float clearPan = 45f, float threshold = 10f)
    {
        var path = new List<float[]>();
        bool fold = false;
        for (int i = 1; i <= 4; i++) if (Mathf.Abs(goal[i] - now[i]) > threshold) fold = true;
        if (fold)
        {
            // 접는 중(어깨 들기가 뒤로 = 값이 작아짐)이면 손목·집게 먼저, 펴는 중이면 팔(어깨 들기·팔꿈치) 먼저
            bool folding = goal[1] < now[1];
            var w1 = (float[])now.Clone(); w1[0] = clearPan;             // ① 어깨만 옆으로
            var w2 = (float[])w1.Clone();                                // ② 손목·집게(접기) 또는 팔(펴기)
            if (folding) { w2[3] = goal[3]; w2[4] = goal[4]; w2[5] = goal[5]; }
            else { w2[1] = goal[1]; w2[2] = goal[2]; }
            var w3 = (float[])goal.Clone(); w3[0] = clearPan;            // ③ 나머지도 목표로
            path.Add(w1); path.Add(w2); path.Add(w3);
        }
        path.Add((float[])goal.Clone());                                 // ④ 어깨를 목표로
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
