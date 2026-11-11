// 유리 패널 뒤 흐림 (듀얼 카와세: 내려가며 줄이고 올라오며 퍼뜨림). DashboardGlassBlur가 씀 (ProductRenderStudio에서 가져옴)
Shader "Hidden/SO101/GlassBlur"
{
    Properties { _MainTex ("", 2D) = "white" {} _Offset ("", Float) = 1 }
    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex; float4 _MainTex_TexelSize; float _Offset;
    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
    v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }

    half4 down(v2f i) : SV_Target
    {
        float2 h = _MainTex_TexelSize.xy * 0.5 * _Offset;
        half4 s = tex2D(_MainTex, i.uv) * 4;
        s += tex2D(_MainTex, i.uv - h);
        s += tex2D(_MainTex, i.uv + h);
        s += tex2D(_MainTex, i.uv + float2(h.x, -h.y));
        s += tex2D(_MainTex, i.uv - float2(h.x, -h.y));
        return s / 8;
    }
    half4 up(v2f i) : SV_Target
    {
        float2 h = _MainTex_TexelSize.xy * 0.5 * _Offset;
        half4 s = tex2D(_MainTex, i.uv + float2(-h.x * 2, 0));
        s += tex2D(_MainTex, i.uv + float2(-h.x, h.y)) * 2;
        s += tex2D(_MainTex, i.uv + float2(0, h.y * 2));
        s += tex2D(_MainTex, i.uv + float2(h.x, h.y)) * 2;
        s += tex2D(_MainTex, i.uv + float2(h.x * 2, 0));
        s += tex2D(_MainTex, i.uv + float2(h.x, -h.y)) * 2;
        s += tex2D(_MainTex, i.uv + float2(0, -h.y * 2));
        s += tex2D(_MainTex, i.uv + float2(-h.x, -h.y)) * 2;
        return s / 12;
    }
    ENDCG
    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        Pass { CGPROGRAM #pragma vertex vert
               #pragma fragment down
               ENDCG }
        Pass { CGPROGRAM #pragma vertex vert
               #pragma fragment up
               ENDCG }
    }
}
