Shader "Custom/DepthColorReprojection"
{
    // Forward warp: depth -> 3D world -> proiezione nella camera RGB -> colore.
    //
    // Per ogni pixel della depth (griglia di output = risoluzione depth):
    //   1. legge raw depth dalla slice 0 di _MainTex (Blit source, sempre legato)
    //   2. ricostruisce il punto 3D world con l'INVERSA della reproj matrix
    //      (_InvReprojMatrix = reprojMatrix.inverse, calcolata lato C#)
    //   3. proietta il punto nella camera RGB usando posa + intrinseci
    //   4. campiona _RGBTex a quelle UV e restituisce il colore
    //
    // Output = point cloud colorato in layout depth. Pixel senza depth valida,
    // dietro la camera, o fuori dal frame RGB -> nero.
    Properties
    {
        _MainTex("Depth Texture Array", 2DArray) = "white" {}
        _RGBTex ("RGB Texture", 2D) = "black" {}
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

            UNITY_DECLARE_TEX2DARRAY(_MainTex);
            sampler2D _RGBTex;

            // Inversa della reproj matrix: mappa il clip-space della depth -> world.
            float4x4 _InvReprojMatrix;

            // Posa camera RGB (world). _RGBRotation mappa cam -> world (come in DepthRegistration).
            float3   _RGBPosition;
            float4x4 _RGBRotation;

            // Intrinseci RGB (in pixel del sensore) + regione di crop sensore->UV immagine.
            float2 _FocalLength;
            float2 _PrincipalPoint;
            float4 _CropRegion; // (cropX, cropY, cropWidth, cropHeight)

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
                float raw = UNITY_SAMPLE_TEX2DARRAY(_MainTex, float3(i.uv, 0)).r;
                if (raw < 0.001) return float4(0,0,0,1); // nessuna depth qui

                // --- 1) depth pixel -> NDC del depth-clip ---
                // xy dalla UV, z dal valore raw (stessa convenzione raw*2-1 del linearize).
                float3 ndc = float3(i.uv.x * 2.0 - 1.0,
                                    i.uv.y * 2.0 - 1.0,
                                    raw     * 2.0 - 1.0);

                // --- 2) unproject a world con l'inversa della reproj matrix ---
                // Qualsiasi w va bene (transf. proiettiva): usiamo w=1 e dividiamo dopo.
                float4 worldH = mul(_InvReprojMatrix, float4(ndc, 1.0));
                float3 world  = worldH.xyz / worldH.w;

                // --- 3) proiezione nella camera RGB ---
                // world -> spazio camera RGB: R^-1 * (world - camPos). R e' ortonormale -> transpose.
                float3 camPoint = mul(transpose((float3x3)_RGBRotation), world - _RGBPosition);

                // Dietro la camera -> scarta.
                if (camPoint.z <= 1e-4) return float4(0,0,0,1);

                // Pinhole: coordinate sensore in pixel (nessuna Y-negation, coerente con DepthRegistration).
                float2 sensor = float2(
                    _FocalLength.x * camPoint.x / camPoint.z + _PrincipalPoint.x,
                    _FocalLength.y * camPoint.y / camPoint.z + _PrincipalPoint.y);

                // Sensore -> UV immagine (inversa del crop map: sensor = crop.xy + uv*crop.zw).
                float2 rgbUV = float2((sensor.x - _CropRegion.x) / _CropRegion.z,
                                      (sensor.y - _CropRegion.y) / _CropRegion.w);

                // Fuori dal frame RGB -> nero.
                if (rgbUV.x < 0.0 || rgbUV.x > 1.0 || rgbUV.y < 0.0 || rgbUV.y > 1.0)
                    return float4(0,0,0,1);

                // --- 4) campiona il colore RGB ---
                float3 col = tex2D(_RGBTex, rgbUV).rgb;
                return float4(col, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
