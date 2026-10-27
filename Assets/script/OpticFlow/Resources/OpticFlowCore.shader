// OpticFlowCore — 实时 Optic Flow 的 GPU 实现（Lucas-Kanade + 区域归约）。
// 只用于内部 Graphics.Blit，不直接渲染到屏幕，最终画面由 OnRenderImage 原样透传。
//
// Pass 0  灰度 + 降采样：src(全分辨率) → 低分辨率灰度图
// Pass 1  Lucas-Kanade：   上一帧/当前帧灰度图 → 光流 (u,v) 单位 px/frame，存 RG
// Pass 2  第一级归约：     光流 → (whole, left, right, 0) 区域和，输出半尺寸
// Pass 3  后续归约：       上一级累积 → 下一级累积（纯加），输出半尺寸
Shader "OpticFlow/OpticFlowCore"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _PrevTex ("Previous Frame", 2D) = "white" {}
        _TexelSize ("Texel Size (1/w,1/h,w,h)", Vector) = (0,0,0,0)
        _LKThreshold ("LK Determinant Threshold", Float) = 0.0001
        _SrcSize ("Source Size (w,h)", Vector) = (0,0,0,0)
        _OutSize ("Output Size (w,h)", Vector) = (0,0,0,0)
        _PeriphStartX ("Peripheral Start X", Float) = 0.02
        _PeriphEndX ("Peripheral End X", Float) = 0.35
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // ─────────────────────────────────────────────
        // Pass 0：灰度 + 降采样（低通 + 亮度）
        // ─────────────────────────────────────────────
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragGrayscale
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 fragGrayscale (v2f i) : SV_Target
            {
                // 单个双线性采样，Unity 在缩小 RT 时自动做小范围平均
                float3 c = tex2D(_MainTex, i.uv).rgb;
                float lum = dot(c, float3(0.299, 0.587, 0.114));
                return float4(lum, 0.0, 0.0, 1.0);
            }
            ENDCG
        }

        // ─────────────────────────────────────────────
        // Pass 1：Lucas-Kanade 光流
        //
        // 对每个像素，在 3×3 邻域上累加梯度张量：
        //   Ix = (I(x+1) - I(x-1)) / 2    中心差分（当前帧）
        //   Iy = (I(y+1) - I(y-1)) / 2
        //   It = I_cur - I_prev           时间差分
        //   A = Σ Ix²,  B = Σ Ix·Iy,  C = Σ Iy²
        //   D = Σ Ix·It, E = Σ Iy·It
        // 解 2×2 线性系统：
        //   [ A B ][ u ]   [ -D ]
        //   [ B C ][ v ] = [ -E ]
        //   det = A·C - B²
        //   u = (B·E - C·D) / det
        //   v = (B·D - A·E) / det
        // det 接近 0（平坦区/光圈问题）时 u=v=0，避免除零发散。
        // ─────────────────────────────────────────────
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragLK
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;   // 当前帧灰度（Blit 的 src）
            sampler2D _PrevTex;   // 上一帧灰度
            float4 _TexelSize;    // (1/w, 1/h, w, h)
            float _LKThreshold;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 fragLK (v2f i) : SV_Target
            {
                float2 texel = _TexelSize.xy;

                float A = 0.0, B = 0.0, C = 0.0, D = 0.0, E = 0.0;

                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    float2 off = float2(dx, dy) * texel;

                    // 空间梯度（当前帧，中心差分）
                    float Ix = (tex2D(_MainTex, i.uv + off + float2(texel.x, 0.0)).r
                              - tex2D(_MainTex, i.uv + off - float2(texel.x, 0.0)).r) * 0.5;
                    float Iy = (tex2D(_MainTex, i.uv + off + float2(0.0, texel.y)).r
                              - tex2D(_MainTex, i.uv + off - float2(0.0, texel.y)).r) * 0.5;

                    // 时间梯度
                    float It = tex2D(_MainTex, i.uv + off).r - tex2D(_PrevTex, i.uv + off).r;

                    A += Ix * Ix;
                    B += Ix * Iy;
                    C += Iy * Iy;
                    D += Ix * It;
                    E += Iy * It;
                }

                float det = A * C - B * B;
                float u = 0.0, v = 0.0;
                if (abs(det) > _LKThreshold)
                {
                    float invDet = 1.0 / det;
                    u = (B * E - C * D) * invDet;
                    v = (B * D - A * E) * invDet;
                }

                // RG 存 (u, v)，单位 px/frame
                return float4(u, v, 0.0, 1.0);
            }
            ENDCG
        }

        // ─────────────────────────────────────────────
        // Pass 2：第一级归约（光流 → 区域和）
        // 输入 _MainTex = 光流 (RG=u,v)，输出半尺寸 (RGBA=whole,left,right,0)
        // 区域判定在「输入纹素」的归一化 x 上做：左外围 [start,end)，右外围 [1-end,1-start)
        // ─────────────────────────────────────────────
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragReduceFirst
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            float4 _SrcSize;  // (w, h, 0, 0)
            float4 _OutSize;  // (w_out, h_out, 0, 0)
            float _PeriphStartX;
            float _PeriphEndX;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 fragReduceFirst (v2f i) : SV_Target
            {
                int2 outIdx = int2(floor(i.uv * _OutSize.xy));
                float whole = 0.0, left = 0.0, right = 0.0;

                for (int oy = 0; oy < 2; oy++)
                for (int ox = 0; ox < 2; ox++)
                {
                    int2 inIdx = min(2 * outIdx + int2(ox, oy), int2(_SrcSize.xy) - 1);
                    float2 uv = (float2(inIdx) + 0.5) / _SrcSize.xy;

                    float2 fl = tex2D(_MainTex, uv).rg; // (u, v)
                    float m = length(fl);

                    float nx = uv.x; // 归一化 x：左=0，右=1（OnRenderImage 只上下翻转，不左右翻转）

                    whole += m;
                    if (nx >= _PeriphStartX && nx < _PeriphEndX)
                        left += m;
                    else if (nx >= 1.0 - _PeriphEndX && nx < 1.0 - _PeriphStartX)
                        right += m;
                }

                return float4(whole, left, right, 0.0);
            }
            ENDCG
        }

        // ─────────────────────────────────────────────
        // Pass 3：后续归约（纯累加）
        // 输入 _MainTex = 上一级累积 (RGBA)，输出半尺寸，4 通道直接相加
        // ─────────────────────────────────────────────
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragReduceAdd
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            float4 _SrcSize;
            float4 _OutSize;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 fragReduceAdd (v2f i) : SV_Target
            {
                int2 outIdx = int2(floor(i.uv * _OutSize.xy));
                float4 sum = 0.0;

                for (int oy = 0; oy < 2; oy++)
                for (int ox = 0; ox < 2; ox++)
                {
                    int2 inIdx = min(2 * outIdx + int2(ox, oy), int2(_SrcSize.xy) - 1);
                    float2 uv = (float2(inIdx) + 0.5) / _SrcSize.xy;
                    sum += tex2D(_MainTex, uv);
                }

                return sum;
            }
            ENDCG
        }
    }

    Fallback Off
}
