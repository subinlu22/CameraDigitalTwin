using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

// =============================================================
// [부위 색 재기] 레퍼런스 대조용: 지금 화면에서 부위(이름표 부위)마다 보이는 픽셀의 평균 색을 잼
// 흐름: Main Camera와 같은 자리에서 두 장을 그림
//   ① 보통 화면 (조명·후처리 포함)
//   ② 부위 마스크: 부위마다 다른 단색(R = 부위 번호), 나머지는 검정 (Hidden/RenderLook/PartId 셰이더로 바꿔 그림)
//   → ②에서 그 부위 번호인 픽셀만 골라 ①의 색을 평균 → 콘솔이 레퍼런스 사진의 같은 부위 색과 비교
// 콘솔이 "measure"를 보낼 때만 (가끔) 그림. 결과는 상태 메시지의 measures로 감
// =============================================================
namespace RenderLook
{
    [Serializable]
    public class PartMeasure
    {
        public string id;
        public float[] rgb;        // 감마(sRGB) 0~1 평균 색
        public float coverage;     // 화면에서 차지하는 비율 (0~1)
    }

    public static class RenderLookMeasure
    {
        const int W = 640, H = 360;          // 재는 그림 크기 (작아도 평균엔 충분)
        static readonly int PartIdProp = Shader.PropertyToID("_PartId");

        public static List<PartMeasure> Measure(RenderLookCore core)
        {
            var result = new List<PartMeasure>();
            var main = Camera.main;
            var shader = Resources.Load<Shader>("RenderLookPartId");
            if (!main || !shader) { Debug.LogWarning("[렌더 룩] 부위 색 재기: 카메라나 마스크 셰이더가 없음"); return result; }

            // 부위 번호: 이름표 부위만 (1부터)
            var parts = core.Parts.FindAll(p => !p.auto);
            var go = new GameObject("_RenderLookMeasureCam") { hideFlags = HideFlags.HideAndDontSave };
            go.SetActive(false);
            var cam = go.AddComponent<Camera>();
            cam.CopyFrom(main);
            cam.enabled = false;
            var post = main.GetComponent<PostProcessLayer>();
            PostProcessLayer myPost = null;
            var res = post ? typeof(PostProcessLayer).GetField("m_Resources", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(post) as PostProcessResources : null;
            if (res && post.enabled)
            {
                myPost = go.AddComponent<PostProcessLayer>();
                myPost.Init(res); myPost.volumeTrigger = go.transform; myPost.volumeLayer = post.volumeLayer;
                myPost.antialiasingMode = PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing;
            }
            go.SetActive(true);

            var beautyRt = RenderTexture.GetTemporary(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var maskRt = RenderTexture.GetTemporary(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var saved = new Dictionary<Renderer, MaterialPropertyBlock>();
            var hidden = new List<Renderer>();   // 마스크에서 잠깐 숨긴 투명 물체 (예: 작업대 위 반사판)
            try
            {
                cam.targetTexture = beautyRt;
                cam.Render();
                var beauty = Read(beautyRt);

                // 마스크: 부위 렌더러에만 번호 색, 나머지는 0(검정). 원래 프로퍼티 블록은 기억했다 되돌림
                if (myPost) myPost.enabled = false;
                foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    var b = new MaterialPropertyBlock();
                    if (r.HasPropertyBlock()) { var old = new MaterialPropertyBlock(); r.GetPropertyBlock(old); saved[r] = old; r.GetPropertyBlock(b); }
                    else saved[r] = null;
                    int idx = parts.FindIndex(p => core.PartOf(r) == p.id);
                    if (idx < 0 && r.enabled && IsSeeThrough(r)) { r.enabled = false; hidden.Add(r); }
                    b.SetVector(PartIdProp, new Vector4((idx + 1) / 255f, 0, 0, 1));
                    r.SetPropertyBlock(b);
                }
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
                cam.allowMSAA = false; cam.allowHDR = false;
                cam.targetTexture = maskRt;
                cam.RenderWithShader(shader, "");
                var mask = Read(maskRt);

                var sum = new double[parts.Count, 3]; var count = new int[parts.Count];
                for (int i = 0; i < mask.Length; i++)
                {
                    int k = mask[i].r - 1;
                    if (k < 0 || k >= parts.Count) continue;
                    sum[k, 0] += beauty[i].r; sum[k, 1] += beauty[i].g; sum[k, 2] += beauty[i].b; count[k]++;
                }
                for (int k = 0; k < parts.Count; k++)
                {
                    if (count[k] == 0) continue;
                    result.Add(new PartMeasure
                    {
                        id = parts[k].id, coverage = count[k] / (float)mask.Length,
                        rgb = new[] { (float)(sum[k, 0] / count[k] / 255.0), (float)(sum[k, 1] / count[k] / 255.0), (float)(sum[k, 2] / count[k] / 255.0) },
                    });
                }
            }
            finally
            {
                foreach (var kv in saved) if (kv.Key) kv.Key.SetPropertyBlock(kv.Value);
                foreach (var r in hidden) if (r) r.enabled = true;
                cam.targetTexture = null;
                RenderTexture.ReleaseTemporary(beautyRt); RenderTexture.ReleaseTemporary(maskRt);
                UnityEngine.Object.Destroy(go);
            }
            return result;
        }

        // 반투명·반사판처럼 뒤가 비치는 재질만 쓰는 렌더러 (마스크에서 부위를 가리면 안 됨)
        static bool IsSeeThrough(Renderer r)
        {
            foreach (var m in r.sharedMaterials) if (m && m.renderQueue < 2450) return false;
            return true;
        }

        static Color32[] Read(RenderTexture rt)
        {
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, rt.sRGB == false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels32();
            UnityEngine.Object.Destroy(tex);
            return px;
        }
    }
}
