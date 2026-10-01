using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI; // Canvas, Image, Text 같은 UI 부품

// =============================================================
// [디지털트윈 대시보드] 오른쪽 세로 글래스 패널(오늘 통계) + 판정 순간 화면 테두리 빛(양품 초록 / 불량 빨강)
//
// - UI를 에디터에서 하나하나 배치하지 않고 이 스크립트가 플레이 시작할 때 코드로 전부 만듦
//   (카메라 프로그램 대시보드를 코드로 만들었던 것과 같은 방식 - 오브젝트에 붙이기만 하면 끝)
//   → 그래서 에디터 화면(플레이 전)에는 안 보이고, 플레이를 눌러야 나타남
// - 둥근 모서리/빛 번짐 같은 이미지도 그림 파일 없이 코드로 직접 그려서 씀
// - 통계는 DigitalTwinReceiver가 메시지를 받을 때마다 RegisterInspection()을 호출해서 쌓임
// =============================================================
public class DashboardUI : MonoBehaviour
{
    [Header("패널 모양")]
    public float panelWidth = 380f;                                  // 패널 가로 폭
    public Color glassColor = new Color(0.06f, 0.08f, 0.12f, 0.55f); // 유리판 색 (마지막 값이 투명도: 0=투명, 1=불투명)
    public Color outlineColor = new Color(1f, 1f, 1f, 0.22f);         // 유리판 가장자리 밝은 테두리

    [Header("테두리 빛")]
    public float flashFadeSeconds = 1.5f;                            // 번쩍한 뒤 완전히 사라질 때까지 걸리는 시간
    public Color goodColor = new Color(0.25f, 0.95f, 0.50f);          // 양품 = 초록
    public Color badColor = new Color(1.00f, 0.28f, 0.28f);           // 불량 = 빨강

    // 색 이름(영어, UDP로 받는 값) → 화면 표시용 한글 이름 / 강조색
    readonly string[] colorKeys = { "white", "blue", "red" };
    readonly Dictionary<string, string> colorLabels = new Dictionary<string, string>
    {
        { "white", "화이트" }, { "blue", "파랑" }, { "red", "빨강" },
    };
    readonly Dictionary<string, Color> colorAccents = new Dictionary<string, Color>
    {
        { "white", new Color(0.95f, 0.95f, 0.95f) },
        { "blue",  new Color(0.30f, 0.56f, 1.00f) },
        { "red",   new Color(1.00f, 0.38f, 0.32f) },
    };
    readonly string[] digitKeys = { "1", "2", "3" };
    readonly Color digitAccent = new Color(0.72f, 0.78f, 0.92f); // 숫자별 막대 색 (은은한 회청색)

    // 집계 값
    int totalCount = 0;
    readonly Dictionary<string, int> colorCounts = new Dictionary<string, int>();
    readonly Dictionary<string, int> digitCounts = new Dictionary<string, int>();

    // [신규] 양품/불량 집계 - 검사 기준이 바뀌면 0부터 다시 (기준이 바뀌면 다른 제품 묶음을 검사하는 거라)
    string currentTarget = null; // 지금 검사 기준 ("all" / "white" / "blue" / "red"), null = 아직 모름
    int goodCount = 0;
    int badCount = 0;

    // 화면 부품 (숫자 바꾸거나 막대 늘릴 때 쓰려고 기억해둠)
    Text totalText;
    Text criterionText, goodText, badText, yieldText; // [신규]
    Image criterionBadge;                             // [신규] 검사 기준 표시용 알약 모양 배지
    Text statusText;
    Image glowImage;
    readonly Dictionary<string, StatRow> rows = new Dictionary<string, StatRow>();

    // 통계 한 줄(점 + 이름 + 개수 + 막대)을 묶어서 기억하는 그릇
    class StatRow
    {
        public Text countText;
        public RectTransform barFill;
        public float shownRatio;  // 지금 화면에 보이는 막대 길이 (0~1)
        public float targetRatio; // 목표 막대 길이 - Update에서 부드럽게 따라감
    }

    Font uiFont;
    Sprite panelSprite, outlineSprite, barSprite, dotSprite, glowSprite, badgeSprite;

