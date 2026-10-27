using UnityEngine;
using UnityEngine.XR;

namespace SwimFlight
{
    /// <summary>
    /// 头部稳定器参数 (控制侧信号清洗)。
    ///
    /// 注意: 本稳定器只清洗「控制信号」, 永不触碰相机的显示位姿。
    /// VR 中对真实头部旋转做低通会造成视觉-前庭失配 → 晕动, 因此
    /// 这里既不改 headTransform, 也不改任何相机 Transform。
    /// </summary>
    [System.Serializable]
    public class HeadStabilizerSettings
    {
        [Tooltip("高频低通时间常数 (秒)。只滤追踪噪声/微颤, 拐点约 1.6Hz。" +
                 "必须高于非自主站姿晃动频段 (~0.5Hz), 否则会给有意动作加上明显滞后。")]
        public float lowPassTime = 0.10f;

        [Tooltip("进入跟随的死区半径 (米)。偏移超出该值才认为是有意位移, 基线才开始跟随。" +
                 "静立头晃约 0.5~3cm, 4cm 可保证正常晃动永不触发。")]
        public float enterRadius = 0.040f;

        [Tooltip("退出跟随的迟滞下沿 (米), 必须小于 enterRadius。")]
        public float exitRadius = 0.015f;

        [Tooltip("基线跟随速率上限 (米/秒)。自然前倾约 0.1~0.3 m/s, 0.5 可零滞后跟随; " +
                 "再高只会让「冻结后重新追赶」那一下变成可见的跳变。")]
        public float maxSlewSpeed = 0.5f;

        [Tooltip("追踪丢失/重定位后, 基线重新收敛的速率 (米/秒)。快一些但仍是缓动, 不跳变。")]
        public float reacquireSlewSpeed = 2.0f;

        [Tooltip("输入速度低于该值 (米/秒) 视为人已停止。基线收敛且输入已停, 才交还死区控制——" +
                 "否则缓慢的持续前倾会被切成一段段阶梯, 本身变成新的抖动。")]
        public float holdSpeed = 0.02f;

        [Tooltip("判定「人已停止」用的速度平滑时间常数 (秒)。必须平滑, 否则晃动的转向点" +
                 "瞬时速度过零会被误判为停止, 退出跟随后再重新进入, 每半个周期爆冲一次。")]
        public float speedSmoothingTime = 0.40f;

        [Tooltip("单步最大时长 (秒)。掉帧时防止低通系数趋近 1 而直接吸附到原始值。")]
        public float maxTimeStep = 0.10f;

        [Tooltip("原始位置相对低通值的跳变超过该距离 (米) 视为追踪重定位, 走缓动重新捕获。")]
        public float resetJumpDistance = 0.25f;

        public void Validate()
        {
            lowPassTime = Mathf.Max(0.001f, lowPassTime);
            enterRadius = Mathf.Max(0f, enterRadius);
            exitRadius = Mathf.Clamp(exitRadius, 0f, enterRadius);
            maxSlewSpeed = Mathf.Max(0.001f, maxSlewSpeed);
            reacquireSlewSpeed = Mathf.Max(0.001f, reacquireSlewSpeed);
            holdSpeed = Mathf.Max(0f, holdSpeed);
            speedSmoothingTime = Mathf.Max(0.001f, speedSmoothingTime);
            maxTimeStep = Mathf.Max(0.001f, maxTimeStep);
            resetJumpDistance = Mathf.Max(0.001f, resetJumpDistance);
        }
    }

