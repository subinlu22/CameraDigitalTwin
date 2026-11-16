// [접지 그림자] 물체가 바닥에 닿는 자리 아래에 까는 부드럽고 어두운 판 (곱하기로 어둡게)
// - 가운데는 진하고 가장자리로 갈수록 부드럽게 사라짐 (둥근 사각형 모양, 그림 파일 없이 계산)
// - 실시간 그림자가 못 그리는 "바닥에 딱 붙은 얇은 그림자"를 채워서 물체가 실제로 놓여 있는 느낌
Shader "SO101/ContactShadow"
{
    Properties
    {
        _Color ("그림자 색", Color) = (0.05, 0.08, 0.10, 1)
        _Opacity ("진하기", Range(0, 1)) = 0.6
        _Soft ("부드러운 가장자리 비율", Range(0.05, 1)) = 0.6
    }
    SubShader
    {
        // 거울 반사 덮개(Transparent)보다 나중에 그려서 그림자가 반사에 묻히지 않게 함
        Tags { "Queue"="Transparent+10" "RenderType"="Transparent" "IgnoreProjector"="True" }
        ZWrite Off
        Offset -1, -1
        Blend DstColor Zero
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color; float _Opacity, _Soft;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert (appdata_base v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord.xy; return o; }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 p = abs(i.uv - 0.5) * 2.0;                       // 0 = 가운데, 1 = 가장자리
                float2 q = max(p - (1.0 - _Soft), 0.0) / _Soft;         // 진한 가운데를 벗어난 만큼
                float a = saturate(1.0 - length(q));
                a = a * a * (3.0 - 2.0 * a);                            // 부드러운 곡선
                float3 shade = lerp(float3(1, 1, 1), _Color.rgb, _Opacity * a);
                return fixed4(shade, 1);
            }
            ENDCG
        }
    }
}
