using UnityEngine;

namespace OpticFlow
{
    /// <summary>
    /// 左右外围视觉 mask（Phase 7）——把视觉调制强度变成眼睛能看到的渐变暗角。
    ///
    /// 只读 VisualFeedbackController 的 G（总强度）和 A（左右不对称度），
    /// 计算左右独立的暗角强度，用 OnRenderImage 画在最终画面上。
    ///
    /// 空间方向性（按需求公式）：
    ///   G_L = Clamp01(G * (1 - beta * A))   左外围强度
    ///   G_R = Clamp01(G * (1 + beta * A))   右外围强度
    ///   A > 0（右侧光流强）→ G_R > G_L，右侧更暗；A < 0 反之。
    ///   beta ∈ [0,1] 保证 1 - beta*A ≥ 0（A∈[-1,1]），不会出现负数。
    ///
    /// 视觉（第一版，易调试）：
    ///   - 中心区 [centerMinX, centerMaxX] 完全清晰
    ///   - 左外围、右外围向屏幕边缘渐变变暗（smoothstep，无硬边）
    ///   - 左右强度独立（leftIntensity / rightIntensity），再乘 maxOpacity 防止全黑
    ///   - 左右强度做低通，避免 A 的抖动导致闪烁
    ///
    /// 注意（Multi Pass）：
    ///   与 OpticFlowAnalyzer 不同，本组件必须【双眼都画】mask，所以 OnRenderImage 不过滤眼睛。
    ///
    /// 注意（组件顺序）：
    ///   本组件与 OpticFlowAnalyzer 都在相机上 OnRenderImage，链式执行按组件顺序。
    ///   请把 OpticFlowAnalyzer 排在【前面】（先分析原始画面），本组件排在后面（最后画 mask）。
    /// </summary>
    [DisallowMultipleComponent]
    public class PeripheralMaskController : MonoBehaviour
    {
        [Header("=== 引用 ===")]
        [Tooltip("视觉反馈控制器（提供 G 和 A）。留空自动查找。")]
        public VisualFeedbackController feedbackController;

        [Header("=== 区域划分（归一化 x，0=左 1=右）===")]
        [Tooltip("中心清晰区左边界。")]
        [Range(0f, 1f)] public float centerMinX = 0.35f;
        [Tooltip("中心清晰区右边界。")]
        [Range(0f, 1f)] public float centerMaxX = 0.65f;

        [Header("=== 空间方向性 ===")]
        [Tooltip("不对称度权重 beta。0=左右对称；越大左右差异越明显。")]
        [Range(0f, 1f)] public float beta = 0.5f;

        [Header("=== 平滑 ===")]
        [Tooltip("左右强度低通时间常数（秒），防止 A 抖动导致左右闪烁。")]
        public float intensitySmoothTime = 0.3f;

        [Header("=== 视觉 ===")]
        [Tooltip("暗角颜色。")]
        public Color vignetteColor = Color.black;
        [Tooltip("暗角最大不透明度 0~1（1=外围可全黑，0.9=保留一点可见）。")]
        [Range(0f, 1f)] public float maxOpacity = 0.9f;

        [Header("=== 调试 ===")]
        [Tooltip("勾选后每 0.25s 打印 G/A/左/右强度。")]
        public bool debugLog = false;

        // === 输出（对外只读）===
        /// <summary>左外围暗角强度 0~1（含 maxOpacity 缩放前）。</summary>
        public float LeftIntensity => _leftIntensity;
        /// <summary>右外围暗角强度 0~1。</summary>
        public float RightIntensity => _rightIntensity;

        // === 内部 ===
        private Shader _shader;
        private Material _mat;
        private VisualFeedbackController _fb;
        private float _leftIntensity;
        private float _rightIntensity;
        private float _debugTimer;
        private float _fbRequeryTimer;
        private bool _warnedNoFeedback;

