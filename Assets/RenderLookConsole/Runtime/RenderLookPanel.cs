using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// =============================================================
// [F6 간단 창] 콘솔 없이 플레이 화면에서 바로 고르는 작은 창 (빌드 exe에서도)
// 자세한 조절(슬라이더·저장)은 따로 뜨는 렌더 룩 콘솔에서. 여기선 ◀ ▶로 고르기만
// 보여 주는 부위: 이름표 부위(parts/<씬>.json). 이름표가 없으면 화면에 보이는 자동 부위 위에서 5개
// =============================================================
namespace RenderLook
{
    public class RenderLookPanel : MonoBehaviour
    {
        const Key ToggleKey = Key.F6;
        const int MaxRows = 6;

        RenderLookCore core;
        bool open;
        bool keepTextures;
        float statusUntil; string lastMsg = "";
        GUIStyle title, label, small, button, nameBox, dim;

        void Start()
        {
            core = GetComponent<RenderLookCore>();
            core.Changed += () => { if (core.Message != lastMsg) { lastMsg = core.Message; statusUntil = Time.unscaledTime + 4f; } };
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb[ToggleKey].wasPressedThisFrame) open = !open;
        }

        List<Part> Rows()
        {
            var rows = new List<Part>();
            foreach (var p in core.Parts) if (!p.auto) rows.Add(p);
            if (rows.Count == 0) foreach (var p in core.Parts) if (p.found && rows.Count < 5) rows.Add(p);
            if (rows.Count > MaxRows) rows.RemoveRange(MaxRows, rows.Count - MaxRows);
            return rows;
        }

        void StepLook(int dir)
        {
            int i = core.Looks.FindIndex(l => l.id == core.LookId) + dir;   // -1 = 원래대로
            if (i >= core.Looks.Count) i = -1; else if (i < -1) i = core.Looks.Count - 1;
            core.SetLook(i < 0 ? null : core.Looks[i].id);
        }

        void StepPart(Part p, int dir)
        {
            core.States.TryGetValue(p.id, out var s);
            int i = s == null || s.mode == "original" ? -1 : core.Materials.FindIndex(m => m.id == s.preset);
            i += dir;
            if (i >= core.Materials.Count) i = -1; else if (i < -1) i = core.Materials.Count - 1;
            var ns = new PartState { id = p.id, mode = i < 0 ? "original" : (keepTextures ? "keep" : "replace"), preset = i < 0 ? null : core.Materials[i].id };
            core.SetPart(ns, true);
        }

        void SetKeep(bool keep)
        {
            if (keep == keepTextures) return;
            keepTextures = keep;
            foreach (var s in new List<PartState>(core.States.Values))
                if (s.mode != "original") { s.mode = keep ? "keep" : "replace"; core.SetPart(s, false); }
        }

        string PartName(Part p) =>
            core.States.TryGetValue(p.id, out var s) && s.mode != "original" ? (core.FindMaterial(s.preset)?.displayName ?? s.preset) : "원래 재질";

        // ================================================================ 화면 (IMGUI, 아래 가운데)
        // 로봇팔 패널과 같은 디지털 트윈 스타일: 어두운 반투명 유리 + 얇은 테두리 + 청록 포인트 하나, 글꼴 SUIT(없으면 기본)
        static readonly Color Ink = new Color32(0xF2, 0xF5, 0xF8, 255), Muted = new Color32(0xAA, 0xB5, 0xC1, 255);
        static readonly Color Blue = new Color32(0x5F, 0xD4, 0xE8, 255);
        static readonly Color Fill = new Color(.04f, .055f, .075f, .88f);
        static readonly Color Card = new Color(1, 1, 1, .05f), CardHover = new Color(1, 1, 1, .11f);
        static readonly Color Edge = new Color(1, 1, 1, .16f), ActiveFill = new Color(.37f, .83f, .91f, .16f), ActiveEdge = new Color(.37f, .83f, .91f, .6f);
        const float OpenW = 196, OpenH = 36;   // 닫혔을 때 버튼 크기 (1080p 기준)

        Font Load(string name) => Resources.Load<Font>("Fonts/" + name);

        void Styles()
        {
            if (title != null) return;
            Font semi = Load("SUIT-SemiBold"), bold = Load("SUIT-Bold") ?? semi, medium = Load("SUIT-Medium") ?? semi;
            title = new GUIStyle(GUI.skin.label) { font = bold, fontSize = 16, normal = { textColor = Ink } };
            label = new GUIStyle(GUI.skin.label) { font = semi, fontSize = 13, normal = { textColor = Ink }, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            small = new GUIStyle(GUI.skin.label) { font = medium, fontSize = 12, normal = { textColor = Muted }, wordWrap = true };
            button = new GUIStyle(GUI.skin.label) { font = semi, fontSize = 14, alignment = TextAnchor.MiddleCenter, normal = { textColor = Ink }, clipping = TextClipping.Clip };
            nameBox = new GUIStyle(button) { fontSize = 13 };
            dim = new GUIStyle(nameBox) { normal = { textColor = Muted } };
        }

        static void Box(Rect r, Color c) { var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = old; }

        // 칸 + 1px 테두리
        static void Frame(Rect r, Color fill, Color edge)
        {
            Box(r, fill);
            Box(new Rect(r.x, r.y, r.width, 1), edge); Box(new Rect(r.x, r.yMax - 1, r.width, 1), edge);
            Box(new Rect(r.x, r.y, 1, r.height), edge); Box(new Rect(r.xMax - 1, r.y, 1, r.height), edge);
        }

        // 버튼: 칸을 그리고 위에 안 보이는 버튼. 마우스 올리면 밝게
        bool Btn(Rect r, string text, bool active = false, GUIStyle style = null)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            Frame(r, active ? ActiveFill : hover ? CardHover : Card, active ? ActiveEdge : Edge);
            GUI.Label(r, text, style ?? button);
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        void OnGUI()
        {
            if (core == null) return;
            Styles();
            float k = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1));
            float h = Screen.height / k;

