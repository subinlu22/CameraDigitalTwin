using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 오른쪽 청록 유리 패널: 장비(로봇·카메라) 카드와 검사 현황을 실시간으로 보여줌
public partial class TwinSidePanel : MonoBehaviour   // 로봇 [자세] 탭은 TwinSidePanel.Poses.cs
{
    [Min(300)] public float width = 400;
    [Min(8)] public float margin = 20;
    public bool realBlur = true;
    [SerializeField] Camera pickingPreviewCamera;
    RenderTexture pickingFrame;

    enum Device { Robot, Vision }

    sealed class Glass
    {
        public RectTransform Root, Mask;
        public RawImage Backdrop;
        public Image Tint;
    }

    // 디지털 트윈 관제 화면 스타일 (11/17): 어두운 반투명 유리 + 청록 포인트 하나. 색은 이상 상태(미연결·불량·경고)에만
    static readonly Color Ink = Hex(0xE9EEF3), Secondary = Hex(0xC4CDD6), Muted = Hex(0x8C98A5);
    static readonly Color Blue = Hex(0x5FD4E8), BlueInk = Hex(0xA8ECF6), Alert = Hex(0xFF6A6E), Warn = Hex(0xF2B441);
    static readonly Color Border = new Color(1, 1, 1, .13f), LineColor = new Color(1, 1, 1, .08f);
    static readonly Color CardFill = new Color(1, 1, 1, .035f);
    static readonly Color ActiveFill = new Color(.37f, .83f, .91f, .12f);
    static readonly Color AccentEdge = new Color(.37f, .83f, .91f, .55f);
    static readonly Color AccentInk = Hex(0x04161B);                    // 청록 버튼 위 글자
    static readonly Color Tint = new Color(.04f, .055f, .075f, .56f);   // 유리: 뒤가 은은하게 비침
    static readonly Color TintNoBlur = new Color(.04f, .055f, .075f, .9f);
    static readonly string[] Colors = { "white", "blue", "red" };
    static readonly string[] Digits = { "1", "2", "3" };
    const float Gap = 10f;
    const float Inset = 17f;

    DashboardUI dash;
    UnityToFollowerGUI robot;
    Camera sceneCamera;
    Transform inspectionAnchor;
    Font regular, medium, semi, bold;
    Sprite panelSprite, panelEdge, cardSprite, cardEdge, pillSprite, dotSprite, barSprite, stripeSprite;
    readonly List<Sprite> generated = new List<Sprite>();
    readonly Vector3[] corners = new Vector3[4];
    Canvas canvas;
    Glass detail, overview, statusBar;
    RectTransform overviewPill;
    Text pillSummary, linkUnity, linkFollower, linkLeader, clockText;
    Image linkUnityDot, linkFollowerDot, linkLeaderDot;
    readonly RectTransform[] jointMarker = new RectTransform[6];
    readonly Image[] jointRowFill = new Image[6];
    RectTransform robotPage, visionPage, tabs, footer;
    RawImage preview;
    Text previewLabel, detailTitle, detailState, detailCaption;
    Text totalText, goodText, badText, yieldText, robotCardState, visionCardState, robotCardMeta, visionCardMeta;
    Text currentRule, sceneState, lastReceiveText, visionTotal, visionYield, visionBad, jointValueText;
    Text primaryText, secondaryText, stopText, modeHint;
    Image robotCard, robotEdge, visionCard, visionEdge, robotDot, visionDot, primaryFill, tabOverview, tabDetail;
    readonly Text[] colorCount = new Text[3], digitCount = new Text[3];
    readonly Text[] jointUnity = new Text[6], jointFollower = new Text[6], jointDifference = new Text[6];
    readonly Button[] stepButtons = new Button[3];
    readonly Text[] jointName = new Text[6];
    Button prevJoint, nextJoint;
    Text manualJointText;
    Button primary, secondary, stop;
    Slider jointSlider;
    Device selected;
    bool detailOpen = true, narrow, dragging;
    int viewWidth, viewHeight;
    float detailWidth, overviewWidth, detailContent, overviewContent;
    float Gutter => Mathf.Min(margin, 13f);

    void Start()
    {
        dash = FindFirstObjectByType<DashboardUI>();
        robot = FindFirstObjectByType<UnityToFollowerGUI>();
        if (robot) robot.show = false;
        sceneCamera = Camera.main;
        var ring = GameObject.Find("RingLight_Glow");
        if (ring) inspectionAnchor = ring.transform;
        if (sceneCamera && !sceneCamera.TryGetComponent<DashboardGlassBlur>(out _))
        {
            var blur = sceneCamera.gameObject.AddComponent<DashboardGlassBlur>();
            blur.iterations = 4;
            blur.spread = 1.35f;
        }

        LoadFonts();
        EnsureEventSystem();
        // 모서리는 작게 (둥근 카드 느낌 대신 각진 계측기 느낌)
        panelSprite = MakeRounded(32, 6);
        panelEdge = MakeRounded(32, 6, 1.2f);
        cardSprite = MakeRounded(16, 3);
        cardEdge = MakeRounded(16, 3, 1.1f);
        pillSprite = MakeRounded(16, 3);
        dotSprite = MakeRounded(32, 16);
        barSprite = MakeRounded(8, 1);
        stripeSprite = MakeStripes();
        Build();
        PreparePickingPreview();
        SetViewport(Screen.width, Screen.height);
        UpdateInspection();
        UpdateRobot();
    }

    void Update()
    {
        if (!canvas) return;
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard != null && keyboard.f2Key.wasPressedThisFrame)
            canvas.gameObject.SetActive(!canvas.gameObject.activeSelf);
        if (!canvas.gameObject.activeSelf) return;

