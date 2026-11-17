using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// =============================================================
// StreamingAssets/RenderTool/ 아래 프리셋 JSON·이미지·HDRI 읽기 (렌더툴 PresetFiles·TextureFiles와 같은 방식)
// 빌드 exe는 에디터 임포트가 없어서 파일을 직접 읽음. 같은 파일은 한 번만 읽고 재사용
// 깨진 JSON은 건너뛰고 콘솔에 이유를 남김
// =============================================================
namespace RenderLook
{
    public static class RenderLookFiles
    {
        public static string Root => Path.Combine(Application.streamingAssetsPath, "RenderTool");
        static string Full(string relative) => Path.Combine(Root, relative);

        public static List<MaterialPresetData> LoadMaterials()
        {
            var list = Load<MaterialPresetData>("materials");
            list.RemoveAll(m => string.IsNullOrEmpty(m.id) || m.@base == null || m.@base.color == null || m.@base.color.Length < 3);
            foreach (var m in list) if (string.IsNullOrEmpty(m.displayName)) m.displayName = m.id;
            return list;
        }

        public static List<LookPresetData> LoadLooks()
        {
            var list = Load<LookPresetData>("looks");
            list.RemoveAll(l => string.IsNullOrEmpty(l.id));
            foreach (var l in list) if (string.IsNullOrEmpty(l.displayName)) l.displayName = l.id;
            list.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) : string.CompareOrdinal(a.id, b.id));
            return list;
        }

        // 부위 이름표 (선택): parts/<씬 이름>.json. 없으면 null
        public static PartsFile LoadParts(string sceneName)
        {
            string path = Full(Path.Combine("parts", sceneName + ".json"));
            if (!File.Exists(path)) return null;
            try { return JsonUtility.FromJson<PartsFile>(File.ReadAllText(path)); }
            catch (Exception e) { Debug.LogWarning($"[렌더 룩] 부위 이름표 읽기 실패 ({path}): {e.Message}"); return null; }
        }

        // 새 프리셋 저장: 같은 id가 있으면 "_2", "_3"을 붙여 새 파일로 (원래 파일은 안 덮어씀). 저장한 id를 돌려줌
        public static string Save(string folder, string id, Func<string, string> toJson)
        {
            string dir = Full(folder);
            Directory.CreateDirectory(dir);
            string baseId = string.IsNullOrEmpty(id) ? "custom" : id, final = baseId;
            for (int n = 2; File.Exists(Path.Combine(dir, final + ".json")); n++) final = baseId + "_" + n;
            File.WriteAllText(Path.Combine(dir, final + ".json"), Tidy(toJson(final)), new UTF8Encoding(false));
            return final;
        }

        // JsonUtility는 0.78을 0.7799999713897705처럼 씀 → 0.001 단위로 줄여 사람이 읽기 쉽게
        static string Tidy(string json) => System.Text.RegularExpressions.Regex.Replace(json, @"-?\d+\.\d{5,}", m =>
            Math.Round(double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture), 3)
                .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

        static List<T> Load<T>(string folder) where T : class
        {
            var list = new List<T>();
            string dir = Full(folder);
            if (!Directory.Exists(dir)) { Debug.LogWarning($"[렌더 룩] 폴더가 없음: {dir}"); return list; }
            var files = Directory.GetFiles(dir, "*.json");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                try
                {
                    var item = JsonUtility.FromJson<T>(File.ReadAllText(file));
                    if (item != null) list.Add(item);
                }
                catch (Exception e) { Debug.LogWarning($"[렌더 룩] {folder}/{Path.GetFileName(file)} 읽기 실패: {e.Message}"); }
            }
            return list;
        }

        // ------------------------------------------------------------ 이미지 (PNG·JPG)
        static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();

        // linear = 노멀맵처럼 색이 아닌 데이터
        public static Texture2D LoadImage(string relative, bool linear)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            string key = relative + (linear ? "|lin" : "|srgb");
            if (Cache.TryGetValue(key, out var cached) && cached) return cached;
            string path = Full(relative);
            if (!File.Exists(path)) { Debug.LogWarning($"[렌더 룩] 이미지 없음: {path}"); return null; }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = Path.GetFileNameWithoutExtension(path) };
            if (!tex.LoadImage(File.ReadAllBytes(path))) return null;
            tex.wrapMode = TextureWrapMode.Repeat; tex.filterMode = FilterMode.Trilinear; tex.anisoLevel = 4;
            return Cache[key] = tex;
        }

        // 노멀맵 PNG(RGB) → Standard 셰이더가 읽는 형식. Built-in은 노멀맵을 DXT5nm(A=X, G=Y)으로 읽어서 R을 A로 옮김
        public static Texture2D LoadNormal(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            string key = relative + "|nrm";
            if (Cache.TryGetValue(key, out var cached) && cached) return cached;
            var src = LoadImage(relative, true);
            if (!src) return null;
            var px = src.GetPixels32();
            for (int i = 0; i < px.Length; i++) { var p = px[i]; px[i] = new Color32(255, p.g, p.g, p.r); }
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, true, true) { name = src.name + "_nrm" };
            tex.SetPixels32(px); tex.Apply(true, false);
            tex.wrapMode = TextureWrapMode.Repeat; tex.filterMode = FilterMode.Trilinear; tex.anisoLevel = 4;
            return Cache[key] = tex;
        }

        // ------------------------------------------------------------ HDRI (Radiance .hdr, RGBE·RLE) → HDR 파노라마
        public static Texture2D LoadHdr(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            string key = relative + "|hdr";
            if (Cache.TryGetValue(key, out var cached) && cached) return cached;
            string path = Full(relative);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[렌더 룩] HDRI 없음: {path} (python Tools/fetch_polyhaven.py 로 받기)");
                return null;
            }
            try { return Cache[key] = ReadHdr(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path)); }
            catch (Exception e) { Debug.LogWarning($"[렌더 룩] HDRI 읽기 실패 ({path}): {e.Message}"); return null; }
        }

        static Texture2D ReadHdr(byte[] d, string name)
        {
            int p = 0;
            string Line()
            {
                int s = p;
                while (p < d.Length && d[p] != '\n') p++;
                string l = Encoding.ASCII.GetString(d, s, p - s).TrimEnd('\r');
                p++;
                return l;
            }
            if (!Line().StartsWith("#?")) throw new FormatException("Radiance HDR 파일이 아님");
            for (string l = Line(); l.Length > 0; l = Line())
                if (l.StartsWith("FORMAT=") && l != "FORMAT=32-bit_rle_rgbe") throw new FormatException("RGBE 형식만 지원: " + l);
            var res = Line().Split(' ');
            if (res.Length != 4 || res[0] != "-Y" || res[2] != "+X") throw new FormatException("지원하지 않는 방향");
            int h = int.Parse(res[1]), w = int.Parse(res[3]);

            var scan = new byte[w * 4];
            var px = new ushort[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                p = Scanline(d, p, scan, w);
                int row = (h - 1 - y) * w * 4;      // 파일은 위→아래, 텍스처는 아래→위
                for (int x = 0; x < w; x++)
                {
                    int e = scan[x * 4 + 3], o = row + x * 4;
                    float f = e == 0 ? 0f : (float)Math.Pow(2, e - 136);
                    px[o] = Mathf.FloatToHalf(scan[x * 4] * f);
                    px[o + 1] = Mathf.FloatToHalf(scan[x * 4 + 1] * f);
                    px[o + 2] = Mathf.FloatToHalf(scan[x * 4 + 2] * f);
                    px[o + 3] = Mathf.FloatToHalf(1f);
                }
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBAHalf, true, true)
            {
                name = name, wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear,
            };
            tex.SetPixelData(px, 0);
            tex.Apply(true, false);
            return tex;
        }

        static int Scanline(byte[] d, int p, byte[] scan, int w)
        {
            bool rle = w >= 8 && w < 0x8000 && d[p] == 2 && d[p + 1] == 2 && (d[p + 2] & 0x80) == 0;
            if (!rle) { Buffer.BlockCopy(d, p, scan, 0, w * 4); return p + w * 4; }
            if (((d[p + 2] << 8) | d[p + 3]) != w) throw new FormatException("줄 폭이 맞지 않음");
            p += 4;
            for (int c = 0; c < 4; c++)
                for (int i = 0; i < w;)
                {
                    int n = d[p++];
                    if (n > 128) { n -= 128; byte v = d[p++]; while (n-- > 0) scan[(i++) * 4 + c] = v; }
                    else { if (n == 0) throw new FormatException("잘못된 RLE"); while (n-- > 0) scan[(i++) * 4 + c] = d[p++]; }
                }
            return p;
        }

        // [최소, 최대] 범위·배열 → Unity 값
        public static float Clamp(float v, float[] range) =>
            range != null && range.Length >= 2 ? Mathf.Clamp(v, Mathf.Min(range[0], range[1]), Mathf.Max(range[0], range[1])) : v;
        public static Color ToColor(float[] c) => c != null && c.Length >= 3 ? new Color(c[0], c[1], c[2], 1f) : Color.white;
        public static Vector3 ToVector3(float[] v) => v != null && v.Length >= 3 ? new Vector3(v[0], v[1], v[2]) : Vector3.zero;
        public static Vector2 ToVector2(float[] v) => v != null && v.Length >= 2 ? new Vector2(v[0], v[1]) : Vector2.one;
    }
}