    /// <summary>
    /// 控制侧头部稳定器。
    ///
    /// 作用: 把「原始头部位置 → 控制量」这条路上去抖, 供三条消费路径共用一份采样:
    ///   1. FlightCoordinateSystem.ComputeBodyCenter  身体中心 (划水模式的力臂/门控参考点)
    ///   2. IntentionalHeadMotionFlightController     前倾位移 (前倾加速模式)
    ///   3. ComparisonMotionMotor.ApplyPendingMotion  偏航支点 (所有模式)
    ///
    /// 为什么是三段串联而不是单纯低通:
    ///   非自主站姿晃动 (~0.5Hz) 与有意前倾在频段上重叠。低通要压到 0.5Hz 就得给有意
    ///   前倾加半秒滞后, 所以低通只负责高频噪声; 低频分离交给「迟滞死区 + 速率限幅基线」
    ///   —— 微晃被锁在死区内推不动基线, 持续的前倾才会带着基线以受限速率跟过去。
    ///
    /// 坐标系: 一律在 **origin 局部** 做。世界坐标里的头部位置含有飞行位移,
    /// 直接滤会引入 τ×v 的滞后 (3m/s、τ=0.12s 时达 0.36m); 局部偏移对平移不变。
    ///
    /// 本类不依赖 InputDevice 之外的任何东西, 且滤波核心 Step() 是纯函数式,
    /// 可用合成信号离线自检 (见 SelfTest)。
    /// </summary>
    public class HeadStabilizer
    {
        public HeadStabilizerSettings Settings = new HeadStabilizerSettings();

        /// <summary>当前是否采到有效头部追踪。无效时输出冻结在最后一次有效值 (不 reset, 避免跳变)。</summary>
        public bool IsTracking { get; private set; }

        /// <summary>稳定后的 origin 局部水平偏移 (XZ)。</summary>
        public Vector2 StabilizedLocalOffset => _center;

        private Vector2 _center;          // 稳定输出 (死区基线)
        private Vector2 _lowPass;         // 低通后的原始输入
        private Vector2 _previousLowPass; // 上一帧低通值, 用于估计输入速度
        private bool _engaged;            // 基线是否正在跟随
        private float _inputSpeedSmoothed; // 平滑后的输入速度, 用于判定「人已停止」
        private bool _initialized;
        private bool _lost;
        private bool _reacquiring;

        private InputDevice _headDevice;
        private float _requeryTimer;

        public void Reset()
        {
            _center = Vector2.zero;
            _lowPass = Vector2.zero;
            _previousLowPass = Vector2.zero;
            _engaged = false;
            _inputSpeedSmoothed = 0f;
            _initialized = false;
            _lost = false;
            _reacquiring = false;
            IsTracking = false;
        }

        /// <summary>
        /// 每帧由 FlightControlModeSwitcher 调用 **一次**, 结果共享给三条消费路径。
        /// 同一帧内所有消费方必须读到同一份采样, 否则三条路径口径不一致。
        /// </summary>
        public void Tick(Transform origin, Transform head, float dt)
        {
            Tick(origin, head, ResolveTracking(dt), dt);
        }

        /// <summary>显式指定追踪有效性的重载 (供离线自检强制追踪有效)。</summary>
        public void Tick(Transform origin, Transform head, bool isTracked, float dt)
        {
            if (dt <= 0f) return;
            if (origin == null || head == null)
            {
                IsTracking = false;
                _lost = true;
                return;
            }
            Vector3 local = origin.InverseTransformPoint(head.position);
            Step(new Vector2(local.x, local.z), isTracked, dt);
        }

