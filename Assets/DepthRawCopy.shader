Shader "Custom/DepthRawCopy"
{
    // Copies slice 0 (left eye) of the environment depth Texture2DArray into a
    // float render target WITHOUT any modification. Used to save the unregistered
    // raw depth (reversed-Z buffer values, raw=0 near .. raw=1 far) to rawDepth.exr.
    //
    // Samples _MainTex (the Blit source) exactly like the working Custom/DepthPreview
    // shader. Writes the raw value into all RGB channels (no 1.0-depth inversion) so
    // EncodeToEXR preserves it regardless of which channel the reader picks.
    Properties { _MainTex("Depth Texture Array", 2DArray) = "white" {} }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma require 2darray
            #include "UnityCG.cginc"

            UNITY_DECLARE_TEX2DARRAY(_MainTex);

            float4 frag(v2f_img i) : SV_Target
            {
                // Raw reversed-Z depth value, slice 0 (left eye), unmodified.
                float d = UNITY_SAMPLE_TEX2DARRAY(_MainTex, float3(i.uv, 0)).r;
                return float4(d, d, d, 1.0);
            }
            ENDCG
        }
    }
    FallBack Off
}
