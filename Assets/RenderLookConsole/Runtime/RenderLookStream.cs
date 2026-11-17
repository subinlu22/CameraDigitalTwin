using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

// =============================================================
// [렌더 뷰 보내기] 콘솔 창 가운데의 "렌더 화면"에 유니티 카메라 그림을 실시간으로 보냄
// 흐름: 콘솔이 켜져 있을 때만(1초마다 오는 ping) → 초당 약 12장, Main Camera를 따로 한 장 그려서(후처리 포함, 유니티 UI는 빠짐)
//       GPU에서 비동기로 읽음(화면 안 멈춤) → JPG로 바꿈 → TCP 5072로 콘솔에 보냄 [길이 4바이트 + JPG]
// 덤: 룩을 고르면 1초 뒤 지금 화면을 작게 찍어 thumbnails/looks/<룩 id>.jpg로 저장 (없을 때만) → 콘솔 룩 카드 사진
// =============================================================
namespace RenderLook
{
    public class RenderLookStream : MonoBehaviour
    {
        const int Port = 5072;
        const float Fps = 12f;
        const int MaxWidth = 1280, Quality = 80;
        const int ThumbW = 384, ThumbH = 240;

        RenderLookCore core;
        RenderLookLink link;
        RenderTexture rt;
        bool waiting;
        float next, nextConnect;
        TcpClient client; NetworkStream net;
        Thread sender; volatile bool running = true;
        class Frame { public byte[] raw; public int w, h; public bool flip; }
        readonly ConcurrentQueue<Frame> outbox = new ConcurrentQueue<Frame>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        string thumbFor; float thumbAt;

        void Start()
        {
            core = GetComponent<RenderLookCore>();
            link = GetComponent<RenderLookLink>();
            core.Changed += () =>
            {
                if (core.LookId != null && core.LookId != thumbFor) { thumbFor = core.LookId; thumbAt = Time.unscaledTime + 1f; }
            };
            sender = new Thread(Send) { IsBackground = true, Name = "RenderLookStream" };
            sender.Start();
        }

        void LateUpdate()
        {
            if (thumbFor != null && thumbAt > 0 && Time.unscaledTime >= thumbAt) { thumbAt = 0; SaveLookThumb(thumbFor); }
            if (!link || !link.ConsoleAlive) return;
            if (Time.unscaledTime < next || waiting) return;
            next = Time.unscaledTime + 1f / Fps;
            if (net == null) { TryConnect(); if (net == null) return; }

            var cam = Camera.main;
            if (!cam) return;
            int w = Mathf.Min(MaxWidth, Screen.width), h = Mathf.Max(1, Mathf.RoundToInt(w * (float)Screen.height / Mathf.Max(1, Screen.width)));
            if (rt == null || rt.width != w || rt.height != h)
            {
                if (rt) rt.Release();
                rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 2 };
            }
            Render(cam, rt);
            waiting = true;
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGB24, req =>
            {
                waiting = false;
                if (req.hasError) return;
                // 원본 픽셀만 복사해 두고, 뒤집기·JPG 만들기는 보내는 스레드에서 (메인 스레드 안 막음)
                while (outbox.Count > 1) outbox.TryDequeue(out _);   // 밀리면 옛 그림은 버림 (늘 최신만)
                outbox.Enqueue(new Frame { raw = req.GetData<byte>().ToArray(), w = w, h = h, flip = !SystemInfo.graphicsUVStartsAtTop }); signal.Set();
            });
        }

        // 카메라를 한 장 더 그림 (원래 화면 출력은 그대로)
        static void Render(Camera cam, RenderTexture target)
        {
            var old = cam.targetTexture;
            cam.targetTexture = target;
            cam.Render();
            cam.targetTexture = old;
        }

        void TryConnect()
        {
            if (Time.unscaledTime < nextConnect) return;
            nextConnect = Time.unscaledTime + 2f;
            try
            {
                var c = new TcpClient { NoDelay = true, SendTimeout = 1000 };
                var ar = c.BeginConnect("127.0.0.1", Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(100)) { c.Close(); return; }
                c.EndConnect(ar);
                client = c; net = c.GetStream();
                Debug.Log($"[렌더 룩] 렌더 뷰 보내기 시작 (TCP {Port})");
            }
            catch (Exception) { }
        }

        void Send()
        {
            var len = new byte[4];
            while (running)
            {
                signal.WaitOne(500);
                while (outbox.TryDequeue(out var f))
                {
                    var s = net;
                    if (s == null) continue;
                    try
                    {
                        // GPU에서 읽은 줄 순서는 그래픽 API마다 다름 (DirectX = 위→아래, OpenGL = 아래→위) → OpenGL일 때만 뒤집음
                        int row = f.w * 3;
                        var flipped = f.raw;
                        if (f.flip)
                        {
                            flipped = new byte[f.raw.Length];
                            for (int y = 0; y < f.h; y++) Buffer.BlockCopy(f.raw, (f.h - 1 - y) * row, flipped, y * row, row);
                        }
                        var jpg = ImageConversion.EncodeArrayToJPG(flipped, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8_SRGB, (uint)f.w, (uint)f.h, 0, Quality);
                        len[0] = (byte)(jpg.Length >> 24); len[1] = (byte)(jpg.Length >> 16); len[2] = (byte)(jpg.Length >> 8); len[3] = (byte)jpg.Length;
                        s.Write(len, 0, 4); s.Write(jpg, 0, jpg.Length);
                    }
                    catch (Exception) { Drop(); }
                }
            }
        }

        void Drop()
        {
            try { client?.Close(); } catch (Exception) { }
            client = null; net = null;
        }

        void SaveLookThumb(string id)
        {
            string dir = Path.Combine(RenderLookFiles.Root, "thumbnails", "looks"), path = Path.Combine(dir, id + ".jpg");
            var cam = Camera.main;
            if (File.Exists(path) || !cam) return;
            var t = RenderTexture.GetTemporary(ThumbW, ThumbH, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Render(cam, t);
            var prev = RenderTexture.active; RenderTexture.active = t;
            var tex = new Texture2D(ThumbW, ThumbH, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, ThumbW, ThumbH), 0, 0); tex.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(t);
            try { Directory.CreateDirectory(dir); File.WriteAllBytes(path, tex.EncodeToJPG(85)); }
            catch (Exception e) { Debug.LogWarning("[렌더 룩] 룩 썸네일 저장 실패: " + e.Message); }
            Destroy(tex);
        }

        void OnDestroy()
        {
            running = false; signal.Set();
            Drop();
            if (rt) rt.Release();
        }
    }
}
