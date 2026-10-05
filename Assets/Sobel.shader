Shader "Custom/DepthSobel"
{
    // Gate del gradiente relativo sulla DEPTH GIÀ ALLINEATA (output di
    // Custom/AlignedDepthExport): texture float in cui il canale R contiene la depth
    // metrica in metri e 0 = pixel senza dato depth.
    //
    // Porta della reference sobel_depth_mask (PyTorch), che applica la maschera alla
    // depth (equivalente a  depth * mask):
    //   - Sobel 3x3 sulla depth metrica  ->  |∇D|
    //   - gradiente RELATIVO  |∇D| / D    (obbligatorio: su depth metrica |∇D| cresce
    //     con la distanza, quindi una soglia assoluta o buca il campo lontano o passa
    //     ogni bordo vicino)
    //   - un pixel viene SCARTATO quando  |∇D| / D  > tau_rel
    //
    // L'output è DEPTH MASCHERATA: il valore metrico originale (in metri, canale R) dove
    // il gate passa, 0 dove il pixel viene scartato (edge-bleeding sulle discontinuità)
    // o è già invalido. Quindi si legge come una normale depth metrica, ma coi bordi
    // ripuliti PRIMA della unprojection.
    //
    // Uso: Graphics.Blit(alignedDepthTex, rt, sobelMaterial), con rt in
    // RenderTextureFormat.ARGBFloat.
    Properties
    {
        _MainTex("Aligned Depth (metric, R = meters)", 2D) = "black" {}
        // Soglia del gradiente relativo (adimensionale, indipendente dalla distanza).
        // Un pixel è scartato quando |∇D|/D > tau_rel. Tipico: 0.05 (5% per texel).
        // <= 0 SALTA il gate: tutti i pixel con D > 0 sono tenuti (ablation/baseline).
        _TauRel("Relative Gradient Threshold", Float) = 0.05
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always
        LOD 200

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            // xy = 1/width,1/height  zw = width,height. Riempito automaticamente da Unity.
            float4 _MainTex_TexelSize;
            float _TauRel;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            // Depth metrica grezza (canale R) al texel offset (dx,dy) dal centro.
            // Come nella reference: i pixel invalidi valgono 0 e ENTRANO nel kernel così
            // come sono (uno zero adiacente genera un gradiente grande, quindi anche i
            // bordi dei buchi vengono scartati). Nessuna sostituzione col centro.
            // Fuori dai bordi dell'immagine ritorna 0, per replicare lo zero-padding di
            // F.conv2d(padding=1) invece del clamp di default di tex2D.
            float D(float2 uv, float2 offset)
            {
                float2 s = uv + offset * _MainTex_TexelSize.xy;
                if (s.x < 0.0 || s.x > 1.0 || s.y < 0.0 || s.y > 1.0)
                    return 0.0;
                return tex2D(_MainTex, s).r;
            }

            float4 frag(v2f i) : SV_Target
            {
                float center = tex2D(_MainTex, i.uv).r;

                // valid = depth > 0. I pixel invalidi restano invalidi (depth = 0).
                if (center <= 0.0)
                    return float4(0.0, 0.0, 0.0, 1.0);

                // tau_rel <= 0: salta il gate, tieni la depth di tutti i pixel con D > 0.
                if (_TauRel <= 0.0)
                    return float4(center, center, center, 1.0);

                // Kernel Sobel 3x3 (stessi segni della reference: il segno non conta,
                // si usa la magnitudine).
                float tl = D(i.uv, float2(-1, -1));
                float tc = D(i.uv, float2( 0, -1));
                float tr = D(i.uv, float2( 1, -1));
                float ml = D(i.uv, float2(-1,  0));
                float mr = D(i.uv, float2( 1,  0));
                float bl = D(i.uv, float2(-1,  1));
                float bc = D(i.uv, float2( 0,  1));
                float br = D(i.uv, float2( 1,  1));

                // Kx = [[-1,0,1],[-2,0,2],[-1,0,1]]
                float gx = (-tl - 2.0 * ml - bl) + (tr + 2.0 * mr + br);
                // Ky = [[-1,-2,-1],[0,0,0],[1,2,1]]
                float gy = (-tl - 2.0 * tc - tr) + (bl + 2.0 * bc + br);

                float gradMag = sqrt(gx * gx + gy * gy);

                // Gradiente relativo (center > 0 qui, divisione sicura).
                float relGrad = gradMag / center;

                // keep se relGrad <= tau_rel: emetti la depth originale, altrimenti 0.
                float outDepth = (relGrad <= _TauRel) ? center : 0.0;
                return float4(outDepth, outDepth, outDepth, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
