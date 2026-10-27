// PeripheralMask —— 左右外围渐变暗角（vignette）。
// 中心区清晰，左右外围向屏幕边缘渐变变暗，左右强度独立。
// 只用于内部 Graphics.Blit，最终叠加在画面上。
Shader "OpticFlow/PeripheralMask"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _LeftIntensity ("Left Intensity", Range(0,1)) = 0
        _RightIntensity ("Right Intensity", Range(0,1)) = 0
        _CenterMinX ("Center Min X", Float) = 0.35
        _CenterMaxX ("Center Max X", Float) = 0.65
        _VignetteColor ("Vignette Color", Color) = (0,0,0,1)
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            UNITY_DECLARE_SCREENSPACE_TEXTURE(_MainTex);
            float4 _MainTex_TexelSize;
            float _LeftIntensity;
            float _RightIntensity;
            float _CenterMinX;
            float _CenterMaxX;
            float4 _VignetteColor;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0) o.uv.y = 1.0 - o.uv.y;
                #endif
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 source = UNITY_SAMPLE_SCREENSPACE_TEXTURE(_MainTex,
                    UnityStereoTransformScreenSpaceTex(i.uv));
                float3 col = source.rgb;

                // uv.x：0=左，1=右（OnRenderImage 只上下翻转，不左右翻转，左右方向可靠）
                float x = i.uv.x;

                // 左外围权重：centerMinX 处=0（清晰）→ 左边缘=1（暗）
                float leftWeight = 1.0 - smoothstep(0.0, _CenterMinX, x);
                // 右外围权重：centerMaxX 处=0（清晰）→ 右边缘=1（暗）
                float rightWeight = smoothstep(_CenterMaxX, 1.0, x);

                // 左右强度独立控制
                float darken = saturate(leftWeight * _LeftIntensity + rightWeight * _RightIntensity);

                col = lerp(col, _VignetteColor.rgb, darken);
                return float4(col, source.a);
            }
            ENDCG
        }
    }

    Fallback Off
}
