using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// =============================================================
// [유니티 → 흰 팔] 게임 화면 왼쪽 아래 작은 창 — 버튼 몇 개로 "유니티 로봇을 움직이면 흰 팔이 따라오게"
//
// 흐름
//   ① [유니티로 조종 시작] : 리더 미러링 끄기 → 관절 슬라이더 켜기(지금 자세 그대로) → 유니티 각도 보내기 켜기
//                            → 패널에 "입력 소스 = 유니티, 연습 모드로 연동" 명령 (흰 팔은 아직 안 움직임)
//   ② 표에서 6관절 "유니티 목표 ↔ 흰 팔 지금" 차이가 모두 2° 안이면 초록
//   ③ [흰 팔 실제로 움직이기] 두 번 눌러야 시작 (실수 방지) → 패널이 흰 팔에 실제로 씀
//   ④ 관절 고르기(6개) → 그 관절 슬라이더(시작 ±30°, 관절 한계 안) / [+15°] [처음 각도] 버튼
//      → 유니티 관절이 초당 10°로 움직이고 흰 팔이 따라감 (브리지가 안전 범위·벨트 충돌 방지로 한 번 더 자름)
//   ⑤ [정지] = 흰 팔 그 자리에서 멈춤, [리더로 돌아가기] = 다 원래대로
//   ※ 로봇 없이 연습 (패널·두 팔 연결 안 됨): [유니티로 조종]을 누르면 유니티 팔만 움직임 (Offline = true)
//      → 패널에 명령을 하나도 안 보내고, 유니티 각도도 안 보냄. [흰 팔 실제로 움직이기]도 안 눌림
//      → 흰 팔을 쓰려면 [리더로 돌아가기] 후 패널 연결하고 다시 [유니티로 조종]
//
// 패널(브리지)과는 UDP로 말함: 명령 → 5061, 상태·응답 ← 5062 (docs/so101_messages.md)
// 키: F2 = 창 보이기/숨기기
// =============================================================
public class UnityToFollowerGUI : MonoBehaviour
{
    public bool show = true;
    public int cmdPort = 5061;       // 브리지 명령 받는 포트
    public int statusPort = 5062;    // 브리지 상태·응답 보내는 포트
    public float okDiffDeg = 2f;     // 이 안이면 "같은 자세"
    // 쉬는 자세 = 10/23 실측 (so101_config.json rest_pose_deg와 같은 값)
    public float[] restPoseDeg = { 0f, -107.6f, 87.2f, 71.1f, 0f, -10f };   // [5] 집게 값은 안 씀 → ClosedGripDeg
    // 자동 자세(ㄱ자·쉬는 자세·처음)에서 집게 = 닫힘. 흰 팔 쉬는 자세 실측 -10~-11° (더 조이면 서보가 계속 누름)
    // 상수라 씬에 저장되지 않음 (씬에 예전 restPoseDeg 집게 0°가 남아 있어서 덮어쓰던 문제)
    public const float ClosedGripDeg = -10f;

    LeaderArmReceiver receiver;
    RobotJointTester tester;
    UnityTargetSender sender;
    ArmPoseMover mover;              // 벨트에 안 닿는 순서로 자세 옮기기 (어깨 45° 먼저)
    ArticulationBody[] joints;       // 6관절 (순서 = 슬라이더 순서)

    // 브리지 상태 (다른 스레드가 채움)
    readonly object lk = new object();
    float[] followerDeg = new float[6];
    bool hasStatus, gotStatus, bFollow, bDry, bEstop;
    string bSource = "?", lastAck = "", lastError = "";
    float statusTime = -10f;

    UdpClient rx, tx;
    Thread rxThread;
    volatile bool running;
    int cmdId = 1000;

    public enum Mode { Leader, UnityPractice, UnityReal }
    Mode mode = Mode.Leader;

