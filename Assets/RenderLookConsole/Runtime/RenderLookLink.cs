using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

// =============================================================
// [렌더 룩 콘솔 ↔ 유니티 연결] UDP, 이 PC 안(127.0.0.1)에서만
//   콘솔 → 유니티 : 5070  {"type":"render_cmd","cmd":...}   (look / look_params / part / reset / save_material / save_look / hello / rescan)
//   유니티 → 콘솔 : 5071  {"type":"render_state",...}        (부위·프리셋 목록 + 지금 값. 바뀔 때 + 1초마다)
// 흐름: 받는 건 별도 스레드 → 큐 → 메인 스레드 Update에서 RenderLookCore를 부름 (유니티 API는 메인 스레드에서만)
// 메시지 형식: docs/render_look_messages.md
// =============================================================
namespace RenderLook
{
    [Serializable]
    class CmdMsg
    {
        public string type, cmd, id, name;
        public bool pick;
        public int w, h;                  // view: 렌더 뷰 픽셀 크기
        public float zoom, cx, cy;        // view: 확대·보는 곳
        public PartState part;
        public LookParams look;
    }

    [Serializable] class LookInfo { public string id, name, note, thumb; }
    [Serializable] class MatInfo { public string id, name, note, category, thumb; public float[] color, rMetal, rRough, rNormal; public float metallic, roughness, normalStrength, tiling; }
    [Serializable] class PartInfo { public string id, label; public bool auto, found; public int count; public float[] color; public float metallic, smoothness; }

    [Serializable]
    class StateMsg
    {
        public string type = "render_state";
        public string scene, msg, look, root;   // root = 프리셋 폴더 (콘솔이 썸네일을 여기서 읽음)
        public int seq;
        public LookParams lookParams, lookDefaults;
        public LookInfo[] looks;
        public MatInfo[] materials;
        public PartInfo[] parts;
        public PartState[] states;
    }

    public class RenderLookLink : MonoBehaviour
    {
        // 바뀌면 안 되는 값은 상수 (씬 저장 값이 덮지 않게)
        const int ListenPort = 5070, ConsolePort = 5071;
        const float Heartbeat = 1f, Debounce = 0.1f;

        RenderLookCore core;
        UdpClient rx, tx;
        Thread thread;
        volatile bool running;
        readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
        IPEndPoint console = new IPEndPoint(IPAddress.Loopback, ConsolePort);
        bool dirty = true; float nextSend, lastChange;
        int seq;
        float lastHeard = -99f;

        // 콘솔이 3초 안에 말을 걸었음 (렌더 뷰는 이때만 보냄)
        public bool ConsoleAlive => Time.unscaledTime - lastHeard < 3f;

        void Start()
        {
            core = GetComponent<RenderLookCore>();
            core.Changed += () => { dirty = true; lastChange = Time.unscaledTime; };
            try
            {
                rx = new UdpClient(new IPEndPoint(IPAddress.Loopback, ListenPort));
                tx = new UdpClient();
                running = true;
                thread = new Thread(Receive) { IsBackground = true, Name = "RenderLookLink" };
                thread.Start();
                Debug.Log($"[렌더 룩] 콘솔 연결 대기: UDP {ListenPort} 받기 → {ConsolePort} 보내기");
            }
            catch (SocketException e) { Debug.LogWarning($"[렌더 룩] UDP {ListenPort}를 열지 못함 (다른 프로그램이 씀?): {e.Message}"); }
        }

        void Receive()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try { inbox.Enqueue(Encoding.UTF8.GetString(rx.Receive(ref any))); }
                catch (SocketException) { if (!running) return; }
                catch (ObjectDisposedException) { return; }
            }
        }

        void Update()
        {
            while (inbox.TryDequeue(out var text)) Handle(text);
            float now = Time.unscaledTime;
            if ((dirty && now - lastChange >= Debounce) || now >= nextSend) Send();
        }

        void Handle(string text)
        {
            CmdMsg c;
            try { c = JsonUtility.FromJson<CmdMsg>(text); } catch { return; }
            if (c == null || c.type != "render_cmd") return;
            bool wasAlive = ConsoleAlive;
            lastHeard = Time.unscaledTime;
            switch (c.cmd)
            {
                case "hello": dirty = true; break;
                case "ping": if (!wasAlive) dirty = true; break;
                case "rescan": core.Reload(); break;
                case "look": core.SetLook(c.id); break;
                case "look_params": core.SetParams(c.look); break;
                case "part": if (c.part != null) core.SetPart(c.part, c.pick); break;
                case "reset": core.ResetAll(); break;
                case "view": GetComponent<RenderLookStream>()?.SetView(c.w, c.h, c.zoom, c.cx, c.cy); break;
                case "save_material": core.SaveMaterial(c.id, c.name); break;
                case "save_look": core.SaveLook(c.name); break;
            }
        }

        void Send()
        {
            if (tx == null) return;
            dirty = false; nextSend = Time.unscaledTime + Heartbeat;
            var looks = new List<LookInfo>();
            foreach (var l in core.Looks)
            {
                string t = System.IO.File.Exists(System.IO.Path.Combine(RenderLookFiles.Root, "thumbnails", "looks", l.id + ".jpg")) ? $"thumbnails/looks/{l.id}.jpg" : l.thumbnail;
                looks.Add(new LookInfo { id = l.id, name = l.displayName, note = l.note, thumb = t });
            }
            var mats = new List<MatInfo>();
            foreach (var m in core.Materials)
                mats.Add(new MatInfo { id = m.id, name = m.displayName, note = m.note, category = m.category, thumb = m.thumbnail, color = m.@base.color,
                                       metallic = m.@base.metallic, roughness = m.@base.roughness,
                                       normalStrength = m.@base.normalStrength, tiling = m.@base.textureScale > 0 ? m.@base.textureScale : 1f, rMetal = m.ranges?.metallic, rRough = m.ranges?.roughness, rNormal = m.ranges?.normalStrength });
            var parts = new List<PartInfo>();
            foreach (var p in core.Parts)
                parts.Add(new PartInfo { id = p.id, label = p.label, auto = p.auto, found = p.found, count = p.count, color = p.color, metallic = p.metallic, smoothness = p.smoothness });
            var msg = new StateMsg
            {
                scene = SceneManager.GetActiveScene().name, root = RenderLookFiles.Root, msg = core.Message, look = core.LookId, seq = ++seq,
                lookParams = core.Params, lookDefaults = core.DefaultParams, looks = looks.ToArray(), materials = mats.ToArray(), parts = parts.ToArray(),
                states = new List<PartState>(core.States.Values).ToArray(),
            };
            var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(msg));
            try { tx.Send(bytes, bytes.Length, console); } catch (SocketException) { }   // 콘솔이 꺼져 있어도 괜찮음
        }

        void OnDestroy()
        {
            running = false;
            rx?.Close(); tx?.Close();
        }
    }
}