    // Awake()는 Start()보다도 먼저, 오브젝트가 깨어날 때 한 번 호출됨 → 다른 스크립트가 호출하기 전에 UI 준비 완료
    void Awake()
    {
        foreach (string key in colorKeys) colorCounts[key] = 0;
        foreach (string key in digitKeys) digitCounts[key] = 0;

        uiFont = LoadKoreanFont();
        CreateSprites();
        BuildUi();
    }

    // =========================================================
    // [신규 - 외부에서 호출] 검사 기준 반영 - 기준이 "바뀌었을 때만" 양품/불량을 0으로 되돌림
    // (같은 기준이 다시 와도 아무 일 없음 → 검사 결과마다 기준이 같이 와도 숫자가 안 날아감)
    // =========================================================
    public void SetCriterion(string target)
    {
        if (string.IsNullOrEmpty(target) || target == currentTarget) return;

        currentTarget = target;
        goodCount = 0;
        badCount = 0;

        // 배지 글자/색: 기준 색이면 그 색으로, 전체면 은은한 회색
        if (colorAccents.ContainsKey(target))
        {
            criterionText.text = colorLabels[target];
            criterionText.color = colorAccents[target];
            Color tint = colorAccents[target];
            tint.a = 0.22f; // 배지 바탕은 같은 색을 옅게
            criterionBadge.color = tint;
        }
        else
        {
            criterionText.text = "전체";
            criterionText.color = new Color(1, 1, 1, 0.85f);
            criterionBadge.color = new Color(1, 1, 1, 0.12f);
        }

        RefreshStats();
    }

    // =========================================================
    // [외부에서 호출] 판정 결과 1건 반영 - DigitalTwinReceiver가 메시지 받을 때마다 부름
    // =========================================================
    public void RegisterInspection(string color, string digit, bool isGood)
    {
        totalCount++;
        if (colorCounts.ContainsKey(color)) colorCounts[color]++;
        if (digitCounts.ContainsKey(digit)) digitCounts[digit]++;
        if (isGood) goodCount++; else badCount++; // [신규]

        RefreshStats();

        // 테두리 번쩍 - 색을 정하고 투명도를 1(완전 진하게)로 올리면, Update에서 서서히 사라짐
        Color flash = isGood ? goodColor : badColor;
        flash.a = 1f;
        glowImage.color = flash;

        statusText.text = $"<color=#5BE38A>●</color>  마지막 수신 {DateTime.Now:HH:mm:ss}";
    }

    // 매 프레임: 테두리 빛 서서히 빼기 + 막대 길이 부드럽게 따라가기
    void Update()
    {
        // [테두리 페이드] MoveTowards = 목표(0)를 향해 "최대 이만큼"씩만 줄임 → 일정한 속도로 사라짐
        Color c = glowImage.color;
        if (c.a > 0f)
        {
            c.a = Mathf.MoveTowards(c.a, 0f, Time.deltaTime / flashFadeSeconds);
            glowImage.color = c;
        }

        // [막대 애니메이션] Lerp = 현재값과 목표값 사이를 일정 비율씩 좁힘 → 처음엔 빠르고 끝에선 천천히 (부드러운 느낌)
        foreach (StatRow row in rows.Values)
        {
            row.shownRatio = Mathf.Lerp(row.shownRatio, row.targetRatio, Time.deltaTime * 8f);
            SetBarRatio(row);
        }
    }

    // 개수 글자 갱신 + 막대 목표 길이 계산 (전체 대비 비율)
    void RefreshStats()
    {
        totalText.text = totalCount.ToString();

        // [신규] 양품/불량/양품률 - 아직 하나도 없으면 양품률은 "-"
        goodText.text = goodCount.ToString();
        badText.text = badCount.ToString();
        int judged = goodCount + badCount;
        yieldText.text = judged > 0 ? $"{100f * goodCount / judged:F1}%" : "-"; // F1 = 소수점 첫째 자리까지

        foreach (string key in colorKeys) UpdateRow("color_" + key, colorCounts[key]);
        foreach (string key in digitKeys) UpdateRow("digit_" + key, digitCounts[key]);
    }