    // ---------- 다른 화면(TwinSidePanel)이 읽고 쓰는 값 ----------
    public Mode CurrentMode => mode;
    // 로봇 없이 연습 중 (유니티로 조종을 패널 연결 없이 시작함) — 이때는 패널·흰 팔에 아무것도 안 보냄
    public bool Offline { get; private set; }
    // 지금 넣고 있는 목표가 슬라이더 목표에 tol° 안으로 다 왔나 (녹화 재생이 따라잡기 기다릴 때)
    public bool NearTarget(float tol) => tester.Reached(tol);
    // 녹화 재생: 6관절 목표를 바로 넣음 (자세 이동 순서 없이, 초당 maxSpeedDps 제한은 그대로)
    public void StreamTarget(float[] deg) { mover.Cancel(); for (int i = 0; i < 6; i++) tester.anglesDeg[i] = Mathf.Clamp(deg[i], JointMin(i), JointMax(i)); }
    public bool Ready => joints != null && joints.Length >= 6;
    public bool Live { get { lock (lk) return hasStatus && Time.time - statusTime < 1.5f; } }
    // 비상 정지: 패널 상태가 1.5초 넘게 안 오면(로봇 없이 연습) 예전 값은 안 씀
    public bool EStopped { get { lock (lk) return bEstop && Time.time - statusTime < 1.5f; } }
    // 흰 팔이 지금 실제로 따라 움직이는 중 (패널 연동 켜짐 + 연습 모드 아님) — 이때 자세 저장은 흰 팔 실제 각도로
    public bool FollowerMovingReal { get { lock (lk) return bFollow && !bDry; } }
    public float FollowerDeg(int i) { lock (lk) return followerDeg[i]; }
    public float UnityDeg(int i) => joints[i].xDrive.target;
    public float JointMin(int i) => joints[i].xDrive.lowerLimit;
    public float JointMax(int i) => joints[i].xDrive.upperLimit;
    public bool JointOk(int i) => Mathf.Abs(UnityDeg(i) - FollowerDeg(i)) <= okDiffDeg;
    public bool AllOk { get { if (!Live) return false; for (int i = 0; i < 6; i++) if (!JointOk(i)) return false; return true; } }
    public float MaxSpeed => tester ? tester.maxSpeedDps : 10f;

    // ---------- 6관절 수동 제어 (고른 관절 하나를 슬라이더·±15° 버튼으로) ----------
    // 관절마다 "시작 각도"(유니티로 조종 시작 / 흰 팔에 맞추기 때)를 기억 → 슬라이더는 시작 ±30°, 관절 한계 안으로 자름
    public const float SliderSpan = 30f;
    public int Selected { get => selected; set => selected = Mathf.Clamp(value, 0, 5); }
    public void SelectNext(int d) => selected = (selected + d + 6) % 6;
    public float StartDeg(int i) => startDeg[i];
    public float Target(int i) => tester.anglesDeg[i];
    public void SetTarget(int i, float v) { mover.Cancel(); tester.anglesDeg[i] = Mathf.Clamp(v, JointMin(i), JointMax(i)); }
    public void ResetAll() => GoClosed(startDeg);              // 5관절 처음 각도로 + 집게 닫힘 (안전 순서)
    // 자동 자세로 — 집게는 닫힘(ClosedGripDeg). 손목 회전까지 5관절은 pose 값 그대로
    void GoClosed(float[] pose) { var g = (float[])pose.Clone(); g[5] = ClosedGripDeg; mover.MoveTo(g); }
    // 자세로 — 집게는 지금 각도 그대로 (브리지 goto_pose와 같음)
    public void GoPose(float[] pose) { var g = (float[])pose.Clone(); g[5] = tester.anglesDeg[5]; mover.MoveTo(g); }
    public void MoveToPose(float[] pose) => mover.MoveTo(pose);   // 저장한 자세로 (집게 포함 6관절, 안전 순서)
    public void HoldHere() => mover.HoldHere();                     // 그 자리에서 멈춤 (흰 팔 연동은 그대로)
    public void GoLPose() => GoClosed(new float[6]);
    public void GoRest() => GoClosed(restPoseDeg);
    public bool MovingPose => mover && mover.Moving;
    public string MoveStatus => mover ? mover.Status : "";
    public float SliderMin(int i) => Mathf.Max(JointMin(i), startDeg[i] - SliderSpan);
    public float SliderMax(int i) => Mathf.Min(JointMax(i), startDeg[i] + SliderSpan);
    public bool WaitingConfirm => Time.time < armConfirmUntil;
    public string LastAck { get { lock (lk) return lastAck; } }
    public static string JointName(int i) => Names[i];

