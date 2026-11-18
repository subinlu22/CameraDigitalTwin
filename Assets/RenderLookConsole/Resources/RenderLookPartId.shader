// 부위 마스크용: 물체를 _PartId 색 한 가지로만 칠함 (조명·후처리 없음). RenderLookMeasure가 대체 셰이더로 씀
Shader "Hidden/RenderLook/PartId"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _PartId;
            float4 vert(float4 p : POSITION) : SV_POSITION { return UnityObjectToClipPos(p); }
            fixed4 frag() : SV_Target { return _PartId; }
            ENDCG
        }
    }
}
