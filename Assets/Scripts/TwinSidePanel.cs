using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 오른쪽 청록 유리 패널: 장비(로봇·카메라) 카드와 검사 현황을 실시간으로 보여줌
public class TwinSidePanel : MonoBehaviour
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

    static readonly Color Ink = Hex(0xF5F4F3), Secondary = Hex(0xD8D8DB), Muted = Hex(0xC2C2C8);
    static readonly Color Blue = Hex(0x78ADFC), BlueInk = Hex(0xC2DBFF), Alert = Hex(0xFFABA8);
    static readonly Color Border = new Color(1, 1, 1, .23f), LineColor = new Color(1, 1, 1, .18f);
    static readonly Color CardFill = new Color(.28f, .26f, .25f, .36f);
    static readonly Color ActiveFill = new Color(.19f, .25f, .31f, .55f);
    static readonly Color Tint = new Color(.23f, .212f, .198f, .72f);
    static readonly Color TintNoBlur = new Color(.13f, .12f, .118f, .91f);
    static readonly string[] Colors = { "white", "blue", "red" };
    static readonly string[] Digits = { "1", "2", "3" };
    const float Gap = 10f;
    const float Inset = 17f;

    DashboardUI dash;
    UnityToFollowerGUI robot;
    Camera sceneCamera;
    Transform inspectionAnchor;
    Font regular, medium, semi, bold;
    Sprite panelSprite, panelEdge, cardSprite, cardEdge, pillSprite, dotSprite, barSprite;
    readonly List<Sprite> generated = new List<Sprite>();
    readonly Vector3[] corners = new Vector3[4];
    Canvas canvas;
    Glass detail, overview;
    RectTransform robotPage, visionPage, tabs, footer;
    RawImage preview;
    Text previewLabel, detailTitle, detailState, detailCaption;
    Text totalText, goodText, badText, yieldText, robotCardState, visionCardState, robotCardMeta, visionCardMeta;
    Text currentRule, sceneState, lastReceiveText, visionTotal, visionYield, visionBad, panText;
    Text primaryText, secondaryText, stopText;
    Image robotCard, robotEdge, visionCard, visionEdge, robotDot, visionDot, primaryFill, tabOverview, tabDetail;
    readonly Text[] colorCount = new Text[3], digitCount = new Text[3];
    readonly Text[] jointUnity = new Text[6], jointFollower = new Text[6], jointDifference = new Text[6];
    readonly Button[] stepButtons = new Button[3];
    Button primary, secondary, stop;
    Slider panSlider;
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
        panelSprite = MakeRounded(96, 25);
        panelEdge = MakeRounded(96, 25, 1.5f);
        cardSprite = MakeRounded(64, 17);
        cardEdge = MakeRounded(64, 17, 1.4f);
        pillSprite = MakeRounded(40, 20);
        dotSprite = MakeRounded(32, 16);
        barSprite = MakeRounded(16, 4);
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
    }

    void SetViewport(int pixelWidth, int pixelHeight)
    {
        viewWidth = pixelWidth;
        viewHeight = pixelHeight;
        narrow = (float)pixelWidth / Mathf.Max(1, pixelHeight) < 1.47f;
        ShowPanels();
    }

    void ShowPanels()
    {
        overview.Root.gameObject.SetActive(!narrow || !detailOpen);
        detail.Root.gameObject.SetActive(detailOpen);
        tabs.gameObject.SetActive(narrow);
        tabDetail.color = detailOpen ? ActiveFill : CardFill;
        tabOverview.color = detailOpen ? CardFill : ActiveFill;
        Dock(overview.Root, overviewWidth, Gutter, narrow ? 43 : 0);
        Dock(detail.Root, detailWidth, Gutter + (narrow ? 0 : overviewWidth + Gap), narrow ? 43 : 0);
        PlaceRight(tabs, Gutter, Gutter, detailWidth, 36);
        footer.anchorMin = footer.anchorMax = Vector2.right;
        footer.pivot = new Vector2(1, 0);
        footer.anchoredPosition = new Vector2(-Gutter - Inset, Gutter + Inset);
        footer.sizeDelta = new Vector2(narrow && detailOpen ? detailContent : overviewContent, 49);
    }

    void Dock(RectTransform panel, float w, float right, float topOffset)
    {
        panel.anchorMin = Vector2.right;
        panel.anchorMax = Vector2.one;
        panel.offsetMin = new Vector2(-right - w, Gutter);
        panel.offsetMax = new Vector2(-right, -Gutter - topOffset);
    }

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
            ? "실물 제어" : mode == UnityToFollowerGUI.Mode.UnityPractice ? "연습 모드" : "리더 따라가기";
        robotCardState.text = !ready ? "미준비" : live ? "연결됨" : "미연결";
        robotDot.color = live ? Blue : Muted;
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
        }

        bool canMove = ready && mode != UnityToFollowerGUI.Mode.Leader && !halted;
        panSlider.interactable = canMove;
        foreach (var b in stepButtons) b.interactable = canMove;
        if (ready)
        {
            panSlider.minValue = robot.StartPan - 30f;
            panSlider.maxValue = robot.StartPan + 30f;
            float pan = canMove ? robot.PanTarget : robot.UnityDeg(0);
            panText.text = $"{pan:F1}°";
            if (!dragging) panSlider.SetValueWithoutNotify(pan);
        }
        else panText.text = "—";

        primary.interactable = live && !halted && mode != UnityToFollowerGUI.Mode.UnityReal;
        secondary.interactable = ready && mode != UnityToFollowerGUI.Mode.Leader;
        primaryText.text = mode == UnityToFollowerGUI.Mode.Leader ? "유니티로 조종" : mode == UnityToFollowerGUI.Mode.UnityReal
            ? "흰 팔 움직이는 중" : !robot.AllOk ? "흰 팔에 맞추기" : robot.WaitingConfirm ? "다시 눌러 실행" : "흰 팔 움직이기";
        secondaryText.text = mode == UnityToFollowerGUI.Mode.UnityReal ? "동작 정지" : "리더로 돌아가기";
        primaryFill.color = ready && mode == UnityToFollowerGUI.Mode.UnityPractice && robot.AllOk && robot.WaitingConfirm
            ? Hex(0x23528C, .95f) : ActiveFill;
        stop.interactable = robot && !halted;
        stopText.text = halted ? "비상 정지 중" : "■  비상 정지";
    }

    void LateUpdate()
    {

        bool panelVisible = canvas && canvas.gameObject.activeSelf;
        bool showPicking = panelVisible && detailOpen && selected == Device.Robot && pickingFrame && pickingPreviewCamera;
        if (pickingPreviewCamera) pickingPreviewCamera.enabled = showPicking;
        if (!panelVisible) return;
        ApplyGlass(detail);
        ApplyGlass(overview);
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
        if (!robot || !robot.Ready || !robot.Live || robot.EStopped) return;
        if (robot.CurrentMode == UnityToFollowerGUI.Mode.Leader) robot.StartUnityMode();
        else if (robot.CurrentMode == UnityToFollowerGUI.Mode.UnityPractice)
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
        if (ManualControlAvailable()) robot.PanTarget = robot.StartPan + delta;
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
        BuildOverview();
        BuildDetail();
        BuildTabs();

        footer = NewRect("SafetyControls", canvas.transform);
        stop = MakeButton(footer, "EmergencyStop", "■  비상 정지", Hex(0x702F36, .96f), Hex(0xD28E93, .88f),
            () => { if (robot) robot.EStop(); }, out stopText, out _, 20);
        Stretch(stop.GetComponent<RectTransform>(), 0);
        stop.colors = ButtonFeedback(.80f);
        SelectDevice(Device.Robot);
    }

    Glass MakePanel(string name, bool selectedPanel)
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
        Stretch(Img("Outline", root, panelEdge, selectedPanel ? Hex(0x6B9DF0, .88f) : Border).rectTransform, 0);
        return new Glass { Root = root, Mask = window.rectTransform, Backdrop = bg, Tint = tint };
    }

    void BuildOverview()
    {
        var pane = overview.Root;
        Label(pane, "장비 현황", Inset, 13, overviewContent, 32, 19, semi, Ink);
        Rule(pane, Inset, 51, overviewContent);
        Label(pane, "검사 누계", Inset, 64, 126, 24, 16, medium, Secondary);
        totalText = Label(pane, "0", Inset, 86, 152, 62, 47, bold, Ink);
        totalText.supportRichText = true;
        Label(pane, "합격률", Inset + overviewContent - 77, 72, 77, 23, 15, medium, Muted, TextAnchor.MiddleRight);
        yieldText = Label(pane, "—", Inset + overviewContent - 82, 98, 82, 39, 29, semi, Ink, TextAnchor.MiddleRight);
        float half = overviewContent / 2f;
        goodText = Row(pane, "양품", Inset, 151, half - 9, 31);
        badText = Row(pane, "불량", Inset + half + 9, 151, half - 9, 31);
        Rule(pane, Inset, 203, overviewContent);
        Label(pane, "장비 목록", Inset, 214, overviewContent, 27, 17, semi, Ink);
        MakeDeviceCard(pane, Device.Robot, 254, "SO-101", "로봇팔", out robotCard, out robotEdge, out robotDot, out robotCardState, out robotCardMeta);
        MakeDeviceCard(pane, Device.Vision, 381, "비전 검사기", "색상 · 숫자", out visionCard, out visionEdge, out visionDot, out visionCardState, out visionCardMeta);
        Rule(pane, Inset, 525, overviewContent);
        Label(pane, "검사 분류", Inset, 538, overviewContent, 29, 17, semi, Ink);
        string[] names = { "화이트", "파랑", "빨강" };
        for (int i = 0; i < names.Length; i++)
        {
            float y = 576 + i * 35;
            colorCount[i] = Row(pane, names[i], Inset, y, half - 9, 27);
            digitCount[i] = Row(pane, $"숫자 {i + 1}", Inset + half + 9, y, half - 9, 27);
        }
    }

    void MakeDeviceCard(RectTransform pane, Device device, float y, string title, string kind,
        out Image fill, out Image edge, out Image stateDot, out Text state, out Text meta)
    {
        fill = Img(title, pane, cardSprite, CardFill);
        Place(fill.rectTransform, Inset, y, overviewContent, 113);
        fill.raycastTarget = true;
        var b = fill.gameObject.AddComponent<Button>();
        b.targetGraphic = fill;
        b.colors = ButtonFeedback(.50f);
        b.onClick.AddListener(() => SelectDevice(device));
        var rect = fill.rectTransform;
        edge = Img("Selection", rect, cardEdge, Border);
        Stretch(edge.rectTransform, 0);
        var iconTile = Img("DeviceIcon", rect, cardSprite, new Color(.12f, .14f, .17f, .80f));
        Place(iconTile.rectTransform, 12, 30, 50, 53);
        DeviceIcon(iconTile.rectTransform, device);
        Label(rect, title, 72, 12, overviewContent - 84, 30, 20, semi, Ink);
        Label(rect, kind, 72, 43, 66, 23, 15, medium, Muted);
        stateDot = Img("StatusDot", rect, dotSprite, Blue);
        PlaceRight(stateDot.rectTransform, 20, 52, 7, 7);
        state = Label(rect, "", 72, 69, overviewContent - 88, 22, 15, medium, Secondary);
        // 장비 이름 옆엔 짧은 상태, 카드 아래엔 실시간 정보
        PlaceRight(state.rectTransform, 31, 43, 85, 24);
        state.alignment = TextAnchor.MiddleRight;
        meta = Label(rect, "", 72, 73, overviewContent - 82, 24, 15, medium, Secondary);
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
        Label(pane, "장비 정보", Inset, 13, detailContent - 34, 32, 19, semi, Ink);
        var close = MakeButton(pane, "CloseDetail", "×", Color.clear, Color.clear,
            () => { detailOpen = false; dragging = false; ShowPanels(); }, out _, out _, 23);
        PlaceRight(close.GetComponent<RectTransform>(), 11, 13, 32, 32);
        var previewRect = Img("ScenePreview", pane, cardSprite, Color.white);
        Place(previewRect.rectTransform, Inset, 57, detailContent, 161);
        previewRect.gameObject.AddComponent<Mask>().showMaskGraphic = false;
        Stretch(Img("PreviewBackground", previewRect.transform, null, Hex(0x26313C)).rectTransform, 0);
        preview = NewRect("SceneFrame", previewRect.transform).gameObject.AddComponent<RawImage>();
        preview.raycastTarget = false;
        Stretch(preview.rectTransform, 0);
        var previewTag = Img("PreviewTag", previewRect.transform, pillSprite, Hex(0x1C2027, .89f));
        Place(previewTag.rectTransform, 8, 130, 123, 24);
        previewLabel = Label(previewTag.rectTransform, "씬 미리보기", 0, 0, 123, 24, 14, medium, Ink, TextAnchor.MiddleCenter);

        detailTitle = Label(pane, "", Inset, 226, detailContent, 41, 27, bold, Ink);
        detailCaption = Label(pane, "", Inset, 274, detailContent - 105, 26, 15, medium, Muted);
        var tag = Img("Mode", pane, pillSprite, new Color(.12f, .17f, .23f, .66f));
        PlaceRight(tag.rectTransform, Inset, 275, 106, 25);
        detailState = Label(tag.rectTransform, "", 5, 0, 96, 25, 14, semi, BlueInk, TextAnchor.MiddleCenter);
        Rule(pane, Inset, 319, detailContent);

        robotPage = NewRect("RobotDetails", pane);
        visionPage = NewRect("VisionDetails", pane);
        Stretch(robotPage, 0);
        Stretch(visionPage, 0);
        BuildRobotDetail();
        BuildVisionDetail();
    }
    void BuildRobotDetail()
    {
        var pane = robotPage;
        Label(pane, "관절 상태", Inset, 330, detailContent, 30, 18, semi, Ink);
        const float rowY = 400f, rowHeight = 32f;
        float column = (detailContent - 97) / 3f;
        Label(pane, "관절", Inset, 369, 87, 24, 14, regular, Muted);
        Label(pane, "Unity", Inset + 91, 369, column, 24, 14, medium, Muted, TextAnchor.MiddleRight);
        Label(pane, "실물", Inset + 91 + column, 369, column, 24, 14, medium, Muted, TextAnchor.MiddleRight);
        Label(pane, "차이", Inset + 91 + 2 * column, 369, column, 24, 14, medium, Muted, TextAnchor.MiddleRight);
        for (int i = 0; i < 6; i++)
        {
            float y = rowY + i * rowHeight;
            Rule(pane, Inset, y - 2, detailContent);
            Label(pane, UnityToFollowerGUI.JointName(i), Inset, y, 89, 29, 16, medium, Ink);
            jointUnity[i] = Label(pane, "—", Inset + 91, y, column, 29, 16, semi, Ink, TextAnchor.MiddleRight);
            jointFollower[i] = Label(pane, "—", Inset + 91 + column, y, column, 29, 15, medium, Secondary, TextAnchor.MiddleRight);
            jointDifference[i] = Label(pane, "—", Inset + 91 + 2 * column, y, column, 29, 15, medium, Muted, TextAnchor.MiddleRight);
        }

        Rule(pane, Inset, 604, detailContent);
        Label(pane, "수동 제어", Inset, 616, 110, 28, 18, semi, Ink);
        Label(pane, "어깨 회전 · 초당 10°", Inset, 646, 182, 25, 15, medium, Muted);
        panText = Label(pane, "—", Inset + detailContent - 80, 644, 80, 27, 17, semi, Ink, TextAnchor.MiddleRight);
        panSlider = MakeSlider(pane, Inset, 672, detailContent);
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
        primary = MakeButton(pane, "ControlMode", "유니티로 조종", ActiveFill, Hex(0x6593D4, .76f), OnPrimary, out primaryText, out primaryFill);
        Place(primary.GetComponent<RectTransform>(), Inset, 780, half, 46);
        secondary = MakeButton(pane, "StopOrFollow", "리더로 돌아가기", CardFill, Border, OnSecondary, out secondaryText, out _);
        Place(secondary.GetComponent<RectTransform>(), Inset + half + 8, 780, half, 46);
    }

    void BuildVisionDetail()
    {
        var pane = visionPage;
        Label(pane, "검사 상태", Inset, 330, detailContent, 30, 18, semi, Ink);
        sceneState = Row(pane, "데이터 수신", Inset, 371, detailContent, 35);
        lastReceiveText = Row(pane, "마지막 수신", Inset, 410, detailContent, 35);
        currentRule = Row(pane, "판정 기준", Inset, 449, detailContent, 35);
        Rule(pane, Inset, 500, detailContent);
        Label(pane, "판정 결과", Inset, 511, detailContent, 30, 18, semi, Ink);
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
        var root = NewRect("ShoulderSlider", parent);
        Place(root, x, y, w, 38);
        var track = Img("Track", root, barSprite, new Color(1, 1, 1, .24f));
        track.raycastTarget = true;
        track.rectTransform.anchorMin = new Vector2(0, .5f);
        track.rectTransform.anchorMax = new Vector2(1, .5f);
        track.rectTransform.sizeDelta = new Vector2(0, 5);
        var fill = Img("Fill", track.transform, barSprite, Blue);
        Stretch(fill.rectTransform, 0);
        var slot = NewRect("HandleArea", root);
        Stretch(slot, 0);
        slot.offsetMin = new Vector2(7, 0);
        slot.offsetMax = new Vector2(-7, 0);
        var handle = Img("Handle", slot, dotSprite, BlueInk);
        handle.rectTransform.sizeDelta = new Vector2(17, 17);
        handle.raycastTarget = true;

        var slider = root.gameObject.AddComponent<Slider>();
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = -30;
        slider.maxValue = 30;
        slider.targetGraphic = handle;
        slider.fillRect = fill.rectTransform;
        slider.handleRect = handle.rectTransform;
        slider.onValueChanged.AddListener(value => { if (ManualControlAvailable()) robot.PanTarget = value; });

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