    // [흰 팔 실제로 움직이기] 두 번 눌러야 시작: 첫 번째 = 3초 동안 확인 대기, 두 번째 = 시작
    public void PressGoReal()
    {
        if (mode != Mode.UnityPractice || Offline || !AllOk || EStopped) return;
        if (WaitingConfirm) { GoReal(); armConfirmUntil = -1f; } else armConfirmUntil = Time.time + 3f;
    }
    // 유니티를 흰 팔 자세로 옮기기 (유니티만 움직임, 어깨 45° 먼저 돌리는 안전 순서)
    public void MatchFollower()
    {
        for (int i = 0; i < 6; i++) startDeg[i] = FollowerDeg(i);
        mover.MoveTo(startDeg);
    }
    public void EStop() { Cmd("\"cmd\":\"estop\""); StopArm(); }   // 비상 정지는 로봇 없이 연습 중에도 늘 보냄 (보내서 손해 없음)
    float armConfirmUntil = -1f;     // [흰 팔 실제로 움직이기] 두 번째 누름 기다리는 시간
    readonly float[] startDeg = new float[6];   // 관절별 시작 각도 (슬라이더 가운데)
    int selected;                    // 수동 제어로 고른 관절 (0 = 어깨 회전)
    Font font;
    GUIStyle sBox, sLabel, sSmall, sBtn, sBig;

    static readonly string[] Names = { "어깨 회전", "어깨 들기", "팔꿈치", "손목 굽힘", "손목 회전", "그리퍼" };
    static readonly string[] Short = { "회전", "들기", "팔꿈치", "손목", "손목회전", "집게" };

    void Start()
    {
        receiver = GetComponent<LeaderArmReceiver>();
        tester = GetComponent<RobotJointTester>();
        sender = GetComponent<UnityTargetSender>();
        mover = GetComponent<ArmPoseMover>();
        if (!mover) mover = gameObject.AddComponent<ArmPoseMover>();   // 씬에 없으면 실행할 때 붙임
        if (!GetComponent<PoseLibrary>()) gameObject.AddComponent<PoseLibrary>();   // 자세 저장 창 (F3)
        var list = new System.Collections.Generic.List<ArticulationBody>();
        foreach (var b in GetComponentsInChildren<ArticulationBody>())
            if (b.jointType == ArticulationJointType.RevoluteJoint) list.Add(b);
        joints = list.ToArray();
        if (!receiver || !tester || !sender || joints.Length < 6)
        { Debug.LogWarning("[유니티→흰 팔] 필요한 부품이 없음 (LeaderArmReceiver / RobotJointTester / UnityTargetSender)"); enabled = false; return; }

        font = Resources.Load<Font>("Fonts/Pretendard-Medium");
        tx = new UdpClient();
        try
        {
            rx = new UdpClient(new IPEndPoint(IPAddress.Loopback, statusPort));
            running = true;
            rxThread = new Thread(RxLoop) { IsBackground = true };
            rxThread.Start();
        }
        catch (Exception e) { lastError = $"상태 포트 {statusPort} 못 엶: {e.Message}"; }
    }

    void OnDestroy()
    {
        running = false;
        try { rx?.Close(); } catch { }
        try { tx?.Close(); } catch { }
    }