        /// <summary>
        /// 滤波核心 (纯函数式, 无 Transform / InputDevice 依赖)。
        /// </summary>
        /// <param name="rawHorizontal">原始输入的 origin 局部水平偏移 (XZ)</param>
        /// <param name="isTracked">本帧追踪是否有效</param>
        public void Step(Vector2 rawHorizontal, bool isTracked, float dt)
        {
            HeadStabilizerSettings s = Settings;
            if (s == null || dt <= 0f) return;
            float h = Mathf.Min(dt, s.maxTimeStep);
            IsTracking = isTracked;

            if (!isTracked)
            {
                // 冻结输出: 不 reset, 也不推进基线。reset 会让重新捕获时跳变。
                _lost = true;
                return;
            }

            if (!_initialized || _lost)
            {
                _lowPass = _previousLowPass = rawHorizontal;
                _inputSpeedSmoothed = 0f;
                if (!_initialized)
                {
                    // 首次: 直接对齐, 无历史可缓动
                    _center = rawHorizontal;
                    _engaged = false;
                    _initialized = true;
                }
                else
                {
                    // 重新捕获: 让基线以 reacquireSlewSpeed 缓动过去, 输出是 _center 所以不跳变
                    _engaged = true;
                    _reacquiring = true;
                }
                _lost = false;
                return;
            }

            if ((rawHorizontal - _lowPass).magnitude > s.resetJumpDistance)
            {
                // 重定位: 同样是缓动而非吸附
                _lowPass = _previousLowPass = rawHorizontal;
                _engaged = true;
                _reacquiring = true;
            }

            // ① 轻量低通: 只吃高频噪声/微颤
            float alpha = 1f - Mathf.Exp(-h / Mathf.Max(0.001f, s.lowPassTime));
            _previousLowPass = _lowPass;
            _lowPass = Vector2.Lerp(_lowPass, rawHorizontal, alpha);
            float inputSpeed = (_lowPass - _previousLowPass).magnitude / h;
            float speedAlpha = 1f - Mathf.Exp(-h / Mathf.Max(0.001f, s.speedSmoothingTime));
            _inputSpeedSmoothed = Mathf.Lerp(_inputSpeedSmoothed, inputSpeed, speedAlpha);

            // ② 死区 + 迟滞
            Vector2 residual = _lowPass - _center;
            float distance = residual.magnitude;
            if (!_engaged)
            {
                if (distance > s.enterRadius) _engaged = true;
            }
            else if (distance < s.exitRadius && _inputSpeedSmoothed < s.holdSpeed)
            {
                // 只有「输入已停」才退出跟随。若只看距离, 一次缓慢的持续前倾会在
                // 基线追上的瞬间退出、随后又涨过 enterRadius 再次追上, 被切成
                // 4cm 的锯齿——那本身就是新的抖动。
                // 且必须用平滑速度: 晃动的转向点瞬时速度过零, 用瞬时值会每半个
                // 周期误判一次「已停止」, 退出后再重新进入 → 爆冲式踏步。
                _engaged = false;
                _reacquiring = false;
            }

            // ③ 速率限幅基线
            if (_engaged)
            {
                float speed = _reacquiring ? s.reacquireSlewSpeed : s.maxSlewSpeed;
                _center = Vector2.MoveTowards(_center, _lowPass, speed * h);
                if (_reacquiring && (_center - _lowPass).sqrMagnitude <= 1e-10f)
                    _reacquiring = false;
            }
        }

        /// <summary>
        /// 稳定后的 origin 局部头部位置 (XZ 取稳定值, Y 原样透传——下蹲/起身不该被滤)。
        /// 供前倾位移与偏航支点使用。
        /// </summary>
        public Vector3 GetStabilizedLocalPosition(Transform origin, Transform head)
        {
            if (origin == null) return Vector3.zero;
            Vector3 raw = head != null ? origin.InverseTransformPoint(head.position) : Vector3.zero;
            if (!_initialized || !IsTracking) return raw;
            return new Vector3(_center.x, raw.y, _center.y);
        }

        /// <summary>稳定后的世界坐标头部位置。供 ComputeBodyCenter 使用。</summary>
        public Vector3 GetStabilizedHeadPosition(Transform origin, Transform head)
        {
            if (head == null) return origin != null ? origin.position : Vector3.zero;
            if (origin == null || !_initialized || !IsTracking) return head.position;
            return origin.TransformPoint(GetStabilizedLocalPosition(origin, head));
        }

        private bool ResolveTracking(float dt)
        {
            _requeryTimer -= dt;
            if (_requeryTimer <= 0f || !_headDevice.isValid)
            {
                _requeryTimer = 2f;
                _headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
                if (!_headDevice.isValid)
                    _headDevice = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            }
            return _headDevice.isValid
                && _headDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked)
                && tracked;
        }

        // ================= 离线自检 =================

