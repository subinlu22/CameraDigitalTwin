using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI; // Canvas, Image, Text 같은 UI 부품

// =============================================================
// [디지털트윈 대시보드 v3] 오른쪽 위에 떠 있는 유리 카드 2장 + 판정 순간 화면 테두리 빛(양품 초록 / 불량 빨강)
//
//   카드 A (요약)  : LIVE 표시 · 날짜 / 오늘 검사 수 / 양품률 원형 게이지 / 양품·불량·검사 기준 / 마지막 수신
//   카드 B (내역)  : 색상별 = 한 줄 색 막대 + 범례 / 숫자별 = 타일 3개
//
// - UI는 플레이 시작할 때 이 스크립트가 코드로 전부 만듦 (에디터에서 배치 안 함 → 플레이해야 보임)
// - 진짜 유리: 카드 뒤 화면을 실제로 흐림(DashboardGlassBlur) → 그 위에 반투명 색 → 테두리·그림자
// - 테마 2개: 밝은 유리(어두운 글자) / 어두운 유리(밝은 글자) — lightTheme 체크로 바꿈 (플레이 전에)
// - 통계는 DigitalTwinReceiver가 SetCriterion() / RegisterInspection()을 불러서 쌓임 (함수 이름·역할은 v1과 같음)
// - 둥근 모서리·그림자·원형 게이지 이미지는 그림 파일 없이 코드로 그림, 글꼴은 Pretendard (SIL OFL)
// =============================================================
public class DashboardUI : MonoBehaviour
{
    [Header("화면 (끄면 집계만 하고 화면은 TwinSidePanel이 그림)")]
    public bool buildUi = true;

    // ---------- 다른 화면(TwinSidePanel)이 읽는 값 ----------
    public int TotalCount => totalCount;
    public int GoodCount => goodCount;
    public int BadCount => badCount;
    public int ColorCount(string key) => colorCounts.TryGetValue(key, out int n) ? n : 0;
    public int DigitCount(string key) => digitCounts.TryGetValue(key, out int n) ? n : 0;
    public float LastReceiveTime => lastReceiveTime;
    public string Criterion => currentTarget;
    public event Action<bool> Inspected;     // 판정 1건 (true = 양품)

    [Header("테마 (플레이 전에 바꾸기)")]
    public bool lightTheme = true;           // 켬 = 밝은 유리, 끔 = 어두운 유리
    public bool realBlur = true;             // 끄면 흐림 없이 반투명만

    [Header("배치 (1920x1080 기준)")]
    public float cardWidth = 360f;
    public float margin = 32f;               // 화면 가장자리와의 여백
    public float cardGap = 14f;              // 카드 사이 간격

    [Header("테두리 빛")]
    public float flashFadeSeconds = 1.5f;

    // ---------- 테마 색 (Awake에서 lightTheme에 따라 채움) ----------
    Color tint, tintNoBlur, edge, shadow, sheen, ink, ink2, ink3, tileBg, tileLine, track, divider;
    Color good, bad;
    Dictionary<string, Color> colorAccents;
    Color digitAccent;

    readonly string[] colorKeys = { "white", "blue", "red" };
    readonly Dictionary<string, string> colorLabels = new Dictionary<string, string>
    {
        { "white", "화이트" }, { "blue", "파랑" }, { "red", "빨강" },
    };
    readonly string[] digitKeys = { "1", "2", "3" };

    // ---------- 집계 값 ----------
    int totalCount = 0;
    readonly Dictionary<string, int> colorCounts = new Dictionary<string, int>();
    readonly Dictionary<string, int> digitCounts = new Dictionary<string, int>();
    string currentTarget = null;   // 검사 기준 ("all"/"white"/"blue"/"red"), 바뀌면 양품·불량 0부터
    int goodCount = 0, badCount = 0;
    float lastReceiveTime = -1f;

