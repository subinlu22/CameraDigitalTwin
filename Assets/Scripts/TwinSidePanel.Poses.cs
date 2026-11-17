using UnityEngine;
using UnityEngine.UI;

// TwinSidePanel 「장비 정보 > 로봇」의 탭 두 개: [관절 제어] (관절 표·수동 제어) / [자세]
// [자세] 탭
//   위: 자동 자세 [ㄱ자] [쉬는 자세] [처음] — 위로 들어 벨트를 피하는 길 (ArmPoseMover)
//   가운데: 그룹(동작 하나) ◀ 이름 ▶ [+] [×] — 그룹 = 자세 목록 하나, 재생은 고른 그룹만
//   아래: 고른 그룹의 자세 목록 (PoseLibrary) — 이름(눌러서 고치기) / [가기] / [×], [+ 저장]
//   그 아래: [▶ 순서대로 재생] [■ 정지] — 재생 중인 자세 줄은 파랗게
//   맨 아래: [● 녹화 / ■ 멈춤] [▶ 동작 재생 ½] [×] — 고른 그룹의 녹화 동작 (PoseLibrary)
public partial class TwinSidePanel
{
    const float RobotTabH = 44f;       // 탭 줄 높이 → 탭 내용이 이만큼 아래로
    const int PoseRows = 6;            // 화면에 보이는 자세 줄 수 (그룹마다 저장도 이만큼까지) — 아래 공통 버튼과 안 겹치게
    const float PoseRowY = 504f, PoseRowH = 30f;   // 녹화 줄 자리를 만들려고 34 → 30
    const float StatusShowSec = 4f;    // 방금 한 일(저장·삭제 등)을 상태 줄에 보여 주는 시간

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
    Button playButton, stopPlayButton, prevGroup, nextGroup, addGroup, delGroup;
    Button recordButton, playMotionButton, delMotionButton;
    Text recordText, playMotionText;
    InputField groupName;
    readonly Text[] poseNameText = new Text[PoseRows];

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
        Label(pane, "자동 자세 · 벨트 피해서 이동", Inset, 334, w, 24, 15, medium, Muted);
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

        // 그룹 줄: ◀ [이름] ▶ [+] [×]
        const float sq = 28, gap = 5;   // 네모 버튼을 줄여 그룹 이름 칸을 넓힘 (긴 이름 안 잘리게)
        float nameW = w - 4 * sq - 4 * gap;
        prevGroup = MakeButton(pane, "PrevGroup", "◀", CardFill, Border, () => { if (poses) poses.NextGroup(-1); }, out _, out _);
        Place(prevGroup.GetComponent<RectTransform>(), Inset, 424, sq, 32);
        groupName = MakeField(pane, Inset + sq + gap, 424, nameW, 32, 13, v => { if (poses) poses.RenameGroup(v); }, out _);
        nextGroup = MakeButton(pane, "NextGroup", "▶", CardFill, Border, () => { if (poses) poses.NextGroup(1); }, out _, out _);
        Place(nextGroup.GetComponent<RectTransform>(), Inset + sq + nameW + 2 * gap, 424, sq, 32);
        addGroup = MakeButton(pane, "AddGroup", "+", CardFill, Border, () => { if (poses) poses.AddGroup(); }, out _, out _, 18);
        Place(addGroup.GetComponent<RectTransform>(), Inset + 2 * sq + nameW + 3 * gap, 424, sq, 32);
        delGroup = MakeButton(pane, "DeleteGroup", "×", CardFill, Border, () => { if (poses) poses.DeleteGroupIfEmpty(); }, out _, out _, 17);
        Place(delGroup.GetComponent<RectTransform>(), Inset + w - sq, 424, sq, 32);

        Label(pane, "자세", Inset, 466, 50, 30, 17, semi, Ink);
        poseCount = Label(pane, "", Inset + 44, 469, 40, 26, 14, medium, Muted);
        saveButton = MakeButton(pane, "SavePose", "+ 지금 자세 저장", ActiveFill, Hex(0x6593D4, .76f),
            () => { if (poses) poses.SaveCurrent(); }, out _, out _, 14);
        PlaceRight(saveButton.GetComponent<RectTransform>(), Inset, 465, 128, 32);

        for (int i = 0; i < PoseRows; i++) BuildPoseRow(pane, i, w);
        float y = PoseRowY + PoseRows * PoseRowH;
        poseStatus = Label(pane, "", Inset, y, w, 22, 14, medium, Muted);
        float playW = (w - 8) * .62f;
        playButton = MakeButton(pane, "PlayPoses", "▶  순서대로 재생", ActiveFill, Hex(0x6593D4, .76f),
            () => { if (poses) poses.PlayAll(); }, out _, out _, 15);
        Place(playButton.GetComponent<RectTransform>(), Inset, y + 24, playW, 32);
        stopPlayButton = MakeButton(pane, "StopPoses", "■  정지", CardFill, Border,
            () => { if (poses) poses.StopPlay(); }, out _, out _, 15);
        Place(stopPlayButton.GetComponent<RectTransform>(), Inset + playW + 8, y + 24, w - playW - 8, 32);

