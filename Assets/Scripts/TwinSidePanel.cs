using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// =============================================================
// [디지털 트윈 오른쪽 패널] 청록 유리 한 장 — 카메라 검사 현황 + 로봇 관절(유니티 ↔ 흰 팔) + 수동 제어 + 비상 정지
// 디자인: 시안 F (2026-11-12, 레퍼런스 스타일). 강조색은 청록 하나, 빨강은 비상 정지·차이 큰 관절에만
//
// - 데이터는 기존 스크립트에서 읽기만 함
//     검사 숫자  ← DashboardUI (buildUi 끄면 집계만 하고 화면은 이 패널이 그림)
//     로봇·버튼  ← UnityToFollowerGUI (흰 팔과 UDP로 말하는 부분, 예전 회색 창은 숨김)
// - 화면은 플레이 시작할 때 코드로 만듦 (UGUI). 진짜 유리 흐림은 DashboardGlassBlur 재사용
// - 키: F2 = 패널 보이기/숨기기
// =============================================================
public class TwinSidePanel : MonoBehaviour
{
    public float width = 400f;        // 1920x1080 기준
    public float margin = 20f;
    public bool realBlur = true;

    DashboardUI dash;
    UnityToFollowerGUI robot;

    // 색 (시안 F)
    static readonly Color Ink = Hex(0xE9FBF8), Ink2 = Hex(0xCFE7E3), Ink3 = Hex(0xA9C9C4);
    static readonly Color Accent = Hex(0x34E0C8), AccentText = Hex(0x7DF5DF), Danger = Hex(0xE5484D), DangerText = Hex(0xFF8A80);
    static readonly Color Tint = new Color(0.035f, 0.10f, 0.125f, 0.76f), TintNoBlur = new Color(0.035f, 0.10f, 0.125f, 0.92f);
    static readonly Color Edge = new Color(0.47f, 0.94f, 0.88f, 0.28f), CardFill = new Color(1f, 1f, 1f, 0.06f), CardEdge = new Color(0.59f, 1f, 0.94f, 0.14f);
    static readonly Color Track = new Color(1f, 1f, 1f, 0.12f), TileFill = new Color(0f, 0f, 0f, 0.18f);

    Font fReg, fMed, fSemi, fBold;
    Sprite panelSprite, edgeSprite, shadowSprite, cardSprite, cardEdgeSprite, btnSprite, btnEdgeSprite, pillSprite, dotSprite, barSprite, ringSprite, sheenSprite;

    GameObject canvasObj;
    RectTransform panelRt, maskRt;
    RawImage blurImg; Image tintImg;

    // 검사 현황
    Text totalText, subText, yieldText; Image ringFill, ringGlow; float ringShown, glowBoost;
    readonly Text[] miniTexts = new Text[6];
    // 헤더
    Text connText, modeText; Image connDot, connPill, modePill;
    // 로봇 관절
    Text jointHead;
    readonly Text[] jVal = new Text[6], jSub = new Text[6]; readonly RectTransform[] jBar = new RectTransform[6]; readonly Image[] jTile = new Image[6];
    // 수동 제어
    Text panText; Slider slider; bool sliderDragging; readonly Button[] stepBtns = new Button[3];
    // 아래 버튼
    Button primaryBtn, secondaryBtn, estopBtn; Text primaryText, secondaryText; Image primaryImg, primaryEdge;

    const float P = 16f, CP = 18f;    // 패널 안쪽 여백, 카드 안쪽 여백

    void Start()
    {
        dash = FindFirstObjectByType<DashboardUI>();
        robot = FindFirstObjectByType<UnityToFollowerGUI>();
        if (robot) robot.show = false;             // 예전 회색 창 숨김
        LoadFonts();
        CreateSprites();
        EnsureEventSystem();
        if (realBlur && Camera.main != null && Camera.main.GetComponent<DashboardGlassBlur>() == null)
        { var b = Camera.main.gameObject.AddComponent<DashboardGlassBlur>(); b.iterations = 5; b.spread = 1.6f; }
        Build();
        if (dash) dash.Inspected += good => glowBoost = 1f;
    }

