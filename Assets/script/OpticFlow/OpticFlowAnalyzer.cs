using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace OpticFlow
{
    /// <summary>
    /// Optic Flow 分析结果（单位统一为 px/s：低分辨率灰度图上「像素位移 / 秒」）。
    /// 只来自两帧图像本身的运动，与飞行速度 / 相机位移 / 刚体速度 / 角速度完全无关。
    /// </summary>
    public struct OpticFlowData
    {
        public float overall;     // O：全帧平均 flow magnitude (px/s)
        public float left;        // M_L：左外围平均 flow magnitude (px/s)
        public float right;       // M_R：右外围平均 flow magnitude (px/s)
        public float asymmetry;   // A：(M_R - M_L) / (M_R + M_L + epsilon)，Clamp[-1,1]
        public bool valid;        // 是否有真实两帧估计（首帧 / 相机禁用 / 读回失败时为 false）
    }

    /// <summary>
    /// 实时 Optic Flow Analyzer（第一阶段：只做「图像 → 光流 → O / M_L / M_R / A」）。
    ///
    /// 数据流：
    ///   当前帧 src ──灰度降采样──▶ _grayCur
    ///   _grayPrev + _grayCur ──Lucas-Kanade──▶ _flow (u,v, 单位 px/frame)
    ///   _flow ──多级 GPU 归约──▶ 1×1 (whole / left / right 三个和)
    ///   AsyncGPUReadback ──▶ CPU 只读 3 个标量 → 除以 dt → px/s → O/M_L/M_R/A
    ///
    /// 单位约定（不要混用）：
    ///   - shader 输出的 (u,v) 是「像素位移」px/frame
    ///   - CPU 除以 deltaTime 后变成「像素位移 / 秒」px/s
    ///   - 对外暴露的 O/M_L/M_R 一律是 px/s
    ///   - 「归一化光流」(0~1) 本阶段不产出，留给后续阈值/调制阶段
    ///
    /// 本组件不读飞行速度、不碰任何 SwimFlight 脚本、不改最终画面（OnRenderImage 原样透传）。
    /// </summary>
    [DisallowMultipleComponent]
    public class OpticFlowAnalyzer : MonoBehaviour
    {
        [Header("=== 采集分辨率（低分辨率灰度图，越小越快）===")]
        [Tooltip("灰度/光流图的宽（像素）。只用于分析，与最终画面无关。")]
        public int flowWidth = 160;
        [Tooltip("灰度/光流图的高（像素）。")]
        public int flowHeight = 90;

        [Header("=== 视觉区域划分（归一化 x，0=左，1=右；Phase3 只用外围）===")]
        [Tooltip("左/右外围区域起始 x。")]
        [Range(0f, 1f)] public float peripheralStartX = 0.02f;
        [Tooltip("左/右外围区域结束 x。左外围 = [start, end)，右外围 = [1-end, 1-start)。")]
        [Range(0f, 1f)] public float peripheralEndX = 0.35f;
        [Tooltip("中心区域起始 x（本阶段保留，暂不参与计算）。")]
        [Range(0f, 1f)] public float centerMinX = 0.35f;
        [Tooltip("中心区域结束 x（本阶段保留，暂不参与计算）。")]
        [Range(0f, 1f)] public float centerMaxX = 0.65f;

        [Header("=== Lucas-Kanade 与不对称度 ===")]
        [Tooltip("LK 行列式阈值。det = A*C - B²，低于它判定为平坦区（光圈问题），光流置 0。")]
        public float lkDetThreshold = 1e-4f;
        [Tooltip("不对称度公式里的 epsilon（单位 px/s，避免除零）。")]
        public float asymmetryEpsilon = 1e-4f;

        [Header("=== 稳健性 ===")]
        [Tooltip("deltaTime 超过该值（暂停/恢复、卡顿）则丢弃上一帧，当作首帧。")]
        public float pauseResetTime = 1.0f;

        [Header("=== 调试 ===")]
        [Tooltip("勾选后每 0.25s 在 Console 打印 O/M_L/M_R/A。验证用，正式运行可关闭。")]
        public bool debugLog = false;

        [Header("=== 引用 ===")]
        [Tooltip("要分析的 VR 相机。留空自动取本物体 Camera，再取 Camera.main。本组件必须挂在相机上。")]
        public Camera targetCamera;

        // === 数据（对外只读）===
        private OpticFlowData _data;

        /// <summary>O：全帧平均 flow magnitude (px/s)。</summary>
        public float OverallFlow => _data.overall;
        /// <summary>M_L：左外围平均 flow magnitude (px/s)。</summary>
        public float LeftPeripheralFlow => _data.left;
        /// <summary>M_R：右外围平均 flow magnitude (px/s)。</summary>
        public float RightPeripheralFlow => _data.right;
        /// <summary>A：左右不对称度，Clamp[-1,1]。A&gt;0 右强，A&lt;0 左强。</summary>
        public float Asymmetry => _data.asymmetry;
        /// <summary>最近一帧完整结果（含 valid 标记）。</summary>
        public OpticFlowData Data => _data;

        // === 内部 ===
        private Shader _shader;
        private Material _mat;
        private Camera _resolvedCamera;

        private RenderTexture _grayA;
        private RenderTexture _grayB;
        private RenderTexture _grayCur;
        private RenderTexture _grayPrev;
        private RenderTexture _flow;
        private readonly List<RenderTexture> _accum = new List<RenderTexture>();

        private bool _hasPrev;
        private int _allocW = -1;
        private int _allocH = -1;
        private bool _disabledWarned;

        // 区域 texel 计数（CPU 解析得出，用于把 GPU 的和转为均值）
        private int _wholeCount;
        private int _leftCount;
        private int _rightCount;

        // 回读用
        private int _historyGeneration;
        private bool _readbackRequested;

        // 调试：是否捕获到帧
        private int _captureCount;
        private float _debugTimer;

        private static readonly int PrevTexId = Shader.PropertyToID("_PrevTex");
        private static readonly int TexelSizeId = Shader.PropertyToID("_TexelSize");
        private static readonly int LKThresholdId = Shader.PropertyToID("_LKThreshold");
        private static readonly int SrcSizeId = Shader.PropertyToID("_SrcSize");
        private static readonly int OutSizeId = Shader.PropertyToID("_OutSize");
        private static readonly int PeriphStartXId = Shader.PropertyToID("_PeriphStartX");
        private static readonly int PeriphEndXId = Shader.PropertyToID("_PeriphEndX");

        private void OnEnable()
        {
            ResolveCamera();
        }

        private void OnDisable()
        {
            ResetHistory();
            ReleaseRTs();
        }

        public void ResetHistory()
        {
            _historyGeneration++;
            _data = default;
            _hasPrev = false;
            _readbackRequested = false;
        }

        private void OnDestroy()
        {
            ReleaseRTs();
            if (_mat != null) { Destroy(_mat); _mat = null; }
        }

        private void OnValidate()
        {
            flowWidth = Mathf.Max(2, flowWidth);
            flowHeight = Mathf.Max(2, flowHeight);
            peripheralStartX = Mathf.Clamp(peripheralStartX, 0f, 1f);
            peripheralEndX = Mathf.Clamp(peripheralEndX, peripheralStartX, 1f);
            centerMinX = Mathf.Clamp(centerMinX, 0f, 1f);
            centerMaxX = Mathf.Clamp(centerMaxX, centerMinX, 1f);
            asymmetryEpsilon = Mathf.Max(1e-6f, asymmetryEpsilon);
            pauseResetTime = Mathf.Max(0f, pauseResetTime);
        }

        // OnRenderImage 只在「脚本所在的相机」渲染时被调用，所以本组件必须挂在 VR Camera 上。
        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            // —— 原样透传，绝不改变最终画面 ——
            Graphics.Blit(src, dst);

            bool analyze = ShouldAnalyzeThisEye();
            if (!analyze) return;

            _captureCount++;
            if (!EnsureReady()) return; // 初始化/分配失败：只透传，不影响渲染

            float dt = Time.deltaTime;

            // 暂停/恢复或长时间卡顿 → 上一帧已过期，当作首帧
            if (dt > pauseResetTime)
            {
                _hasPrev = false;
            }

            // 1) 当前帧 → 灰度降采样
            Graphics.Blit(src, _grayCur, _mat, 0);

            // 2) 有上一帧 → LK + 归约 + 异步回读
            if (_hasPrev)
            {
                float safeDt = Mathf.Max(dt, 1e-4f);

                _mat.SetTexture(PrevTexId, _grayPrev);
                _mat.SetVector(TexelSizeId, new Vector4(1f / flowWidth, 1f / flowHeight, flowWidth, flowHeight));
                _mat.SetFloat(LKThresholdId, lkDetThreshold);
                Graphics.Blit(_grayCur, _flow, _mat, 1);

                ReduceAndRequest(safeDt);
            }

            // 3) 交换 prev/cur
            SwapGray();
            _hasPrev = true;
        }

        private void Update()
        {
            if (debugLog && _data.valid)
            {
                _debugTimer += Time.deltaTime;
                if (_debugTimer >= 0.25f)
                {
                    _debugTimer = 0f;
                    Debug.Log($"[OpticFlow] O={_data.overall:F2} px/s  M_L={_data.left:F2}  M_R={_data.right:F2}  A={_data.asymmetry:F3}");
                }
            }

#if UNITY_EDITOR
            // 便于在 Editor/真机上确认 OnRenderImage 是否真的在跑（XR Multi Pass 下可能不触发）
            if (Application.isPlaying && !_disabledWarned && Time.time > 3f && _captureCount == 0)
            {
                _disabledWarned = true;
                Debug.LogWarning(
                    "[OpticFlowAnalyzer] 运行 3 秒仍未捕获到帧。若在 XR 真机，可能 OnRenderImage 未触发" +
                    "（Multi Pass XR 的已知坑）。请确认本组件挂在 VR Main Camera 上；" +
                    "若确认挂了仍不触发，Phase 4 需改用 CommandBuffer 采集。");
            }
#endif
        }

        // ─────────────────────────────────────────────
        // 初始化 / 分配
        // ─────────────────────────────────────────────

        private void ResolveCamera()
        {
            if (_resolvedCamera != null) return;
            _resolvedCamera = targetCamera != null ? targetCamera : GetComponent<Camera>();
            if (_resolvedCamera == null) _resolvedCamera = Camera.main;
        }

        /// <summary>只在一只眼上分析。Multi Pass 下左眼算一次、右眼跳过，避免重复 + 加倍开销。</summary>
        private bool ShouldAnalyzeThisEye()
        {
            ResolveCamera();
            if (_resolvedCamera == null) return true; // 拿不到相机就按普通相机处理
            return _resolvedCamera.stereoActiveEye != Camera.MonoOrStereoscopicEye.Right;
        }

        private bool EnsureReady()
        {
            ResolveCamera();

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                if (!_disabledWarned)
                {
                    _disabledWarned = true;
                    Debug.LogWarning("[OpticFlowAnalyzer] 当前平台不支持 AsyncGPUReadback，分析被禁用（不做 ReadPixels 回退）。");
                }
                return false;
            }

            if (_shader == null)
            {
                _shader = Shader.Find("OpticFlow/OpticFlowCore");
                if (_shader == null)
                {
                    if (!_disabledWarned)
                    {
                        _disabledWarned = true;
                        Debug.LogError("[OpticFlowAnalyzer] 找不到 shader \"OpticFlow/OpticFlowCore\"（应位于 Resources 目录）。");
                    }
                    return false;
                }
            }

            if (_mat == null) _mat = new Material(_shader);

            if (_allocW != flowWidth || _allocH != flowHeight)
            {
                if (!Allocate())
                {
                    if (!_disabledWarned)
                    {
                        _disabledWarned = true;
                        Debug.LogError("[OpticFlowAnalyzer] RenderTexture 分配失败（格式/显存不支持），分析被禁用。");
                    }
                    return false;
                }
            }

            return true;
        }

        private bool Allocate()
        {
            ReleaseRTs();

            _grayA = CreateRT(flowWidth, flowHeight, "OFL_GrayA");
            _grayB = CreateRT(flowWidth, flowHeight, "OFL_GrayB");
            _flow = CreateRT(flowWidth, flowHeight, "OFL_Flow");
            if (_grayA == null || _grayB == null || _flow == null)
            {
                ReleaseRTs();
                return false;
            }

            _grayCur = _grayA;
            _grayPrev = _grayB;
            _hasPrev = false;

            // 归约金字塔：尺寸逐级减半直到 1×1
            _accum.Clear();
            int cw = flowWidth, ch = flowHeight;
            while (cw > 1 || ch > 1)
            {
                cw = Mathf.Max(1, (cw + 1) / 2);
                ch = Mathf.Max(1, (ch + 1) / 2);
                var rt = CreateRT(cw, ch, "OFL_Accum" + _accum.Count);
                if (rt == null)
                {
                    ReleaseRTs();
                    return false;
                }
                _accum.Add(rt);
            }

            ComputeRegionCounts();
            _allocW = flowWidth;
            _allocH = flowHeight;
            return true;
        }

        private RenderTexture CreateRT(int w, int h, string name)
        {
            try
            {
                var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat);
                rt.name = name;
                rt.filterMode = FilterMode.Bilinear;
                rt.wrapMode = TextureWrapMode.Clamp;
                rt.useMipMap = false;
                rt.autoGenerateMips = false;
                rt.enableRandomWrite = false;
                if (!rt.Create())
                {
                    rt.Release();
                    Destroy(rt);
                    return null;
                }
                return rt;
            }
            catch
            {
                return null;
            }
        }

        private void ReleaseRTs()
        {
            if (_grayA != null) { _grayA.Release(); Destroy(_grayA); _grayA = null; }
            if (_grayB != null) { _grayB.Release(); Destroy(_grayB); _grayB = null; }
            if (_flow != null) { _flow.Release(); Destroy(_flow); _flow = null; }
            foreach (var rt in _accum)
            {
                if (rt != null) { rt.Release(); Destroy(rt); }
            }
            _accum.Clear();
            _grayCur = null;
            _grayPrev = null;
            _allocW = -1;
            _allocH = -1;
            _hasPrev = false;
        }

        private void SwapGray()
        {
            var tmp = _grayCur;
            _grayCur = _grayPrev;
            _grayPrev = tmp;
        }

        private void ComputeRegionCounts()
        {
            int leftX = 0, rightX = 0;
            for (int x = 0; x < flowWidth; x++)
            {
                float nx = (x + 0.5f) / flowWidth;
                if (nx >= peripheralStartX && nx < peripheralEndX) leftX++;
                else if (nx >= 1f - peripheralEndX && nx < 1f - peripheralStartX) rightX++;
            }
            _wholeCount = flowWidth * flowHeight;
            _leftCount = leftX * flowHeight;
            _rightCount = rightX * flowHeight;
        }

        // ─────────────────────────────────────────────
        // 归约 + 回读
        // ─────────────────────────────────────────────

        private void ReduceAndRequest(float dt)
        {
            if (_accum.Count == 0) return;

            // 第一级：光流(RG=u,v) → 区域和(RGBA=whole,left,right,0)
            var first = _accum[0];
            _mat.SetVector(SrcSizeId, new Vector4(flowWidth, flowHeight, 0, 0));
            _mat.SetVector(OutSizeId, new Vector4(first.width, first.height, 0, 0));
            _mat.SetFloat(PeriphStartXId, peripheralStartX);
            _mat.SetFloat(PeriphEndXId, peripheralEndX);
            Graphics.Blit(_flow, first, _mat, 2);

            // 后续各级：纯累加
            for (int i = 1; i < _accum.Count; i++)
            {
                var srcRT = _accum[i - 1];
                var dstRT = _accum[i];
                _mat.SetVector(SrcSizeId, new Vector4(srcRT.width, srcRT.height, 0, 0));
                _mat.SetVector(OutSizeId, new Vector4(dstRT.width, dstRT.height, 0, 0));
                Graphics.Blit(srcRT, dstRT, _mat, 3);
            }

            var final = _accum[_accum.Count - 1];
            int generation = _historyGeneration;
            _readbackRequested = true;
            // final 一定是 1×1，直接读整张纹理（等价于读 1×1 区域），避开区域重载的版本差异
            AsyncGPUReadback.Request(final, 0, TextureFormat.RGBAFloat, request => OnReadback(request, dt, generation));
        }

        private void OnReadback(AsyncGPUReadbackRequest req, float sampleDt, int generation)
        {
            if (generation != _historyGeneration || !isActiveAndEnabled) return;
            _readbackRequested = false;
            if (req.hasError)
            {
                _data.valid = false;
                return;
            }

            var arr = req.GetData<float>();
            if (arr.Length < 4)
            {
                _data.valid = false;
                return;
            }

            float wholeSum = arr[0];
            float leftSum = arr[1];
            float rightSum = arr[2];

            float dt = Mathf.Max(sampleDt, 1e-4f);

            // GPU 给的是「像素位移的和」（px/frame），除以 count 得均值，再除以 dt 得 px/s
            float o = wholeSum / Mathf.Max(1, _wholeCount) / dt;
            float ml = leftSum / Mathf.Max(1, _leftCount) / dt;
            float mr = rightSum / Mathf.Max(1, _rightCount) / dt;

            // A = (M_R - M_L) / (M_R + M_L + epsilon)，Clamp[-1,1]
            float a = (mr - ml) / (mr + ml + Mathf.Max(1e-6f, asymmetryEpsilon));
            a = Mathf.Clamp(a, -1f, 1f);

            _data.overall = o;
            _data.left = ml;
            _data.right = mr;
            _data.asymmetry = a;
            _data.valid = true;
        }
    }
}
