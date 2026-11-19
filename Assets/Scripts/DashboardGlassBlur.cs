using UnityEngine;
using UnityEngine.Rendering;

// =============================================================
// [유리 패널 뒤 흐린 화면] 카메라가 다 그린 화면(후처리까지)을 복사해서 줄이며 흐리게 → Blurred
// 대시보드(DashboardUI)가 이 텍스처를 패널 위치에 맞춰 깔고 그 위에 반투명 색을 덮음 = 진짜 유리
//
// 흐름: 카메라 그리기 끝(AfterImageEffects) → 1/2 → 1/4 → … 로 줄이며 흐림 → 다시 키우며 퍼뜨림 (듀얼 카와세)
// ※ ProductRenderStudio(렌더 툴)의 GlassBackdrop을 이 프로젝트용으로 가져온 것
// ※ UI(오버레이 캔버스)는 카메라 다음에 그려져서 흐림에 안 들어감 → 패널이 자기 자신을 흐리는 일 없음
// =============================================================
[RequireComponent(typeof(Camera))]
public class DashboardGlassBlur : MonoBehaviour
{
    [Range(2, 6)] public int iterations = 4;   // 많을수록 더 흐림 (단계마다 크기 절반)
    [Range(0.5f, 3f)] public float spread = 1.4f;

    public static RenderTexture Blurred { get; private set; }
    public static RenderTexture SceneFrame { get; private set; }

    Camera cam;
    Material mat;
    CommandBuffer cb;
    int w, h;
    RenderTexture[] chain;
    RenderTexture sceneFrame;

    void OnEnable()
    {
        cam = GetComponent<Camera>();
        // 빌드 exe에서는 흐린 화면이 밝은 회색 한 덩어리로 나와 패널이 하얗게 뜸(11/19, 원인 확인 못 함)
        // → exe에서는 흐림을 끄고 패널이 "흐림 없는 어두운 반투명" 색을 쓰게 함 (에디터는 그대로 흐림)
        if (!Application.isEditor) { enabled = false; return; }
        // Resources 폴더에 둬서 빌드 exe에도 셰이더가 들어감
        Shader shader = Resources.Load<Shader>("Shaders/GlassBlur");
        if (shader == null) { Debug.LogWarning("[대시보드] 유리 흐림 셰이더가 없음 → 흐림 없이 반투명만"); enabled = false; return; }
        mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
    }

    void OnDisable()
    {
        if (cb != null && cam) cam.RemoveCommandBuffer(CameraEvent.AfterImageEffects, cb);
        cb?.Release(); cb = null;
        ReleaseChain();
        if (mat) Destroy(mat);
    }

    // 화면 크기가 바뀔 때만 흐림 단계를 다시 만듦
    void OnPreRender()
    {
        int nw = Mathf.Max(1, cam.pixelWidth), nh = Mathf.Max(1, cam.pixelHeight);
        if (cb != null && nw == w && nh == h) return;
        if (cb != null) { cam.RemoveCommandBuffer(CameraEvent.AfterImageEffects, cb); cb.Release(); }
        ReleaseChain();
        w = nw; h = nh;

        chain = new RenderTexture[iterations + 1];
        for (int i = 0; i <= iterations; i++)
        {
            int d = 1 << (i + 1);   // 1/2, 1/4, 1/8 …
            chain[i] = new RenderTexture(Mathf.Max(1, w / d), Mathf.Max(1, h / d), 0, RenderTextureFormat.ARGB32)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        }
        sceneFrame = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
        { name = "Equipment preview source", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
          hideFlags = HideFlags.HideAndDontSave };
        cb = new CommandBuffer { name = "Dashboard glass blur" };
        cb.Blit(BuiltinRenderTextureType.CameraTarget, sceneFrame);
        SceneFrame = sceneFrame;
        cb.SetGlobalFloat("_Offset", spread);
        cb.Blit(BuiltinRenderTextureType.CameraTarget, chain[0], mat, 0);
        for (int i = 1; i <= iterations; i++) cb.Blit(chain[i - 1], chain[i], mat, 0);      // 줄이며 흐림
        for (int i = iterations - 1; i >= 0; i--) cb.Blit(chain[i + 1], chain[i], mat, 1); // 키우며 퍼뜨림
        cam.AddCommandBuffer(CameraEvent.AfterImageEffects, cb);
        Blurred = chain[0];
    }

    void ReleaseChain()
    {
        if (chain != null) foreach (var rt in chain) if (rt) { rt.Release(); Destroy(rt); }
        if (sceneFrame)
        {
            if (SceneFrame == sceneFrame) SceneFrame = null;
            sceneFrame.Release();
            Destroy(sceneFrame);
            sceneFrame = null;
        }
        chain = null;
        Blurred = null;
    }
}