        /// <summary>
        /// 用合成信号自检, 无需头显。返回多行结果文本。
        /// </summary>
        public static string SelfTest()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("[HeadStabilizer] 自检 (合成信号, 无需头显)");
            report.AppendLine(TestSwayRejection());
            report.AppendLine(TestNoSteppingAboveDeadband());
            report.AppendLine(TestIntentionalLean());
            report.AppendLine(TestReacquire());
            report.AppendLine(TestTranslationInvariance());
            return report.ToString();
        }

        private const float TestDt = 1f / 72f;

        /// <summary>非自主晃动应被完全挡在死区内: 输出峰峰值应趋近 0。</summary>
        private static string TestSwayRejection()
        {
            var stabilizer = new HeadStabilizer();
            float min = float.MaxValue, max = float.MinValue;
            const int frames = 360; // 5s
            for (int i = 0; i < frames; i++)
            {
                float t = i * TestDt;
                // 0.5Hz / 2cm 站姿晃动 + 37Hz / 2mm 追踪噪声
                float sway = 0.02f * Mathf.Sin(2f * Mathf.PI * 0.5f * t);
                float noise = 0.002f * Mathf.Sin(2f * Mathf.PI * 37f * t);
                stabilizer.Step(new Vector2(0f, sway + noise), true, TestDt);
                if (i < 72) continue; // 跳过起始
                float output = stabilizer.StabilizedLocalOffset.y;
                min = Mathf.Min(min, output);
                max = Mathf.Max(max, output);
            }
            float peakToPeak = max - min;
            bool pass = peakToPeak < 0.003f;
            return $"  晃动抑制: 输出峰峰值 {peakToPeak * 100f:F3} cm (阈值 0.300 cm) -> " +
                   (pass ? "PASS" : "FAIL");
        }

        /// <summary>
        /// 幅度超过死区的晃动, 输出必须平滑跟随, 不得出现「冻结 → 猛追」的爆冲式踏步。
        /// 回归对象: 用瞬时速度判定「人已停止」时, 晃动转向点速度过零会被误判为停止,
        /// 退出跟随后再重新进入, 每半个周期爆冲一次 (最大单帧步进达 maxSlewSpeed·dt)。
        /// </summary>
        private static string TestNoSteppingAboveDeadband()
        {
            var stabilizer = new HeadStabilizer();
            const float amplitude = 0.06f;   // 1.5 × enterRadius(0.04)
            const float frequency = 0.5f;
            float previous = 0f, maxOutputStep = 0f;
            float inputStep = 2f * Mathf.PI * frequency * amplitude * TestDt; // 输入自身的最大帧间位移
            for (int i = 0; i < 72 * 10; i++)
            {
                float t = i * TestDt;
                stabilizer.Step(new Vector2(0f, amplitude * Mathf.Sin(2f * Mathf.PI * frequency * t)),
                    true, TestDt);
                float output = stabilizer.StabilizedLocalOffset.y;
                // 跳过启动头 1 秒: 首次进入跟随那一次追赶是一次性瞬态, 不是持续抖动
                if (i > 72) maxOutputStep = Mathf.Max(maxOutputStep, Mathf.Abs(output - previous));
                previous = output;
            }
            // 平滑跟随时, 输出单帧位移不应显著超过输入自身的单帧位移
            float limit = inputStep * 2f;
            bool pass = maxOutputStep <= limit;
            return $"  超死区晃动: 稳态最大单帧步进 {maxOutputStep * 1e6f:F1} um " +
                   $"(上限 {limit * 1e6f:F1} um = 2×输入自身步进) -> " +
                   (pass ? "PASS" : "FAIL");
        }

        /// <summary>有意前倾必须能透传: 及时到位且无超调。</summary>
        private static string TestIntentionalLean()
        {
            var stabilizer = new HeadStabilizer();
            const float target = 0.18f;
            const float rampTime = 0.25f;
            float timeTo90 = -1f;
            float maxOutput = 0f;
            float t = 0f;
            const int frames = 72; // 1s
            for (int i = 0; i < frames; i++)
            {
                t += TestDt;
                float input = target * Mathf.Clamp01(t / rampTime);
                stabilizer.Step(new Vector2(0f, input), true, TestDt);
                float output = stabilizer.StabilizedLocalOffset.y;
                maxOutput = Mathf.Max(maxOutput, output);
                if (timeTo90 < 0f && output >= target * 0.9f) timeTo90 = t;
            }
            float settled = stabilizer.StabilizedLocalOffset.y;
            bool pass = timeTo90 > 0f && timeTo90 < 0.45f
                && maxOutput <= target + 0.001f
                && Mathf.Abs(settled - target) <= 0.02f;
            return $"  有意前倾: 到 90% 用时 {timeTo90:F3} s (阈值 <0.450), " +
                   $"稳态 {settled * 100f:F2} cm (目标 {target * 100f:F0} cm) -> " +
                   (pass ? "PASS" : "FAIL");
        }

        /// <summary>追踪丢失后重新捕获应缓动收敛, 绝不跳变。</summary>
        private static string TestReacquire()
        {
            var stabilizer = new HeadStabilizer();
            // 先稳定在原点
            for (int i = 0; i < 36; i++) stabilizer.Step(Vector2.zero, true, TestDt);
            // 丢失 0.3s: 输出必须冻结
            float before = stabilizer.StabilizedLocalOffset.y;
            for (int i = 0; i < 22; i++) stabilizer.Step(Vector2.zero, false, TestDt);
            bool frozen = Mathf.Abs(stabilizer.StabilizedLocalOffset.y - before) < 1e-6f;
            // 重新出现且跳变 +0.3m
            float previous = stabilizer.StabilizedLocalOffset.y;
            float maxStep = 0f, settleTime = -1f, t = 0f;
            const float target = 0.3f;
            for (int i = 0; i < 60; i++)
            {
                t += TestDt;
                stabilizer.Step(new Vector2(0f, target), true, TestDt);
                float output = stabilizer.StabilizedLocalOffset.y;
                maxStep = Mathf.Max(maxStep, Mathf.Abs(output - previous));
                previous = output;
                if (settleTime < 0f && Mathf.Abs(output - target) < 0.015f) settleTime = t;
            }
            float stepLimit = stabilizer.Settings.reacquireSlewSpeed * TestDt + 1e-4f;
            bool pass = frozen && maxStep <= stepLimit && settleTime > 0f && settleTime < 0.30f;
            return $"  重新捕获: 丢失期冻结 {frozen}, 最大单帧步进 {maxStep * 100f:F2} cm " +
                   $"(上限 {stepLimit * 100f:F2} cm), 收敛 {settleTime:F3} s (阈值 <0.300) -> " +
                   (pass ? "PASS" : "FAIL");
        }

        /// <summary>
        /// 平移不变性: origin 以 3m/s 前进、头部在局部静止时, 稳定后的局部偏移不得漂移。
        /// 这条是「滤波必须在 origin 局部做」的回归测试——若误在世界坐标滤波,
        /// 基线会被飞行位移拖走 (τ×v)。
        /// </summary>
        private static string TestTranslationInvariance()
        {
            var originGo = new GameObject("HeadStabilizerSelfTest_Origin")
            { hideFlags = HideFlags.HideAndDontSave };
            var headGo = new GameObject("HeadStabilizerSelfTest_Head")
            { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                headGo.transform.SetParent(originGo.transform, false);
                var localOffset = new Vector3(0.03f, 1.6f, 0.02f);
                headGo.transform.localPosition = localOffset;

                var stabilizer = new HeadStabilizer();
                for (int i = 0; i < 360; i++) // 5s @ 3m/s = 15m
                {
                    originGo.transform.position += Vector3.forward * (3f * TestDt);
                    // 显式指定追踪有效: 离线无头显设备
                    stabilizer.Tick(originGo.transform, headGo.transform, true, TestDt);
                }
                Vector3 stabilized = stabilizer.GetStabilizedLocalPosition(
                    originGo.transform, headGo.transform);
                float drift = new Vector2(stabilized.x - localOffset.x,
                    stabilized.z - localOffset.z).magnitude;
                bool pass = drift < 0.001f;
                return $"  平移不变性: 局部偏移漂移 {drift * 1000f:F3} mm (阈值 1.000 mm) -> " +
                       (pass ? "PASS" : "FAIL");
            }
            finally
            {
                DestroyTemporary(originGo);
                DestroyTemporary(headGo);
            }
        }

        private static void DestroyTemporary(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Object.Destroy(go);
            else Object.DestroyImmediate(go);
        }
    }
}
