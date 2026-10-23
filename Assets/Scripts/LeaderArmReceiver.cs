using System;
using System.Net;
using System.Net.Sockets;              // UDP 통신
using System.Text;
using System.Threading;                // 메시지를 계속 기다리는 작업을 별도 스레드로 돌리기 위해 필요함
using UnityEngine;

// =============================================================
// [리더암 미러링 수신기] 파이썬 리더 허브(Tools/leader_hub/leader_hub.py)가 UDP로 보낸
// 리더암 6관절 각도를 받아서, 유니티 로봇팔 관절을 똑같은 각도로 움직이는 스크립트
//
// 흐름: (별도 스레드) UDP 수신 → 가장 최근 메시지 하나만 보관
//       → (FixedUpdate, 물리 단계 50Hz) 새 메시지가 오면 "지금 각도 → 새 각도" 구간을 만들고,
//         매 단계 그 구간의 중간값(보간)을 관절 목표 각도(xDrive.target)로 넣기
// ※ 메시지는 30Hz, 물리는 50Hz라 보간이 없으면 목표가 멈췄다 튀었다 함 → 끊김
//   보간하면 메시지 1개 간격(약 33ms)만큼 늦는 대신 매 단계 조금씩 이어서 움직임
// ※ 미러링은 "지금 자세"만 중요해서, 밀린 옛날 메시지는 버리고 최신 것만 씀
// ※ 0자세·방향은 10/22에 확인함: 웹 시뮬 각도 = 유니티 각도 (오프셋 0, 반전 없음)
// =============================================================
public class LeaderArmReceiver : MonoBehaviour
{
    [Header("UDP 설정")]
    public int port = 5060; // leader_hub.py의 DEFAULT_UDP 포트와 같아야 함

    [Header("관절 (순서 = 메시지의 deg 순서)")]
    // shoulder_pan, shoulder_lift, elbow_flex, wrist_flex, wrist_roll, gripper 를 움직이는 링크 이름
    public string[] linkNames = {
        "shoulder_link", "upper_arm_link", "lower_arm_link",
        "wrist_link", "gripper_link", "moving_jaw_so101_v1_link"
    };
    public bool[] useJoint = { true, true, true, true, true, true }; // 체크한 관절만 리더를 따라감
    public bool holdOthersAtZero = true;   // 체크 안 한 관절은 0°에서 버티게 (끄면 중력에 처짐)

    [Header("보정값 (10/22 확인: 전부 0 / 반전 없음)")]
    public float[] offsetDeg = new float[6]; // 유니티 각도 = (리더 각도 × 방향) + 오프셋
    public bool[] invert = new bool[6];

    [Header("관절 힘 (RobotJointTester와 같은 값)")]
    public float stiffness = 10000f;
    public float damping = 100f;
    public float forceLimit = 1000f;

    [Header("보간 (10/23 B안: 두 메시지 사이를 시간에 맞춰 채우기)")]
    public bool interpolate = true;        // 끄면 예전처럼 메시지 올 때 바로 점프
    public float maxGapSec = 0.1f;         // 메시지 간격이 이보다 길면(끊김) 이 시간 안에 따라잡음

    [Header("측정 (최근 1초, 보기용)")]
    public float frozenStepRatio;   // 리더가 움직이는 중인데 목표 각도가 안 바뀐 물리 단계 비율 (끊김)
    public float maxStepDeg;        // 물리 단계 한 번에 목표가 가장 크게 바뀐 양 (점프 크기)
    public float avgLagDeg;         // 리더 각도와 유니티 실제 관절 각도의 평균 차이 (지연)
    public float maxLagDeg;
    public bool logMeasure = true;  // 1초마다 측정값을 Console에 찍기 (비교 기록용)

    [Header("측정 누계 (리더가 움직인 단계만, 보기용)")]
    public int totalSteps, totalFrozen;
    public float totalMaxStep, totalAvgLag, totalMaxLag;
    float tLagSum; int tLagN;
    public void ResetTotals() { totalSteps = totalFrozen = tLagN = 0; totalMaxStep = totalAvgLag = totalMaxLag = tLagSum = 0f; }

