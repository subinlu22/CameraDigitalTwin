using System;
using System.Collections.Concurrent;   // 스레드끼리 안전하게 주고받는 큐(ConcurrentQueue) 쓰려고 필요함
using System.Collections.Generic;      // Dictionary 쓰려고 필요함
using System.Net;
using System.Net.Sockets;              // UDP 통신
using System.Text;
using System.Threading;                // 메시지를 계속 기다리는 작업을 별도 스레드로 돌리기 위해 필요함
using UnityEngine;

// =============================================================
// [디지털트윈 수신기] 카메라 검사 프로그램(C#)이 보낸 판정 결과를 UDP로 받아서,
// 같은 색/숫자의 뚜껑을 벨트 위에 띄우고 흘려보내는 스크립트
//
// 흐름: (별도 스레드) UDP 수신 → 큐에 쌓기 → (Update, 메인 스레드) 큐에서 꺼내서 뚜껑 생성
// ※ 유니티 오브젝트(Instantiate 등)는 메인 스레드에서만 만질 수 있어서, 수신 스레드는
//   받은 글자만 큐에 넣고 실제 생성은 Update에서 함 (HandBlock 때랑 같은 원리)
// =============================================================
public class DigitalTwinReceiver : MonoBehaviour
{
    [Header("UDP 설정")]
    public int port = 5053; // 카메라 프로그램(Form1.cs)의 UnityPort와 같아야 함

    [Header("뚜껑 견본 (b1~b3, r1~r3, w1~w3가 들어있는 부모 오브젝트)")]
    public Transform lidTemplatesParent; // Hierarchy의 number 오브젝트를 끌어다 넣기

    [Header("벨트 경로 (FBX 밖에 따로 만든 빈 오브젝트)")]
    public Transform spawnPoint; // 뚜껑이 나타날 위치 - 카메라 바로 아래 벨트 위
    public Transform endPoint;   // 뚜껑이 사라질 위치 - 벨트 끝

    [Header("벨트 속도 (초당 이동 거리, 플레이 중에도 조절 가능)")]
    public float beltSpeed = 0.2f; // 나중에 벨트 텍스처 스크롤도 이 값을 같이 쓸 예정

    [Header("대시보드 UI (비워두면 씬에서 자동으로 찾음)")]
    public DashboardUI dashboard; // [신규] 통계 패널 + 테두리 빛

    UdpClient udpClient;
    Thread receiveThread;
    volatile bool isRunning; // volatile = 다른 스레드에서 바꾼 값을 바로 보이게 하는 표시

    // 수신 스레드 → 메인 스레드로 메시지를 넘기는 통로 (여러 스레드가 동시에 써도 안전)
    readonly ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();

    // "r1" 같은 이름 → 견본 오브젝트 (이름으로 찾기 때문에 FBX를 교체해도 이름만 같으면 그대로 동작)
    readonly Dictionary<string, GameObject> lidTemplates = new Dictionary<string, GameObject>();

    // 받은 JSON을 담을 그릇 - 필드 이름이 JSON 키 이름과 정확히 같아야 JsonUtility가 채워줌
    [Serializable]
    class InspectionMessage
    {
        public string color; // "white" / "blue" / "red"
        public string digit; // "1" / "2" / "3"
        public bool good;    // 검사 기준 통과 여부
        public string type;   // [신규] "inspection"(검사 결과) / "criterion"(검사 기준 변경)
        public string target; // [신규] 현재 검사 기준 ("all" / "white" / "blue" / "red")
    }

    // Start()는 플레이 시작할 때 유니티가 한 번 자동으로 호출하는 함수
    void Start()
    {
        // [견본 등록] number 아래 자식들을 이름표 붙여서 사전에 넣어둠
        foreach (Transform child in lidTemplatesParent)
        {
            lidTemplates[child.name] = child.gameObject;
        }
        Debug.Log($"[연동] 뚜껑 견본 {lidTemplates.Count}개 등록: {string.Join(", ", lidTemplates.Keys)}");

        // [신규] 대시보드 칸을 비워뒀으면 씬에서 DashboardUI가 붙은 오브젝트를 알아서 찾음
        if (dashboard == null)
        {
            dashboard = FindFirstObjectByType<DashboardUI>();
        }

        // [UDP 열기] 이 포트로 오는 메시지를 받을 준비
        udpClient = new UdpClient(port);
        isRunning = true;
        receiveThread = new Thread(ReceiveLoop);
        receiveThread.IsBackground = true; // 유니티가 꺼지면 이 스레드도 같이 꺼지게
        receiveThread.Start();
        Debug.Log($"[연동] UDP 수신 대기 시작 (포트 {port})");
    }