            if (!open)
            {
                // 화면 아래 가운데 (양옆 패널과 안 겹치게): 유리 칸 + 왼쪽 청록 줄 + 오른쪽 F6 키 표시
                var r = new Rect(Screen.width / k / 2 - OpenW / 2, h - OpenH - 12, OpenW, OpenH);
                bool hover = r.Contains(Event.current.mousePosition);
                Frame(r, hover ? new Color(.08f, .11f, .14f, .92f) : Fill, hover ? ActiveEdge : Edge);
                Box(new Rect(r.x, r.y, 2, r.height), Blue);
                GUI.Label(new Rect(r.x + 16, r.y, r.width - 70, r.height), "룩 프리셋", label);
                var key = new Rect(r.xMax - 50, r.y + 8, 38, r.height - 16);
                Frame(key, Color.clear, new Color(1, 1, 1, .45f));
                GUI.Label(key, "F6", dim);
                if (GUI.Button(r, GUIContent.none, GUIStyle.none)) open = true;
                return;
            }

            var rows = Rows();
            const float W = 340, Row = 30;
            float inner = W - 28;
            var cur = core.Looks.Find(l => l.id == core.LookId);
            string note = cur == null ? (core.Looks.Count == 0 ? "looks 폴더에 프리셋이 없어요" : $"프리셋 {core.Looks.Count}개 · 자세한 조절은 렌더 룩 콘솔에서") : cur.note;
            float noteH = Mathf.Max(16, small.CalcHeight(new GUIContent(note), inner));
            float H = 12 + 30 + 20 + Row + 4 + noteH + 10 + 22 + 34 + rows.Count * (Row + 6) + 4 + 34 + 22;
            var box = new Rect(Screen.width / k / 2 - W / 2, h - H - 12, W, H);
            Frame(box, Fill, Edge);
            Box(new Rect(box.x, box.y, box.width, 2), Blue);   // 위 청록 줄
            float x = box.x + 14, y = box.y + 12;

            GUI.Label(new Rect(x, y, inner, 22), "룩 프리셋", title);
            GUI.Label(new Rect(x + 90, y + 3, inner - 90, 18), "골라서 바로 비교", small);
            y += 30;

            GUI.Label(new Rect(x, y, inner, 18), "조명 분위기", small); y += 20;
            if (Stepper(new Rect(x, y, inner, Row), cur == null ? "원래대로" : cur.displayName, cur != null, out int d)) StepLook(d);
            y += Row + 4;
            GUI.Label(new Rect(x, y, inner, noteH), note, small); y += noteH + 10;

            GUI.Label(new Rect(x, y, inner, 18), "재질 (부위별) · 조명만 바꾸려면 '원래 재질'로 두기", small); y += 22;
            float half = (inner - 6) / 2;
            if (Segment(new Rect(x, y, half, 26), "전부 바꾸기", !keepTextures)) SetKeep(false);
            if (Segment(new Rect(x + half + 6, y, half, 26), "텍스처 유지 · 수치만", keepTextures)) SetKeep(true);
            y += 34;
            foreach (var p in rows)
            {
                GUI.Label(new Rect(x, y, 84, Row), p.label, label);
                bool on = core.States.TryGetValue(p.id, out var s) && s.mode != "original";
                if (Stepper(new Rect(x + 86, y, inner - 86, Row), PartName(p), on, out int pd)) StepPart(p, pd);
                y += Row + 6;
            }

            y += 4;
            if (Btn(new Rect(x, y, inner * 0.62f, 30), "모두 원래대로")) core.ResetAll();
            if (Btn(new Rect(x + inner * 0.62f + 8, y, inner * 0.38f - 8, 30), "닫기  F6")) open = false;
            y += 34;
            GUI.Label(new Rect(x, y, inner, 18), Time.unscaledTime < statusUntil ? lastMsg : "원본 재질 파일은 안 바뀌어요 · 끄면 그대로 돌아와요", small);
        }

        bool Segment(Rect r, string text, bool on)
        {
            return Btn(r, text, on, on ? nameBox : dim);
        }

        bool Stepper(Rect r, string text, bool active, out int dir)
        {
            dir = 0;
            Frame(new Rect(r.x + 34, r.y, r.width - 68, r.height), active ? ActiveFill : Card, active ? ActiveEdge : Edge);
            if (Btn(new Rect(r.x, r.y, 30, r.height), "◀")) dir = -1;
            GUI.Label(new Rect(r.x + 36, r.y, r.width - 72, r.height), text, active ? nameBox : dim);
            if (Btn(new Rect(r.xMax - 30, r.y, 30, r.height), "▶")) dir = 1;
            return dir != 0;
        }
    }
}
