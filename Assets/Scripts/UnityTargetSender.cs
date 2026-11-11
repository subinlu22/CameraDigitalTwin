using System;
using System.Net.Sockets;              // UDP 통신
using System.Text;
using UnityEngine;

// =============================================================
// [유니티 → 흰 팔 목표 자세 송신기] 유니티 로봇팔의 6관절 각도를
// 컨트롤 패널 브리지(Tools/so101_bridge)로 UDP 5063에 보내는 스크립트
//
// 흐름: 1초에 sendHz번 → 6개 링크의 관절 각도를 읽음 → JSON {"type":"so101_target", ...} → UDP 전송
// ※ 보내기만 함. 흰 팔은 패널에서 입력 소스 = "unity" + 연동을 켰을 때만 따라감
//   (브리지가 안전 범위·속도 제한·0.3초 끊김 정지를 다 맡음 → docs/so101_safety.md)
// ※ 각도 규칙은 리더 미러링과 같음: 유니티 각도 = 흰 팔 각도 (오프셋 0, 반전 없음, 10/22 확인)
// ※ send를 끄면(기본) 아무것도 안 보냄 → 브리지가 0.3초 뒤 자동 정지
// =============================================================
public class UnityTargetSender : MonoBehaviour
{
    [Header("보내기 (기본 꺼짐)")]
    public bool send = false;              // 체크하면 보내기 시작
    public string host = "127.0.0.1";      // 브리지는 같은 PC에서만 받음
    public int port = 5063;                // so101_config.json의 target_in_port와 같아야 함
    [Range(10, 60)] public float sendHz = 30f;

    [Header("어떤 각도를 보낼지")]
    [Tooltip("켬: 관절이 실제로 있는 각도(물리 결과, 벨트에 막히면 막힌 각도) / 끔: 관절에 넣은 목표 각도(xDrive.target)")]
    public bool sendActualAngle = false;

    [Header("관절 (순서 = 메시지의 deg 순서, LeaderArmReceiver와 같음)")]
    public string[] linkNames = {
        "shoulder_link", "upper_arm_link", "lower_arm_link",
        "wrist_link", "gripper_link", "moving_jaw_so101_v1_link"
    };

    [Header("상태 (보기용)")]
    public int sentCount;
    public float[] lastDeg = new float[6];

    ArticulationBody[] joints;
    UdpClient udp;
    float timer;
    int seq;

    // 보낼 JSON 그릇 - 필드 이름이 브리지가 읽는 키 이름과 같아야 함
    [Serializable]
    class TargetMessage
    {
        public string type = "so101_target";
        public int seq;
        public double t;      // 보낸 시각(초, 유닉스 시간)
        public float[] deg;   // 6관절 각도(도)
    }

    void Start()
    {
        // 링크 이름으로 관절 찾기
        joints = new ArticulationBody[linkNames.Length];
        foreach (var body in GetComponentsInChildren<ArticulationBody>())
        {
            int i = Array.IndexOf(linkNames, body.name);
            if (i >= 0) joints[i] = body;
        }
        for (int i = 0; i < joints.Length; i++)
            if (joints[i] == null) Debug.LogWarning($"[흰 팔 송신] 관절을 못 찾음: {linkNames[i]}");

        udp = new UdpClient();
    }

    // 물리 단계(50Hz)마다 시간을 세다가 1/sendHz초가 지나면 한 번 보냄
    void FixedUpdate()
    {
        if (!send || udp == null) { timer = 0f; return; }
        timer += Time.fixedDeltaTime;
        if (timer < 1f / sendHz) return;
        timer = 0f;

        var msg = new TargetMessage { seq = ++seq, t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, deg = new float[joints.Length] };
        for (int i = 0; i < joints.Length; i++)
        {
            if (joints[i] == null) return;                 // 관절이 하나라도 없으면 안 보냄 (잘못된 자세 방지)
            msg.deg[i] = sendActualAngle
                ? joints[i].jointPosition[0] * Mathf.Rad2Deg // 실제 각도 (라디안 → 도)
                : joints[i].xDrive.target;                   // 목표 각도 (이미 도)
            lastDeg[i] = msg.deg[i];
        }

        byte[] data = Encoding.UTF8.GetBytes(JsonUtility.ToJson(msg));
        try { udp.Send(data, data.Length, host, port); sentCount++; }
        catch (Exception e) { Debug.LogWarning("[흰 팔 송신] 보내기 실패: " + e.Message); }
    }

    void OnDestroy() { udp?.Close(); }
}