    // [수신 스레드] 메시지가 올 때까지 기다렸다가, 오면 큐에 넣고 다시 기다리기를 반복
    void ReceiveLoop()
    {
        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0); // 아무 데서 오든 받음
        while (isRunning)
        {
            try
            {
                byte[] data = udpClient.Receive(ref remote); // 메시지 올 때까지 여기서 멈춰서 기다림
                messageQueue.Enqueue(Encoding.UTF8.GetString(data));
            }
            catch (SocketException)
            {
                // 종료할 때 소켓을 닫으면 Receive가 깨지면서 나는 예외 - 정상이라 무시
            }
            catch (ObjectDisposedException)
            {
                break; // 소켓이 이미 정리됨 → 반복 종료
            }
        }
    }

    // Update()는 매 프레임마다 유니티가 자동으로 호출하는 함수
    void Update()
    {
        // 큐에 쌓인 메시지를 전부 꺼내서 처리
        while (messageQueue.TryDequeue(out string json))
        {
            HandleMessage(json);
        }
    }

    // [메시지 처리] JSON 해석 → 견본 이름 만들기 → 뚜껑 생성
    void HandleMessage(string json)
    {
        Debug.Log("[연동] 수신: " + json);

        InspectionMessage msg;
        try
        {
            msg = JsonUtility.FromJson<InspectionMessage>(json);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[연동] JSON 해석 실패: " + e.Message);
            return;
        }

        // [신규] 검사 기준 반영 - 기준 변경 메시지든 검사 결과든 target이 실려 오면 대시보드에 전달
        // (기준이 실제로 바뀌었을 때만 양품/불량이 0부터 다시 시작 - 판단은 DashboardUI가 함)
        if (dashboard != null)
        {
            dashboard.SetCriterion(msg.target);
        }
        if (msg.type == "criterion")
        {
            return; // 기준 변경 메시지는 여기까지 (뚜껑 생성 없음)
        }

        // 색 이름 → 견본 이름 앞글자 (white→w, blue→b, red→r)
        string prefix;
        if (msg.color == "white") prefix = "w";
        else if (msg.color == "blue") prefix = "b";
        else if (msg.color == "red") prefix = "r";
        else
        {
            Debug.LogWarning("[연동] 알 수 없는 색: " + msg.color);
            return;
        }

        // [신규] 대시보드 통계 + 테두리 빛 반영 (뚜껑 모델이 없어도 검사 결과는 집계되게 여기서 먼저 처리)
        if (dashboard != null)
        {
            dashboard.RegisterInspection(msg.color, msg.digit, msg.good);
        }

        string templateName = prefix + msg.digit; // 예: "r1"
        if (!lidTemplates.TryGetValue(templateName, out GameObject template))
        {
            Debug.LogWarning("[연동] 견본을 못 찾음: " + templateName);
            return;
        }

        SpawnLid(template, msg.good);
    }

    // [뚜껑 생성] 견본을 복사해서 SpawnPoint에 놓고, 벨트 끝까지 흘러가게 함
    void SpawnLid(GameObject template, bool isGood)
    {
        // 견본의 회전(숫자가 위로 보이게 놓인 방향)은 그대로 쓰고, 위치만 SpawnPoint로
        GameObject lid = Instantiate(template, spawnPoint.position, template.transform.rotation);
        lid.transform.localScale = template.transform.lossyScale; // 부모 밖으로 나와도 크기가 그대로 보이게 (lossyScale = 화면에 실제로 보이는 크기)
        lid.name = template.name + "_clone";

        // 견본에 물리(Rigidbody)가 붙어 있으면 떨어지거나 굴러가서 이동 코드랑 싸우니까 꺼둠
        Rigidbody rb = lid.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
        }

        // 도착 지점 높이는 출발 지점과 맞춤 → EndPoint를 대충 놓아도 비스듬히 안 날아가고 벨트를 따라 수평으로 이동
        Vector3 target = endPoint.position;
        target.y = spawnPoint.position.y;

        LidMover mover = lid.AddComponent<LidMover>();
        mover.Init(target, this);

        Debug.Log($"[연동] {template.name} 생성 (양품: {isGood})");
    }

    // [테스트용] 카메라 없이 확인하는 방법:
    // 플레이 중에 Inspector에서 이 컴포넌트 이름 옆 ⋮ 메뉴(또는 우클릭) → "테스트: 빨강1 생성"
    [ContextMenu("테스트: 빨강1 생성")]
    void TestSpawnRed1()
    {
        HandleMessage("{\"color\":\"red\",\"digit\":\"1\",\"good\":true}");
    }

    // [신규] 불량(빨간 테두리) 확인용 테스트
    [ContextMenu("테스트: 파랑2 불량")]
    void TestSpawnBlue2Bad()
    {
        HandleMessage("{\"color\":\"blue\",\"digit\":\"2\",\"good\":false}");
    }

    // OnDestroy()는 플레이를 멈추거나 오브젝트가 사라질 때 자동으로 호출됨 → 소켓 정리
    void OnDestroy()
    {
        isRunning = false;   // 먼저 반복을 멈추라고 표시하고
        udpClient?.Close();  // 소켓을 닫으면 기다리던 Receive가 깨지면서 스레드가 끝남
    }
}