    void UpdateRow(string rowKey, int count)
    {
        StatRow row = rows[rowKey];
        row.countText.text = count.ToString();
        row.targetRatio = totalCount > 0 ? (float)count / totalCount : 0f;
    }

    // 막대 길이 = 막대 칸의 오른쪽 끝 위치(anchorMax.x)를 0~1 사이로 조절
    void SetBarRatio(StatRow row)
    {
        Vector2 max = row.barFill.anchorMax;
        max.x = row.shownRatio;
        row.barFill.anchorMax = max;
        row.barFill.gameObject.SetActive(row.shownRatio > 0.01f); // 0일 땐 숨김 (둥근 모서리가 뭉개져 보이는 것 방지)
    }

    // =========================================================
    // [UI 만들기]
    // =========================================================
    void BuildUi()
    {
        // [캔버스] UI를 그리는 도화지 - Screen Space Overlay = 3D 화면 맨 위에 항상 덮어서 그림
        GameObject canvasObj = new GameObject("DashboardCanvas");
        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;

        // [화면 크기 대응] 1920x1080 기준으로 디자인 → 창 크기가 바뀌어도 비율대로 늘었다 줄었다 함
        CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f; // 세로 길이 기준으로 맞춤 (세로 패널이라)

        // [유리 패널] 오른쪽에 위아래로 길게 붙임 (여백 40)
        RectTransform panel = CreateImage("GlassPanel", canvasObj.transform, panelSprite, glassColor).rectTransform;
        panel.anchorMin = new Vector2(1, 0);
        panel.anchorMax = new Vector2(1, 1);
        panel.pivot = new Vector2(1, 0.5f);
        panel.sizeDelta = new Vector2(panelWidth, -80);   // 세로는 화면 높이에서 80(위아래 여백) 뺀 만큼
        panel.anchoredPosition = new Vector2(-40, 0);

        // 패널 가장자리 밝은 테두리 (같은 크기로 위에 겹침)
        RectTransform outline = CreateImage("Outline", panel, outlineSprite, outlineColor).rectTransform;
        Stretch(outline);

        // [제목]
        CreateText("Title", panel, "오늘 통계", 32, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft, 28, -32, 36);
        DateTime now = DateTime.Now;
        CreateText("Date", panel, $"{now.Month}월 {now.Day}일  ·  실시간 검사 현황", 17, FontStyle.Normal,
            new Color(1, 1, 1, 0.55f), TextAnchor.MiddleLeft, 28, -72, 24);
        CreateDivider(panel, -110);

        // [총 검사 수]
        CreateText("TotalLabel", panel, "총 검사", 17, FontStyle.Normal, new Color(1, 1, 1, 0.55f), TextAnchor.MiddleLeft, 28, -130, 24);
        totalText = CreateText("TotalValue", panel, "0", 64, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft, 28, -156, 76);
        CreateDivider(panel, -252);

        // [신규 - 검사 기준] 왼쪽 글자 + 오른쪽 알약 배지 (카메라 프로그램에서 기준 버튼 누르면 바뀜)
        CreateText("CriterionLabel", panel, "검사 기준", 17, FontStyle.Normal, new Color(1, 1, 1, 0.55f), TextAnchor.MiddleLeft, 28, -272, 30);
        criterionBadge = CreateImage("CriterionBadge", panel, badgeSprite, new Color(1, 1, 1, 0.12f));
        SetTopRight(criterionBadge.rectTransform, -28, -272, 96, 30);
        criterionText = CreateText("CriterionText", criterionBadge.rectTransform, "전체", 17, FontStyle.Bold,
            new Color(1, 1, 1, 0.85f), TextAnchor.MiddleCenter, 0, 0, 30, 0f);

        // [신규 - 양품 / 불량 / 양품률] 가로로 3칸
        CreateMiniStat(panel, "양품", 28, goodColor, out goodText);
        CreateMiniStat(panel, "불량", 28 + 112, badColor, out badText);
        CreateMiniStat(panel, "양품률", 28 + 224, Color.white, out yieldText);
        CreateDivider(panel, -392);

        // [색상별]
        CreateText("ColorLabel", panel, "색상별", 17, FontStyle.Normal, new Color(1, 1, 1, 0.55f), TextAnchor.MiddleLeft, 28, -412, 24);
        float y = -448;
        foreach (string key in colorKeys)
        {
            rows["color_" + key] = CreateStatRow(panel, y, colorLabels[key], colorAccents[key]);
            y -= 60;
        }
        CreateDivider(panel, y - 8);

        // [숫자별]
        CreateText("DigitLabel", panel, "숫자별", 17, FontStyle.Normal, new Color(1, 1, 1, 0.55f), TextAnchor.MiddleLeft, 28, y - 28, 24);
        y -= 64;
        foreach (string key in digitKeys)
        {
            rows["digit_" + key] = CreateStatRow(panel, y, $"숫자 {key}", digitAccent);
            y -= 60;
        }

        // [하단 상태줄] 패널 맨 아래에 붙임
        statusText = CreateText("Status", panel, "<color=#8A93A6>●</color>  수신 대기 중 (UDP)", 16, FontStyle.Normal,
            new Color(1, 1, 1, 0.6f), TextAnchor.MiddleLeft, 28, 0, 24);
        RectTransform statusRt = statusText.rectTransform;
        statusRt.anchorMin = new Vector2(0, 0);
        statusRt.anchorMax = new Vector2(1, 0);
        statusRt.pivot = new Vector2(0, 0);
        statusRt.anchoredPosition = new Vector2(28, 24);

        // [화면 테두리 빛] 화면 전체를 덮는 액자 모양 이미지 - 가장자리만 빛나고 가운데는 비어 있음
        glowImage = CreateImage("EdgeGlow", canvasObj.transform, glowSprite, new Color(1, 1, 1, 0));
        glowImage.fillCenter = false; // 가운데는 아예 안 그림
        Stretch(glowImage.rectTransform);

        RefreshStats(); // [신규] 양품률 "-" 등 처음 글자 채우기
    }

