using UnityEngine;

// =============================================================
// [밑판 거울 반사] 스텐 밑판에 로봇·벨트가 비치게 하는 스크립트 (Built-in 렌더용)
//
// 흐름: 메인 카메라가 이 판(밑판 위 얇은 덮개)을 그리기 직전에
//   ① 판 면을 거울로 삼아 카메라를 아래로 뒤집은 "반사 카메라"를 만들고
//   ② 판 아래쪽은 잘라낸 채(비스듬한 근평면) 장면을 한 번 더 찍어서
//   ③ 그 그림을 덮개 재질(SO101/PlanarReflectionOverlay)에 넘김 → 화면 좌표로 겹쳐 그림
// - 메인 카메라일 때만 찍음 (씬 뷰·반사 프로브 카메라는 무시)
// - 덮개는 Water 레이어(4번)에 두고 반사 카메라에선 빼서 자기 자신을 안 찍음
// - 해상도는 화면의 1/textureScale (기본 절반) → 부담 줄임
// =============================================================
[RequireComponent(typeof(Renderer))]
public class PlanarReflection : MonoBehaviour
{
    public int textureScale = 2;
    [Min(0)] public float reflectionDistance; // 반사에 담을 거리(m). 0이면 메인 카메라가 보는 만큼 전부
    public Color reflectionBackground = new Color(0.035f, 0.065f, 0.085f);
    readonly float[] reflectionCullDistances = new float[32];
    public float clipPlaneOffset = 0.002f;
    public LayerMask reflectLayers = ~((1 << 4) | (1 << 5));   // Water(덮개), UI 빼고 전부

    Camera reflCam;
    RenderTexture rt;
    Material mat;
    static bool s_Inside;                                       // 반사 찍는 중에 또 불리는 것 막기
    static readonly int ReflTex = Shader.PropertyToID("_ReflectionTex");

    void OnEnable() { mat = GetComponent<Renderer>().sharedMaterial; }

    void OnDisable()
    {
        if (rt) { rt.Release(); Destroy(rt); rt = null; }
        if (reflCam) { Destroy(reflCam.gameObject); reflCam = null; }
    }

    void OnWillRenderObject()
    {
        Camera cam = Camera.current;
        if (!enabled || cam == null || (cam != Camera.main && cam.cameraType != CameraType.SceneView) || s_Inside || mat == null) return;
        s_Inside = true;
        try { RenderReflection(cam); }
        finally { s_Inside = false; }
    }

    void RenderReflection(Camera cam)
    {
        int w = Mathf.Max(64, cam.pixelWidth / textureScale), h = Mathf.Max(64, cam.pixelHeight / textureScale);
        if (rt == null || rt.width != w || rt.height != h)
        {
            if (rt) { rt.Release(); Destroy(rt); }
            rt = new RenderTexture(w, h, 24, RenderTextureFormat.DefaultHDR) { name = "PlanarReflection", hideFlags = HideFlags.DontSave, useMipMap = true, autoGenerateMips = true };   // 흐린 축소본(밉맵)도 자동으로 → 셰이더가 뭉개서 읽음
        }
        if (reflCam == null)
        {
            var go = new GameObject("_PlanarReflectionCamera") { hideFlags = HideFlags.HideAndDontSave };
            reflCam = go.AddComponent<Camera>();
            reflCam.enabled = false;
        }
        reflCam.CopyFrom(cam);
        reflCam.allowMSAA = false;
        reflCam.cullingMask = reflectLayers & cam.cullingMask;
        reflCam.targetTexture = rt;

        // 판 면 = 거울. 법선 = 판이 보이는 쪽 (유니티 Quad는 -Z 쪽이 앞면 → X로 90° 눕히면 위를 봄)
        Vector3 pos = transform.position, normal = -transform.forward;
        float d = -Vector3.Dot(normal, pos) - clipPlaneOffset;
        Vector4 plane = new Vector4(normal.x, normal.y, normal.z, d);
        Matrix4x4 refl = ReflectionMatrix(plane);
        Vector3 reflectedPosition = refl.MultiplyPoint(cam.transform.position);
        Vector3 reflectedForward = refl.MultiplyVector(cam.transform.forward);
        Vector3 reflectedUp = refl.MultiplyVector(cam.transform.up);
        reflCam.transform.SetPositionAndRotation(reflectedPosition, Quaternion.LookRotation(reflectedForward, reflectedUp));
        reflCam.worldToCameraMatrix = cam.worldToCameraMatrix * refl;

        // 판 아래(거울 뒤)는 잘라냄
        Vector4 clip = CameraSpacePlane(reflCam, pos, normal);
        if (reflectionDistance > 0f)
        {
            float[] sourceCullDistances = cam.layerCullDistances;
            reflCam.farClipPlane = Mathf.Min(cam.farClipPlane, reflectionDistance);
            reflCam.ResetProjectionMatrix();
            reflCam.projectionMatrix = reflCam.CalculateObliqueMatrix(clip);
            // 비스듬한 근평면 때문에 먼 쪽 경계가 틀어짐 → 거리로도 한 번 더 잘라서 먼 물체는 반사에서 뺌
            for (int i = 0; i < reflectionCullDistances.Length; i++)
            {
                float sourceLimit = sourceCullDistances[i];
                reflectionCullDistances[i] = sourceLimit > 0f ? Mathf.Min(sourceLimit, reflectionDistance) : reflectionDistance;
            }
            reflCam.layerCullDistances = reflectionCullDistances;
            reflCam.layerCullSpherical = true;
            reflCam.clearFlags = CameraClearFlags.SolidColor;
            reflCam.backgroundColor = reflectionBackground;
        }
        else
        {
            reflCam.projectionMatrix = cam.CalculateObliqueMatrix(clip);
        }

        bool oldCull = GL.invertCulling;
        GL.invertCulling = !oldCull;            // 거울이라 앞뒤가 뒤집힘
        try { reflCam.Render(); }
        finally { GL.invertCulling = oldCull; }

        mat.SetTexture(ReflTex, rt);
    }

    Vector4 CameraSpacePlane(Camera c, Vector3 pos, Vector3 normal)
    {
        Vector3 offsetPos = pos + normal * clipPlaneOffset;
        Matrix4x4 m = c.worldToCameraMatrix;
        Vector3 cpos = m.MultiplyPoint(offsetPos);
        Vector3 cnormal = m.MultiplyVector(normal).normalized;
        return new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));
    }

    static Matrix4x4 ReflectionMatrix(Vector4 p)
    {
        Matrix4x4 r = Matrix4x4.identity;
        r.m00 = 1F - 2F * p[0] * p[0]; r.m01 = -2F * p[0] * p[1]; r.m02 = -2F * p[0] * p[2]; r.m03 = -2F * p[3] * p[0];
        r.m10 = -2F * p[1] * p[0]; r.m11 = 1F - 2F * p[1] * p[1]; r.m12 = -2F * p[1] * p[2]; r.m13 = -2F * p[3] * p[1];
        r.m20 = -2F * p[2] * p[0]; r.m21 = -2F * p[2] * p[1]; r.m22 = 1F - 2F * p[2] * p[2]; r.m23 = -2F * p[3] * p[2];
        r.m30 = 0F; r.m31 = 0F; r.m32 = 0F; r.m33 = 1F;
        return r;
    }
}
