using UnityEngine;
using UnityEngine.InputSystem;

// =============================================================
// [렌더 룩 콘솔 버튼] 화면 아래 가운데 작은 칸. 누르거나 F6 → 따로 뜨는 렌더 룩 콘솔 창을 띄움 (빌드 exe에서도)
// (11/19) 예전 F6 간단 창(◀ ▶로 룩·재질 고르기)은 콘솔과 겹치고 결과도 달라서 뺌 → 조절은 전부 콘솔에서
// =============================================================
namespace RenderLook
{
    public class RenderLookPanel : MonoBehaviour
    {
        const Key ToggleKey = Key.F6;

        RenderLookCore core;
        float statusUntil; string lastMsg = "";
        GUIStyle label, small, dim;

        void Start()
        {
            core = GetComponent<RenderLookCore>();
            core.Changed += () => { if (core.Message != lastMsg) { lastMsg = core.Message; statusUntil = Time.unscaledTime + 4f; } };
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb[ToggleKey].wasPressedThisFrame) OpenConsole();
        }

        // ================================================================ 화면 (IMGUI, 아래 가운데)
        // 로봇팔 패널과 같은 디지털 트윈 스타일: 어두운 반투명 유리 + 얇은 테두리 + 청록 포인트 하나, 글꼴 SUIT(없으면 기본)
        static readonly Color Ink = new Color32(0xF2, 0xF5, 0xF8, 255), Muted = new Color32(0xAA, 0xB5, 0xC1, 255);
        static readonly Color Blue = new Color32(0x5F, 0xD4, 0xE8, 255);
        static readonly Color Fill = new Color(.04f, .055f, .075f, .88f);
        static readonly Color Edge = new Color(1, 1, 1, .16f), ActiveEdge = new Color(.37f, .83f, .91f, .6f);
        const float OpenW = 196, OpenH = 36;   // 버튼 크기 (1080p 기준)

        Font Load(string name) => Resources.Load<Font>("Fonts/" + name);

        void Styles()
        {
            if (label != null) return;
            Font semi = Load("SUIT-SemiBold"), medium = Load("SUIT-Medium") ?? semi;
            label = new GUIStyle(GUI.skin.label) { font = semi, fontSize = 13, normal = { textColor = Ink }, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            small = new GUIStyle(GUI.skin.label) { font = medium, fontSize = 12, normal = { textColor = Muted }, wordWrap = true };
            dim = new GUIStyle(GUI.skin.label) { font = semi, fontSize = 13, alignment = TextAnchor.MiddleCenter, normal = { textColor = Muted }, clipping = TextClipping.Clip };
        }

        static void Box(Rect r, Color c) { var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = old; }

        // 칸 + 1px 테두리
        static void Frame(Rect r, Color fill, Color edge)
        {
            Box(r, fill);
            Box(new Rect(r.x, r.y, r.width, 1), edge); Box(new Rect(r.x, r.yMax - 1, r.width, 1), edge);
            Box(new Rect(r.x, r.y, 1, r.height), edge); Box(new Rect(r.xMax - 1, r.y, 1, r.height), edge);
        }

        void OnGUI()
        {
            if (core == null) return;
            Styles();
            float k = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1));
            float h = Screen.height / k;

            // 화면 아래 가운데 (양옆 패널과 안 겹치게): 유리 칸 + 왼쪽 청록 줄 + 오른쪽 F6 키 표시 → 누르면 렌더 룩 콘솔 창을 띄움
            var r = new Rect(Screen.width / k / 2 - OpenW / 2, h - OpenH - 12, OpenW, OpenH);
            bool hover = r.Contains(Event.current.mousePosition);
            Frame(r, hover ? new Color(.08f, .11f, .14f, .92f) : Fill, hover ? ActiveEdge : Edge);
            Box(new Rect(r.x, r.y, 2, r.height), Blue);
            GUI.Label(new Rect(r.x + 16, r.y, r.width - 70, r.height), "렌더 룩 콘솔", label);
            var key = new Rect(r.xMax - 50, r.y + 8, 38, r.height - 16);
            Frame(key, Color.clear, new Color(1, 1, 1, .45f));
            GUI.Label(key, "F6", dim);
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) OpenConsole();
            if (Time.unscaledTime < statusUntil)
                GUI.Label(new Rect(r.x - 120, r.y - 26, r.width + 240, 20), lastMsg, new GUIStyle(small) { alignment = TextAnchor.MiddleCenter });
        }

        // 렌더 룩 콘솔(Tools/render_console/start.bat)을 띄움. 에디터·빌드 exe 모두 프로젝트 폴더 쪽으로 올라가며 찾음
        void OpenConsole()
        {
            string bat = null;
            var dir = new System.IO.DirectoryInfo(Application.dataPath);
            for (int i = 0; i < 6 && dir != null && bat == null; i++, dir = dir.Parent)
            {
                string p = System.IO.Path.Combine(dir.FullName, "Tools", "render_console", "start.bat");
                if (System.IO.File.Exists(p)) bat = p;
            }
            if (bat == null) { lastMsg = "렌더 룩 콘솔(Tools/render_console)을 못 찾았어요"; statusUntil = Time.unscaledTime + 4f; return; }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(bat) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(bat) });
                lastMsg = "렌더 룩 콘솔 여는 중…";
            }
            catch (System.Exception e) { lastMsg = "콘솔을 못 열었어요: " + e.Message; }
            statusUntil = Time.unscaledTime + 4f;
        }
    }
}
