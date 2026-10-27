using UnityEngine;
using UnityEngine.XR;

namespace OpticFlow
{
    /// <summary>
    /// 视觉反馈状态（纯数据）。供后续视觉 mask / Debug UI 读取。
    /// </summary>
    public struct VisualFeedbackState
    {
        public float opticFlowO;          // O：全帧平均 flow magnitude (px/s)
        public float leftFlow;            // M_L
        public float rightFlow;           // M_R
        public float asymmetry;           // A
        public float headAngularVelocity; // omega：HMD 角速度模长 (rad/s)
        public float go;                  // G_O：光流驱动强度 0~1
        public float gOmega;              // G_omega：角速度驱动强度 0~1
        public float target;              // G = Max(G_O, G_omega)（瞬时目标）
        public float modulationLevel;     // visualModulationLevel：attack/release 平滑后 0~1
        public bool engaged;              // 迟滞状态（是否处于调节区间）
    }

    /// <summary>
    /// 视觉反馈控制器（Phase 5 + 6）：光流 + HMD 角速度 → 视觉调制强度 G。
    ///
    /// 只读 OpticFlowAnalyzer 的输出和 HMD 角速度，不产生任何视觉效果、不改飞行逻辑。
    ///
    /// 关键设计（严格区分两类量）：
    ///   - O   = 图像光流强度（px/s），来自 OpticFlowAnalyzer
    ///   - A   = 左右不对称度（无量纲）
    ///   - omega = HMD 旋转角速度（rad/s），来自陀螺仪（或相机旋转差分兜底），与光流完全独立
    ///
    /// 强度映射（按需求公式）：
    ///   G_O     = Clamp01((O     - oEnter)     / (oMax     - oEnter))
    ///   G_omega = Clamp01((omega - omegaEnter) / (omegaMax - omegaEnter))
    ///   G = Max(G_O, G_omega)
    ///
    /// 迟滞（防阈值附近闪烁）：
    ///   engaged 进入：O >= oEnter 或 omega >= omegaEnter
    ///   engaged 退出：O <= oExit 且 omega <= omegaExit
    ///   （enter 与 exit 分开，中间区间保持上一状态）
    ///
    /// 平滑（attack/release 独立，帧率无关指数平滑）：
    ///   level 向 target 逼近，上升用 attackTime（快），下降用 releaseTime（慢，防闪烁）
    /// </summary>
    [DisallowMultipleComponent]
    public class VisualFeedbackController : MonoBehaviour
    {
        [Header("=== 引用 ===")]
        [Tooltip("光流分析器。留空自动查找。")]
        public OpticFlowAnalyzer opticFlowAnalyzer;
        [Tooltip("头部/相机 Transform（角速度差分兜底用）。留空自动 Camera.main。")]
        public Transform headTransform;

        [Header("=== Optic Flow 阈值（O，单位 px/s，需实测标定）===")]
        [Tooltip("O 超过它开始调节（进入阈值）。")]
        public float oEnter = 20f;
        [Tooltip("O 低于它停止调节（退出阈值，必须 ≤ oEnter）。")]
        public float oExit = 12f;
        [Tooltip("O 达到它时强度满 1。")]
        public float oMax = 80f;

        [Header("=== 角速度阈值（omega，单位 rad/s，1 rad/s ≈ 57.3°/s）===")]
        [Tooltip("角速度超过它开始调节。")]
        public float omegaEnter = 0.8f;
        [Tooltip("角速度低于它停止调节（必须 ≤ omegaEnter）。")]
        public float omegaExit = 0.4f;
        [Tooltip("角速度达到它时强度满 1。")]
        public float omegaMax = 3.0f;

        [Header("=== 平滑（时间常数，秒）===")]
        [Tooltip("强度上升时的时间常数（越小越跟手）。")]
        public float attackTime = 0.15f;
        [Tooltip("强度下降时的时间常数（越大越缓慢，防闪烁）。")]
        public float releaseTime = 0.8f;

        [Header("=== omega 读取 ===")]
        [Tooltip("优先用 HMD 陀螺仪角速度（真机最准）。串流下陀螺仪可能不可用，可关闭改用相机旋转差分。")]
        public bool useDeviceGyro = true;

        [Header("=== 调试 ===")]
        [Tooltip("勾选后每 0.25s 打印 O/omega/G/level。")]
        public bool debugLog = false;

        // === 输出（对外只读）===
        private float _level;   // visualModulationLevel
        private float _target;  // G
        private float _go, _gOmega;
        private float _omega;
        private bool _engaged;

        /// <summary>视觉调制强度 0~1（attack/release 平滑后）。</summary>
        public float VisualModulationLevel => _level;
        /// <summary>瞬时目标 G = Max(G_O, G_omega)。</summary>
        public float ModulationTarget => _target;
        /// <summary>HMD 角速度模长 (rad/s)。</summary>
        public float HeadAngularVelocity => _omega;
        /// <summary>光流驱动强度 G_O。</summary>
        public float GO => _go;
        /// <summary>角速度驱动强度 G_omega。</summary>
        public float GOmega => _gOmega;
        /// <summary>光流不对称度 A（透传自 OpticFlowAnalyzer）。</summary>
        public float Asymmetry => _analyzer != null ? _analyzer.Asymmetry : 0f;
        /// <summary>迟滞状态。</summary>
        public bool Engaged => _engaged;
        /// <summary>最近一帧完整状态。</summary>
        public VisualFeedbackState State => new VisualFeedbackState
        {
            opticFlowO = _analyzer != null ? _analyzer.OverallFlow : 0f,
            leftFlow = _analyzer != null ? _analyzer.LeftPeripheralFlow : 0f,
            rightFlow = _analyzer != null ? _analyzer.RightPeripheralFlow : 0f,
            asymmetry = _analyzer != null ? _analyzer.Asymmetry : 0f,
            headAngularVelocity = _omega,
            go = _go,
            gOmega = _gOmega,
            target = _target,
            modulationLevel = _level,
            engaged = _engaged
        };

