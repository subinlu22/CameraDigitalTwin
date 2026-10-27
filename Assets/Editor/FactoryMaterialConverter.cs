using UnityEditor;
using UnityEngine;

// Unity Factory(HDRP Shader Graph) 재질 → Built-in Standard 로 바꾸기
// 흐름: 재질에 저장된 원래 값(텍스처·색·숫자)을 이름으로 찾아서 Standard 칸에 옮겨 담음
//   이름에 Main/Albedo/Base 들어간 텍스처 → 색 텍스처, Normal/_N → 노멀맵,
//   Color → 색, Metal → 금속성, Smooth → 매끈함, Emiss → 발광
public static class FactoryMaterialConverter
{
    const string Folder = "Assets/ThirdParty/UnityFactory/Materials";

    [MenuItem("SO101/Unity Factory 재질 변환")]
    public static void ConvertAll()
    {
        var standard = Shader.Find("Standard");
        int n = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { Folder }))
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (mat == null) continue;  // 다시 실행해도 원래 저장값에서 새로 변환
            Convert(mat, standard);
            n++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[FactoryMaterialConverter] {n}개 변환");
    }

    static void Convert(Material mat, Shader standard)
    {
        Texture albedo = null, normal = null, ao = null;
        Color? color = null, emission = null;
        Vector2 tiling = Vector2.one;
        var f = new System.Collections.Generic.Dictionary<string, float>();

        var so = new SerializedObject(mat);
        var props = so.FindProperty("m_SavedProperties");
        foreach (SerializedProperty p in props.FindPropertyRelative("m_TexEnvs"))
        {
            string name = p.FindPropertyRelative("first").stringValue;
            var tex = p.FindPropertyRelative("second.m_Texture").objectReferenceValue as Texture;
            if (tex == null) continue;
            if (normal == null && (name.Contains("Normal") || name.EndsWith("_N"))) normal = tex;
            else if (ao == null && (name.Contains("Ao_") || name.Contains("Occlu"))) ao = tex;
            else if (albedo == null && (name.Contains("Main") || name.Contains("Albedo") || name.Contains("Base"))) albedo = tex;
        }
        foreach (SerializedProperty p in props.FindPropertyRelative("m_Colors"))
        {
            string name = p.FindPropertyRelative("first").stringValue;
            var c = p.FindPropertyRelative("second").colorValue;
            if (name.Contains("Emiss")) emission = c;
            else if (name == "_Tiling" || name == "_Albedo_Tiling") tiling = new Vector2(c.r, c.g);
            else if (name == "_BaseColor") color = c;
            else if (color == null && name.Contains("Color")) color = c;
        }
        foreach (SerializedProperty p in props.FindPropertyRelative("m_Floats"))
        {
            f[p.FindPropertyRelative("first").stringValue] = p.FindPropertyRelative("second").floatValue;
        }

        mat.shader = standard;
        mat.SetTexture("_MainTex", albedo);
        var baseC = color ?? Color.white; baseC.a = 1f;
        mat.SetColor("_Color", baseC);
        // 노멀은 약하게 (원본 세기 그대로면 Built-in에서 회벽처럼 울퉁불퉁해 보임)
        if (normal != null) { mat.SetTexture("_BumpMap", normal); mat.SetFloat("_BumpScale", 0.3f); mat.EnableKeyword("_NORMALMAP"); }
        // 금속성·매끈함: Shader Graph는 _Metallic_Max/_Smoothness_Max에 실제 값을 둠 (없으면 HDRP 기본 칸)
        float Get(string a, string b, float d) => f.ContainsKey(a) ? f[a] : f.ContainsKey(b) ? f[b] : d;
        // 원래는 텍스처로 Min~Max 사이를 칠함 → 텍스처 없이 쓰니 가운데 값
        float Mid(string k, float d) => f.ContainsKey(k + "_Max") ? (f[k + "_Max"] + Get(k + "_Min", k + "_Min", f[k + "_Max"])) * 0.5f : Get(k, k, d);
        // Built-in은 반사 환경이 HDRP만큼 없어서 금속성 1이면 검게 보임 → 0.6까지만
        mat.SetFloat("_Metallic", Mathf.Clamp(Mid("_Metallic", 0f), 0f, 0.6f));
        mat.SetFloat("_Glossiness", Mathf.Clamp01(Mid("_Smoothness", 0.4f)));
        if (tiling.x > 0 && tiling.y > 0) mat.mainTextureScale = tiling;
        if (ao != null) { mat.SetTexture("_OcclusionMap", ao); mat.SetFloat("_OcclusionStrength", 1f); }

        // 투명 (HDRP _SurfaceType=1 → Standard Transparent): 유리·아크릴
        bool transparent = Get("_SurfaceType", "_SurfaceType", 0f) > 0.5f;
        if (transparent)
        {
            var c = color ?? Color.white;
            c.a = Mathf.Max(c.a, 0.12f);  // 알파 0이면 아예 안 보여서 살짝 남김
            mat.SetColor("_Color", c);
            mat.SetFloat("_Mode", 3f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON"); mat.DisableKeyword("_ALPHABLEND_ON"); mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }
        else
        {
            mat.SetFloat("_Mode", 0f);
            mat.SetInt("_SrcBlend", 1); mat.SetInt("_DstBlend", 0); mat.SetInt("_ZWrite", 1);
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = -1;
        }
        // 차가운 은색 톤 (원본 영상처럼 알루미늄·스테인리스가 은색으로 보이게)
        string nm = mat.name.ToLower();
        if (!transparent && (nm.Contains("frame") || nm.Contains("almi") || nm.Contains("sus") || nm.Contains("chrome")
            || nm.Contains("cchannel") || nm.Contains("support") || nm.Contains("spacer") || nm.Contains("duct") || nm.Contains("pipe")))
        {
            mat.SetColor("_Color", new Color(0.74f, 0.77f, 0.81f));
            mat.SetFloat("_Metallic", 0.35f);  // 0.6이면 주변 반사가 어두울 때 검게 보임
            mat.SetFloat("_Glossiness", 0.75f);
        }
        // 벽·기둥·천장은 하얗게 (그림자와 대비되게)
        if (mat.name == "Wall" || mat.name == "Pillar" || mat.name == "Ceiling" || mat.name == "Skirt")
        {
            mat.SetColor("_Color", new Color(1.1f, 1.1f, 1.12f));
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Glossiness", 0.2f);
        }
        // 바닥: 에폭시 코팅 느낌 (수빈이 고른 어두운 회색 + 은은한 광)
        if (mat.name == "Floor")
        {
            mat.SetColor("_Color", new Color(0.22f, 0.22f, 0.22f));
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Glossiness", 0.75f);
            mat.SetFloat("_BumpScale", 0.1f);
        }
        // 검은 부품(프레임·벨트·고무·제어반)은 배경이 지저분해 보여서 라이트그레이 메탈로
        if (!transparent && (nm.Contains("black") || nm.Contains("rubber") || nm.Contains("belt") || nm.Contains("moter") || nm.Contains("cable") || nm == "gap"))
        {
            mat.SetTexture("_MainTex", null);
            mat.SetColor("_Color", new Color(0.6f, 0.63f, 0.67f));
            mat.SetFloat("_Metallic", 0.3f);
            mat.SetFloat("_Glossiness", 0.6f);
        }

        // 발광: 저장된 발광색은 HDRP 기본값(빨강)이라 안 씀. 조명 재질만 흰색으로 빛나게
        bool lamp = mat.name.Contains("lamp") || mat.name == "Light";
        if (lamp)
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", new Color(0.9f, 0.95f, 1f) * 2f);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        else
        {
            mat.DisableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", Color.black);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        }
        EditorUtility.SetDirty(mat);
    }
}