    // ---------- 화면 부품 ----------
    Text totalText, yieldText, goodText, badText, criterionText, statusText, liveText, colorTotalText;
    Image criterionBadge, liveDot, ringFill, glowImage;
    readonly Dictionary<string, Text> legendCounts = new Dictionary<string, Text>();
    readonly Dictionary<string, RectTransform> segments = new Dictionary<string, RectTransform>();
    readonly Dictionary<string, float> segShown = new Dictionary<string, float>();
    readonly Dictionary<string, Text> tileCounts = new Dictionary<string, Text>();
    readonly Dictionary<string, RectTransform> tileBars = new Dictionary<string, RectTransform>();
    readonly Dictionary<string, float> tileShown = new Dictionary<string, float>();
    float ringShown;

    // 유리 카드마다: 둥근 틀 + 흐린 화면 + 덮는 색 (흐린 화면 위치를 매 프레임 맞춤)
    class Glass { public RectTransform mask; public RawImage blur; public Image tint; }
    readonly List<Glass> glasses = new List<Glass>();

    Font fRegular, fMedium, fSemiBold, fBold;
    Sprite cardSprite, edgeSprite, shadowSprite, tileSprite, tileLineSprite, pillSprite, dotSprite,
           barSprite, ringSprite, sheenSprite, glowSprite;

    const float Pad = 22f;

    void Awake()
    {
        foreach (string k in colorKeys) colorCounts[k] = 0;
        foreach (string k in digitKeys) digitCounts[k] = 0;

        SetTheme();
        if (!buildUi) return;     // 화면 없이 집계만
        LoadFonts();
        CreateSprites();
        BuildUi();
    }

    void Start()
    {
        // 메인 카메라에 흐림 컴포넌트 (없을 때만). 흐림을 조금 세게
        if (!buildUi || !realBlur || Camera.main == null) return;
        var blur = Camera.main.GetComponent<DashboardGlassBlur>();
        if (blur == null) { blur = Camera.main.gameObject.AddComponent<DashboardGlassBlur>(); blur.iterations = 5; blur.spread = 1.6f; }
    }

    // 테마별 색. 밝은 유리 = 하얀 반투명 + 어두운 글자 / 어두운 유리 = 검은 반투명 + 밝은 글자
    void SetTheme()
    {
        if (lightTheme)
        {
            tint = new Color(1f, 1f, 1f, 0.46f);       tintNoBlur = new Color(0.97f, 0.97f, 0.98f, 0.86f);
            edge = new Color(1f, 1f, 1f, 0.75f);       shadow = new Color(0.05f, 0.08f, 0.14f, 0.22f);
            sheen = new Color(1f, 1f, 1f, 0.35f);
            ink = Hex(0x14171C);                       ink2 = Hex(0x14171C, 0.66f); ink3 = Hex(0x14171C, 0.46f);
            tileBg = new Color(1f, 1f, 1f, 0.42f);     tileLine = Hex(0x14171C, 0.06f);
            track = Hex(0x14171C, 0.08f);              divider = Hex(0x14171C, 0.08f);
            good = Hex(0x18A058);                      bad = Hex(0xE5484D);
            colorAccents = new Dictionary<string, Color> { { "white", Hex(0xA3ABB8) }, { "blue", Hex(0x3B6CF6) }, { "red", Hex(0xF0545A) } };
            digitAccent = Hex(0x14171C, 0.78f);
        }
        else
        {
            tint = new Color(0.06f, 0.07f, 0.09f, 0.55f); tintNoBlur = new Color(0.06f, 0.07f, 0.09f, 0.82f);
            edge = new Color(1f, 1f, 1f, 0.16f);       shadow = new Color(0f, 0f, 0f, 0.4f);
            sheen = new Color(1f, 1f, 1f, 0.08f);
            ink = Hex(0xF3F4F6);                       ink2 = new Color(1f, 1f, 1f, 0.7f); ink3 = new Color(1f, 1f, 1f, 0.5f);
            tileBg = new Color(1f, 1f, 1f, 0.06f);     tileLine = new Color(1f, 1f, 1f, 0.07f);
            track = new Color(1f, 1f, 1f, 0.1f);       divider = new Color(1f, 1f, 1f, 0.09f);
            good = Hex(0x54E38F);                      bad = Hex(0xFF6166);
            colorAccents = new Dictionary<string, Color> { { "white", Hex(0xEEF0F4) }, { "blue", Hex(0x6E9CFF) }, { "red", Hex(0xFF6B66) } };
            digitAccent = new Color(1f, 1f, 1f, 0.85f);
        }
    }

