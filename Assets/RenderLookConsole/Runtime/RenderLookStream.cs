using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

// =============================================================
// [렌더 뷰 보내기] 콘솔 창 가운데의 "렌더 화면"에 유니티 카메라 그림을 실시간으로 보냄
// 흐름: 콘솔이 켜져 있을 때만(1초마다 오는 ping) → 초당 약 12장, Main Camera와 같은 위치·설정의 숨은 카메라로 따로 그려서(후처리 포함, 유니티 UI는 빠짐)
//       GPU에서 비동기로 읽음(화면 안 멈춤) → JPG로 바꿈 → TCP 5072로 콘솔에 보냄 [길이 4바이트 + JPG]
// 덤: 룩을 고르면 1초 뒤 지금 화면을 작게 찍어 thumbnails/looks/<룩 id>.jpg로 저장 (없을 때만) → 콘솔 룩 카드 사진
// =============================================================
namespace RenderLook
{
    public class RenderLookStream : MonoBehaviour
    {
        const int Port = 5072;
        const float Fps = 12f;
        const int MaxWidth = 2560, MaxHeight = 1600, MinSide = 160, Quality = 90, Msaa = 8;
        const float MaxZoom = 12f;
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

        // 콘솔이 알려 준 렌더 뷰 크기(픽셀)와 확대 (zoom 1 = 전체, cx·cy = 보는 곳 가운데, 0~1, 왼쪽 위 기준)
        int viewW = 1280, viewH = 720;
        float zoom = 1f, cx = 0.5f, cy = 0.5f;

        public void SetView(int w, int h, float z, float x, float y)
        {
            if (w > 0 && h > 0)
            {
                float k = Mathf.Min(1f, Mathf.Min(MaxWidth / (float)w, MaxHeight / (float)h));   // 너무 크면 비율 유지하며 줄임
                viewW = Mathf.Max(MinSide, Mathf.RoundToInt(w * k)); viewH = Mathf.Max(MinSide, Mathf.RoundToInt(h * k));
            }
            zoom = Mathf.Clamp(z, 1f, MaxZoom);
            float half = 0.5f / zoom;
            cx = Mathf.Clamp(x, half, 1f - half); cy = Mathf.Clamp(y, half, 1f - half);
            next = 0f;   // 바로 새로 그림
        }

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
            // 유니티 Game 창 크기와 상관없이 콘솔 렌더 뷰 크기 그대로 그림 (늘려 보여 주지 않아서 선명함)
            int w = viewW, h = viewH;
            if (rt == null || rt.width != w || rt.height != h)
            {
                if (rt) rt.Release();
                rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = Msaa };
            }
            RenderView(cam, rt, zoom, cx, cy);
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

        // 렌더 뷰 전용 카메라: Main Camera를 직접 다시 그리면 그 카메라에 붙은 효과(예: 패널 유리 흐림이 화면을 가져가는 커맨드 버퍼)가
        // 콘솔용 그림을 가져가서 게임 화면 UI가 번쩍거림 → 숨긴 카메라에 Main Camera 설정·위치만 복사해 따로 그림
        Camera streamCam;
        PostProcessLayer streamPost;

        Camera StreamCam(Camera main)
        {
            if (!streamCam)
            {
                var go = new GameObject("_RenderLookStreamCam") { hideFlags = HideFlags.HideAndDontSave };
                go.SetActive(false);                       // 후처리 준비(Init)가 끝난 뒤에 켬
                streamCam = go.AddComponent<Camera>();
                streamCam.enabled = false;                 // 화면에 자동으로 안 그림 (Render()로만)
                var src = main.GetComponent<PostProcessLayer>();
                var res = src ? typeof(PostProcessLayer).GetField("m_Resources", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(src) as PostProcessResources : null;
                if (res)
                {
                    streamPost = go.AddComponent<PostProcessLayer>();
                    streamPost.Init(res);
                    streamPost.volumeTrigger = go.transform;
                    streamPost.volumeLayer = src.volumeLayer;
                    // TAA는 매 프레임 연속으로 그려야 맞으므로 따로 그리는 카메라에선 SMAA로
                    streamPost.antialiasingMode = src.antialiasingMode == PostProcessLayer.Antialiasing.TemporalAntialiasing
                        ? PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing : src.antialiasingMode;
                }
                go.SetActive(true);
            }
            streamCam.ResetProjectionMatrix();
            streamCam.CopyFrom(main);                      // 설정 + 위치·회전 (커맨드 버퍼는 안 따라옴)
            streamCam.enabled = false;
            var mainPost = main.GetComponent<PostProcessLayer>();
            if (streamPost) streamPost.enabled = mainPost && mainPost.enabled;   // F5 가벼운 룩이면 같이 끔
            return streamCam;
        }

        // 확대: 보는 범위만 좁혀 다시 그림 (그림을 늘리는 게 아니라 진짜로 그 부분을 크게 그려서 선명함)
        // 원리: 원래 시야(세로 FOV, 이 그림의 가로세로 비율)의 앞면 사각형에서 1/zoom 크기 조각만 잘라 투영 행렬로 씀
        void RenderView(Camera main, RenderTexture target, float zoom, float cx, float cy)
        {
            var cam = StreamCam(main);
            cam.targetTexture = target;
            if (zoom > 1.001f)
            {
                float n = cam.nearClipPlane, f = cam.farClipPlane;
                float top = n * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), right = top * target.width / target.height;
                float l = -right + 2f * right * (cx - 0.5f / zoom), r = l + 2f * right / zoom;
                float t = top - 2f * top * (cy - 0.5f / zoom), b = t - 2f * top / zoom;
                cam.projectionMatrix = Matrix4x4.Frustum(l, r, b, t, n, f);
            }
            cam.Render();
            cam.targetTexture = null;
            cam.ResetProjectionMatrix();
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
            RenderView(cam, t, 1f, 0.5f, 0.5f);
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
            if (streamCam) Destroy(streamCam.gameObject);
        }
    }
}
