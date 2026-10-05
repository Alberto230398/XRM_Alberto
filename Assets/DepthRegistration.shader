Shader "Custom/AlignedDepthExport"
{
    Properties
    {
        _MainTex("Depth Texture Array", 2DArray) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma require 2darray
            #include "UnityCG.cginc"

            // Depth Texture2DArray fornita come sorgente del Blit (slot _MainTex).
            // NON usare il globale _EnvironmentDepthTexture: in un contesto di
            // Graphics.Blit non è garantito legato e il sample torna nero,
            // producendo una texture di export vuota.
            UNITY_DECLARE_TEX2DARRAY(_MainTex);
            uniform float4 _EnvironmentDepthZBufferParams;

            float4x4 _ReprojMatrix;
            float3 _RGBPosition;
            float4x4 _RGBRotation;
            float2 _FocalLength;
            float2 _PrincipalPoint;
            float2 _SensorResolution;
            // Crop region in sensor pixels: (cropX, cropY, cropWidth, cropHeight)
            // Derived from SDK's CalcSensorCropRegion: maps image UV → sensor coords
            float4 _CropRegion;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            // Reversed-Z infinite projection: z_eye = near / rawDepth
            // zParams=(-0.20,-1.00,0,0) -> x/(y*raw) = -0.20/(-1.0*raw) = 0.20/raw
            // rawDepth=1 -> z_eye=0.20m (near plane), rawDepth->0 -> z_eye->inf (far)
            float LinearizeDepth(float rawDepth)
            {
                float ndc = rawDepth * 2.0 - 1.0;
                return _EnvironmentDepthZBufferParams.x / (ndc + _EnvironmentDepthZBufferParams.y);
            }

            float4 frag(v2f i) : SV_Target
            {
                // Map image UV -> sensor pixel coords using crop region.
                // Camera runs at 1280x960 on a 1280x1280 sensor: cropY=160, cropH=960.
                // Without this, sensor.y would span [0,1280] instead of [160,1120].
                float2 sensor = float2(_CropRegion.x + i.uv.x * _CropRegion.z,
                                       _CropRegion.y + i.uv.y * _CropRegion.w);

                // Unproject to camera-space ray - no Y-negation (sensor Y is already up)
                float3 d_cam = float3(
                    (sensor.x - _PrincipalPoint.x) / _FocalLength.x,
                    (sensor.y - _PrincipalPoint.y) / _FocalLength.y,
                    1.0
                );

                // World-space ray direction
                float3 d_world = mul((float3x3)_RGBRotation, d_cam);

                // clip(t) = A + t*B  for worldPos = RGBPos + t * d_world
                // Correct order: mul(matrix, vector) - confirmed by Meta's EnvironmentOcclusion.cginc line 85
                float4 A = mul(_ReprojMatrix, float4(_RGBPosition, 1.0));
                float4 B = mul(_ReprojMatrix, float4(d_world, 0.0));

                // Analytical t-solve. linZ = zx/(ndc+zy), ndc = clip.z/clip.w
                // -> k = zx/linZ - zy = clip.z/clip.w = (A.z+t*B.z)/(A.w+t*B.w)
                // -> t = (k*A.w - A.z) / (B.z - k*B.w)
                float zx = _EnvironmentDepthZBufferParams.x;
                float zy = _EnvironmentDepthZBufferParams.y;

                float t = 1.0; // initial guess: 1 meter

                [unroll]
                for (int iter = 0; iter < 7; iter++)
                {
                    float4 clip = A + t * B;
                    float2 depthUV = clip.xy / clip.w * 0.5 + 0.5;

                    if (depthUV.x < 0.0 || depthUV.x > 1.0 ||
                        depthUV.y < 0.0 || depthUV.y > 1.0)
                        return float4(0.0, 0.0, 0.0, 1.0);

                    float rawDepth = UNITY_SAMPLE_TEX2DARRAY(_MainTex, float3(depthUV, 0)).r;
                    if (rawDepth < 0.001) return float4(0.0, 0.0, 0.0, 1.0); // no depth data at this pixel
                    float linZ = LinearizeDepth(rawDepth);

                    float k = zx / linZ - zy;
                    float denom = B.z - k * B.w;
                    if (abs(denom) < 1e-5)
                        return float4(0.0, 0.0, 0.0, 1.0);

                    t = (k * A.w - A.z) / denom;
                }

                // Final bounds check
                float4 finalClip = A + t * B;
                float2 finalUV = finalClip.xy / finalClip.w * 0.5 + 0.5;
                if (finalUV.x < 0.0 || finalUV.x > 1.0 ||
                    finalUV.y < 0.0 || finalUV.y > 1.0)
                    return float4(0.0, 0.0, 0.0, 1.0);

                // t is the z-depth in RGB camera space (since d_cam.z = 1.0).
                // Scritto su RGB (grayscale) come una depth map normale; il valore
                // metrico in metri resta leggibile da qualsiasi canale.
                float depth = max(t, 0.0);
                return float4(depth, depth, depth, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