        if (viewWidth != Screen.width || viewHeight != Screen.height)
            SetViewport(Screen.width, Screen.height);
        UpdateInspection();
        UpdateRobot();
        UpdatePoses();
    }

    void SetViewport(int pixelWidth, int pixelHeight)
    {
        viewWidth = pixelWidth;
        viewHeight = pixelHeight;
        narrow = false;   // 패널을 한쪽(왼쪽)에 나란히 → 좁은 화면에서도 둘 다 보임 (예전 탭 전환은 안 씀)
        ShowPanels();
    }

    // 배치: 전부 왼쪽에 모음 (로봇은 화면 가운데~오른쪽이라 안 가림)
    //   [장비 정보] [장비 현황 — 접으면 작은 막대만] , 비상 정지 = 둘째 칸 맨 아래에 늘 보임, 위 = 상태 바
    const float StatusH = 34f, StopH = 52f, CollapsedH = 40f;
    bool overviewOpen = true;

    void ShowPanels()
    {
        detail.Root.gameObject.SetActive(detailOpen);
        tabs.gameObject.SetActive(false);
        float top = Gutter + StatusH + Gap;
        float x2 = Gutter + detailWidth + Gap;   // 둘째 칸 왼쪽
        DockLeft(detail.Root, Gutter, detailWidth, top, Gutter);
        overview.Root.gameObject.SetActive(overviewOpen);
        overviewPill.gameObject.SetActive(!overviewOpen);
        DockLeft(overview.Root, x2, overviewWidth, top, Gutter + StopH + Gap);
        Place(overviewPill, x2, top, overviewWidth, CollapsedH);
        var bar = statusBar.Root;
        bar.anchorMin = new Vector2(0, 1); bar.anchorMax = Vector2.one; bar.pivot = new Vector2(.5f, 1);
        bar.offsetMin = new Vector2(Gutter, -Gutter - StatusH);
        bar.offsetMax = new Vector2(-Gutter, -Gutter);
        footer.anchorMin = footer.anchorMax = footer.pivot = Vector2.zero;
        footer.anchoredPosition = new Vector2(x2, Gutter);
        footer.sizeDelta = new Vector2(overviewWidth, StopH);
    }

    // 화면 왼쪽에서 x만큼 떨어진 곳에, 위아래로 늘어나는 칸
    void DockLeft(RectTransform panel, float x, float w, float top, float bottom)
    {
        panel.anchorMin = Vector2.zero;
        panel.anchorMax = Vector2.up;
        panel.offsetMin = new Vector2(x, bottom);
        panel.offsetMax = new Vector2(x + w, -top);
    }

    void SetOverview(bool open) { overviewOpen = open; ShowPanels(); }

    void SelectDevice(Device device)
    {
        selected = device;
        detailOpen = true;
        bool isRobot = device == Device.Robot;
        robotPage.gameObject.SetActive(isRobot);
        visionPage.gameObject.SetActive(!isRobot);
        detailTitle.text = isRobot ? "SO-101 로봇팔" : "비전 검사기";
        detailCaption.text = isRobot ? "Unity ↔ 실물 로봇" : "색상 및 숫자 판정";
        robotCard.color = isRobot ? ActiveFill : CardFill;
        visionCard.color = isRobot ? CardFill : ActiveFill;
        robotEdge.color = isRobot ? Blue : Border;
        visionEdge.color = isRobot ? Border : Blue;
        ShowPanels();
    }

    void UpdateInspection()
    {
        int total = dash ? dash.TotalCount : 0;
        int good = dash ? dash.GoodCount : 0;
        int bad = dash ? dash.BadCount : 0;
        int judged = good + bad;
        string rate = judged == 0 ? "—" : $"{Mathf.RoundToInt(100f * good / judged)}%";
        bool received = dash && dash.LastReceiveTime >= 0f;
        totalText.text = $"{total}<size=17> 건</size>";
        goodText.text = good.ToString();
        badText.text = bad.ToString();
        yieldText.text = rate;
        visionCardState.text = received ? "수신됨" : "대기";
        visionDot.color = received ? Blue : Muted;
        pillSummary.text = $"누계 {total} · 불량 {bad} · {rate}";
        clockText.text = System.DateTime.Now.ToString("HH:mm:ss");
        visionCardMeta.text = $"검사 {total}건";
        currentRule.text = RuleLabel(dash ? dash.Criterion : null);
        sceneState.text = received ? "수신 기록 있음" : "수신 대기";
        lastReceiveText.text = received ? $"{Mathf.Max(0, Mathf.FloorToInt(Time.time - dash.LastReceiveTime))}초 전" : "—";
        visionTotal.text = $"{total}건";
        visionYield.text = rate;
        visionBad.text = $"{bad}건";
        for (int i = 0; i < 3; i++)
        {
            colorCount[i].text = dash ? dash.ColorCount(Colors[i]).ToString() : "0";
            digitCount[i].text = dash ? dash.DigitCount(Digits[i]).ToString() : "0";
        }
        if (selected == Device.Vision)
        {
            detailState.text = visionCardState.text;
            detailState.color = received ? BlueInk : Muted;
        }
    }

    static string RuleLabel(string rule)
    {
        switch (rule)
        {
            case "all": return "전체";
            case "white": return "화이트";
            case "blue": return "파랑";
            case "red": return "빨강";
            default: return string.IsNullOrWhiteSpace(rule) ? "기준 미설정" : rule;
        }
    }

    void UpdateRobot()
    {
        bool ready = robot && robot.Ready;
        bool live = ready && robot.Live;
        bool halted = robot && robot.EStopped;
        var mode = ready ? robot.CurrentMode : UnityToFollowerGUI.Mode.Leader;
        string modeLabel = !ready ? "신호 없음" : halted ? "비상 정지 중" : mode == UnityToFollowerGUI.Mode.UnityReal
            ? "실물 제어" : mode == UnityToFollowerGUI.Mode.UnityPractice ? (robot.Offline ? "로봇 없이 연습" : "연습 모드") : "리더 따라가기";
        robotCardState.text = !ready ? "미준비" : live ? "연결됨" : "미연결";
        robotDot.color = live ? Blue : ready ? Warn : Muted;
        SetLink(linkUnity, linkUnityDot, "UNITY " + (ready ? "준비" : "대기"), ready ? Blue : Muted);
        SetLink(linkFollower, linkFollowerDot, "FOLLOWER " + (live ? "연결" : "미연결"), live ? Blue : Warn);
        bool leading = ready && mode == UnityToFollowerGUI.Mode.Leader;
        SetLink(linkLeader, linkLeaderDot, "LEADER " + (leading ? "따라가는 중" : "대기"), leading && live ? Blue : Muted);
        robotCardMeta.text = modeLabel;
        if (selected == Device.Robot)
        {
            detailState.text = modeLabel;
            detailState.color = halted ? Alert : live ? BlueInk : Muted;
        }

        for (int i = 0; i < 6; i++)
        {
            float unity = ready ? robot.UnityDeg(i) : 0f;
            float follower = live ? robot.FollowerDeg(i) : 0f;
            jointUnity[i].text = ready ? $"{unity:F1}°" : "—";
            jointFollower[i].text = live ? $"{follower:F1}°" : "—";
            jointDifference[i].text = live ? $"{Mathf.Abs(unity - follower):F1}°" : "—";
            bool warn = live && !robot.JointOk(i) && mode != UnityToFollowerGUI.Mode.Leader;
            jointDifference[i].color = warn ? Alert : Muted;
            if (ready)   // 범위 안 위치 (0 = 하한, 1 = 상한)
            {
                float lo = robot.SliderMin(i), hi = robot.SliderMax(i);
                float t = hi > lo ? Mathf.Clamp01((unity - lo) / (hi - lo)) : .5f;
                jointMarker[i].anchorMin = jointMarker[i].anchorMax = new Vector2(t, .5f);
            }
        }

        bool canMove = ready && mode != UnityToFollowerGUI.Mode.Leader && !halted;
        jointSlider.interactable = canMove;
        foreach (var b in stepButtons) b.interactable = canMove;
        prevJoint.interactable = nextJoint.interactable = ready;
        int sel = robot ? robot.Selected : 0;
        for (int i = 0; i < 6; i++)   // 고른 관절 줄 강조
        {
            jointName[i].color = i == sel ? BlueInk : Ink;
            jointRowFill[i].color = i == sel ? ActiveFill : Color.clear;
        }
        manualJointText.text = $"{UnityToFollowerGUI.JointName(sel)} · 초당 {(robot ? robot.MaxSpeed : 10f):F0}°";
        if (ready)
        {
            jointSlider.minValue = robot.SliderMin(sel);
            jointSlider.maxValue = robot.SliderMax(sel);
            float v = canMove ? robot.Target(sel) : robot.UnityDeg(sel);
            jointValueText.text = $"{v:F1}°";
            if (!dragging) jointSlider.SetValueWithoutNotify(v);
        }
        else jointValueText.text = "—";

        // 리더 모드: 연결 없어도 눌림 (= 로봇 없이 연습). 연습 모드에서 흰 팔 쪽 버튼은 연결돼 있고 Offline이 아닐 때만
        bool offline = ready && robot.Offline;
        primary.interactable = ready && !halted && (mode == UnityToFollowerGUI.Mode.Leader
            || (mode == UnityToFollowerGUI.Mode.UnityPractice && live && !offline));
        secondary.interactable = ready && mode != UnityToFollowerGUI.Mode.Leader;
        primaryText.text = mode == UnityToFollowerGUI.Mode.Leader ? (live ? "유니티로 조종" : "로봇 없이 연습")
            : offline ? "유니티 팔만 움직임" : mode == UnityToFollowerGUI.Mode.UnityReal
            ? "흰 팔 움직이는 중" : !robot.AllOk ? "흰 팔에 맞추기" : robot.WaitingConfirm ? "다시 눌러 실행" : "흰 팔 움직이기";
        secondaryText.text = mode == UnityToFollowerGUI.Mode.UnityReal ? "동작 정지" : "리더로 돌아가기";
        primaryFill.color = ready && mode == UnityToFollowerGUI.Mode.UnityPractice && robot.AllOk && robot.WaitingConfirm
            ? Warn : Blue;   // 두 번 눌러야 하는 순간은 주황
        stop.interactable = robot && !halted;
        stopText.text = halted ? "비상 정지 중" : "■  비상 정지";
        modeHint.text = ModeHint(ready, live, halted, mode, offline);
        modeHint.color = ready && (halted || mode == UnityToFollowerGUI.Mode.UnityReal) ? Alert : Muted;
    }

    static void SetLink(Text t, Image dot, string value, Color c) { t.text = value; dot.color = c; }

    // 모드별 안내: 첫 줄 = 지금 상태, 둘째 줄 = 다음에 누를 것
    static string Two(string now, string next) => now + "\n" + next;

    string ModeHint(bool ready, bool live, bool halted, UnityToFollowerGUI.Mode mode, bool offline)
    {
        if (!ready) return "로봇 신호 없음";
        if (halted) return Two("비상 정지 중 — 흰 팔이 멈춰 있어요", "패널에서 해제해야 다시 움직일 수 있어요");
        switch (mode)
        {
            case UnityToFollowerGUI.Mode.Leader:
                return live ? Two("리더 팔을 따라 움직이는 중", "[유니티로 조종] → 연습 모드 (흰 팔은 아직 안 움직임)")
                            : Two("로봇 연결 없음 — 유니티 팔만 쓸 수 있어요", "[로봇 없이 연습] → 슬라이더·[자세]로 움직이기");
            case UnityToFollowerGUI.Mode.UnityPractice:
                if (offline) return Two("로봇 없이 연습 중 — 흰 팔은 안 움직여요", "흰 팔을 쓰려면 [리더로 돌아가기] → 패널 연결");
                return robot.AllOk ? Two("연습 모드 — 흰 팔과 자세가 같아요", "[흰 팔 움직이기]를 두 번 누르면 실물이 움직여요")
                                   : Two("연습 모드 — 흰 팔은 가만히 있어요", "[흰 팔에 맞추기] → 차이 2° 안이면 실물로 갈 수 있어요");
            default:
                return Two("⚠ 실물 모드 — 흰 팔이 유니티대로 움직여요", "멈추려면 [동작 정지] 또는 [비상 정지]");
        }
    }

    void LateUpdate()
    {

        bool panelVisible = canvas && canvas.gameObject.activeSelf;
        bool showPicking = panelVisible && detailOpen && selected == Device.Robot && pickingFrame && pickingPreviewCamera;
        if (pickingPreviewCamera) pickingPreviewCamera.enabled = showPicking;
        if (!panelVisible) return;
        ApplyGlass(detail);
        ApplyGlass(overview);
        ApplyGlass(statusBar);
        if (showPicking)
        {
            preview.enabled = true;
            preview.texture = pickingFrame;
            preview.uvRect = new Rect(0, 0, 1, 1);
            previewLabel.text = "집기 미리보기";
            return;
        }
        var frame = DashboardGlassBlur.SceneFrame;
        preview.enabled = frame;
        previewLabel.text = frame ? "씬 미리보기" : "씬 미리보기 대기";
        if (!frame || !sceneCamera || !detailOpen) return;
        preview.texture = frame;

        Vector2 center = selected == Device.Robot ? new Vector2(.57f, .43f) : new Vector2(.27f, .68f);
        Transform subject = selected == Device.Robot && robot ? robot.transform : inspectionAnchor;
        if (subject)
        {
            float offset = selected == Device.Robot ? .14f : .09f;
            Vector3 p = sceneCamera.WorldToViewportPoint(subject.position + Vector3.up * offset);
            if (p.z > 0) center = new Vector2(p.x, p.y);
        }
        float h = selected == Device.Robot ? .47f : .43f;
        float w = Mathf.Min(.88f, h * (detailContent / 161f) / Mathf.Max(.1f, sceneCamera.aspect));
        float y = Mathf.Clamp(center.y - h / 2, 0, 1 - h);
        bool flip = SystemInfo.graphicsUVStartsAtTop;
        preview.uvRect = new Rect(Mathf.Clamp(center.x - w / 2, 0, 1 - w), flip ? 1 - y : y, w, flip ? -h : h);
    }

    void ApplyGlass(Glass glass)
    {
        if (!glass.Root.gameObject.activeSelf) return;
        var blurred = DashboardGlassBlur.Blurred;
        bool useBlur = realBlur && blurred;
        glass.Backdrop.enabled = useBlur;
        glass.Tint.color = useBlur ? Tint : TintNoBlur;
        if (!useBlur) return;
        glass.Backdrop.texture = blurred;
        glass.Mask.GetWorldCorners(corners);
        float w = Mathf.Max(1, Screen.width), h = Mathf.Max(1, Screen.height);
        glass.Backdrop.uvRect = new Rect(corners[0].x / w, corners[0].y / h,
            (corners[2].x - corners[0].x) / w, (corners[2].y - corners[0].y) / h);
    }

    void OnPrimary()
    {
        if (!robot || !robot.Ready || robot.EStopped) return;
        if (robot.CurrentMode == UnityToFollowerGUI.Mode.Leader) robot.StartUnityMode();   // 연결 없으면 로봇 없이 연습
        else if (robot.CurrentMode == UnityToFollowerGUI.Mode.UnityPractice && robot.Live && !robot.Offline)
        {
            if (!robot.AllOk) robot.MatchFollower();
            else robot.PressGoReal();
        }
    }

    void OnSecondary()
    {
        if (!robot || !robot.Ready) return;
        if (robot.CurrentMode == UnityToFollowerGUI.Mode.UnityReal) robot.StopArm();
        else if (robot.CurrentMode == UnityToFollowerGUI.Mode.UnityPractice) robot.BackToLeader();
    }

    bool ManualControlAvailable() => robot && robot.Ready && !robot.EStopped &&
        robot.CurrentMode != UnityToFollowerGUI.Mode.Leader;

    void Step(float delta)
    {
        if (ManualControlAvailable()) robot.SetTarget(robot.Selected, robot.StartDeg(robot.Selected) + delta);
    }

    void Build()
    {
        canvas = new GameObject("TwinSidePanelCanvas", typeof(RectTransform), typeof(Canvas),
            typeof(CanvasScaler), typeof(GraphicRaycaster)).GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 20;
        canvas.pixelPerfect = true;
        var scaler = canvas.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;

        detailWidth = Mathf.Clamp(width * .745f, 286, 318);
        overviewWidth = Mathf.Clamp(width * .665f, 255, 286);
        detailContent = detailWidth - 2 * Inset;
        overviewContent = overviewWidth - 2 * Inset;
        overview = MakePanel("EquipmentOverview", false);
        detail = MakePanel("EquipmentDetail", true);
        statusBar = MakePanel("StatusBar", false, false);
        BuildOverview();
        BuildDetail();
        BuildTabs();
        BuildStatusBar();
        BuildOverviewPill();

        // 비상 정지: 장비 현황을 접어도 늘 보이게 따로. 산업 장비처럼 빨간 사선 줄무늬 + 단축키 표시
        footer = NewRect("SafetyControls", canvas.transform);
        stop = MakeButton(footer, "EmergencyStop", "■  비상 정지", Color.white, Hex(0xFF6B70),
            () => { if (robot) robot.EStop(); }, out stopText, out var stopFill, 19);
        stopFill.sprite = stripeSprite; stopFill.type = Image.Type.Tiled;
        Stretch(stop.GetComponent<RectTransform>(), 0);
        stopText.font = bold; stopText.color = Color.white; stopText.alignment = TextAnchor.MiddleLeft;
        stopText.rectTransform.offsetMin = new Vector2(16, 0);
        var key = Img("Key", stop.transform, null, Color.clear);
        PlaceRight(key.rectTransform, 14, 15, 58, 22);
        Stretch(Img("KeyEdge", key.transform, cardEdge, new Color(1, 1, 1, .65f)).rectTransform, 0);
        Stretch(Label(key.rectTransform, "SPACE", 0, 0, 58, 22, 11, bold, Color.white, TextAnchor.MiddleCenter).rectTransform, 0);
        stop.colors = ButtonFeedback(.80f);
        SelectDevice(Device.Robot);
    }

    Glass MakePanel(string name, bool selectedPanel, bool ticks = true)
    {
        var root = NewRect(name, canvas.transform);
        var window = Img("Window", root, panelSprite, Color.white);
        Stretch(window.rectTransform, 0);
        window.gameObject.AddComponent<Mask>().showMaskGraphic = false;
        var bg = NewRect("Backdrop", window.transform).gameObject.AddComponent<RawImage>();
        bg.raycastTarget = false;
        Stretch(bg.rectTransform, 0);
        var tint = Img("Tint", window.transform, null, TintNoBlur);
        // 패널 위 클릭이 뒤의 씬(3D)으로 새지 않게 막음
        tint.raycastTarget = true;
        Stretch(tint.rectTransform, 0);
        Stretch(Img("Outline", root, panelEdge, Border).rectTransform, 0);
        if (ticks) CornerTicks(root);
        return new Glass { Root = root, Mask = window.rectTransform, Backdrop = bg, Tint = tint };
    }

    // 왼쪽 위·오른쪽 아래 L자 눈금 (청록) — 계측기 느낌
    void CornerTicks(RectTransform root)
    {
        const float len = 11f, th = 2f;
        Place(Img("TickTL_H", root, null, Blue).rectTransform, -1, -1, len, th);
        Place(Img("TickTL_V", root, null, Blue).rectTransform, -1, -1, th, len);
        var a = Img("TickBR_H", root, null, Blue).rectTransform;
        a.anchorMin = a.anchorMax = a.pivot = Vector2.right; a.sizeDelta = new Vector2(len, th); a.anchoredPosition = new Vector2(1, -1);
        var b = Img("TickBR_V", root, null, Blue).rectTransform;
        b.anchorMin = b.anchorMax = b.pivot = Vector2.right; b.sizeDelta = new Vector2(th, len); b.anchoredPosition = new Vector2(1, -1);
    }

    // 구획 제목: "01  JOINT STATUS · 관절 상태" (번호는 청록, 제목은 흐리게, 오른쪽에 단위)
    Text Section(RectTransform root, string no, string title, float x, float y, float w, string right = null)
    {
        Label(root, no, x, y, 22, 22, 12, bold, Blue);
        var t = Label(root, title, x + 24, y, w - 24, 22, 12, semi, Muted);
        if (right != null) Label(root, right, x, y, w, 22, 12, medium, Muted, TextAnchor.MiddleRight);
        return t;
    }

    // 위 상태 바: 어디(라인·셀) + 연결 상태 3개 + 시각
    void BuildStatusBar()
    {
        var bar = statusBar.Root;
        Label(bar, "SO-101 DIGITAL TWIN", 14, 0, 220, StatusH, 13, bold, Ink);
        Label(bar, "/   LINE 09   /   CELL 01 · 컨베이어 집기", 178, 0, 320, StatusH, 13, medium, Muted);
        clockText = Label(bar, "", 0, 0, 1, StatusH, 12, medium, Muted, TextAnchor.MiddleRight);
        PlaceRight(clockText.rectTransform, 14, 0, 70, StatusH);
        linkLeader = LinkItem(bar, 100, "LEADER", out linkLeaderDot);
        linkFollower = LinkItem(bar, 260, "FOLLOWER", out linkFollowerDot);
        linkUnity = LinkItem(bar, 440, "UNITY", out linkUnityDot);
    }

    Text LinkItem(RectTransform bar, float right, string name, out Image dot)
    {
        var t = Label(bar, name, 0, 0, 1, StatusH, 12, semi, Secondary);
        PlaceRight(t.rectTransform, right, 0, 140, StatusH);
        dot = Img("Link_" + name, bar, null, Muted);
        PlaceRight(dot.rectTransform, right + 146, 13, 8, 8);
        return t;
    }

    // 장비 현황을 접었을 때 오른쪽 위에 남는 작은 막대 (누르면 다시 펼침)
    void BuildOverviewPill()
    {
        overviewPill = NewRect("OverviewCollapsed", canvas.transform);
        var g = MakeButton(overviewPill, "Open", "", TintNoBlur, Border, () => SetOverview(true), out _, out _);
        Stretch(g.GetComponent<RectTransform>(), 0);
        CornerTicks(overviewPill);
        Label(overviewPill, "장비 현황", 14, 0, 100, CollapsedH, 15, semi, Ink);
        pillSummary = Label(overviewPill, "", 0, 0, 1, CollapsedH, 13, medium, Muted, TextAnchor.MiddleRight);
        PlaceRight(pillSummary.rectTransform, 62, 0, 160, CollapsedH);
        var arrow = Label(overviewPill, "펼치기", 0, 0, 1, CollapsedH, 12, semi, Blue, TextAnchor.MiddleCenter);
        PlaceRight(arrow.rectTransform, 8, 0, 50, CollapsedH);
    }

    // 비상 정지 버튼 무늬: 빨간 사선 줄무늬 (반복 타일)
    Sprite MakeStripes()
    {
        const int n = 20;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
        Color a = Hex(0xC3272D), b = Hex(0xAD2127);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                tex.SetPixel(x, y, ((x + y) % n) < n / 2 ? a : b);
        tex.Apply();
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(.5f, .5f), 100f, 0, SpriteMeshType.FullRect);
        generated.Add(sp);
        return sp;
    }

    void BuildOverview()
    {
        var pane = overview.Root;
        Label(pane, "장비 현황", Inset, 12, overviewContent, 30, 18, semi, Ink);
        Label(pane, "OVERVIEW", Inset, 12, overviewContent - 48, 30, 11, semi, Muted, TextAnchor.MiddleRight);
        var fold = MakeButton(pane, "Collapse", "접기", Color.clear, Border, () => SetOverview(false), out var foldText, out _, 12);
        foldText.color = Muted;
        PlaceRight(fold.GetComponent<RectTransform>(), 10, 16, 40, 22);
        Rule(pane, 0, 50, overviewWidth);

        // 01 검사: 숫자 3칸 (불량만 빨강), 오른쪽 위 합격률
        Section(pane, "01", "INSPECTION · 검사", Inset, 62, overviewContent);
        yieldText = Label(pane, "—", Inset, 62, overviewContent, 22, 13, semi, Ink, TextAnchor.MiddleRight);
        float third = overviewContent / 3f;
        string[] kpi = { "누계", "양품", "불량" };
        var kpiValue = new Text[3];
        for (int i = 0; i < 3; i++)
        {
            float x = Inset + i * third;
            if (i > 0) Place(Img("KpiDivider", pane, null, LineColor).rectTransform, x - 1, 92, 1, 54);
            float pad = i > 0 ? 10 : 0;
            Label(pane, kpi[i], x + pad, 90, third - pad, 18, 12, semi, Muted);
            kpiValue[i] = Label(pane, "0", x + pad, 108, third - pad, 40, 30, bold, i == 2 ? Alert : Ink);
        }
        totalText = kpiValue[0]; goodText = kpiValue[1]; badText = kpiValue[2];
        totalText.supportRichText = true;
        Rule(pane, 0, 160, overviewWidth);

        // 02 장비: 한 줄씩
        Section(pane, "02", "DEVICES · 장비", Inset, 172, overviewContent, "2");
        MakeDeviceCard(pane, Device.Robot, 202, "SO-101", "로봇팔", out robotCard, out robotEdge, out robotDot, out robotCardState, out robotCardMeta);
        MakeDeviceCard(pane, Device.Vision, 264, "비전 검사기", "색상 · 숫자", out visionCard, out visionEdge, out visionDot, out visionCardState, out visionCardMeta);
        Rule(pane, 0, 334, overviewWidth);

        // 03 분류: 두 열, 가는 선으로 구분
        Section(pane, "03", "CLASSIFY · 분류", Inset, 346, overviewContent, "건");
        float half = overviewContent / 2f;
        string[] names = { "화이트", "파랑", "빨강" };
        for (int i = 0; i < names.Length; i++)
        {
            float y = 376 + i * 32;
            Rule(pane, Inset, y, half - 9);
            Rule(pane, Inset + half + 9, y, half - 9);
            colorCount[i] = Row(pane, names[i], Inset, y + 2, half - 9, 28);
            digitCount[i] = Row(pane, $"숫자 {i + 1}", Inset + half + 9, y + 2, half - 9, 28);
        }
    }

    // 장비 한 줄: 왼쪽 청록 막대(고른 장비) · ARM/CAM 표시 · 이름 · 네모 상태 점
    void MakeDeviceCard(RectTransform pane, Device device, float y, string title, string kind,
        out Image fill, out Image edge, out Image stateDot, out Text state, out Text meta)
    {
        fill = Img(title, pane, null, CardFill);
        Place(fill.rectTransform, Inset, y, overviewContent, 56);
        fill.raycastTarget = true;
        var b = fill.gameObject.AddComponent<Button>();
        b.targetGraphic = fill;
        b.colors = ButtonFeedback(.50f);
        b.onClick.AddListener(() => SelectDevice(device));
        var rect = fill.rectTransform;
        edge = Img("Selection", rect, null, Blue);
        Place(edge.rectTransform, 0, 0, 2, 56);
        Label(rect, device == Device.Robot ? "ARM" : "CAM", 12, 0, 40, 56, 11, bold, Muted);
        Label(rect, title, 52, 7, overviewContent - 140, 24, 16, semi, Ink);
        meta = Label(rect, kind, 52, 30, overviewContent - 64, 20, 13, medium, Muted);
        stateDot = Img("StatusDot", rect, null, Blue);
        PlaceRight(stateDot.rectTransform, 74, 15, 7, 7);
        state = Label(rect, "", 0, 0, 1, 1, 12, bold, Secondary, TextAnchor.MiddleRight);
        PlaceRight(state.rectTransform, 10, 7, 60, 24);
    }

    void DeviceIcon(RectTransform tile, Device device)
    {
        var color = device == Device.Robot ? Hex(0xE0B55F) : BlueInk;
        if (device == Device.Robot)
        {
            IconBar(tile, color, 11, 41, 29, 3, 0);
            IconBar(tile, color, 17, 39, 17, 4, 55);
            IconBar(tile, color, 26, 24, 15, 4, -26);
            IconBar(tile, color, 38, 26, 8, 3, -74);
            Place(Img("Pivot", tile, dotSprite, color).rectTransform, 23, 19, 8, 8);
        }
        else
        {
            Place(Img("Housing", tile, cardSprite, color).rectTransform, 11, 14, 28, 20);
            Place(Img("Lens", tile, dotSprite, Hex(0x25364E)).rectTransform, 21, 18, 11, 11);
            IconBar(tile, color, 17, 39, 17, 2, 0);
            IconBar(tile, color, 25, 33, 8, 2, -90);
        }
    }

    void IconBar(RectTransform root, Color color, float x, float y, float w, float h, float angle)
    {
        var bar = Img("IconStroke", root, barSprite, color).rectTransform;
        Place(bar, x, y, w, h);
        bar.localEulerAngles = new Vector3(0, 0, angle);
    }

    void BuildDetail()
    {
        var pane = detail.Root;
        Label(pane, "장비 정보", Inset, 12, detailContent - 34, 30, 18, semi, Ink);
        Label(pane, "EQUIPMENT", Inset, 12, detailContent - 34, 30, 11, semi, Muted, TextAnchor.MiddleRight);
        var close = MakeButton(pane, "CloseDetail", "×", Color.clear, Color.clear,
            () => { detailOpen = false; dragging = false; ShowPanels(); }, out _, out _, 20);
        PlaceRight(close.GetComponent<RectTransform>(), 8, 12, 28, 30);
        Rule(pane, 0, 50, detailWidth);
        var previewRect = Img("ScenePreview", pane, cardSprite, Color.white);
        Place(previewRect.rectTransform, Inset, 57, detailContent, 161);
        previewRect.gameObject.AddComponent<Mask>().showMaskGraphic = false;
        Stretch(Img("PreviewBackground", previewRect.transform, null, Hex(0x1A1F27)).rectTransform, 0);
        preview = NewRect("SceneFrame", previewRect.transform).gameObject.AddComponent<RawImage>();
        preview.raycastTarget = false;
        Stretch(preview.rectTransform, 0);
        var previewTag = Img("PreviewTag", previewRect.transform, null, new Color(0, 0, 0, .58f));
        Place(previewTag.rectTransform, 0, 137, 130, 24);
        previewLabel = Label(previewTag.rectTransform, "씬 미리보기", 0, 0, 130, 24, 13, semi, Ink, TextAnchor.MiddleCenter);

        detailTitle = Label(pane, "", Inset, 226, detailContent, 38, 25, bold, Ink);
        detailCaption = Label(pane, "", Inset, 266, detailContent - 112, 24, 14, medium, Muted);
        var tag = Img("Mode", pane, null, Color.clear);   // 모드 = 청록 테두리 글자 (상자 대신)
        PlaceRight(tag.rectTransform, Inset, 266, 112, 24);
        Stretch(Img("ModeEdge", tag.transform, cardEdge, AccentEdge).rectTransform, 0);
        detailState = Label(tag.rectTransform, "", 0, 0, 112, 24, 13, bold, BlueInk, TextAnchor.MiddleCenter);
        Rule(pane, 0, 304, detailWidth);

        robotPage = NewRect("RobotDetails", pane);
        visionPage = NewRect("VisionDetails", pane);
        Stretch(robotPage, 0);
        Stretch(visionPage, 0);
        BuildRobotDetail();
        BuildVisionDetail();
    }
    void BuildRobotDetail()
    {
        // [관절 제어] [자세] 탭 — 탭 내용은 탭 높이만큼 아래로, 조종 버튼(아래 두 개)은 두 탭 공통
        BuildRobotTabs(robotPage);
        var pane = jointPage;
        Section(pane, "01", "JOINT STATUS · 관절 상태", Inset, 328, detailContent, "°");
        const float rowY = 386f, rowHeight = 33f;
        const float codeW = 24f, nameW = 64f, barW = 60f;
        float col = (detailContent - codeW - nameW - barW - 8) / 3f;
        float xName = Inset + codeW, xBar = xName + nameW, xVal = xBar + barW + 8;
        Label(pane, "관절", xName, 358, nameW, 22, 11, semi, Muted);
        Label(pane, "범위 안 위치", xBar, 358, barW + 8, 22, 11, semi, Muted);
        Label(pane, "UNITY", xVal, 358, col, 22, 11, semi, Muted, TextAnchor.MiddleRight);
        Label(pane, "실물", xVal + col, 358, col, 22, 11, semi, Muted, TextAnchor.MiddleRight);
        Label(pane, "차이", xVal + 2 * col, 358, col, 22, 11, semi, Muted, TextAnchor.MiddleRight);
        for (int i = 0; i < 6; i++)
        {
            float y = rowY + i * rowHeight;
            jointRowFill[i] = Img("RowFill", pane, null, Color.clear);   // 고른 관절 줄 = 옅은 청록
            Place(jointRowFill[i].rectTransform, Inset - 6, y - 1, detailContent + 12, rowHeight - 1);
            Rule(pane, Inset, y - 2, detailContent);
            Label(pane, "J" + (i + 1), Inset, y, codeW, 29, 12, semi, Muted);
            jointName[i] = Label(pane, UnityToFollowerGUI.JointName(i), xName, y, nameW, 29, 14, medium, Ink);
            // 위치 막대: 가는 선 위에 지금 각도 표시 (세로 흰 줄), 0°에 옅은 눈금
            var track = Img("PosTrack", pane, null, new Color(1, 1, 1, .12f));
            Place(track.rectTransform, xBar, y + 14, barW, 2);
            var marker = Img("PosMarker", track.transform, null, Ink).rectTransform;
            marker.anchorMin = marker.anchorMax = new Vector2(.5f, .5f); marker.pivot = new Vector2(.5f, .5f);
            marker.sizeDelta = new Vector2(2, 11);
            jointMarker[i] = marker;
            jointUnity[i] = Label(pane, "—", xVal, y, col, 29, 14, semi, Ink, TextAnchor.MiddleRight);
            jointFollower[i] = Label(pane, "—", xVal + col, y, col, 29, 14, medium, Secondary, TextAnchor.MiddleRight);
            jointDifference[i] = Label(pane, "—", xVal + 2 * col, y, col, 29, 13, medium, Muted, TextAnchor.MiddleRight);
        }

        Rule(pane, 0, 604, detailWidth);
        Section(pane, "02", "MANUAL · 수동 제어", Inset, 614, detailContent - 92);
        manualJointText = Label(pane, "어깨 회전 · 초당 10°", Inset, 644, 182, 25, 15, semi, Ink);
        // 관절 고르기 ◀ ▶ (6관절 중 하나를 슬라이더로)
        prevJoint = MakeButton(pane, "PrevJoint", "◀", CardFill, Border, () => { if (robot) robot.SelectNext(-1); }, out _, out _);
        Place(prevJoint.GetComponent<RectTransform>(), Inset + detailContent - 86, 614, 40, 30);
        nextJoint = MakeButton(pane, "NextJoint", "▶", CardFill, Border, () => { if (robot) robot.SelectNext(1); }, out _, out _);
        Place(nextJoint.GetComponent<RectTransform>(), Inset + detailContent - 40, 614, 40, 30);
        jointValueText = Label(pane, "—", Inset + detailContent - 110, 640, 110, 32, 24, bold, Ink, TextAnchor.MiddleRight);
        jointSlider = MakeSlider(pane, Inset, 672, detailContent);
        float stepWidth = (detailContent - 14) / 3f;
        float[] delta = { -15f, 0, 15f };
        string[] plus = { "-15°", "처음", "+15°" };
        for (int i = 0; i < plus.Length; i++)
        {
            float step = delta[i];
            var button = MakeButton(pane, "Step_" + i, plus[i], CardFill, Border, () => Step(step), out _, out _);
            Place(button.GetComponent<RectTransform>(), Inset + i * (stepWidth + 7), 719, stepWidth, 38);
            stepButtons[i] = button;
        }
        float half = (detailContent - 8) / 2;
        primary = MakeButton(robotPage, "ControlMode", "유니티로 조종", Blue, Blue, OnPrimary, out primaryText, out primaryFill);
        primaryText.color = AccentInk;   // 청록 버튼 위는 어두운 글자
        Place(primary.GetComponent<RectTransform>(), Inset, 780 + RobotTabH, half, 46);
        secondary = MakeButton(robotPage, "StopOrFollow", "리더로 돌아가기", CardFill, Border, OnSecondary, out secondaryText, out _);
        Place(secondary.GetComponent<RectTransform>(), Inset + half + 8, 780 + RobotTabH, half, 46);
        // 조종 버튼 아래 안내 두 줄: 지금 어떤 모드인지 + 다음에 무엇을 누르면 되는지 (배우는 사람용)
        modeHint = Label(robotPage, "", Inset, 780 + RobotTabH + 52, detailContent, 36, 13, medium, Muted, TextAnchor.UpperLeft);
        BuildPosePage(posePage);
    }

    void BuildVisionDetail()
    {
        var pane = visionPage;
        Section(pane, "01", "STATUS · 검사 상태", Inset, 328, detailContent);
        sceneState = Row(pane, "데이터 수신", Inset, 371, detailContent, 35);
        lastReceiveText = Row(pane, "마지막 수신", Inset, 410, detailContent, 35);
        currentRule = Row(pane, "판정 기준", Inset, 449, detailContent, 35);
        Rule(pane, Inset, 500, detailContent);
        Section(pane, "02", "RESULT · 판정 결과", Inset, 511, detailContent);
        visionTotal = Row(pane, "총 검사", Inset, 552, detailContent, 35);
        visionYield = Row(pane, "합격률", Inset, 591, detailContent, 35);
        visionBad = Row(pane, "불량", Inset, 630, detailContent, 35);
    }

    void BuildTabs()
    {
        tabs = NewRect("CompactTabs", canvas.transform);
        float half = (detailWidth - 7) / 2;
        var a = MakeButton(tabs, "OverviewTab", "장비 현황", CardFill, Border,
            () => { detailOpen = false; dragging = false; ShowPanels(); }, out _, out tabOverview);
        Place(a.GetComponent<RectTransform>(), 0, 0, half, 36);
        var b = MakeButton(tabs, "DetailTab", "장비 정보", ActiveFill, Border,
            () => SelectDevice(selected), out _, out tabDetail);
        Place(b.GetComponent<RectTransform>(), half + 7, 0, half, 36);
    }

    Text Row(RectTransform root, string key, float x, float y, float w, float h)
    {
        Label(root, key, x, y, w * .51f, h, 16, medium, Muted);
        return Label(root, "", x + w * .46f, y, w * .54f, h, 16, semi, Ink, TextAnchor.MiddleRight);
    }

    void Rule(RectTransform root, float x, float y, float w)
    {
        Place(Img("Divider", root, null, LineColor).rectTransform, x, y, w, 1.5f);
    }

    Slider MakeSlider(RectTransform parent, float x, float y, float w)
    {
        var root = NewRect("JointSlider", parent);
        Place(root, x, y, w, 38);
        var track = Img("Track", root, null, new Color(1, 1, 1, .16f));
        track.raycastTarget = true;
        track.rectTransform.anchorMin = new Vector2(0, .5f);
        track.rectTransform.anchorMax = new Vector2(1, .5f);
        track.rectTransform.sizeDelta = new Vector2(0, 2);
        var fill = Img("Fill", track.transform, null, Blue);
        Stretch(fill.rectTransform, 0);
        var slot = NewRect("HandleArea", root);
        Stretch(slot, 0);
        slot.offsetMin = new Vector2(7, 0);
        slot.offsetMax = new Vector2(-7, 0);
        var handle = Img("Handle", slot, null, Ink);
        handle.rectTransform.sizeDelta = new Vector2(5, -18);   // 손잡이는 위아래로 늘어나서 음수 = 그만큼 짧게
        handle.raycastTarget = true;

        var slider = root.gameObject.AddComponent<Slider>();
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = -30;
        slider.maxValue = 30;
        slider.targetGraphic = handle;
        slider.fillRect = fill.rectTransform;
        slider.handleRect = handle.rectTransform;
        slider.onValueChanged.AddListener(value => { if (ManualControlAvailable()) robot.SetTarget(robot.Selected, value); });

        var triggers = root.gameObject.AddComponent<EventTrigger>();
        AddTrigger(triggers, EventTriggerType.PointerDown, () => dragging = true);
        AddTrigger(triggers, EventTriggerType.PointerUp, () => dragging = false);
        return slider;
    }

    static void AddTrigger(EventTrigger trigger, EventTriggerType kind, System.Action callback)
    {
        var entry = new EventTrigger.Entry { eventID = kind };
        entry.callback.AddListener(_ => callback());
        trigger.triggers.Add(entry);
    }

    Button MakeButton(RectTransform parent, string name, string value, Color fillColor, Color edgeColor,
        UnityEngine.Events.UnityAction callback, out Text label, out Image fill, int size = 16)
    {
        fill = Img(name, parent, cardSprite, fillColor);
        fill.raycastTarget = true;
        Stretch(Img("Outline", fill.transform, cardEdge, edgeColor).rectTransform, 0);
        label = Label(fill.rectTransform, value, 0, 0, 1, 1, size, semi, Ink, TextAnchor.MiddleCenter);
        Stretch(label.rectTransform, 0);
        var button = fill.gameObject.AddComponent<Button>();
        button.targetGraphic = fill;
        button.colors = ButtonFeedback(.53f);
        button.onClick.AddListener(callback);
        return button;
    }

    static ColorBlock ButtonFeedback(float disabledOpacity)
    {
        var colors = ColorBlock.defaultColorBlock;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f);
        colors.selectedColor = colors.highlightedColor;
        colors.pressedColor = new Color(.78f, .82f, .9f);
        colors.disabledColor = new Color(.75f, .75f, .75f, disabledOpacity);
        return colors;
    }

    static Text Label(RectTransform root, string value, float x, float y, float w, float h, int size, Font font, Color color,
        TextAnchor align = TextAnchor.MiddleLeft)
    {
        var text = NewRect("Text", root).gameObject.AddComponent<Text>();
        text.font = font;
        text.text = value;
        text.fontSize = size;
        text.color = color;
        text.alignment = align;
        text.supportRichText = false;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        Place(text.rectTransform, x, y, w, h);
        return text;
    }

    void LoadFonts()
    {
        regular = Resources.Load<Font>("Fonts/SUIT-Regular");
        medium = Resources.Load<Font>("Fonts/SUIT-Medium");
        semi = Resources.Load<Font>("Fonts/SUIT-SemiBold");
        bold = Resources.Load<Font>("Fonts/SUIT-Bold");
        if (regular && medium && semi && bold) return;
        var fallback = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        regular = medium = semi = bold = fallback;
    }

    Sprite MakeRounded(int size, int radius, float edge = 0)
    {
        var sprite = Rounded(size, radius, edge, 0);
        generated.Add(sprite);
        return sprite;
    }



    void PreparePickingPreview()
    {
        if (!pickingPreviewCamera) return;
        pickingPreviewCamera.enabled = false;
        pickingFrame = new RenderTexture(520, 320, 24, RenderTextureFormat.ARGB32)
        {
            name = "Picking preview",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave
        };
        pickingPreviewCamera.targetTexture = pickingFrame;
    }

    void OnDisable()
    {
        if (pickingPreviewCamera) pickingPreviewCamera.enabled = false;
    }

    void OnDestroy()
    {
        if (pickingPreviewCamera && pickingPreviewCamera.targetTexture == pickingFrame) pickingPreviewCamera.targetTexture = null;
        if (pickingFrame) { pickingFrame.Release(); Destroy(pickingFrame); }
        if (canvas) Destroy(canvas.gameObject);
        foreach (var sprite in generated)
        {
            if (!sprite) continue;
            Destroy(sprite.texture);
            Destroy(sprite);
        }
    }
    static RectTransform NewRect(string name, Transform parent)
    { var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); rt.SetParent(parent, false); return rt; }
    static Image Img(string name, Transform parent, Sprite sp, Color c)
    {
        var img = NewRect(name, parent).gameObject.AddComponent<Image>();
        img.sprite = sp; img.type = (sp != null && sp.border != Vector4.zero) ? Image.Type.Sliced : Image.Type.Simple; img.color = c; img.raycastTarget = false;
        return img;
    }
    static void Place(RectTransform rt, float x, float y, float w, float h)   // 부모 왼쪽 위 기준, y는 아래로
    { rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1); rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(x, -y); }
    static void PlaceRight(RectTransform rt, float right, float y, float w, float h)   // 부모 오른쪽 위 기준
    { rt.anchorMin = rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(1, 1); rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(-right, -y); }
    static void Stretch(RectTransform rt, float inset)   // inset > 0 = 안쪽으로, < 0 = 바깥으로
    { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(inset, inset); rt.offsetMax = new Vector2(-inset, -inset); }
    static Color Hex(int rgb, float a = 1f) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);
    static void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        var go = new GameObject("EventSystem", typeof(EventSystem));
        go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();   // 이 프로젝트는 새 Input System만 씀
    }

    static Sprite Rounded(int size, int radius, float ring, float soft)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        float half = size / 2f, r = radius;
        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                float qx = Mathf.Abs(px + 0.5f - half) - (half - soft - r), qy = Mathf.Abs(py + 0.5f - half) - (half - soft - r);
                float dist = new Vector2(Mathf.Max(qx, 0), Mathf.Max(qy, 0)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0) - r;
                float a;
                if (soft > 0) { float t = Mathf.Clamp01(0.5f - dist / (soft * 2f)); a = t * t * (3f - 2f * t); }
                else { a = Mathf.Clamp01(0.5f - dist); if (ring > 0) a = Mathf.Clamp01(a - Mathf.Clamp01(0.5f - (dist + ring))); }
                tex.SetPixel(px, py, new Color(1, 1, 1, a));
            }
        tex.Apply();
        float b = Mathf.Min(r + soft + 1, size / 2 - 1);
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
    }
}
