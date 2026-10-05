Shader "Custom/DepthLinearExport"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            UNITY_DECLARE_TEX2DARRAY(_EnvironmentDepthTexture);
            uniform float4 _EnvironmentDepthZBufferParams;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float raw = UNITY_SAMPLE_TEX2DARRAY(_EnvironmentDepthTexture, float3(i.uv, 0)).r;
                float ndc = raw * 2.0 - 1.0;
                float linearDepth = (1.0 / (ndc + _EnvironmentDepthZBufferParams.y)) * _EnvironmentDepthZBufferParams.x;
                return float4(linearDepth, linearDepth, linearDepth, 1.0);
            }
            ENDHLSL
        }
    }
}