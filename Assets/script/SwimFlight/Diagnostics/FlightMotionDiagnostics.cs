using UnityEngine;
using System.Text;

namespace SwimFlight.Diagnostics
{
    /// <summary>
    /// 诊断模式: 用于把「真实头动」与「人工虚拟运动」分开观察, 逐层定位抖动来源。
    ///
    /// 关键: 三种模式都保留正常 HMD 位置/旋转追踪, 也都不对 Main Camera 做任何
    /// 低通/冻结/反向抵消。差别只在于是否把人工运动写到 XR Origin 上。
    /// </summary>
    public enum FlightDiagnosticMode
    {
        /// <summary>完整飞行: 人工平移 + 手臂力矩产生的虚拟 yaw 都施加。</summary>
        Full = 0,

        /// <summary>正常平移, 但关闭手臂力矩产生的虚拟 yaw (世界不转, 只移动)。</summary>
        TranslationOnly = 1,

        /// <summary>仅正常 HMD 追踪: 人工平移与人工转向都关闭, XR Origin 完全不动。</summary>
        HeadTrackingOnly = 2
    }

    /// <summary>
    /// 单帧诊断快照 (由 SwimFlightControllerV2 每帧填充)。纯数据, 无分配。
    /// </summary>
    public struct FlightDiagSnapshot
    {
        public bool valid;
        public float strokeComparison;
        public float gateEnter;
        public float gateExit;
        public float radialRetention;
        public float leftGateActive;
        public float rightGateActive;
        public float leftGateSeconds;
        public float rightGateSeconds;
        public float leftDistance;
        public float rightDistance;
        public float gripTurnHeld;
        public Vector3 rawLeft;
        public Vector3 rawRight;
        public Vector3 tanLeft;
        public Vector3 tanRight;
        public Vector3 effectiveLeft;
        public Vector3 effectiveRight;

        public float frameTime;            // 本帧 Time.deltaTime (s)
        public int mode;

        public Vector3 hmdLocalPosition;   // 头显相对 XR Origin 的位置 (m)
        public Vector3 hmdLocalEuler;      // 头显相对 XR Origin 的旋转 (度)
        public Vector3 originPosition;     // XR Origin 世界位置
        public float originYaw;            // XR Origin 世界 yaw (度)

        public float leftForceMagnitude;   // 左手输入力幅值 (N)
        public float rightForceMagnitude;  // 右手输入力幅值 (N)
        public Vector3 totalForce;         // 净平移输入力 (N)
        public Vector3 directionInputForce;
        public Vector3 directionOutputForce;
        public float horizontalSuppression;
        public float verticalSuppression;
        public float verticalPermissionEnabled;
        public float verticalPermissionWeight;
        public float verticalPermissionStepWeight;
        public float verticalPermissionDamping;
        public float leftTriggerHeld;
        public float rightTriggerHeld;
        public float rawTorqueY;           // 稳定化前的手臂力矩 y (N·m)
        public float torqueY;              // 稳定化后实际参与积分的力矩 y (N·m)

        public Vector3 velocity;           // 动力学速度 (m/s)
        public float speed;
        public Vector3 acceleration;       // 实际进入积分的加速度 (m/s²)
        public float yawRate;              // 动力学 yaw 角速度 (rad/s)
        public float yawAcceleration;      // yaw 角加速度 (rad/s²)

        public Vector3 appliedTranslationDelta;  // 本帧实际加到 XR Origin 的平移 (m)
        public float appliedYawDelta;            // 本帧实际加到 XR Origin 的 yaw (度)

        public float turnOpposition;             // 旧判据「双手对抗度」[0,1] (仅诊断对照)
        public float turnCoherence;              // 新判据「力矩同向度」[0,1], 1 = 两手同向拧身体 (转向手势)
        public float turnTranslationScale;       // 原地转弯辅助对平移分量的缩放 [0,1]
    }