        // === 内部 ===
        private OpticFlowAnalyzer _analyzer;
        private float _analyzerRequeryTimer;
        private bool _warnedNoAnalyzer;

        private InputDevice _headDevice;
        private float _deviceRequeryTimer;
        private Quaternion _prevHeadRot;
        private bool _hasPrevHeadRot;

        private float _debugTimer;

        private void Update()
        {
            ResolveReferences();

            float dt = Mathf.Max(Time.deltaTime, 1e-4f);

            // 读 O（来自光流分析器，单位 px/s；无效时按 0 处理，避免误触发）
            float O = (_analyzer != null && _analyzer.Data.valid) ? _analyzer.OverallFlow : 0f;

            // 读 omega（HMD 角速度，rad/s，与 O 完全独立）
            _omega = ReadHeadAngularVelocity(dt);

            // —— 迟滞状态机（enter/exit 分开，防阈值附近闪烁）——
            if (!_engaged)
            {
                if (O >= oEnter || _omega >= omegaEnter) _engaged = true;
            }
            else
            {
                if (O <= oExit && _omega <= omegaExit) _engaged = false;
            }

            // —— 强度映射（按需求公式）——
            _go = Clamp01((O - oEnter) / Mathf.Max(1e-3f, oMax - oEnter));
            _gOmega = Clamp01((_omega - omegaEnter) / Mathf.Max(1e-3f, omegaMax - omegaEnter));
            float rawTarget = Mathf.Max(_go, _gOmega);
            _target = _engaged ? rawTarget : 0f;

            // —— attack/release 平滑（帧率无关指数平滑）——
            float timeConst = _target > _level ? attackTime : releaseTime;
            float alpha = 1f - Mathf.Exp(-dt / Mathf.Max(timeConst, 1e-4f));
            _level += (_target - _level) * alpha;
            _level = Mathf.Clamp01(_level);

            if (debugLog)
            {
                _debugTimer += dt;
                if (_debugTimer >= 0.25f)
                {
                    _debugTimer = 0f;
                    Debug.Log($"[VFB] O={O:F1} omega={_omega:F2} G_O={_go:F2} G_w={_gOmega:F2} " +
                              $"target={_target:F2} level={_level:F2} engaged={_engaged}");
                }
            }
        }

        private void ResolveReferences()
        {
            _analyzerRequeryTimer -= Time.deltaTime;
            if (_analyzer == null && _analyzerRequeryTimer <= 0f)
            {
                _analyzerRequeryTimer = 2f;
                _analyzer = opticFlowAnalyzer != null ? opticFlowAnalyzer : FindObjectOfType<OpticFlowAnalyzer>();
                if (_analyzer == null && !_warnedNoAnalyzer)
                {
                    _warnedNoAnalyzer = true;
                    Debug.LogWarning("[VisualFeedbackController] 找不到 OpticFlowAnalyzer，请拖入引用或保证同场景存在。");
                }
            }

            if (headTransform == null && Camera.main != null)
                headTransform = Camera.main.transform;
        }

        /// <summary>读 HMD 角速度模长 (rad/s)。陀螺仪优先，失败用相机旋转差分兜底。</summary>
        private float ReadHeadAngularVelocity(float dt)
        {
            // 设备句柄重取（与项目其它 XR 输入一致，5 秒间隔 + 失效立即重取）
            _deviceRequeryTimer -= dt;
            if (_deviceRequeryTimer <= 0f || !_headDevice.isValid)
            {
                _deviceRequeryTimer = 5f;
                _headDevice = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
                if (!_headDevice.isValid) _headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            }

            if (useDeviceGyro && _headDevice.isValid
                && _headDevice.TryGetFeatureValue(CommonUsages.deviceAngularVelocity, out Vector3 angVel))
            {
                return angVel.magnitude; // rad/s
            }

            // 兜底：相机旋转差分 → 角速度
            if (headTransform == null) return 0f;
            Quaternion cur = headTransform.localRotation;
            if (!_hasPrevHeadRot)
            {
                _prevHeadRot = cur;
                _hasPrevHeadRot = true;
                return 0f;
            }
            float angleDeg = Quaternion.Angle(_prevHeadRot, cur);
            _prevHeadRot = cur;
            return angleDeg * Mathf.Deg2Rad / Mathf.Max(dt, 1e-4f);
        }

        private static float Clamp01(float v) => Mathf.Clamp01(v);

        private void OnDisable() => ResetFeedback();

        public void ResetFeedback()
        {
            _engaged = false;
            _level = 0f;
            _target = 0f;
            _go = 0f;
            _gOmega = 0f;
            _omega = 0f;
            _hasPrevHeadRot = false;
        }

        private void OnValidate()
        {
            oEnter = Mathf.Max(0f, oEnter);
            oExit = Mathf.Clamp(oExit, 0f, oEnter);
            oMax = Mathf.Max(oMax, oEnter + 1e-3f);

            omegaEnter = Mathf.Max(0f, omegaEnter);
            omegaExit = Mathf.Clamp(omegaExit, 0f, omegaEnter);
            omegaMax = Mathf.Max(omegaMax, omegaEnter + 1e-3f);

            attackTime = Mathf.Max(0.01f, attackTime);
            releaseTime = Mathf.Max(0.01f, releaseTime);
        }
    }
}