    [Header("상태 (보기용)")]
    public int receivedCount;      // 받은 메시지 수 (화면 프레임보다 빨리 오면 최신 것만 적용됨 — 정상)
    public int lastSeq;            // 마지막 메시지 순서 번호
    public int lostCount;          // 네트워크에서 잃어버린 메시지 수 (보낸 번호 − 받은 수)
    public float receiveHz;        // 최근 1초 동안 받은 속도
    public float[] lastDeg = new float[6];

    UdpClient udpClient;
    Thread receiveThread;
    volatile bool isRunning;       // volatile = 다른 스레드에서 바꾼 값을 바로 보이게 하는 표시
    volatile string latestJson;    // 수신 스레드가 넣고 Update가 꺼내 가는 "가장 최근 메시지"
    int newMessages;               // 마지막 Update 이후 새로 받은 수 (스레드끼리 Interlocked로 셈)
    int seqBase = -1;              // 잃어버린 수 계산 기준 (허브를 다시 켜면 다시 잡음)

    ArticulationBody[] joints;
    int hzCount; float hzTimer;    // 수신 속도 계산용

    // 보간용: "출발 각도"에서 "최신 각도"까지 duration초 동안 이어서 그림
    float[] fromDeg = new float[6], toDeg = new float[6], cmdDeg = new float[6];
    float segStart, segDuration = 1f / 30f;
    double lastMsgT = -1;
    bool hasTarget;

    // 측정용
    float[] prevCmd = new float[6];
    int mSteps, mFrozen; float mMaxStep, mLagSum, mLagMax; int mLagN; float mTimer;
    float lastReceiveTime = -1f;
    bool warnedStale;

    // 받은 JSON을 담을 그릇 - 필드 이름이 JSON 키 이름과 정확히 같아야 JsonUtility가 채워줌
    [Serializable]
    class LeaderMessage
    {
        public string type;  // "so101_leader"
        public int seq;      // 보낸 순서 번호
        public double t;     // 보낸 시각(초)
        public float[] deg;  // 6관절 각도(도)
    }

    // Start()는 플레이 시작할 때 유니티가 한 번 자동으로 호출하는 함수
    // (DroopRobotPose.Awake가 먼저 돌아서 힘을 빼둔 뒤라, 여기서 쓸 관절에 다시 힘을 넣음)
    void Start()
    {
        // [관절 찾기] 링크 이름으로 자식 중에서 ArticulationBody를 찾음
        joints = new ArticulationBody[linkNames.Length];
        foreach (var body in GetComponentsInChildren<ArticulationBody>())
        {
            int i = Array.IndexOf(linkNames, body.name);
            if (i >= 0) joints[i] = body;
        }
        for (int i = 0; i < joints.Length; i++)
        {
            if (joints[i] == null) { Debug.LogWarning($"[미러링] 관절을 못 찾음: {linkNames[i]}"); continue; }
            if (useJoint[i] || holdOthersAtZero) SetDrive(joints[i], 0f, true);
        }

        // [UDP 열기] 이 포트로 오는 메시지를 받을 준비
        udpClient = new UdpClient(port);
        isRunning = true;
        receiveThread = new Thread(ReceiveLoop);
        receiveThread.IsBackground = true; // 유니티가 꺼지면 이 스레드도 같이 꺼지게
        receiveThread.Start();
        Debug.Log($"[미러링] UDP 수신 대기 시작 (포트 {port})");
    }

