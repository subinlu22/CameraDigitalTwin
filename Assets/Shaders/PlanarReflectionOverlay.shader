// [밑판 거울 반사 덮개] PlanarReflection 스크립트가 찍은 "거꾸로 본 화면"을 밑판 위 얇은 판에 살짝 겹쳐 그림
// - 화면 좌표로 반사 그림을 읽음 (거울 반사의 기본 방식)
// - 반사 그림의 흐린 축소본(밉맵)을 읽어서 부드럽게 뭉갬 → 작은 밝은 점이 자글자글 찍히지 않음
// - 헤어라인 스텐 느낌: 반사를 세로로 살짝 번지게 (결 방향과 직각으로 늘어짐)
// - 비스듬히 볼수록 더 잘 비침 (프레넬)
Shader "SO101/PlanarReflectionOverlay"
{
    Properties
    {
        _ReflectionTex ("Reflection", 2D) = "black" {}
        _Strength ("세기", Range(0, 1)) = 0.35
        _Blur ("결 번짐 (픽셀)", Range(0, 40)) = 10
        _BlurX ("가로 번짐 비율", Range(0, 1)) = 0.5
        _Lod ("뭉갬 정도 (축소본 단계)", Range(0, 7)) = 3
        _FresnelPower ("비스듬할수록 (지수)", Range(0, 8)) = 2.5
        _Tint ("색", Color) = (0.92, 0.96, 1, 1)
        _EdgeFade ("가장자리 흐림", Range(0.001, 0.5)) = 0.06
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _ReflectionTex; float4 _ReflectionTex_TexelSize;
            float _Strength, _Blur, _BlurX, _FresnelPower, _EdgeFade, _Lod;
            fixed4 _Tint;

            struct v2f { float4 pos : SV_POSITION; float4 screen : TEXCOORD0; float3 worldPos : TEXCOORD1; float2 uv : TEXCOORD2; };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.screen = ComputeScreenPos(o.pos);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.uv = v.texcoord.xy;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.screen.xy / i.screen.w;
                // 반무광(새틴): 세로로 길게 + 가로로도 조금 번지게 (십자 모양으로 읽어서 평균)
                float stepY = _Blur * _ReflectionTex_TexelSize.y / 3.0;
                float stepX = _Blur * _BlurX * _ReflectionTex_TexelSize.x / 3.0;
                float3 c = 0; float wsum = 0;
                [unroll] for (int k = -3; k <= 3; k++)
                {
                    float w = 1.0 - abs(k) / 4.0;
                    c += tex2Dlod(_ReflectionTex, float4(uv + float2(0, k * stepY), 0, _Lod)).rgb * w;
                    c += tex2Dlod(_ReflectionTex, float4(uv + float2(k * stepX, k * stepY * 0.5), 0, _Lod)).rgb * w * 0.6;
                    c += tex2Dlod(_ReflectionTex, float4(uv + float2(-k * stepX, k * stepY * 0.5), 0, _Lod)).rgb * w * 0.6;
                    wsum += w * 2.2;
                }
                c /= wsum;
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
                float f = pow(1.0 - saturate(viewDir.y), _FresnelPower);
                float2 e = smoothstep(0, _EdgeFade, i.uv) * smoothstep(0, _EdgeFade, 1 - i.uv);
                float a = _Strength * lerp(0.3, 1.0, f) * e.x * e.y;
                return fixed4(c * _Tint.rgb, a);
            }
            ENDCG
        }
    }
}