        // 녹화 줄: [● 녹화 / ■ 멈춤] [▶ 동작 재생 ½] [×]
        const float delW = 32;
        float recW = (w - delW - 10) * .46f, motW = w - delW - 10 - recW;
        recordButton = MakeButton(pane, "RecordMotion", "●  녹화", CardFill, Border,
            () => { if (!poses) return; if (poses.Recording) poses.StopRecord(); else poses.StartRecord(); }, out recordText, out _, 14);
        Place(recordButton.GetComponent<RectTransform>(), Inset, y + 60, recW, 32);
        playMotionButton = MakeButton(pane, "PlayMotion", "▶  동작 재생 ½", ActiveFill, Hex(0x6593D4, .76f),
            () => { if (poses) poses.PlayMotion(); }, out playMotionText, out _, 14);
        Place(playMotionButton.GetComponent<RectTransform>(), Inset + recW + 5, y + 60, motW, 32);
        delMotionButton = MakeButton(pane, "DeleteMotion", "×", CardFill, Border,
            () => { if (poses) poses.DeleteMotion(); }, out _, out _, 17);
        Place(delMotionButton.GetComponent<RectTransform>(), Inset + w - delW, y + 60, delW, 32);
    }

    void BuildPoseRow(RectTransform pane, int i, float w)
    {
        int idx = i;
        var row = NewRect("Pose_" + i, pane);
        Place(row, Inset, PoseRowY + i * PoseRowH, w, PoseRowH - 3);
        poseRow[i] = row;
        Label(row, $"{i + 1}", 0, 0, 18, PoseRowH - 3, 15, semi, Muted);

        const float goW = 50, delW = 32, gap = 5;
        float nameW = w - 20 - goW - delW - 2 * gap;
        poseName[i] = MakeField(row, 20, 0, nameW, PoseRowH - 3, 15, v => { if (poses) poses.Rename(idx, v); }, out poseNameText[i]);

        poseGo[i] = MakeButton(row, "Go", "가기", ActiveFill, Hex(0x6593D4, .76f), () => { if (poses) poses.Go(idx); }, out _, out _, 14);
        Place(poseGo[i].GetComponent<RectTransform>(), 20 + nameW + gap, 0, goW, PoseRowH - 3);
        var del = MakeButton(row, "Delete", "×", CardFill, Border, () => { if (poses) poses.Delete(idx); }, out _, out _, 17);
        Place(del.GetComponent<RectTransform>(), w - delW, 0, delW, PoseRowH - 3);
    }

    // 눌러서 고치는 이름 칸 (어두운 둥근 바탕 + 글자)
    InputField MakeField(RectTransform parent, float x, float y, float w, float h, int size,
        UnityEngine.Events.UnityAction<string> onEnd, out Text text)
    {
        var bg = Img("Field", parent, cardSprite, new Color(0, 0, 0, .16f));
        bg.raycastTarget = true;
        Place(bg.rectTransform, x, y, w, h);
        text = Label(bg.rectTransform, "", 10, 0, w - 20, h, size, medium, Ink);
        var field = bg.gameObject.AddComponent<InputField>();
        field.textComponent = text;
        field.targetGraphic = bg;
        field.lineType = InputField.LineType.SingleLine;
        field.onEndEdit.AddListener(onEnd);
        return field;
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
            if (!groupName.isFocused) groupName.SetTextWithoutNotify(poses.Current.name);
        }

        bool canGo = poses.CanGo, playing = poses.Playing, rec = poses.Recording;
        foreach (var b in quickPose) b.interactable = canGo && !playing;
        for (int i = 0; i < PoseRows && i < count; i++)
        {
            poseGo[i].interactable = canGo && !playing;
            poseNameText[i].color = i == poses.PlayIndex ? BlueInk : Ink;   // 재생 중인 자세 강조
        }
        playButton.interactable = canGo && !playing && !rec && count > 0;
        prevGroup.interactable = nextGroup.interactable = addGroup.interactable = delGroup.interactable = !playing && !rec;
        stopPlayButton.interactable = playing || rec;
        recordButton.interactable = robot.Ready && (rec || !playing);
        recordText.text = rec ? $"■ 멈춤 {poses.RecordSec:F0}초" : "●  녹화";
        playMotionButton.interactable = canGo && !playing && !rec && poses.HasMotion;
        playMotionText.text = poses.HasMotion ? "▶ 동작 재생 ½" : "녹화 없음";
        delMotionButton.interactable = !playing && !rec && poses.HasMotion;
        saveButton.interactable = robot.Ready && count < PoseRows;
        // 상태 줄: 재생·녹화·이동 중이면 진행 상황, 방금 한 일은 몇 초만 보여 주고 → 그다음은 "다음에 할 일" 안내
        bool fresh = Time.time - poses.StatusTime < StatusShowSec && poses.Status != "";
        poseStatus.text = playing || rec ? poses.Status
            : robot.MovingPose ? robot.MoveStatus
            : fresh ? poses.Status
            : count >= PoseRows ? $"최대 {PoseRows}개 — 하나 지우고 저장"
            : !canGo ? $"가기·재생은 아래 [{(robot.Live ? "유니티로 조종" : "로봇 없이 연습")}]을 누른 뒤"
            : poses.HasMotion ? $"녹화한 동작 {poses.MotionSec:F1}초 · ▶ 누르면 절반 속도로 재생"
            : "● 녹화 → 팔 움직이기 → ■ 멈춤 = 동작 저장";
    }
}