    static Color Hex(int rgb, float a = 1f) =>
        new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);

    static string HexStr(Color c) => ColorUtility.ToHtmlStringRGB(c);

    // =========================================================
    // [외부에서 호출] 검사 기준 반영 - 기준이 "바뀌었을 때만" 양품/불량을 0으로
    // =========================================================
    public void SetCriterion(string target)
    {
        if (string.IsNullOrEmpty(target) || target == currentTarget) return;
        currentTarget = target;
        goodCount = 0;
        badCount = 0;
        if (!buildUi) return;

        if (colorAccents.ContainsKey(target))
        {
            Color a = colorAccents[target];
            criterionText.text = "기준 · " + colorLabels[target];
            criterionText.color = lightTheme ? (target == "white" ? ink : a) : Color.Lerp(a, Color.white, 0.4f);
            a.a = lightTheme ? 0.16f : 0.26f;
            if (target == "white") a = lightTheme ? Hex(0x14171C, 0.07f) : new Color(1, 1, 1, 0.14f);
            criterionBadge.color = a;
        }
        else
        {
            criterionText.text = "기준 · 전체";
            criterionText.color = ink;
            criterionBadge.color = lightTheme ? Hex(0x14171C, 0.07f) : new Color(1, 1, 1, 0.1f);
        }
        RefreshStats();
    }

    // =========================================================
    // [외부에서 호출] 판정 결과 1건 반영
    // =========================================================
    public void RegisterInspection(string color, string digit, bool isGood)
    {
        totalCount++;
        if (colorCounts.ContainsKey(color)) colorCounts[color]++;
        if (digitCounts.ContainsKey(digit)) digitCounts[digit]++;
        if (isGood) goodCount++; else badCount++;
        lastReceiveTime = Time.time;
        Inspected?.Invoke(isGood);
        if (!buildUi) return;
        RefreshStats();

        Color flash = isGood ? good : bad;
        flash.a = 1f;
        glowImage.color = flash;

        statusText.text = $"마지막 수신  {DateTime.Now:HH:mm:ss}";
        lastReceiveTime = Time.time;
    }

    // 숫자 글자 갱신 (막대·게이지 길이는 Update에서 부드럽게 따라감)
    void RefreshStats()
    {
        totalText.text = $"{totalCount}<size=22><color=#{HexStr(ink3)}>  건</color></size>";
        goodText.text = $"<color=#{HexStr(good)}>●</color>  양품  {goodCount}";
        badText.text = $"<color=#{HexStr(bad)}>●</color>  불량  {badCount}";
        int judged = goodCount + badCount;
        yieldText.text = judged > 0 ? $"{Mathf.RoundToInt(100f * goodCount / judged)}<size=13>%</size>" : "–";
        colorTotalText.text = $"총 {totalCount}건";
        foreach (string k in colorKeys) legendCounts[k].text = colorCounts[k].ToString();
        foreach (string k in digitKeys) tileCounts[k].text = digitCounts[k].ToString();
    }

    void Update()
    {
        if (!buildUi) return;
        float k = 1f - Mathf.Exp(-Time.deltaTime * 8f);   // 부드럽게 따라가는 비율 (프레임 속도와 무관)

        // 화면 테두리 빛 서서히 빼기
        Color c = glowImage.color;
        if (c.a > 0f) { c.a = Mathf.MoveTowards(c.a, 0f, Time.deltaTime / flashFadeSeconds); glowImage.color = c; }

        // 양품률 게이지
        int judged = goodCount + badCount;
        ringShown = Mathf.Lerp(ringShown, judged > 0 ? (float)goodCount / judged : 0f, k);
        ringFill.fillAmount = ringShown;

        // 색상별 한 줄 막대: 각 색이 차지하는 구간 [시작, 끝]을 비율로
        float start = 0f;
        foreach (string key in colorKeys)
        {
            float target = totalCount > 0 ? (float)colorCounts[key] / totalCount : 0f;
            segShown[key] = Mathf.Lerp(segShown[key], target, k);
            RectTransform seg = segments[key];
            seg.anchorMin = new Vector2(start, 0);
            seg.anchorMax = new Vector2(start + segShown[key], 1);
            seg.offsetMin = Vector2.zero;
            seg.offsetMax = new Vector2(segShown[key] > 0.001f ? -2f : 0f, 0);   // 구간 사이 2px 틈
            seg.gameObject.SetActive(segShown[key] > 0.004f);
            start += segShown[key];
        }

        // 숫자별 타일 막대
        foreach (string key in digitKeys)
        {
            float target = totalCount > 0 ? (float)digitCounts[key] / totalCount : 0f;
            tileShown[key] = Mathf.Lerp(tileShown[key], target, k);
            tileBars[key].anchorMax = new Vector2(tileShown[key], 1);
            tileBars[key].gameObject.SetActive(tileShown[key] > 0.01f);
        }

        // LIVE 표시: 받은 적 없으면 "대기"(회색), 받았으면 "LIVE"(초록, 숨 쉬듯 깜빡)
        if (lastReceiveTime < 0)
        {
            liveText.text = "대기 중";
            liveDot.color = ink3;
        }
        else
        {
            liveText.text = "LIVE";
            Color g = good;
            g.a = 0.55f + 0.45f * (0.5f + 0.5f * Mathf.Cos(Time.time * 3f));
            liveDot.color = g;
        }
    }

    // 카드마다 흐린 화면 중 "카드가 있는 부분"만 잘라서 보여줌 → 카드 뒤가 흐려 보임
    void LateUpdate()
    {
        if (!buildUi) return;
        RenderTexture blurred = DashboardGlassBlur.Blurred;
        bool on = realBlur && blurred != null;
        float sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
        Vector3[] corners = new Vector3[4];
        foreach (Glass g in glasses)
        {
            g.blur.enabled = on;
            g.tint.color = on ? tint : tintNoBlur;
            if (!on) continue;
            g.blur.texture = blurred;
            g.mask.GetWorldCorners(corners);   // 오버레이 캔버스라 = 화면 픽셀 좌표 (0=왼쪽 아래, 2=오른쪽 위)
            g.blur.uvRect = new Rect(corners[0].x / sw, corners[0].y / sh,
                                     (corners[2].x - corners[0].x) / sw, (corners[2].y - corners[0].y) / sh);
        }
    }

    // =========================================================
    // [UI 만들기]
    // =========================================================
    void BuildUi()
    {
        GameObject canvasObj = new GameObject("DashboardCanvas");
        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;
        Transform root = canvasObj.transform;

        const float heightA = 236f, heightB = 290f;
        RectTransform a = CreateGlassCard(root, "SummaryCard", -margin, heightA);
        RectTransform b = CreateGlassCard(root, "BreakdownCard", -margin - heightA - cardGap, heightB);
        float inner = cardWidth - Pad * 2;

        // ---------------- 카드 A: 요약 ----------------
        // LIVE 알약 (왼쪽 위)
        RectTransform live = CreateImage("LivePill", a, pillSprite, lightTheme ? Hex(0x14171C, 0.06f) : new Color(1, 1, 1, 0.08f)).rectTransform;
        SetTopLeft(live, Pad, -20, 82, 26);
        liveDot = CreateImage("LiveDot", live, dotSprite, ink3);
        SetMiddleLeft(liveDot.rectTransform, 10, 7, 7);
        liveText = CreateText("LiveText", live, "대기 중", 12, fSemiBold, ink2, TextAnchor.MiddleLeft, 22, 0, 24, 4f);
        FillHeight(liveText.rectTransform, 22, 4);

        // 날짜 (오른쪽 위)
        DateTime now = DateTime.Now;
        string[] dow = { "일", "월", "화", "수", "목", "금", "토" };
        CreateText("Date", a, $"{now.Month}월 {now.Day}일 ({dow[(int)now.DayOfWeek]})", 14, fMedium, ink3,
            TextAnchor.MiddleRight, Pad, -20, 26, Pad);

        // 오늘 검사 + 큰 숫자 (왼쪽)
        CreateText("TotalLabel", a, "오늘 검사", 15, fMedium, ink2, TextAnchor.MiddleLeft, Pad, -60, 20);
        totalText = CreateText("TotalValue", a, "0", 60, fBold, ink, TextAnchor.MiddleLeft, Pad - 3, -80, 70);

        // 양품률 원형 게이지 (오른쪽)
        const float ring = 92f;
        RectTransform ringBox = new GameObject("YieldRing", typeof(RectTransform)).GetComponent<RectTransform>();
        ringBox.SetParent(a, false);
        SetTopRight(ringBox, -Pad, -56, ring, ring);
        Image ringTrack = CreateImage("Track", ringBox, ringSprite, track);
        ringTrack.type = Image.Type.Simple;
        Stretch(ringTrack.rectTransform);
        ringFill = CreateImage("Fill", ringBox, ringSprite, good);
        ringFill.type = Image.Type.Filled;
        ringFill.fillMethod = Image.FillMethod.Radial360;
        ringFill.fillOrigin = (int)Image.Origin360.Top;
        ringFill.fillClockwise = true;
        ringFill.fillAmount = 0f;
        Stretch(ringFill.rectTransform);
        yieldText = CreateText("YieldValue", ringBox, "–", 22, fBold, ink, TextAnchor.MiddleCenter, 0, -24, 30, 0f);
        CreateText("YieldLabel", ringBox, "양품률", 12, fMedium, ink3, TextAnchor.MiddleCenter, 0, -53, 16, 0f);

        // 구분선
        CreateLine(a, -164, inner);

        // 양품 · 불량 · 검사 기준
        goodText = CreateText("Good", a, "", 15, fSemiBold, ink, TextAnchor.MiddleLeft, Pad, -176, 28);
        badText = CreateText("Bad", a, "", 15, fSemiBold, ink, TextAnchor.MiddleLeft, Pad + 100, -176, 28);
        criterionBadge = CreateImage("CriterionPill", a, pillSprite, lightTheme ? Hex(0x14171C, 0.07f) : new Color(1, 1, 1, 0.1f));
        SetTopRight(criterionBadge.rectTransform, -Pad, -177, 108, 28);
        criterionText = CreateText("CriterionText", criterionBadge.rectTransform, "기준 · 전체", 13, fSemiBold, ink,
            TextAnchor.MiddleCenter, 0, 0, 28, 0f);

        // 마지막 수신
        statusText = CreateText("Status", a, "카메라 신호 기다리는 중 (UDP)", 13, fRegular, ink3, TextAnchor.MiddleLeft, Pad, -206, 18);

        // ---------------- 카드 B: 내역 ----------------
        CreateText("ColorTitle", b, "색상별", 15, fSemiBold, ink, TextAnchor.MiddleLeft, Pad, -20, 20);
        colorTotalText = CreateText("ColorTotal", b, "총 0건", 13, fMedium, ink3, TextAnchor.MiddleRight, Pad, -20, 20, Pad);

        // 한 줄 색 막대 (둥근 틀 안에서 구간별로 칠함)
        Image barBg = CreateImage("ColorBar", b, barSprite, track);
        SetTopLeft(barBg.rectTransform, Pad, -50, inner, 10);
        barBg.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        foreach (string key in colorKeys)
        {
            Image seg = CreateImage("Seg_" + key, barBg.rectTransform, null, colorAccents[key]);
            segments[key] = seg.rectTransform;
            segShown[key] = 0f;
            seg.gameObject.SetActive(false);
        }

        // 범례: ● 화이트 2   ● 파랑 5   ● 빨강 2
        float col = inner / 3f;
        for (int i = 0; i < colorKeys.Length; i++)
        {
            string key = colorKeys[i];
            float x = Pad + col * i;
            Image dot = CreateImage("LegendDot_" + key, b, dotSprite, colorAccents[key]);
            SetTopLeft(dot.rectTransform, x, -79, 9, 9);
            Text name = CreateText("Legend_" + key, b, colorLabels[key], 14, fMedium, ink2, TextAnchor.MiddleLeft, x + 15, -72, 22);
            SetTopLeft(name.rectTransform, x + 15, -72, col - 15, 22);
            Text cnt = CreateText("LegendCount_" + key, b, "0", 14, fBold, ink, TextAnchor.MiddleRight, 0, -72, 22, 0f);
            SetTopLeft(cnt.rectTransform, x + 15, -72, col - 26, 22);
            legendCounts[key] = cnt;
        }

        CreateLine(b, -112, inner);

        // 숫자별 타일 3개
        CreateText("DigitTitle", b, "숫자별", 15, fSemiBold, ink, TextAnchor.MiddleLeft, Pad, -126, 20);
        float gap = 10f, tw = (inner - gap * 2) / 3f;
        for (int i = 0; i < digitKeys.Length; i++)
        {
            string key = digitKeys[i];
            RectTransform tile = CreateImage("Tile_" + key, b, tileSprite, tileBg).rectTransform;
            SetTopLeft(tile, Pad + (tw + gap) * i, -158, tw, 108);
            Stretch(CreateImage("Line", tile, tileLineSprite, tileLine).rectTransform);
            CreateText("Label", tile, $"숫자 {key}", 13, fMedium, ink3, TextAnchor.MiddleLeft, 14, -12, 18, 10f);
            tileCounts[key] = CreateText("Count", tile, "0", 34, fBold, ink, TextAnchor.MiddleLeft, 12, -34, 44, 10f);

            Image tb = CreateImage("BarBg", tile, barSprite, track);
            RectTransform tbr = tb.rectTransform;
            tbr.anchorMin = new Vector2(0, 0); tbr.anchorMax = new Vector2(1, 0); tbr.pivot = new Vector2(0.5f, 0);
            tbr.offsetMin = new Vector2(14, 0); tbr.offsetMax = new Vector2(-14, 0);
            tbr.sizeDelta = new Vector2(tbr.sizeDelta.x, 4); tbr.anchoredPosition = new Vector2(0, 16);
            Image fill = CreateImage("BarFill", tbr, barSprite, digitAccent);
            RectTransform fr = fill.rectTransform;
            fr.anchorMin = Vector2.zero; fr.anchorMax = new Vector2(0, 1); fr.offsetMin = fr.offsetMax = Vector2.zero;
            fill.gameObject.SetActive(false);
            tileBars[key] = fr;
            tileShown[key] = 0f;
        }

        // 화면 테두리 빛 (가장자리만 빛나는 액자)
        glowImage = CreateImage("EdgeGlow", root, glowSprite, new Color(1, 1, 1, 0));
        glowImage.fillCenter = false;
        Stretch(glowImage.rectTransform);

        RefreshStats();
    }

    // 유리 카드 하나: 그림자 → (둥근 틀 안) 흐린 화면 → 덮는 색 → 위쪽 빛 반사 → [내용] → 테두리
    // 돌려주는 값 = 내용을 붙일 곳 (둥근 틀)
    RectTransform CreateGlassCard(Transform root, string name, float top, float height)
    {
        RectTransform card = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        card.SetParent(root, false);
        card.anchorMin = card.anchorMax = new Vector2(1, 1);
        card.pivot = new Vector2(1, 1);
        card.sizeDelta = new Vector2(cardWidth, height);
        card.anchoredPosition = new Vector2(-margin, top);

        RectTransform sh = CreateImage("Shadow", card, shadowSprite, shadow).rectTransform;
        Stretch(sh);
        sh.offsetMin = new Vector2(-40, -54);   // 그림자는 아래로 조금 더
        sh.offsetMax = new Vector2(40, 26);

        Image maskImg = CreateImage("Glass", card, cardSprite, Color.white);
        Stretch(maskImg.rectTransform);
        maskImg.gameObject.AddComponent<Mask>().showMaskGraphic = false;

        RawImage blur = new GameObject("Blur", typeof(RectTransform)).AddComponent<RawImage>();
        blur.rectTransform.SetParent(maskImg.rectTransform, false);
        blur.raycastTarget = false;
        Stretch(blur.rectTransform);
        Image t = CreateImage("Tint", maskImg.rectTransform, null, tintNoBlur);
        Stretch(t.rectTransform);
        RectTransform s = CreateImage("Sheen", maskImg.rectTransform, sheenSprite, sheen).rectTransform;
        s.anchorMin = new Vector2(0, 1); s.anchorMax = new Vector2(1, 1); s.pivot = new Vector2(0.5f, 1);
        s.sizeDelta = new Vector2(0, Mathf.Min(140, height * 0.6f)); s.anchoredPosition = Vector2.zero;

        // 테두리: 내용은 둥근 틀 안에, 테두리는 틀 다음 형제로 붙어서 항상 내용 위에 그려짐 (둥근 가장자리 계단도 가려줌)
        Stretch(CreateImage("Edge", card, edgeSprite, edge).rectTransform);

        glasses.Add(new Glass { mask = maskImg.rectTransform, blur = blur, tint = t });
        return maskImg.rectTransform;
    }

    void CreateLine(RectTransform parent, float y, float width)
    {
        RectTransform line = CreateImage("Divider", parent, null, divider).rectTransform;
        SetTopLeft(line, Pad, y, width, 1);
    }

    // =========================================================
    // [UI 도우미 함수들]
    // =========================================================
    Image CreateImage(string name, Transform parent, Sprite sprite, Color color)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        Image img = obj.AddComponent<Image>();
        img.sprite = sprite;
        img.type = (sprite != null && sprite.border != Vector4.zero) ? Image.Type.Sliced : Image.Type.Simple;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    // 글자 - 부모 위쪽 기준 (x, y), 높이 height, 가로는 부모 폭(오른쪽 여백 rightMargin)
    Text CreateText(string name, Transform parent, string content, int size, Font font, Color color,
                    TextAnchor align, float x, float y, float height, float rightMargin = Pad)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        Text text = obj.AddComponent<Text>();
        text.font = font;
        text.text = content;
        text.fontSize = size;
        text.fontStyle = FontStyle.Normal;   // 굵기는 글꼴 파일로 (가짜 굵게 안 씀)
        text.color = color;
        text.alignment = align;
        text.supportRichText = true;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;

        RectTransform rt = text.rectTransform;
        rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0, 1);
        rt.offsetMin = new Vector2(x, 0); rt.offsetMax = new Vector2(-rightMargin, 0);
        rt.sizeDelta = new Vector2(rt.sizeDelta.x, height);
        rt.anchoredPosition = new Vector2(x, y);
        return text;
    }

    void SetTopLeft(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(x, y);
    }

    void SetTopRight(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(1, 1);
        rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(x, y);
    }

    void SetMiddleLeft(RectTransform rt, float x, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0, 0.5f); rt.pivot = new Vector2(0, 0.5f);
        rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(x, 0);
    }

    // 부모 높이를 꽉 채우고 좌우 여백만 줌 (알약 안 글자용)
    void FillHeight(RectTransform rt, float left, float right)
    {
        rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0, 0.5f);
        rt.offsetMin = new Vector2(left, 0); rt.offsetMax = new Vector2(-right, 0);
    }

    void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    // Pretendard(Resources/Fonts) → 없으면 맑은 고딕 → 없으면 유니티 기본 폰트
    void LoadFonts()
    {
        fRegular = Resources.Load<Font>("Fonts/Pretendard-Regular");
        fMedium = Resources.Load<Font>("Fonts/Pretendard-Medium");
        fSemiBold = Resources.Load<Font>("Fonts/Pretendard-SemiBold");
        fBold = Resources.Load<Font>("Fonts/Pretendard-Bold");
        if (fRegular && fMedium && fSemiBold && fBold) return;

        Debug.LogWarning("[대시보드] Pretendard를 못 찾아서 맑은 고딕을 씀");
        Font fallback = null;
        foreach (string installed in Font.GetOSInstalledFontNames())
            if (installed == "Malgun Gothic" || installed == "맑은 고딕") { fallback = Font.CreateDynamicFontFromOSFont(installed, 32); break; }
        if (fallback == null) fallback = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        fRegular = fMedium = fSemiBold = fBold = fallback;
    }

    // =========================================================
    // [이미지 직접 그리기] 그림 파일 없이 코드로 픽셀을 칠해서 스프라이트를 만듦
    // =========================================================
    void CreateSprites()
    {
        cardSprite = CreateRoundedSprite(96, 26, 0f, 0f);       // 유리 카드: 반지름 26
        edgeSprite = CreateRoundedSprite(96, 26, 1f, 0f);       // 유리 테두리 1px
        shadowSprite = CreateRoundedSprite(224, 66, 0f, 40f);   // 그림자: 가장자리가 40px에 걸쳐 흐려짐
        tileSprite = CreateRoundedSprite(64, 16, 0f, 0f);       // 타일: 반지름 16
        tileLineSprite = CreateRoundedSprite(64, 16, 1f, 0f);
        pillSprite = CreateRoundedSprite(40, 20, 0f, 0f);       // 알약 (양끝 반원)
        dotSprite = CreateRoundedSprite(32, 16, 0f, 0f);        // 동그라미
        barSprite = CreateRoundedSprite(16, 5, 0f, 0f);         // 막대 (양끝 둥금)
        ringSprite = CreateRoundedSprite(128, 64, 9f, 0f);      // 원형 게이지: 두께 9px 고리
        sheenSprite = CreateVerticalFadeSprite(64);             // 위쪽 빛 반사
        glowSprite = CreateGlowFrameSprite(128, 60);            // 화면 테두리 빛
    }

    // 둥근 사각형: ringWidth > 0 → 그 두께 고리만 / softness > 0 → 가장자리를 그만큼 흐림(그림자)
    Sprite CreateRoundedSprite(int size, int radius, float ringWidth, float softness)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        float half = size / 2f, r = radius, inset = softness;
        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                // 둥근 사각형까지의 거리 (음수 = 안쪽, 양수 = 바깥) — SDF 방식
                float qx = Mathf.Abs(px + 0.5f - half) - (half - inset - r);
                float qy = Mathf.Abs(py + 0.5f - half) - (half - inset - r);
                float outside = new Vector2(Mathf.Max(qx, 0), Mathf.Max(qy, 0)).magnitude;
                float inside = Mathf.Min(Mathf.Max(qx, qy), 0);
                float dist = outside + inside - r;

                float alpha;
                if (softness > 0f)
                {
                    float t = Mathf.Clamp01(0.5f - dist / (softness * 2f));
                    alpha = t * t * (3f - 2f * t);                                   // 부드러운 곡선
                }
                else
                {
                    alpha = Mathf.Clamp01(0.5f - dist);                              // 경계 1px만 부드럽게
                    if (ringWidth > 0f)
                        alpha = Mathf.Clamp01(alpha - Mathf.Clamp01(0.5f - (dist + ringWidth))); // 바깥 - 안쪽 = 고리
                }
                tex.SetPixel(px, py, new Color(1, 1, 1, alpha));
            }
        }
        tex.Apply();

        float b = Mathf.Min(r + inset + softness + 1, size / 2 - 1);
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                             SpriteMeshType.FullRect, new Vector4(b, b, b, b));
    }

    // 위가 가장 밝고 아래로 갈수록 0
    Sprite CreateVerticalFadeSprite(int size)
    {
        Texture2D tex = new Texture2D(4, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        for (int py = 0; py < size; py++)
        {
            float t = (float)py / (size - 1);
            for (int px = 0; px < 4; px++) tex.SetPixel(px, py, new Color(1, 1, 1, t * t * t));
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 4, size), new Vector2(0.5f, 0.5f), 100f);
    }

    // 화면 테두리 빛 - 가장자리가 가장 진하고 안쪽으로 갈수록 투명
    Sprite CreateGlowFrameSprite(int size, int glowWidth)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                int edgeDist = Mathf.Min(Mathf.Min(px, py), Mathf.Min(size - 1 - px, size - 1 - py));
                float t = Mathf.Clamp01(1f - (float)edgeDist / glowWidth);
                tex.SetPixel(px, py, new Color(1, 1, 1, t * t));
            }
        }
        tex.Apply();
        float b = size / 2 - 1;
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                             SpriteMeshType.FullRect, new Vector4(b, b, b, b));
    }
}