    // ---------------- 매 프레임 ----------------
    void Update()
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.f2Key.wasPressedThisFrame) canvasObj.SetActive(!canvasObj.activeSelf);
        if (!canvasObj.activeSelf) return;
        float k = 1f - Mathf.Exp(-Time.deltaTime * 8f);

        // 검사 현황
        int total = dash ? dash.TotalCount : 0, good = dash ? dash.GoodCount : 0, bad = dash ? dash.BadCount : 0;
        int judged = good + bad;
        totalText.text = $"{total}<size=20><color=#{H(Ink3)}> 건</color></size>";
        subText.text = $"총 검사 · 불량 {bad}";
        yieldText.text = judged > 0 ? $"{Mathf.RoundToInt(100f * good / judged)}<size=16>%</size>" : "–";
        ringShown = Mathf.Lerp(ringShown, judged > 0 ? (float)good / judged : 0f, k);
        ringFill.fillAmount = ringShown;
        glowBoost = Mathf.MoveTowards(glowBoost, 0f, Time.deltaTime / 1.2f);
        ringGlow.color = new Color(Accent.r, Accent.g, Accent.b, 0.28f + 0.5f * glowBoost);
        string[] ck = { "white", "blue", "red" }, cl = { "화이트", "파랑", "빨강" };
        for (int i = 0; i < 3; i++) miniTexts[i].text = $"{cl[i]}  <color=#{H(Ink)}>{(dash ? dash.ColorCount(ck[i]) : 0)}</color>";
        for (int i = 0; i < 3; i++) miniTexts[3 + i].text = $"숫자{i + 1}  <color=#{H(Ink)}>{(dash ? dash.DigitCount((i + 1).ToString()) : 0)}</color>";

        if (robot == null || !robot.Ready) { modeText.text = "로봇 없음"; return; }

        // 헤더
        bool live = robot.Live, est = robot.EStopped;
        var mode = robot.CurrentMode;
        connText.text = live ? "연결됨" : "패널 꺼짐";
        connText.color = live ? AccentText : Ink3;
        connDot.color = live ? Accent : Ink3;
        connPill.color = live ? new Color(Accent.r, Accent.g, Accent.b, 0.14f) : new Color(1, 1, 1, 0.06f);
        if (est) { modeText.text = "비상 정지 중"; modeText.color = DangerText; modePill.color = new Color(Danger.r, Danger.g, Danger.b, 0.2f); }
        else if (mode == UnityToFollowerGUI.Mode.UnityReal) { modeText.text = "흰 팔 움직이는 중"; modeText.color = Hex(0x1A2E33); modePill.color = Accent; }
        else if (mode == UnityToFollowerGUI.Mode.UnityPractice) { modeText.text = "연습 모드"; modeText.color = Ink2; modePill.color = new Color(1, 1, 1, 0.1f); }
        else { modeText.text = "리더 따라하기"; modeText.color = Ink2; modePill.color = new Color(1, 1, 1, 0.1f); }

        // 로봇 관절
        int okCount = 0;
        for (int i = 0; i < 6; i++)
        {
            float u = robot.UnityDeg(i), f = robot.FollowerDeg(i), d = Mathf.Abs(u - f);
            bool ok = robot.JointOk(i); if (live && ok) okCount++;
            jVal[i].text = $"{u:F1}°";
            float lo = robot.JointMin(i), hi = robot.JointMax(i);
            float t = hi > lo ? Mathf.Clamp01((u - lo) / (hi - lo)) : 0.5f;
            jBar[i].anchorMax = new Vector2(Mathf.Lerp(jBar[i].anchorMax.x, t, k), 1);
            bool warn = live && !ok && mode != UnityToFollowerGUI.Mode.Leader;   // 리더 모드에선 흰 팔이 유니티를 안 따라가니 빨강 안 씀
            jSub[i].text = live ? $"흰 팔 {f:F1}° · 차이 {d:F1}°" : "흰 팔 —";
            jSub[i].color = warn ? DangerText : Ink3;
            jTile[i].color = warn ? new Color(Danger.r, Danger.g, Danger.b, 0.14f) : TileFill;
        }
        jointHead.text = !live ? $"<color=#{H(Ink3)}>흰 팔 신호 없음</color>"
                       : mode == UnityToFollowerGUI.Mode.Leader ? $"<color=#{H(Ink3)}>리더 따라하는 중</color>"
                       : (okCount == 6 ? $"<color=#{H(AccentText)}>6/6 일치</color>" : $"<color=#{H(DangerText)}>{okCount}/6 일치</color>");

        // 수동 제어
        bool unityMode = mode != UnityToFollowerGUI.Mode.Leader && !est;
        slider.interactable = unityMode;
        foreach (var b in stepBtns) b.interactable = unityMode;
        float sp = robot.StartPan;
        slider.minValue = sp - 30f; slider.maxValue = sp + 30f;
        if (!sliderDragging) slider.SetValueWithoutNotify(unityMode ? robot.PanTarget : robot.UnityDeg(0));
        panText.text = $"{(unityMode ? robot.PanTarget : robot.UnityDeg(0)):F1}°";

        // 아래 버튼
        if (mode == UnityToFollowerGUI.Mode.Leader)
        { primaryText.text = "유니티로 조종"; primaryBtn.interactable = live && !est; secondaryText.text = "리더 따라하는 중"; secondaryBtn.interactable = false; }
        else if (mode == UnityToFollowerGUI.Mode.UnityPractice)
        {
            primaryText.text = !robot.AllOk ? "유니티를 흰 팔에 맞추기" : robot.WaitingConfirm ? "한 번 더 누르면 움직여요" : "흰 팔 움직이기";
            primaryBtn.interactable = live && !est;
            secondaryText.text = "리더로 돌아가기"; secondaryBtn.interactable = true;
        }
        else { primaryText.text = "흰 팔 움직이는 중"; primaryBtn.interactable = false; secondaryText.text = "정지"; secondaryBtn.interactable = true; }
        bool hot = mode == UnityToFollowerGUI.Mode.UnityPractice && robot.AllOk && robot.WaitingConfirm;
        primaryImg.color = hot ? new Color(Accent.r, Accent.g, Accent.b, 0.42f) : new Color(Accent.r, Accent.g, Accent.b, 0.16f);
        estopBtn.interactable = !est;
    }

    void LateUpdate()
    {
        if (!canvasObj || !canvasObj.activeSelf) return;
        RenderTexture blurred = DashboardGlassBlur.Blurred;
        bool on = realBlur && blurred != null;
        blurImg.enabled = on;
        tintImg.color = on ? Tint : TintNoBlur;
        if (!on) return;
        blurImg.texture = blurred;
        var c = new Vector3[4]; maskRt.GetWorldCorners(c);
        float sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
        blurImg.uvRect = new Rect(c[0].x / sw, c[0].y / sh, (c[2].x - c[0].x) / sw, (c[2].y - c[0].y) / sh);
    }

    // ---------------- 버튼 동작 ----------------
    void OnPrimary()
    {
        if (!robot) return;
        var m = robot.CurrentMode;
        if (m == UnityToFollowerGUI.Mode.Leader) robot.StartUnityMode();
        else if (m == UnityToFollowerGUI.Mode.UnityPractice) { if (!robot.AllOk) robot.MatchFollower(); else robot.PressGoReal(); }
    }
    void OnSecondary()
    {
        if (!robot) return;
        if (robot.CurrentMode == UnityToFollowerGUI.Mode.UnityReal) robot.StopArm(); else robot.BackToLeader();
    }
    void Step(float delta) { if (robot) robot.PanTarget = delta == 0f ? robot.StartPan : robot.StartPan + delta; }

    // ---------------- 화면 만들기 ----------------
    void Build()
    {
        canvasObj = new GameObject("TwinSidePanelCanvas");
        var canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 20;
        var scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 1f;
        canvasObj.AddComponent<GraphicRaycaster>();

        // 패널 틀: 오른쪽에 위아래 꽉 (여백 margin)
        panelRt = NewRect("Panel", canvasObj.transform);
        panelRt.anchorMin = new Vector2(1, 0); panelRt.anchorMax = new Vector2(1, 1); panelRt.pivot = new Vector2(1, 0.5f);
        panelRt.offsetMin = new Vector2(-width - margin, margin); panelRt.offsetMax = new Vector2(-margin, -margin);

        var shadow = Img("Shadow", panelRt, shadowSprite, new Color(0, 0, 0, 0.5f)); Stretch(shadow.rectTransform, -34);
        var maskImg = Img("Glass", panelRt, panelSprite, Color.white); Stretch(maskImg.rectTransform, 0);
        maskImg.gameObject.AddComponent<Mask>().showMaskGraphic = false; maskRt = maskImg.rectTransform;
        blurImg = new GameObject("Blur", typeof(RectTransform)).AddComponent<RawImage>(); blurImg.transform.SetParent(maskRt, false); blurImg.raycastTarget = false; Stretch(blurImg.rectTransform, 0);
        tintImg = Img("Tint", maskRt, null, Tint); Stretch(tintImg.rectTransform, 0);
        var sheen = Img("Sheen", maskRt, sheenSprite, new Color(1, 1, 1, 0.07f)); var sr = sheen.rectTransform;
        sr.anchorMin = new Vector2(0, 1); sr.anchorMax = new Vector2(1, 1); sr.pivot = new Vector2(0.5f, 1); sr.sizeDelta = new Vector2(0, 160); sr.anchoredPosition = Vector2.zero;
        Stretch(Img("Edge", panelRt, edgeSprite, Edge).rectTransform, 0);

        RectTransform content = NewRect("Content", panelRt); Stretch(content, P);
        float cw = width - 2 * P;

        // 헤더
        var title = Txt(content, "디지털 트윈", 24, fSemi, Ink, TextAnchor.MiddleLeft); Place(title.rectTransform, 4, 4, 200, 40);
        modePill = Img("ModePill", content, pillSprite, new Color(1, 1, 1, 0.1f)); PlaceRight(modePill.rectTransform, 0, 11, 108, 28);
        modeText = Txt(modePill.rectTransform, "리더 따라하기", 13, fSemi, Ink2, TextAnchor.MiddleCenter); Stretch(modeText.rectTransform, 0);
        connPill = Img("ConnPill", content, pillSprite, new Color(1, 1, 1, 0.06f)); PlaceRight(connPill.rectTransform, 116, 11, 86, 28);
        connDot = Img("Dot", connPill.rectTransform, dotSprite, Ink3); Place(connDot.rectTransform, 11, 10, 8, 8);
        connText = Txt(connPill.rectTransform, "패널 꺼짐", 13, fSemi, Ink3, TextAnchor.MiddleLeft); Place(connText.rectTransform, 24, 0, 60, 28);

        // 카드 1: 검사 현황
        float y = 56;
        var c1 = Card(content, y, 254);
        Place(Txt(c1, "검사 현황", 15, fSemi, Ink2, TextAnchor.MiddleLeft).rectTransform, CP, 14, 200, 20);
        totalText = Txt(c1, "0", 54, fBold, Ink, TextAnchor.MiddleLeft); Place(totalText.rectTransform, CP - 2, 44, 200, 64);
        subText = Txt(c1, "총 검사 · 불량 0", 13, fMed, Ink3, TextAnchor.MiddleLeft); Place(subText.rectTransform, CP, 112, 200, 18);
        const float ring = 112f;
        ringGlow = Img("RingGlow", c1, shadowSprite, new Color(Accent.r, Accent.g, Accent.b, 0.3f)); PlaceRight(ringGlow.rectTransform, CP - 22, 40 - 22, ring + 44, ring + 44);
        var trackImg = Img("RingTrack", c1, ringSprite, Track); trackImg.type = Image.Type.Simple; PlaceRight(trackImg.rectTransform, CP, 40, ring, ring);
        ringFill = Img("RingFill", c1, ringSprite, Accent); ringFill.type = Image.Type.Filled; ringFill.fillMethod = Image.FillMethod.Radial360;
        ringFill.fillOrigin = (int)Image.Origin360.Top; ringFill.fillClockwise = true; ringFill.fillAmount = 0; PlaceRight(ringFill.rectTransform, CP, 40, ring, ring);
        yieldText = Txt(ringFill.rectTransform, "–", 28, fBold, Ink, TextAnchor.MiddleCenter); Place(yieldText.rectTransform, 0, 32, ring, 36);
        Place(Txt(ringFill.rectTransform, "합격률", 12, fMed, Ink3, TextAnchor.MiddleCenter).rectTransform, 0, 66, ring, 16);
        float mw = (cw - 2 * CP - 16) / 3f;
        for (int i = 0; i < 6; i++)
        {
            var tile = Img("Mini" + i, c1, btnSprite, new Color(1, 1, 1, 0.05f));
            Place(tile.rectTransform, CP + (i % 3) * (mw + 8), 160 + (i / 3) * 42, mw, 34);
            miniTexts[i] = Txt(tile.rectTransform, "", 13, fMed, Ink3, TextAnchor.MiddleLeft); Stretch(miniTexts[i].rectTransform, -10);
        }

        // 카드 2: 로봇 관절
        y += 254 + 12;
        var c2 = Card(content, y, 312);
        Place(Txt(c2, $"로봇 관절  <color=#{H(Ink3)}><size=13>유니티 ↔ 흰 팔</size></color>", 15, fSemi, Ink2, TextAnchor.MiddleLeft).rectTransform, CP, 14, 260, 20);
        jointHead = Txt(c2, "", 13, fSemi, AccentText, TextAnchor.MiddleRight); PlaceRight(jointHead.rectTransform, CP, 14, 140, 20);
        float tw = (cw - 2 * CP - 8) / 2f, th = 78f;
        for (int i = 0; i < 6; i++)
        {
            float tx = CP + (i % 2) * (tw + 8), ty = 44 + (i / 2) * (th + 8);
            jTile[i] = Img("Joint" + i, c2, btnSprite, TileFill); Place(jTile[i].rectTransform, tx, ty, tw, th);
            Stretch(Img("Edge", jTile[i].rectTransform, btnEdgeSprite, CardEdge).rectTransform, 0);
            var t = jTile[i].rectTransform;
            Place(Txt(t, UnityToFollowerGUI.JointName(i), 13, fMed, Ink3, TextAnchor.MiddleLeft).rectTransform, 12, 8, tw - 24, 20);
            jVal[i] = Txt(t, "", 17, fSemi, Ink, TextAnchor.MiddleRight); Place(jVal[i].rectTransform, 12, 7, tw - 24, 22);
            var trk = Img("Track", t, barSprite, Track); Place(trk.rectTransform, 12, 36, tw - 24, 4);
            var fill = Img("Fill", trk.rectTransform, barSprite, Accent); jBar[i] = fill.rectTransform;
            jBar[i].anchorMin = Vector2.zero; jBar[i].anchorMax = new Vector2(0.5f, 1); jBar[i].offsetMin = jBar[i].offsetMax = Vector2.zero;
            jSub[i] = Txt(t, "", 12, fReg, Ink3, TextAnchor.MiddleLeft); Place(jSub[i].rectTransform, 12, 48, tw - 20, 20);
        }

        // 카드 3: 수동 제어
        y += 312 + 12;
        var c3 = Card(content, y, 146);
        Place(Txt(c3, $"수동 제어  <color=#{H(Ink3)}><size=13>어깨 회전 · 초당 10°</size></color>", 15, fSemi, Ink2, TextAnchor.MiddleLeft).rectTransform, CP, 14, 260, 20);
        panText = Txt(c3, "", 20, fSemi, Ink, TextAnchor.MiddleRight); PlaceRight(panText.rectTransform, CP, 11, 100, 26);
        slider = BuildSlider(c3, CP, 50, cw - 2 * CP);
        string[] sl = { "−15°", "처음", "+15°" }; float[] sd = { -15f, 0f, 15f };
        float bw = (cw - 2 * CP - 16) / 3f;
        for (int i = 0; i < 3; i++)
        {
            float d = sd[i];
            stepBtns[i] = Btn(c3, sl[i], CP + i * (bw + 8), 88, bw, 40, new Color(1, 1, 1, 0.06f), CardEdge, Ink, 15, () => Step(d), out _, out _, out _);
        }

        // 아래: 버튼 두 개 + 비상 정지 (패널 바닥에 붙음)
        float half = (cw - 8) / 2f;
        primaryBtn = BtnBottom(content, "유니티로 조종", 0, 72, half, 48, new Color(Accent.r, Accent.g, Accent.b, 0.16f), new Color(Accent.r, Accent.g, Accent.b, 0.6f), AccentText, 15, OnPrimary, out primaryText, out primaryImg, out primaryEdge);
        secondaryBtn = BtnBottom(content, "리더 따라하는 중", half + 8, 72, half, 48, new Color(1, 1, 1, 0.06f), CardEdge, Ink, 15, OnSecondary, out secondaryText, out _, out _);
        var glow = Img("EStopGlow", content, shadowSprite, new Color(Danger.r, Danger.g, Danger.b, 0.5f));
        var gr = glow.rectTransform; gr.anchorMin = gr.anchorMax = new Vector2(0, 0); gr.pivot = new Vector2(0, 0); gr.sizeDelta = new Vector2(cw + 44, 62 + 44); gr.anchoredPosition = new Vector2(-22, -22);
        estopBtn = BtnBottom(content, "■  비상 정지", 0, 0, cw, 62, new Color(0.74f, 0.17f, 0.16f, 0.92f), new Color(1f, 0.5f, 0.45f, 0.8f), Color.white, 19, () => { if (robot) robot.EStop(); }, out _, out _, out _);
        var es = Img("Sheen", estopBtn.transform, sheenSprite, new Color(1, 1, 1, 0.18f)); var esr = es.rectTransform;
        esr.anchorMin = new Vector2(0, 0.5f); esr.anchorMax = new Vector2(1, 1); esr.offsetMin = new Vector2(2, 0); esr.offsetMax = new Vector2(-2, -2);
    }

    RectTransform Card(RectTransform parent, float y, float h)
    {
        var img = Img("Card", parent, cardSprite, CardFill); Place(img.rectTransform, 0, y, width - 2 * P, h);
        Stretch(Img("Edge", img.rectTransform, cardEdgeSprite, CardEdge).rectTransform, 0);
        return img.rectTransform;
    }

    Slider BuildSlider(RectTransform parent, float x, float y, float w)
    {
        var root = NewRect("PanSlider", parent); Place(root, x, y, w, 28);
        var bg = Img("Track", root, barSprite, Track); var br = bg.rectTransform; br.anchorMin = new Vector2(0, 0.5f); br.anchorMax = new Vector2(1, 0.5f); br.sizeDelta = new Vector2(0, 6);
        var fillArea = NewRect("FillArea", root); fillArea.anchorMin = new Vector2(0, 0.5f); fillArea.anchorMax = new Vector2(1, 0.5f); fillArea.sizeDelta = new Vector2(0, 6);
        var fill = Img("Fill", fillArea, barSprite, Accent); fill.rectTransform.sizeDelta = Vector2.zero;
        var handleArea = NewRect("HandleArea", root); handleArea.anchorMin = Vector2.zero; handleArea.anchorMax = Vector2.one; handleArea.offsetMin = new Vector2(11, 0); handleArea.offsetMax = new Vector2(-11, 0);
        var hglow = Img("HandleGlow", handleArea, shadowSprite, new Color(Accent.r, Accent.g, Accent.b, 0.4f)); hglow.rectTransform.sizeDelta = new Vector2(40, 40);
        var handle = Img("Handle", handleArea, dotSprite, Color.white); handle.raycastTarget = true; handle.rectTransform.sizeDelta = new Vector2(22, 22);
        hglow.transform.SetParent(handle.transform, false); hglow.rectTransform.anchoredPosition = Vector2.zero; hglow.transform.SetAsFirstSibling();
        var s = root.gameObject.AddComponent<Slider>();
        s.fillRect = fill.rectTransform; s.handleRect = handle.rectTransform; s.targetGraphic = handle; s.direction = Slider.Direction.LeftToRight;
        s.onValueChanged.AddListener(v => { if (robot && robot.CurrentMode != UnityToFollowerGUI.Mode.Leader) robot.PanTarget = v; });
        var trig = root.gameObject.AddComponent<EventTrigger>();
        AddTrigger(trig, EventTriggerType.PointerDown, () => sliderDragging = true);
        AddTrigger(trig, EventTriggerType.PointerUp, () => sliderDragging = false);
        return s;
    }
    static void AddTrigger(EventTrigger t, EventTriggerType type, System.Action a)
    { var e = new EventTrigger.Entry { eventID = type }; e.callback.AddListener(_ => a()); t.triggers.Add(e); }

    Button Btn(RectTransform parent, string label, float x, float y, float w, float h, Color fill, Color edge, Color ink, int size,
               UnityEngine.Events.UnityAction onClick, out Text text, out Image img, out Image edgeImg)
    {
        img = Img("Btn_" + label, parent, btnSprite, fill); img.raycastTarget = true; Place(img.rectTransform, x, y, w, h);
        edgeImg = Img("Edge", img.rectTransform, btnEdgeSprite, edge); Stretch(edgeImg.rectTransform, 0);
        text = Txt(img.rectTransform, label, size, fSemi, ink, TextAnchor.MiddleCenter); Stretch(text.rectTransform, 0);
        var b = img.gameObject.AddComponent<Button>(); b.targetGraphic = img; b.onClick.AddListener(onClick);
        var cb = b.colors; cb.normalColor = Color.white; cb.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f); cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
        cb.disabledColor = new Color(1f, 1f, 1f, 0.4f); cb.colorMultiplier = 1.5f; b.colors = cb;
        return b;
    }
    Button BtnBottom(RectTransform parent, string label, float x, float bottom, float w, float h, Color fill, Color edge, Color ink, int size,
                     UnityEngine.Events.UnityAction onClick, out Text text, out Image img, out Image edgeImg)
    {
        var b = Btn(parent, label, x, 0, w, h, fill, edge, ink, size, onClick, out text, out img, out edgeImg);
        var rt = img.rectTransform; rt.anchorMin = rt.anchorMax = new Vector2(0, 0); rt.pivot = new Vector2(0, 0); rt.anchoredPosition = new Vector2(x, bottom);
        return b;
    }

    // ---------------- 작은 도우미 ----------------
    static RectTransform NewRect(string name, Transform parent)
    { var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); rt.SetParent(parent, false); return rt; }
    static Image Img(string name, Transform parent, Sprite sp, Color c)
    {
        var img = NewRect(name, parent).gameObject.AddComponent<Image>();
        img.sprite = sp; img.type = (sp != null && sp.border != Vector4.zero) ? Image.Type.Sliced : Image.Type.Simple; img.color = c; img.raycastTarget = false;
        return img;
    }
    Text Txt(Transform parent, string s, int size, Font f, Color c, TextAnchor a)
    {
        var t = NewRect("Text", parent).gameObject.AddComponent<Text>();
        t.font = f; t.text = s; t.fontSize = size; t.color = c; t.alignment = a; t.supportRichText = true; t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }
    static void Place(RectTransform rt, float x, float y, float w, float h)   // 부모 왼쪽 위 기준, y는 아래로
    { rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1); rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(x, -y); }
    static void PlaceRight(RectTransform rt, float right, float y, float w, float h)   // 부모 오른쪽 위 기준
    { rt.anchorMin = rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(1, 1); rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(-right, -y); }
    static void Stretch(RectTransform rt, float inset)   // inset > 0 = 안쪽으로, < 0 = 바깥으로
    { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(inset, inset); rt.offsetMax = new Vector2(-inset, -inset); }
    static Color Hex(int rgb, float a = 1f) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);
    static string H(Color c) => ColorUtility.ToHtmlStringRGB(c);

    static void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        var go = new GameObject("EventSystem", typeof(EventSystem));
        go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();   // 이 프로젝트는 새 Input System만 씀
    }

    void LoadFonts()
    {
        fReg = Resources.Load<Font>("Fonts/Pretendard-Regular"); fMed = Resources.Load<Font>("Fonts/Pretendard-Medium");
        fSemi = Resources.Load<Font>("Fonts/Pretendard-SemiBold"); fBold = Resources.Load<Font>("Fonts/Pretendard-Bold");
        if (fReg && fMed && fSemi && fBold) return;
        var fb = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); fReg = fMed = fSemi = fBold = fb;
    }

    // 그림 파일 없이 코드로 둥근 모양 만들기 (DashboardUI와 같은 방식)
    void CreateSprites()
    {
        panelSprite = Rounded(96, 22, 0, 0); edgeSprite = Rounded(96, 22, 1, 0); shadowSprite = Rounded(224, 66, 0, 40);
        cardSprite = Rounded(64, 16, 0, 0); cardEdgeSprite = Rounded(64, 16, 1, 0);
        btnSprite = Rounded(48, 11, 0, 0); btnEdgeSprite = Rounded(48, 11, 1, 0);
        pillSprite = Rounded(40, 20, 0, 0); dotSprite = Rounded(32, 16, 0, 0); barSprite = Rounded(16, 3, 0, 0);
        ringSprite = Rounded(128, 64, 10, 0); sheenSprite = Fade(64);
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
    static Sprite Fade(int size)
    {
        var tex = new Texture2D(4, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        for (int py = 0; py < size; py++) { float t = (float)py / (size - 1); for (int px = 0; px < 4; px++) tex.SetPixel(px, py, new Color(1, 1, 1, t * t)); }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 4, size), new Vector2(0.5f, 0.5f), 100f);
    }
}