    // 통계 한 줄 만들기: ● 이름 ............ 개수
    //                   ▬▬▬▬▬▬▬▬ (비율 막대)
    StatRow CreateStatRow(RectTransform panel, float y, string label, Color accent)
    {
        GameObject rowObj = new GameObject("Row_" + label, typeof(RectTransform));
        RectTransform rowRt = rowObj.GetComponent<RectTransform>();
        rowRt.SetParent(panel, false);
        rowRt.anchorMin = new Vector2(0, 1);
        rowRt.anchorMax = new Vector2(1, 1);
        rowRt.pivot = new Vector2(0.5f, 1);
        rowRt.offsetMin = new Vector2(28, 0);   // 왼쪽 여백 28
        rowRt.offsetMax = new Vector2(-28, 0);  // 오른쪽 여백 28
        rowRt.sizeDelta = new Vector2(rowRt.sizeDelta.x, 52);
        rowRt.anchoredPosition = new Vector2(0, y);

        // 색 점
        RectTransform dot = CreateImage("Dot", rowRt, dotSprite, accent).rectTransform;
        SetTopLeft(dot, 0, -9, 14, 14);

        // 이름 (왼쪽) / 개수 (오른쪽)
        // (줄 자체에 이미 좌우 여백이 있어서 글자의 오른쪽 여백은 0 → 개수 숫자가 막대 끝과 딱 맞음)
        CreateText("Label", rowRt, label, 21, FontStyle.Normal, new Color(1, 1, 1, 0.9f), TextAnchor.MiddleLeft, 26, 0, 32, 0f);
        Text count = CreateText("Count", rowRt, "0", 24, FontStyle.Bold, Color.white, TextAnchor.MiddleRight, 0, 0, 32, 0f);

        // 막대 바탕 (희미한 전체 길이)
        RectTransform barBg = CreateImage("BarBg", rowRt, barSprite, new Color(1, 1, 1, 0.08f)).rectTransform;
        barBg.anchorMin = new Vector2(0, 1);
        barBg.anchorMax = new Vector2(1, 1);
        barBg.pivot = new Vector2(0, 1);
        barBg.offsetMin = Vector2.zero;
        barBg.offsetMax = Vector2.zero;
        barBg.sizeDelta = new Vector2(0, 6);
        barBg.anchoredPosition = new Vector2(0, -40);

        // 막대 채움 (비율만큼) - 처음엔 길이 0
        RectTransform barFill = CreateImage("BarFill", barBg, barSprite, accent).rectTransform;
        barFill.anchorMin = new Vector2(0, 0);
        barFill.anchorMax = new Vector2(0, 1);
        barFill.offsetMin = Vector2.zero;
        barFill.offsetMax = Vector2.zero;
        barFill.gameObject.SetActive(false);

        return new StatRow { countText = count, barFill = barFill };
    }