        private static readonly int LeftIntensityId = Shader.PropertyToID("_LeftIntensity");
        private static readonly int RightIntensityId = Shader.PropertyToID("_RightIntensity");
        private static readonly int CenterMinXId = Shader.PropertyToID("_CenterMinX");
        private static readonly int CenterMaxXId = Shader.PropertyToID("_CenterMaxX");
        private static readonly int VignetteColorId = Shader.PropertyToID("_VignetteColor");

        private void OnEnable()
        {
            ResolveShader();
        }

        private void OnDisable() => ResetFeedback();

        public void ResetFeedback()
        {
            _leftIntensity = 0f;
            _rightIntensity = 0f;
        }

        private void OnDestroy()
        {
            if (_mat != null) { Destroy(_mat); _mat = null; }
        }

        private void OnValidate()
        {
            centerMinX = Mathf.Clamp01(centerMinX);
            centerMaxX = Mathf.Clamp01(centerMaxX);
            if (centerMinX > centerMaxX) centerMinX = centerMaxX;
            beta = Mathf.Clamp01(beta);
            intensitySmoothTime = Mathf.Max(0.01f, intensitySmoothTime);
            maxOpacity = Mathf.Clamp01(maxOpacity);
        }

        private void Update()
        {
            ResolveFeedback();

            float G = _fb != null ? _fb.VisualModulationLevel : 0f;
            float A = _fb != null ? _fb.Asymmetry : 0f;

            // 左右目标强度（Clamp01；beta ≤ 1 保证 1 - beta*A ≥ 0）
            float targetLeft = Mathf.Clamp01(G * (1f - beta * A));
            float targetRight = Mathf.Clamp01(G * (1f + beta * A));

            // 低通平滑，防 A 抖动导致左右闪烁
            float alpha = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(intensitySmoothTime, 1e-4f));
            _leftIntensity += (targetLeft - _leftIntensity) * alpha;
            _rightIntensity += (targetRight - _rightIntensity) * alpha;

            if (debugLog)
            {
                _debugTimer += Time.deltaTime;
                if (_debugTimer >= 0.25f)
                {
                    _debugTimer = 0f;
                    Debug.Log($"[Mask] G={G:F2} A={A:F3} L={_leftIntensity:F2} R={_rightIntensity:F2}");
                }
            }
        }

        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (_mat == null) ResolveShader();

            // shader 缺失时兜底：只透传，不画 mask
            if (_mat == null)
            {
                Graphics.Blit(src, dst);
                return;
            }

            _mat.SetFloat(LeftIntensityId, _leftIntensity * maxOpacity);
            _mat.SetFloat(RightIntensityId, _rightIntensity * maxOpacity);
            _mat.SetFloat(CenterMinXId, centerMinX);
            _mat.SetFloat(CenterMaxXId, centerMaxX);
            _mat.SetColor(VignetteColorId, vignetteColor);

            Graphics.Blit(src, dst, _mat, 0);
        }

        private void ResolveShader()
        {
            if (_shader == null) _shader = Shader.Find("OpticFlow/PeripheralMask");
            if (_shader != null && _mat == null) _mat = new Material(_shader);
            if (_shader == null)
                Debug.LogWarning("[PeripheralMask] 找不到 shader \"OpticFlow/PeripheralMask\"（应位于 Resources 目录）。");
        }

        private void ResolveFeedback()
        {
            _fbRequeryTimer -= Time.deltaTime;
            if (_fb == null && _fbRequeryTimer <= 0f)
            {
                _fbRequeryTimer = 2f;
                _fb = feedbackController != null ? feedbackController : FindObjectOfType<VisualFeedbackController>();
                if (_fb == null && !_warnedNoFeedback)
                {
                    _warnedNoFeedback = true;
                    Debug.LogWarning("[PeripheralMask] 找不到 VisualFeedbackController，mask 强度恒 0。");
                }
            }
        }
    }
}
