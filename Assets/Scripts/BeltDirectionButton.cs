using UnityEngine;

// =============================================================
// [벨트 방향 버튼] 화면 아래 가운데([렌더 룩 콘솔] 버튼 왼쪽)에 "벨트 ▶ 정방향 / ◀ 반대 방향" 칸
// 누르면 벨트가 물체를 나르는 방향(BeltMover)과 벨트 무늬가 흐르는 방향(BeltScroll)을 같이 뒤집음
// 장면에 BeltMover가 있으면 플레이 시작 때 자동으로 붙음 (씬 수정 없음)
// ※ 카메라 검사 결과로 생기는 뚜껑(LidMover)은 SpawnPoint → EndPoint로 따로 움직여서 이 버튼과 상관없음
// =============================================================
public class BeltDirectionButton : MonoBehaviour
{
    BeltMover belt;
    BeltScroll[] scrolls;
    bool reversed;
    GUIStyle label;

    static readonly Color Ink = new Color32(0xF2, 0xF5, 0xF8, 255);
    static readonly Color Cyan = new Color32(0x5F, 0xD4, 0xE8, 255);
    static readonly Color Fill = new Color(.04f, .055f, .075f, .88f), HoverFill = new Color(.08f, .11f, .14f, .92f);
    static readonly Color Edge = new Color(1, 1, 1, .16f), HoverEdge = new Color(.37f, .83f, .91f, .6f);
    const float W = 168, H = 36, LookW = 196, Gap = 8;   // LookW = 옆의 렌더 룩 콘솔 버튼 너비 (1080p 기준)

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoAdd()
    {
        if (FindFirstObjectByType<BeltMover>() && !FindFirstObjectByType<BeltDirectionButton>())
            new GameObject("BeltDirectionButton").AddComponent<BeltDirectionButton>();
    }

    void Start()
    {
        belt = FindFirstObjectByType<BeltMover>();
        scrolls = FindObjectsByType<BeltScroll>(FindObjectsSortMode.None);
    }

    // 방향 뒤집기: 나르는 방향과 무늬 방향을 함께
    void Flip()
    {
        if (!belt) return;
        reversed = !reversed;
        belt.localDirection = -belt.localDirection;
        foreach (var s in scrolls) if (s && s.beltMover == belt) s.reverse = !s.reverse;
        Debug.Log("[벨트] 방향 " + (reversed ? "반대" : "정방향"));
    }

    static void Box(Rect r, Color c) { var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = old; }

    void OnGUI()
    {
        if (!belt) return;
        if (label == null)
        {
            var font = Resources.Load<Font>("Fonts/SUIT-SemiBold");
            label = new GUIStyle(GUI.skin.label) { font = font, fontSize = 13, alignment = TextAnchor.MiddleCenter, normal = { textColor = Ink } };
        }
        float k = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2f);   // 렌더 룩 콘솔 버튼과 같은 크기 규칙
        GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1));
        float sw = Screen.width / k, sh = Screen.height / k;

        var r = new Rect(sw / 2 - LookW / 2 - Gap - W, sh - H - 12, W, H);
        bool hover = r.Contains(Event.current.mousePosition);
        Color edge = hover ? HoverEdge : Edge;
        Box(r, hover ? HoverFill : Fill);
        Box(new Rect(r.x, r.y, r.width, 1), edge); Box(new Rect(r.x, r.yMax - 1, r.width, 1), edge);
        Box(new Rect(r.x, r.y, 1, r.height), edge); Box(new Rect(r.xMax - 1, r.y, 1, r.height), edge);
        Box(new Rect(r.x, r.y, 2, r.height), Cyan);
        GUI.Label(r, reversed ? "벨트  ◀  반대 방향" : "벨트  ▶  정방향", label);
        if (GUI.Button(r, GUIContent.none, GUIStyle.none)) Flip();
    }
}