    /// <summary>
    /// 虚拟运动诊断记录器。
    ///
    /// 为什么要 CSV 而不是 Debug.Log:
    ///   Debug.Log 每次调用都要拼字符串 (分配) + 同步写日志, 本身就会制造帧时间尖峰,
    ///   而「帧时间尖峰」正是本次要排查的对象之一 —— 用它来测它自己会污染结果。
    ///   这里每帧只往预分配的 float[] 里写数字, 零分配; 导出时才生成一次 CSV。
    ///
    /// 用法:
    ///   1. 挂到任意 GameObject, controller 留空会自动查找。
    ///   2. 头部追踪原始位姿另外用已有的 HeadTrackingDebug 观察。
    ///   3. 停止 Play, 或调用 Dump() / Inspector 右键「导出诊断 CSV」。
    ///   注意: 本组件读的是每帧结束时 (LateUpdate 之后) 的状态, 与画面实际提交的位姿接近。
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class FlightMotionDiagnostics : MonoBehaviour
    {
        [Tooltip("留空自动查找场景中的 SwimFlightControllerV2")]
        public SwimFlightControllerV2 controller;

        [Tooltip("采样间隔 (s)。0 = 每帧采样。>0 时按固定间隔采样, 长时间记录用。")]
        public float sampleInterval = 0f;

        [Tooltip("最多记录多少条样本 (超出后环形覆盖最旧的)。")]
        public int capacity = 30000;

        [Tooltip("记录时把超过阈值的帧时间打印出来? 会分配, 只在排查卡顿时短暂开启。")]
        public bool logFrameSpikes = false;

        [Tooltip("帧时间超过此值 (s) 时记一条警告 (需 logFrameSpikes 开启)。")]
        public float spikeThreshold = 0.02f;

        [Tooltip("退出 Play 模式时自动导出 CSV")]
        public bool dumpOnDisable = true;

        [Tooltip("CSV 输出路径 (相对工程根目录, 或绝对路径)")]
        public string outputPath = "flight_diagnostics.csv";

        // 列顺序必须与 Columns 严格一致
        private const int Stride = 80;
        private static readonly string[] Columns =
        {
            "frame", "t", "dt", "mode",
            "hmdLocX", "hmdLocY", "hmdLocZ", "hmdEulerX", "hmdEulerY", "hmdEulerZ",
            "originX", "originY", "originZ", "originYaw",
            "leftForce", "rightForce",
            "inForceX", "inForceY", "inForceZ", "rawTorqueY", "torqueY",
            "velX", "velY", "velZ", "speed",
            "accelX", "accelY", "accelZ", "yawRate", "yawAccel",
            "appliedDX", "appliedDY", "appliedDZ", "appliedYaw",
            "turnOpposition", "turnCoherence", "turnTransScale",
            "directionInX", "directionInY", "directionInZ",
            "directionOutX", "directionOutY", "directionOutZ", "horizontalSuppression", "verticalSuppression",
            "verticalPermissionEnabled", "verticalPermissionWeight", "verticalPermissionStepWeight",
            "verticalPermissionDamping", "leftTriggerHeld", "rightTriggerHeld",
            "strokeComparison", "gateEnter", "gateExit", "radialRetention", "leftGateActive", "rightGateActive", "leftGateSeconds", "rightGateSeconds", "leftDistance", "rightDistance", "gripTurnHeld", "rawLeftX", "rawLeftY", "rawLeftZ", "rawRightX", "rawRightY", "rawRightZ", "tanLeftX", "tanLeftY", "tanLeftZ", "tanRightX", "tanRightY", "tanRightZ", "effectiveLeftX", "effectiveLeftY", "effectiveLeftZ", "effectiveRightX", "effectiveRightY", "effectiveRightZ"
        };

        private float[] _buffer;
        private int _count;
        private int _writeIndex;
        private float _timer;
        private int _frameIndex;
        private string _lastExport = "";

        [ContextMenu("Diagnostics/Clear recording")]
        public void ClearRecording()
        {
            _count = _writeIndex = _frameIndex = 0;
            _timer = 0f;
        }

        private void Awake()
        {
            _buffer = new float[Mathf.Max(1, capacity) * Stride];
        }

        private void OnDisable()
        {
            if (dumpOnDisable) Dump();
        }

        private void LateUpdate()
        {
            if (controller == null) controller = FindObjectOfType<SwimFlightControllerV2>();
            if (controller == null) return;

            if (sampleInterval > 0f)
            {
                _timer -= Time.unscaledDeltaTime;
                if (_timer > 0f) return;
                _timer = sampleInterval;
            }

            if (logFrameSpikes && Time.deltaTime > spikeThreshold)
            {
                Debug.LogWarning($"[SFDiag] 帧时间尖峰 dt={Time.deltaTime * 1000f:F1}ms " +
                                 $"(阈值 {spikeThreshold * 1000f:F0}ms) frame={Time.frameCount}");
            }

            FlightDiagSnapshot s = controller.Diag;
            if (!s.valid) return;

            if (_buffer == null) _buffer = new float[Mathf.Max(1, capacity) * Stride];

            int i = _writeIndex * Stride;
            _buffer[i++] = _frameIndex;
            _buffer[i++] = Time.time;
            _buffer[i++] = s.frameTime;
            _buffer[i++] = s.mode;

            _buffer[i++] = s.hmdLocalPosition.x;
            _buffer[i++] = s.hmdLocalPosition.y;
            _buffer[i++] = s.hmdLocalPosition.z;
            _buffer[i++] = s.hmdLocalEuler.x;
            _buffer[i++] = s.hmdLocalEuler.y;
            _buffer[i++] = s.hmdLocalEuler.z;

            _buffer[i++] = s.originPosition.x;
            _buffer[i++] = s.originPosition.y;
            _buffer[i++] = s.originPosition.z;
            _buffer[i++] = s.originYaw;

            _buffer[i++] = s.leftForceMagnitude;
            _buffer[i++] = s.rightForceMagnitude;

            _buffer[i++] = s.totalForce.x;
            _buffer[i++] = s.totalForce.y;
            _buffer[i++] = s.totalForce.z;
            _buffer[i++] = s.rawTorqueY;
            _buffer[i++] = s.torqueY;

            _buffer[i++] = s.velocity.x;
            _buffer[i++] = s.velocity.y;
            _buffer[i++] = s.velocity.z;
            _buffer[i++] = s.speed;

            _buffer[i++] = s.acceleration.x;
            _buffer[i++] = s.acceleration.y;
            _buffer[i++] = s.acceleration.z;
            _buffer[i++] = s.yawRate;
            _buffer[i++] = s.yawAcceleration;

            _buffer[i++] = s.appliedTranslationDelta.x;
            _buffer[i++] = s.appliedTranslationDelta.y;
            _buffer[i++] = s.appliedTranslationDelta.z;
            _buffer[i++] = s.appliedYawDelta;

            _buffer[i++] = s.turnOpposition;
            _buffer[i++] = s.turnCoherence;
            _buffer[i++] = s.turnTranslationScale;
            _buffer[i++] = s.directionInputForce.x;
            _buffer[i++] = s.directionInputForce.y;
            _buffer[i++] = s.directionInputForce.z;
            _buffer[i++] = s.directionOutputForce.x;
            _buffer[i++] = s.directionOutputForce.y;
            _buffer[i++] = s.directionOutputForce.z;
            _buffer[i++] = s.horizontalSuppression;
            _buffer[i++] = s.verticalSuppression;
            _buffer[i++] = s.verticalPermissionEnabled;
            _buffer[i++] = s.verticalPermissionWeight;
            _buffer[i++] = s.verticalPermissionStepWeight;
            _buffer[i++] = s.verticalPermissionDamping;
            _buffer[i++] = s.leftTriggerHeld;
            _buffer[i++] = s.rightTriggerHeld;
            _buffer[i++] = s.strokeComparison;
            _buffer[i++] = s.gateEnter;
            _buffer[i++] = s.gateExit;
            _buffer[i++] = s.radialRetention;
            _buffer[i++] = s.leftGateActive;
            _buffer[i++] = s.rightGateActive;
            _buffer[i++] = s.leftGateSeconds;
            _buffer[i++] = s.rightGateSeconds;
            _buffer[i++] = s.leftDistance;
            _buffer[i++] = s.rightDistance;
            _buffer[i++] = s.gripTurnHeld;
            _buffer[i++] = s.rawLeft.x;
            _buffer[i++] = s.rawLeft.y;
            _buffer[i++] = s.rawLeft.z;
            _buffer[i++] = s.rawRight.x;
            _buffer[i++] = s.rawRight.y;
            _buffer[i++] = s.rawRight.z;
            _buffer[i++] = s.tanLeft.x;
            _buffer[i++] = s.tanLeft.y;
            _buffer[i++] = s.tanLeft.z;
            _buffer[i++] = s.tanRight.x;
            _buffer[i++] = s.tanRight.y;
            _buffer[i++] = s.tanRight.z;
            _buffer[i++] = s.effectiveLeft.x;
            _buffer[i++] = s.effectiveLeft.y;
            _buffer[i++] = s.effectiveLeft.z;
            _buffer[i++] = s.effectiveRight.x;
            _buffer[i++] = s.effectiveRight.y;
            _buffer[i++] = s.effectiveRight.z;


            _writeIndex++;
            _frameIndex++;
            _count++;
            if (_writeIndex >= _buffer.Length / Stride) _writeIndex = 0;
        }

        /// <summary>导出 CSV。可挂到 UI 按钮, 或 Inspector 右键菜单。</summary>
        [ContextMenu("导出诊断 CSV")]
        public void Dump()
        {
            if (_buffer == null) return;
            int capacityActual = _buffer.Length / Stride;
            int samples = Mathf.Min(_count, capacityActual);
            if (samples == 0) return;

            var sb = new StringBuilder(samples * 128);
            sb.AppendLine(string.Join(",", Columns));

            int start = _count >= capacityActual ? _writeIndex : 0;
            for (int n = 0; n < samples; n++)
            {
                int idx = ((start + n) % capacityActual) * Stride;
                for (int c = 0; c < Stride; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(_buffer[idx + c].ToString("F4",
                        System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.AppendLine();
            }

            string path = outputPath;
            if (Application.platform == RuntimePlatform.Android && !System.IO.Path.IsPathRooted(path))
                path = System.IO.Path.Combine(Application.persistentDataPath, path);
            if (!System.IO.Path.IsPathRooted(path))
                path = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, "..", outputPath));
            System.IO.File.WriteAllText(path, sb.ToString());
            _lastExport = path;
            Debug.Log($"[SFDiag] 已导出 {samples} 条样本 → {path}");
        }

        /// <summary>最近一次导出路径 (供 UI 显示)。</summary>
        public string LastExportPath => _lastExport;

        /// <summary>已记录样本数。</summary>
        public int SampleCount => _count;
    }
}