    // 브리지가 보내는 상태·응답 받기 (백그라운드)
    void RxLoop()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                string s = Encoding.UTF8.GetString(rx.Receive(ref any));
                if (s.Contains("\"so101_ack\""))
                {
                    var a = JsonUtility.FromJson<Ack>(s);
                    lock (lk) lastAck = (a.ok ? "✔ " : "✖ ") + a.msg;
                }
                else if (s.Contains("\"so101_status\""))
                {
                    var st = JsonUtility.FromJson<Status>(s);
                    lock (lk)
                    {
                        if (st.follower != null && st.follower.deg != null && st.follower.deg.Length >= 6)
                            for (int i = 0; i < 6; i++) followerDeg[i] = st.follower.deg[i];
                        bFollow = st.follow; bDry = st.dry_run; bEstop = st.estop; bSource = st.source;
                        hasStatus = st.follower != null && st.follower.connected;
                        gotStatus = true;   // 메인 스레드(Update)에서 받은 시간 찍음
                    }
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
            catch (Exception) { }
        }
    }

    [Serializable] class Ack { public bool ok; public string msg; }
    [Serializable] class Arm { public bool connected; public float[] deg; }
    [Serializable] class Status { public Arm follower; public bool follow, dry_run, estop; public string source; }

    void Cmd(string json)
    {
        string body = "{\"type\":\"so101_cmd\",\"id\":" + (cmdId++) + "," + json + "}";
        byte[] b = Encoding.UTF8.GetBytes(body);
        try { tx.Send(b, b.Length, "127.0.0.1", cmdPort); } catch (Exception e) { lastError = e.Message; }
    }

    void Update()
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;     // 이 프로젝트는 새 Input System만 씀
        if (kb != null && kb.f2Key.wasPressedThisFrame) show = !show;
        lock (lk) if (gotStatus) { gotStatus = false; statusTime = Time.time; }
    }

    // ---------- 버튼 동작 ----------
    public void StartUnityMode()
    {
        if (mode != Mode.Leader || EStopped) return;
        Offline = !Live;                       // 패널·흰 팔 연결이 없으면 "로봇 없이 연습"
        receiver.enabled = false;              // 리더가 유니티 팔을 덮어쓰지 않게
        tester.StartFromCurrentPose();         // 슬라이더 = 지금 자세 (안 튐)
        tester.control = true;
        tester.enabled = true;
        for (int i = 0; i < 6; i++) startDeg[i] = tester.anglesDeg[i];
        if (Offline)
        {
            sender.send = false;               // 브리지로 아무것도 안 보냄
            mode = Mode.UnityPractice;
            Debug.Log("[유니티→흰 팔] 로봇 없이 연습 시작 (유니티 팔만 움직임, 패널에 명령 안 보냄)");
            return;
        }
        sender.send = true;                    // 유니티 목표 각도 → 브리지(5063)
        Cmd("\"cmd\":\"set_source\",\"source\":\"unity\"");
        // 연습 모드 연동은 0.5초 뒤에: 유니티 각도가 먼저 패널에 도착해 있어야 "신호 끊김"으로 바로 안 멈춤
        Invoke(nameof(SendPracticeFollow), 0.5f);
        mode = Mode.UnityPractice;
        Debug.Log("[유니티→흰 팔] 유니티로 조종 시작 (연습 모드, 흰 팔 안 움직임)");
    }

    void SendPracticeFollow()
    {
        if (mode == Mode.UnityPractice && !Offline) Cmd("\"cmd\":\"follow\",\"on\":true,\"dry_run\":true");   // 연습 모드: 흰 팔엔 안 씀
    }

    void GoReal()
    {
        // 브리지는 연동 중이면 새 연동을 거절함 ("이미 움직이는 중") → 연습 모드 연동을 먼저 멈추고 실제로 다시 시작
        Cmd("\"cmd\":\"stop\"");
        Cmd("\"cmd\":\"follow\",\"on\":true,\"dry_run\":false");
        mode = Mode.UnityReal;
        Debug.Log("[유니티→흰 팔] 흰 팔 실제로 움직이기 시작");
    }

    public void StopArm()
    {
        if (!Offline) Cmd("\"cmd\":\"stop\"");
        mover.Cancel();
        if (mode == Mode.UnityReal) mode = Mode.UnityPractice;
        tester.StartFromCurrentPose();
    }

    public void BackToLeader()
    {
        CancelInvoke(nameof(SendPracticeFollow));
        mover.Cancel();
        if (!Offline)
        {
            Cmd("\"cmd\":\"follow\",\"on\":false");
            Cmd("\"cmd\":\"set_source\",\"source\":\"leader\"");
        }
        Offline = false;
        sender.send = false;
        tester.enabled = false;                // 힘은 그대로 두고 슬라이더만 멈춤
        receiver.enabled = true;               // 다시 리더 따라하기
        mode = Mode.Leader;
        Debug.Log("[유니티→흰 팔] 리더로 돌아감 (흰 팔 연동은 패널에서 다시 켜기)");
    }

    // ---------- 화면 ----------
    void Styles()
    {
        if (sBox != null) return;
        sBox = new GUIStyle(GUI.skin.box) { padding = new RectOffset(12, 12, 10, 10) };
        sLabel = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };
        sSmall = new GUIStyle(sLabel) { fontSize = 12 };
        sBtn = new GUIStyle(GUI.skin.button) { fontSize = 14, fixedHeight = 30 };
        sBig = new GUIStyle(sBtn) { fontSize = 15, fontStyle = FontStyle.Bold, fixedHeight = 36 };
        if (font) foreach (var s in new[] { sBox, sLabel, sSmall, sBtn, sBig }) s.font = font;
    }

    void OnGUI()
    {
        if (!show || joints == null) return;
        Styles();
        float w = 380, h = mode == Mode.Leader ? 120 : 545;
        GUILayout.BeginArea(new Rect(12, Screen.height - h - 12, w, h), sBox);

        float[] fol; bool has, fol2, dry, est; string src, ack, err; float age;
        lock (lk) { fol = (float[])followerDeg.Clone(); has = hasStatus; fol2 = bFollow; dry = bDry; est = bEstop; src = bSource; ack = lastAck; err = lastError; age = Time.time - statusTime; }
        bool live = has && age < 1.5f;

        GUILayout.Label("<b>유니티 → 흰 팔</b>   <size=11>(F2 숨기기)</size>", sLabel);
        string st = !live ? "<color=#e57373>패널 연결 안 됨 (패널 켜고 [두 팔 연결])</color>"
                  : est ? "<color=#e57373>비상 정지 중 — 패널에서 해제</color>"
                  : $"패널: 소스 {src} · {(fol2 ? (dry ? "연습 모드" : "<color=#ffb74d>흰 팔 움직이는 중</color>") : "연동 꺼짐")}";
        GUILayout.Label(st, sSmall);

        if (mode == Mode.Leader)
        {
            GUI.enabled = !(live && est);
            if (GUILayout.Button(live ? "유니티로 조종 시작  (흰 팔은 아직 안 움직임)" : "로봇 없이 연습 시작  (유니티 팔만)", sBig)) StartUnityMode();
            GUI.enabled = true;
            GUILayout.EndArea();
            return;
        }

        // 6관절 차이 표
        bool allOk = live;
        for (int i = 0; i < 6; i++)
        {
            float u = joints[i].xDrive.target, f = fol[i], d = Mathf.Abs(u - f);
            bool ok = d <= okDiffDeg; if (!ok) allOk = false;
            string c = ok ? "#81c784" : "#e57373";
            GUILayout.Label($"{Names[i],-6}  유니티 {u,7:F1}°   흰 팔 {f,7:F1}°   <color={c}>차이 {d:F1}°</color>", sSmall);
        }

        if (Offline) GUILayout.Label("<color=#81c784>로봇 없이 연습 중 — 유니티 팔만 움직여요 (패널에 안 보냄)</color>", sSmall);
        // 실제로 움직이기
        if (mode == Mode.UnityPractice && !Offline)
        {
            // 차이가 크면: 유니티 로봇을 흰 팔 자세로 옮김 (유니티만 초당 10°로 움직임, 흰 팔은 그대로)
            if (!allOk && live && GUILayout.Button("유니티를 흰 팔 자세에 맞추기  (유니티만 움직임)", sBtn))
                MatchFollower();
            GUI.enabled = allOk && !est;
            bool waiting = Time.time < armConfirmUntil;
            string label = !allOk ? $"차이가 {okDiffDeg:F0}° 넘는 관절이 있어요" : waiting ? "⚠ 한 번 더 누르면 흰 팔이 움직여요" : "흰 팔 실제로 움직이기";
            if (GUILayout.Button(label, sBig))
            {
                if (waiting) { GoReal(); armConfirmUntil = -1f; } else armConfirmUntil = Time.time + 3f;
            }
            GUI.enabled = true;
        }

        // 관절 고르기 → 그 관절 슬라이더 (시작 각도 ±30°, 관절 한계 안) — 유니티는 초당 10°로 따라감
        GUILayout.Space(4);
        selected = GUILayout.Toolbar(selected, Short, sSmall);
        int j = selected;
        GUILayout.Label($"{Names[j]} 목표 <b>{tester.anglesDeg[j]:F1}°</b>  (시작 {startDeg[j]:F1}°, 초당 {tester.maxSpeedDps:F0}°)", sLabel);
        float slid = GUILayout.HorizontalSlider(tester.anglesDeg[j], SliderMin(j), SliderMax(j));
        if (slid != tester.anglesDeg[j]) SetTarget(j, slid);       // 끌었을 때만 (안 그러면 매 프레임 순서 이동이 취소됨)
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("처음 각도", sBtn)) SetTarget(j, startDeg[j]);
        if (GUILayout.Button("+15°", sBtn)) SetTarget(j, startDeg[j] + 15f);
        if (GUILayout.Button("−15°", sBtn)) SetTarget(j, startDeg[j] - 15f);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("처음 각도로", sBtn)) ResetAll();
        if (GUILayout.Button("ㄱ자로", sBtn)) GoLPose();
        if (GUILayout.Button("쉬는 자세로", sBtn)) GoRest();
        GUILayout.EndHorizontal();
        if (MovingPose) GUILayout.Label($"<color=#ffb74d>{MoveStatus} — 어깨 45° 돌림 → 손목/팔 → 나머지 → 어깨 되돌림</color>", sSmall);

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("정지", sBtn)) StopArm();
        if (GUILayout.Button("비상 정지", sBtn)) EStop();
        if (GUILayout.Button("리더로 돌아가기", sBtn)) BackToLeader();
        GUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(ack)) GUILayout.Label(ack, sSmall);
        if (!string.IsNullOrEmpty(err)) GUILayout.Label($"<color=#e57373>{err}</color>", sSmall);
        GUILayout.EndArea();
    }
}
