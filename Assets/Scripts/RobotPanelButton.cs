using UnityEngine;

// =============================================================
// [로봇 조종 패널 버튼] 화면 아래 가운데([렌더 룩 콘솔] 버튼 오른쪽)에 "로봇 조종 패널" 칸
// 누르면 로봇팔 컨트롤 패널(Tools/so101_bridge/panel_start.bat)을 띄움 → 유니티 없이 exe에서 바로 실물 팔 연결
// 패널이 이미 켜져 있으면 창만 다시 열림 (panel_start.bat 동작). 장면에 넣지 않아도 플레이 시작 때 자동으로 붙음
// =============================================================
public class RobotPanelButton : MonoBehaviour
{
    GUIStyle label, small;
    string msg = ""; float msgUntil;

    static readonly Color Ink = new Color32(0xF2, 0xF5, 0xF8, 255), Muted = new Color32(0xAA, 0xB5, 0xC1, 255);
    static readonly Color Cyan = new Color32(0x5F, 0xD4, 0xE8, 255);
    static readonly Color Fill = new Color(.04f, .055f, .075f, .88f), HoverFill = new Color(.08f, .11f, .14f, .92f);
    static readonly Color Edge = new Color(1, 1, 1, .16f), HoverEdge = new Color(.37f, .83f, .91f, .6f);
    const float W = 168, H = 36, LookW = 196, Gap = 8;   // LookW = 왼쪽 렌더 룩 콘솔 버튼 너비 (1080p 기준)

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoAdd()
    {
        if (FindFirstObjectByType<RobotJointTester>() && !FindFirstObjectByType<RobotPanelButton>())
            new GameObject("RobotPanelButton").AddComponent<RobotPanelButton>();
    }

    // 프로젝트 폴더 쪽으로 올라가며 panel_start.bat을 찾아 실행 (에디터: Assets 위, exe: Builds/…/…_Data 위)
    void OpenPanel()
    {
        string bat = null;
        var dir = new System.IO.DirectoryInfo(Application.dataPath);
        for (int i = 0; i < 6 && dir != null && bat == null; i++, dir = dir.Parent)
        {
            string p = System.IO.Path.Combine(dir.FullName, "Tools", "so101_bridge", "panel_start.bat");
            if (System.IO.File.Exists(p)) bat = p;
        }
        if (bat == null) msg = "로봇 조종 패널(Tools/so101_bridge)을 못 찾았어요";
        else
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(bat) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(bat) });
                msg = "로봇 조종 패널 여는 중…";
            }
            catch (System.Exception e) { msg = "패널을 못 열었어요: " + e.Message; }
        }
        msgUntil = Time.unscaledTime + 4f;
    }

    static void Box(Rect r, Color c) { var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = old; }

    void OnGUI()
    {
        if (label == null)
        {
            Font semi = Resources.Load<Font>("Fonts/SUIT-SemiBold"), medium = Resources.Load<Font>("Fonts/SUIT-Medium") ?? semi;
            label = new GUIStyle(GUI.skin.label) { font = semi, fontSize = 13, alignment = TextAnchor.MiddleCenter, normal = { textColor = Ink } };
            small = new GUIStyle(GUI.skin.label) { font = medium, fontSize = 12, alignment = TextAnchor.MiddleCenter, normal = { textColor = Muted } };
        }
        float k = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2f);   // 렌더 룩 콘솔 버튼과 같은 크기 규칙
        GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1));
        float sw = Screen.width / k, sh = Screen.height / k;

        var r = new Rect(sw / 2 + LookW / 2 + Gap, sh - H - 12, W, H);
        bool hover = r.Contains(Event.current.mousePosition);
        Color edge = hover ? HoverEdge : Edge;
        Box(r, hover ? HoverFill : Fill);
        Box(new Rect(r.x, r.y, r.width, 1), edge); Box(new Rect(r.x, r.yMax - 1, r.width, 1), edge);
        Box(new Rect(r.x, r.y, 1, r.height), edge); Box(new Rect(r.xMax - 1, r.y, 1, r.height), edge);
        Box(new Rect(r.x, r.y, 2, r.height), Cyan);
        GUI.Label(r, "로봇 조종 패널", label);
        if (GUI.Button(r, GUIContent.none, GUIStyle.none)) OpenPanel();
        if (Time.unscaledTime < msgUntil) GUI.Label(new Rect(r.x - 60, r.y - 26, r.width + 120, 20), msg, small);
    }
}