    // =========================================================
    // [UI 도우미 함수들]
    // =========================================================
    Image CreateImage(string name, Transform parent, Sprite sprite, Color color)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform));
        obj.transform.SetParent(parent, false); // false = 부모 기준 위치로 붙임 (UI는 항상 false)
        Image img = obj.AddComponent<Image>();
        img.sprite = sprite;
        img.type = (sprite != null && sprite.border != Vector4.zero) ? Image.Type.Sliced : Image.Type.Simple; // Sliced = 모서리는 그대로 두고 가운데만 늘림 (둥근 모서리가 안 찌그러짐)
        img.color = color;
        img.raycastTarget = false; // 마우스 클릭 받을 일 없음
        return img;
    }

    // 글자 만들기 - 부모 위쪽 기준으로 (x, y) 위치에 높이 height만큼, 가로는 부모 폭에 맞춰 늘림
    Text CreateText(string name, Transform parent, string content, int size, FontStyle style, Color color,
                    TextAnchor align, float x, float y, float height, float rightMargin = 28f)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        Text text = obj.AddComponent<Text>();
        text.font = uiFont;
        text.text = content;
        text.fontSize = size;
        text.fontStyle = style;
        text.color = color;
        text.alignment = align;
        text.supportRichText = true;                       // <color=...> 같은 태그로 글자 일부만 색 바꾸기 가능
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;

        RectTransform rt = text.rectTransform;
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(0, 1);
        rt.offsetMin = new Vector2(x, 0);
        rt.offsetMax = new Vector2(-rightMargin, 0); // 오른쪽 여백 (패널 글자는 28, 통계 줄 안 글자는 0)
        rt.sizeDelta = new Vector2(rt.sizeDelta.x, height);
        rt.anchoredPosition = new Vector2(x, y);
        return text;
    }

    // [신규] 작은 통계 칸: 위에 작은 이름, 아래에 큰 숫자 (양품/불량/양품률용)
    void CreateMiniStat(RectTransform panel, string label, float x, Color valueColor, out Text valueText)
    {
        CreateText("Mini_" + label, panel, label, 16, FontStyle.Normal, new Color(1, 1, 1, 0.55f), TextAnchor.MiddleLeft, x, -316, 22);
        valueText = CreateText("MiniValue_" + label, panel, "0", 30, FontStyle.Bold, valueColor, TextAnchor.MiddleLeft, x, -340, 40);
    }

    // [신규] 부모 오른쪽 위 기준으로 고정 크기 배치 (x는 오른쪽 끝에서 얼마나 안쪽인지, 음수)
    void SetTopRight(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = new Vector2(1, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(1, 1);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, y);
    }

    // 얇은 구분선
    void CreateDivider(RectTransform panel, float y)
    {
        RectTransform line = CreateImage("Divider", panel, null, new Color(1, 1, 1, 0.12f)).rectTransform;
        line.anchorMin = new Vector2(0, 1);
        line.anchorMax = new Vector2(1, 1);
        line.pivot = new Vector2(0.5f, 1);
        line.offsetMin = new Vector2(28, 0);
        line.offsetMax = new Vector2(-28, 0);
        line.sizeDelta = new Vector2(line.sizeDelta.x, 1);
        line.anchoredPosition = new Vector2(0, y);
    }

    // 부모를 꽉 채우게
    void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    // 부모 왼쪽 위 기준으로 고정 크기 배치
    void SetTopLeft(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, y);
    }

    // 맑은 고딕(윈도우 기본 한글 폰트)을 씀 - 없으면 유니티 기본 폰트로 대체
    Font LoadKoreanFont()
    {
        foreach (string installed in Font.GetOSInstalledFontNames())
        {
            if (installed == "Malgun Gothic" || installed == "맑은 고딕")
            {
                return Font.CreateDynamicFontFromOSFont(installed, 32);
            }
        }
        Debug.LogWarning("[대시보드] 맑은 고딕을 못 찾아서 기본 폰트를 씀 (한글이 깨지면 알려줘)");
        return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    // =========================================================
    // [이미지 직접 그리기] 그림 파일 없이 코드로 픽셀을 칠해서 스프라이트(UI용 이미지)를 만듦
    // =========================================================
    void CreateSprites()
    {
        panelSprite = CreateRoundedSprite(96, 28, 0f);    // 유리판: 모서리 반지름 28, 꽉 채움
        outlineSprite = CreateRoundedSprite(96, 28, 1.5f); // 유리판 테두리: 같은 모양의 두께 1.5px 고리
        barSprite = CreateRoundedSprite(16, 3, 0f);       // 막대: 살짝 둥근 얇은 막대
        dotSprite = CreateRoundedSprite(32, 16, 0f);      // 점: 반지름 = 절반 → 동그라미
        glowSprite = CreateGlowFrameSprite(128, 60);      // 화면 테두리 빛
        badgeSprite = CreateRoundedSprite(32, 15, 0f);    // [신규] 검사 기준 배지: 양끝이 둥근 알약 모양
    }

    // 둥근 사각형 그리기
    // ringWidth = 0 이면 꽉 찬 도형, 0보다 크면 그 두께의 테두리(고리)만
    Sprite CreateRoundedSprite(int size, int radius, float ringWidth)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        float half = size / 2f;
        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                // [둥근 사각형까지의 거리] 음수 = 도형 안쪽, 양수 = 바깥쪽 (SDF라는 방식)
                float qx = Mathf.Abs(px + 0.5f - half) - (half - radius);
                float qy = Mathf.Abs(py + 0.5f - half) - (half - radius);
                float outside = new Vector2(Mathf.Max(qx, 0), Mathf.Max(qy, 0)).magnitude;
                float inside = Mathf.Min(Mathf.Max(qx, qy), 0);
                float dist = outside + inside - radius;

                // 경계에서 1px 정도 부드럽게 흐려서 계단 현상(깍두기) 없앰
                float alpha = Mathf.Clamp01(0.5f - dist);
                if (ringWidth > 0f)
                {
                    float innerAlpha = Mathf.Clamp01(0.5f - (dist + ringWidth));
                    alpha = Mathf.Clamp01(alpha - innerAlpha); // 바깥 도형에서 안쪽 도형을 빼면 고리만 남음
                }
                tex.SetPixel(px, py, new Color(1, 1, 1, alpha)); // 흰색으로 그려두고 색은 Image.color로 입힘
            }
        }
        tex.Apply();

        // border = 늘어나지 않을 모서리 영역 (Sliced 방식용) - 모서리 반지름보다 조금 크게
        float b = Mathf.Min(radius + 1, size / 2 - 1);
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                             SpriteMeshType.FullRect, new Vector4(b, b, b, b));
    }

    // 화면 테두리 빛 그리기 - 가장자리에서 가장 진하고 안쪽으로 갈수록 투명해지는 액자
    Sprite CreateGlowFrameSprite(int size, int glowWidth)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                // 가장 가까운 가장자리까지의 거리
                int edgeDist = Mathf.Min(Mathf.Min(px, py), Mathf.Min(size - 1 - px, size - 1 - py));
                float t = Mathf.Clamp01(1f - (float)edgeDist / glowWidth);
                float alpha = t * t; // 제곱 = 가장자리 쪽에 빛이 몰리고 안쪽은 금방 옅어짐 (자연스러운 번짐)
                tex.SetPixel(px, py, new Color(1, 1, 1, alpha));
            }
        }
        tex.Apply();

        float b = size / 2 - 1;
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                             SpriteMeshType.FullRect, new Vector4(b, b, b, b));
    }
}