    // [수신 스레드] 메시지가 올 때마다 "가장 최근 메시지" 자리에 덮어씀
    void ReceiveLoop()
    {
        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0); // 아무 데서 오든 받음
        while (isRunning)
        {
            try
            {
                byte[] data = udpClient.Receive(ref remote); // 메시지 올 때까지 여기서 기다림
                latestJson = Encoding.UTF8.GetString(data);
                Interlocked.Increment(ref newMessages);
            }
            catch (SocketException) { }                 // 종료할 때 나는 예외 - 정상이라 무시
            catch (ObjectDisposedException) { break; }  // 소켓이 이미 정리됨 → 반복 종료
        }
    }

    // FixedUpdate()는 물리 계산 직전마다(0.02초 = 50Hz) 유니티가 자동으로 호출하는 함수
    // 관절은 물리로 움직여서, 목표 각도도 물리 단계마다 넣어야 매끄러움
    void FixedUpdate()
    {
        float now = Time.time; // FixedUpdate 안에서는 물리 시각
        int count = Interlocked.Exchange(ref newMessages, 0); // 새로 받은 수를 꺼내고 0으로
        receivedCount += count;
        UpdateRate(count, Time.fixedDeltaTime);

        if (count > 0)
        {
            ApplyMessage(latestJson, now);
            lastReceiveTime = now;
            warnedStale = false;
        }
        else if (lastReceiveTime > 0 && now - lastReceiveTime > 1f && !warnedStale)
        {
            Debug.LogWarning("[미러링] 1초 넘게 메시지가 없음 — leader_hub.py가 켜져 있나요?");
            warnedStale = true;
        }

        if (!hasTarget) return;

        // [보간] 출발 각도 → 최신 각도 사이에서 지금 시각에 해당하는 각도를 계산
        float a = interpolate ? Mathf.Clamp01((now - segStart) / segDuration) : 1f;
        for (int i = 0; i < joints.Length; i++)
        {
            cmdDeg[i] = Mathf.Lerp(fromDeg[i], toDeg[i], a);
            if (!useJoint[i] || joints[i] == null) continue;
            SetDrive(joints[i], cmdDeg[i], false);
        }
        Measure(Time.fixedDeltaTime);
    }

    // [메시지 적용] JSON 해석 → 보간의 새 구간을 시작 (지금 그리는 각도에서 최신 각도로)
    void ApplyMessage(string json, float now)
    {
        LeaderMessage msg;
        try { msg = JsonUtility.FromJson<LeaderMessage>(json); }
        catch (Exception e) { Debug.LogWarning("[미러링] JSON 해석 실패: " + e.Message); return; }
        if (msg == null || msg.deg == null || msg.deg.Length < joints.Length) return;

        // 허브를 처음 받거나 다시 켰으면(번호가 1부터 다시 시작) 계산 기준을 새로 잡음
        if (seqBase < 0 || msg.seq < lastSeq)
        {
            receivedCount = 1;
            seqBase = msg.seq - 1;
        }
        lastSeq = msg.seq;
        lostCount = Mathf.Max(0, (lastSeq - seqBase) - receivedCount);

        // 구간 길이 = 보낸 쪽 시각 차이 (보통 1/30초). 이상하면 1/30초, 너무 길면 maxGapSec
        double gap = lastMsgT > 0 ? msg.t - lastMsgT : 1.0 / 30.0;
        segDuration = (float)(gap > 0.005 ? Math.Min(gap, maxGapSec) : 1.0 / 30.0);
        lastMsgT = msg.t;

        for (int i = 0; i < joints.Length; i++)
        {
            lastDeg[i] = msg.deg[i];
            float deg = (invert[i] ? -msg.deg[i] : msg.deg[i]) + offsetDeg[i];
            // 첫 메시지는 바로 그 자세로, 이후엔 "지금 그리는 각도"에서 출발 → 중간에 와도 안 튐
            fromDeg[i] = hasTarget ? cmdDeg[i] : deg;
            toDeg[i] = deg;
        }
        // 구간 시작을 한 단계 앞으로 잡음 → 메시지가 온 이 단계부터 바로 움직임
        // (now로 잡으면 이 단계의 비율이 0이라 목표가 그대로 = 메시지마다 한 번씩 멈춤)
        segStart = now - Time.fixedDeltaTime;
        hasTarget = true;
    }

    // [측정] 1초마다: 끊김(목표가 멈춘 단계 비율), 점프 크기, 지연(리더 vs 실제 관절 각도 차이)
    void Measure(float dt)
    {
        // 리더가 움직이는 중 = 0.1초 안에 메시지가 왔고, 그 메시지에서 각도가 바뀜 (허브가 꺼지면 안 셈)
        bool leaderMoving = false;
        if (Time.time - lastReceiveTime < 0.1f)
            for (int i = 0; i < 5; i++) // 그리퍼 제외 5관절
                if (Mathf.Abs(toDeg[i] - fromDeg[i]) > 0.3f) leaderMoving = true;

        float step = 0f;
        for (int i = 0; i < 5; i++)
        {
            step = Mathf.Max(step, Mathf.Abs(cmdDeg[i] - prevCmd[i]));
            prevCmd[i] = cmdDeg[i];
            if (joints[i] == null || !useJoint[i]) continue;
            float actual = joints[i].jointPosition[0] * Mathf.Rad2Deg; // 실제 관절 각도
            float lag = Mathf.Abs(toDeg[i] - actual);
            mLagSum += lag; mLagN++; mLagMax = Mathf.Max(mLagMax, lag);
        }
        if (leaderMoving) { mSteps++; if (step < 0.01f) mFrozen++; }
        mMaxStep = Mathf.Max(mMaxStep, step);

        // 누계 (보간 끔/켬 구간 비교용 — ResetTotals()로 0부터 다시 셈)
        if (leaderMoving)
        {
            totalSteps++; if (step < 0.01f) totalFrozen++;
            totalMaxStep = Mathf.Max(totalMaxStep, step);
            for (int i = 0; i < 5; i++)
            {
                if (joints[i] == null || !useJoint[i]) continue;
                float lag = Mathf.Abs(toDeg[i] - joints[i].jointPosition[0] * Mathf.Rad2Deg);
                tLagSum += lag; tLagN++; totalMaxLag = Mathf.Max(totalMaxLag, lag);
            }
            totalAvgLag = tLagN > 0 ? tLagSum / tLagN : 0f;
        }

        mTimer += dt;
        if (mTimer >= 1f)
        {
            frozenStepRatio = mSteps > 0 ? (float)mFrozen / mSteps : 0f;
            maxStepDeg = mMaxStep;
            avgLagDeg = mLagN > 0 ? mLagSum / mLagN : 0f;
            maxLagDeg = mLagMax;
            if (logMeasure && mSteps > 0) // 리더가 움직인 1초만 찍음
                Debug.Log($"[미러링 측정] 보간={(interpolate ? "켬" : "끔")} 수신={receiveHz:F0}Hz " +
                          $"끊김={frozenStepRatio * 100f:F0}% 최대점프={maxStepDeg:F2}° " +
                          $"지연 평균={avgLagDeg:F2}° 최대={maxLagDeg:F2}° 움직인단계={mSteps}");
            mSteps = mFrozen = mLagN = 0; mMaxStep = mLagSum = mLagMax = 0f; mTimer = 0f;
        }
    }

    // [관절 구동] 목표 각도를 넣음. 관절 한계 밖이면 한계로 자름
    void SetDrive(ArticulationBody body, float targetDeg, bool setForce)
    {
        ArticulationDrive drive = body.xDrive; // 구조체라서 복사본을 꺼내서
        if (setForce)
        {
            drive.stiffness = stiffness;        // 자세를 버티는 힘
            drive.damping = damping;            // 흔들림을 잡는 저항
            drive.forceLimit = forceLimit;
        }
        if (drive.upperLimit > drive.lowerLimit)
            targetDeg = Mathf.Clamp(targetDeg, drive.lowerLimit, drive.upperLimit);
        drive.target = targetDeg;
        body.xDrive = drive;                    // 다시 넣어준다
    }

    // [수신 속도] 1초마다 받은 개수를 Hz로 표시
    void UpdateRate(int count, float dt)
    {
        hzCount += count;
        hzTimer += dt;
        if (hzTimer >= 1f)
        {
            receiveHz = hzCount / hzTimer;
            hzCount = 0; hzTimer = 0f;
        }
    }

    // 플레이를 멈추거나 오브젝트가 사라질 때: 스레드와 소켓 정리 (안 하면 포트가 계속 잡혀 있음)
    void OnDestroy()
    {
        isRunning = false;
        udpClient?.Close();
    }
}
