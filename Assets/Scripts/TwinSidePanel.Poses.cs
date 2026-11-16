using UnityEngine;
using UnityEngine.UI;

// TwinSidePanel 「장비 정보 > 로봇」의 탭 두 개: [관절 제어] (관절 표·수동 제어) / [자세]
// [자세] 탭
//   위: 자동 자세 [ㄱ자] [쉬는 자세] [처음] — 어깨 45° 돌려 벨트를 피하는 순서 (ArmPoseMover)
//   아래: 저장한 자세 목록 (PoseLibrary) — 이름(눌러서 고치기) / [가기] / [×], [+ 저장]
public partial class TwinSidePanel
{
    const float RobotTabH = 44f;       // 탭 줄 높이 → 탭 내용이 이만큼 아래로
    const int PoseRows = 8;            // 화면에 보이는 자세 줄 수 (저장도 이만큼까지)
    const float PoseRowY = 462f, PoseRowH = 36f;

    RectTransform jointPage, posePage;
    Image jointTabFill, poseTabFill;
    Text jointTabText, poseTabText;
    PoseLibrary poses;
    int poseVersion = -1;
    readonly RectTransform[] poseRow = new RectTransform[PoseRows];
    readonly InputField[] poseName = new InputField[PoseRows];
    readonly Button[] poseGo = new Button[PoseRows];
    readonly Button[] quickPose = new Button[3];
    Button saveButton;
    Text poseCount, poseStatus;

    void BuildRobotTabs(RectTransform root)
    {
        float half = (detailContent - 7) / 2f;
        var a = MakeButton(root, "JointTab", "관절 제어", ActiveFill, Border, () => ShowRobotTab(false), out jointTabText, out jointTabFill, 15);
        Place(a.GetComponent<RectTransform>(), Inset, 330, half, 34);
        var b = MakeButton(root, "PoseTab", "자세", CardFill, Border, () => ShowRobotTab(true), out poseTabText, out poseTabFill, 15);
        Place(b.GetComponent<RectTransform>(), Inset + half + 7, 330, half, 34);
        jointPage = NewRect("JointPage", root);
        posePage = NewRect("PosePage", root);
        foreach (var page in new[] { jointPage, posePage })
        {
            Stretch(page, 0);
            page.offsetMax = new Vector2(0, -RobotTabH);   // 안쪽 요소는 위 기준이라 전부 탭 높이만큼 내려감
        }
        ShowRobotTab(false);
    }

    void ShowRobotTab(bool pose)
    {
        jointPage.gameObject.SetActive(!pose);
        posePage.gameObject.SetActive(pose);
        // 고른 탭 = 파란 바탕·파란 글씨, 안 고른 탭 = 흐리게
        jointTabFill.color = pose ? Color.clear : Hex(0x23528C, .85f);
        poseTabFill.color = pose ? Hex(0x23528C, .85f) : Color.clear;
        jointTabText.color = pose ? Muted : BlueInk;
        poseTabText.color = pose ? BlueInk : Muted;
        dragging = false;
    }

    void BuildPosePage(RectTransform pane)
    {
        float w = detailContent;
        Label(pane, "자동 자세 · 어깨 45° 돌려 벨트 피함", Inset, 334, w, 24, 15, medium, Muted);
        string[] names = { "ㄱ자", "쉬는 자세", "처음" };
        UnityEngine.Events.UnityAction[] acts = { () => robot.GoLPose(), () => robot.GoRest(), () => robot.ResetAll() };
        float bw = (w - 14) / 3f;
        for (int i = 0; i < 3; i++)
        {
            var act = acts[i];
            quickPose[i] = MakeButton(pane, "Quick_" + i, names[i], CardFill, Border, () => { if (poses && poses.CanGo) act(); }, out _, out _);
            Place(quickPose[i].GetComponent<RectTransform>(), Inset + i * (bw + 7), 364, bw, 38);
        }
        Rule(pane, Inset, 414, w);

        Label(pane, "저장한 자세", Inset, 424, 110, 30, 18, semi, Ink);
        poseCount = Label(pane, "", Inset + 104, 427, 40, 26, 14, medium, Muted);
        saveButton = MakeButton(pane, "SavePose", "+ 지금 자세 저장", ActiveFill, Hex(0x6593D4, .76f),
            () => { if (poses) poses.SaveCurrent(); }, out _, out _, 14);
        PlaceRight(saveButton.GetComponent<RectTransform>(), Inset, 422, 128, 32);

        for (int i = 0; i < PoseRows; i++) BuildPoseRow(pane, i, w);
        poseStatus = Label(pane, "", Inset, PoseRowY + PoseRows * PoseRowH + 2, w, 22, 14, medium, Muted);
    }

    void BuildPoseRow(RectTransform pane, int i, float w)
    {
        int idx = i;
        var row = NewRect("Pose_" + i, pane);
        Place(row, Inset, PoseRowY + i * PoseRowH, w, PoseRowH - 4);
        poseRow[i] = row;
        Label(row, $"{i + 1}", 0, 0, 18, 32, 15, semi, Muted);

        const float goW = 50, delW = 32, gap = 5;
        float nameW = w - 20 - goW - delW - 2 * gap;
        var nameBg = Img("Name", row, cardSprite, new Color(0, 0, 0, .16f));
        nameBg.raycastTarget = true;
        Place(nameBg.rectTransform, 20, 0, nameW, 32);
        var nameText = Label(nameBg.rectTransform, "", 10, 0, nameW - 20, 32, 15, medium, Ink);
        var field = nameBg.gameObject.AddComponent<InputField>();
        field.textComponent = nameText;
        field.targetGraphic = nameBg;
        field.lineType = InputField.LineType.SingleLine;
        field.onEndEdit.AddListener(v => { if (poses) poses.Rename(idx, v); });
        poseName[i] = field;

        poseGo[i] = MakeButton(row, "Go", "가기", ActiveFill, Hex(0x6593D4, .76f), () => { if (poses) poses.Go(idx); }, out _, out _, 14);
        Place(poseGo[i].GetComponent<RectTransform>(), 20 + nameW + gap, 0, goW, 32);
        var del = MakeButton(row, "Delete", "×", CardFill, Border, () => { if (poses) poses.Delete(idx); }, out _, out _, 17);
        Place(del.GetComponent<RectTransform>(), w - delW, 0, delW, 32);
    }

    void UpdatePoses()
    {
        if (!poses && robot)
        {
            poses = robot.GetComponent<PoseLibrary>();
            if (!poses) poses = robot.gameObject.AddComponent<PoseLibrary>();
        }
        if (!poses || !posePage || !posePage.gameObject.activeInHierarchy) return;

        int count = poses.Poses.Count;
        if (poseVersion != poses.Version)
        {
            poseVersion = poses.Version;
            for (int i = 0; i < PoseRows; i++)
            {
                bool has = i < count;
                poseRow[i].gameObject.SetActive(has);
                if (has && !poseName[i].isFocused) poseName[i].SetTextWithoutNotify(poses.Poses[i].name);
            }
            poseCount.text = $"{count}개";
        }

        bool canGo = poses.CanGo;
        foreach (var b in quickPose) b.interactable = canGo;
        for (int i = 0; i < PoseRows && i < count; i++) poseGo[i].interactable = canGo;
        saveButton.interactable = robot.Ready && count < PoseRows;
        poseStatus.text = robot.MovingPose ? robot.MoveStatus
            : count >= PoseRows ? $"최대 {PoseRows}개 — 지우고 저장"
            : !canGo ? "가기·자동 자세는 [유니티로 조종] 후" : poses.Status;
    }
}
