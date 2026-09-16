Shader "Hidden/TexturePackEditor/ReadNormal"
{
    Properties { _MainTex ("Normal map", 2D) = "bump" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _UvRect;
            float4 frag(v2f_img input) : SV_Target
            {
                float2 uv = _UvRect.xy + input.uv * _UvRect.zw;
                float3 normal = UnpackNormal(tex2D(_MainTex, uv));
                return float4(normal * .5 + .5, 1);
            }
            ENDCG
        }
    }
}
