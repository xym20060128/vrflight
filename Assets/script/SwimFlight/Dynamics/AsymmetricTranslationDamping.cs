using UnityEngine;

namespace SwimFlight.Dynamics
{
    /// <summary>
    /// 平移阻尼相位。由「净平移输入力 F_input」与「本步施加输入前的当前速度 v」的夹角决定。
    /// </summary>
    public enum TranslationDampingPhase
    {
        /// <summary>新功能关闭: 恒为原有线性阻尼, 行为与改动前完全一致</summary>
        Disabled = 0,

        /// <summary>无有效平移输入 (空间门控回收 / 停手 / 双手合力抵消 / 纯转向) → 滑行阻尼</summary>
        Glide = 1,

        /// <summary>有输入但方向判定无效 (速度太小) 或夹角接近垂直 → 基础阻尼, 不触发强制动</summary>
        Neutral = 2,

        /// <summary>输入力与速度明确同向 → 低阻尼, 便于积累速度</summary>
        Propel = 3,

        /// <summary>输入力与速度明确反向且已短时确认 → 叠加辅助制动阻尼</summary>
        Brake = 4
    }

    /// <summary>
    /// 基于「输入力—速度关系」的非对称平移阻尼。
    ///
    /// 解决的问题: 阻尼大则难以积累速度, 阻尼小则漂移难停; 且同一个反向动作
    /// 既可能用于停车、也可能用于反向行进, 仅凭手势无法区分意图。
    ///
    /// 判据 (只用两个量, 均为世界空间, 由 FlightDynamicsV2 传入):
    ///   F_input = 门控后、阻尼前的净平移输入力  (= command.leftForce + command.rightForce)
    ///   v       = 施加本步输入前的当前速度      (= state.velocity)
    ///   cosθ    = dot(F_input, v) / (|F_input|·|v|)
    ///
    ///   cosθ 明确为正 → Propel    (推进; 默认阻尼与基础值相同, 调低才生效)
    ///   cosθ 明确为负 → Brake     (辅助制动, 提高阻尼)
    ///   cosθ 接近垂直 → Neutral   (侧向/转向, 不触发强制动)
    ///   |F_input| 很小 → Glide    (回收或滑行, 用独立滑行阻尼)
    ///   |v| 很小       → Neutral  (方向判定无效, 允许输入力正常启动运动)
    ///
    /// 反向判据不使用 dot < 0 直接触发, 而是「夹角阈值 + 滞回 + 短时确认」三者叠加,
    /// 避免手部速度估计噪声导致状态在推进/制动之间频繁切换。
    ///
    /// 本类只回答「本步的有效线性阻尼系数是多少」, 不做积分、不写速度。
    /// 数值安全性 (不越过零反号、不增加动能) 由 FlightDynamicsV2 在执行积分时保证。
    ///
    /// 单位:
    ///   阻尼系数      N/(m/s)   —— 与 FlightDynamicsV2.linearDamping 同量纲, 可直接比较
    ///   减速度上限    m/s²
    ///   输入力阈值    N
    ///   速度阈值/退出 m/s
    ///   时间阈值      s
    ///   夹角余弦      无量纲, [-1, 1]
    /// </summary>
    [System.Serializable]
    public class AsymmetricTranslationDamping
    {
        // ==================== 开关 ====================

        [Tooltip("非对称平移阻尼总开关。关闭后手臂模式恢复改动前的恒定线性阻尼行为。")]
        public bool enabled = true;

        // ==================== 阻尼系数 (N/(m/s)) ====================

        [Tooltip("推进阻尼: 输入力与速度明确同向时使用。\n" +
                 "【默认 = 基础阻尼, 即不做任何改变】。\n" +
                 "注意: 「同向」判据是各向同性的, 侧向划水时速度也是侧向的, cosθ 同样为 1 —— " +
                 "把它调低于基础阻尼会同时放大侧向平移 (实测 0.6 会让侧向速度 +27%), " +
                 "导致平移盖过转向。要做这个取舍请明确调低, 不要用默认值。")]
        public float propelDamping = 1.2f;

        [Tooltip("滑行阻尼: 无有效平移输入时使用 (回收 / 停手 / 双手抵消 / 纯转向)。" +
                 "与主动制动无关, 保留动作之间的惯性衔接。调试初值, 非最优值。")]
        public float glideDamping = 1.2f;

        [Tooltip("辅助制动阻尼: 明确反向且已确认时, 在基础阻尼之上叠加的额外阻尼 (N/(m/s))。" +
                 "实际强度还受「制动强度过渡」「最大制动减速度」「低速退出」三重限制。调试初值, 非最优值。")]
        public float assistBrakeDamping = 6f;

        [Tooltip("辅助制动允许的最大减速度 (m/s²)。制动阻尼按 |v| 缩放, 该项给高速段封顶。")]
        public float maxAssistBrakeDeceleration = 4f;

        // ==================== 方向判定阈值 ====================

        [Tooltip("有效平移输入力阈值 (N)。低于此值视为「回收 / 停手 / 合力抵消 / 纯转向」, " +
                 "进入滑行阻尼, 不触发辅助制动。")]
        public float inputForceThreshold = 0.15f;

        [Tooltip("方向判定最低速度 (m/s)。低于此值不做夹角判定, 允许输入力正常启动运动。")]
        public float directionMinSpeed = 0.15f;

        [Range(-1f, 0f), Tooltip("进入辅助制动的夹角余弦上界 (留空默认 ≈ 110°)。越小越难触发, 越不容易误判。")]
        public float brakeEnterCos = -0.34f;

        [Range(-1f, 0f), Tooltip("退出辅助制动的夹角余弦下界 (留空默认 ≈ 100°)。必须大于进入阈值以形成滞回。")]
        public float brakeExitCos = -0.17f;

        [Range(0f, 1f), Tooltip("进入推进(低阻尼)的夹角余弦下界 (默认 ≈ 70°)。")]
        public float propelEnterCos = 0.34f;

        [Range(0f, 1f), Tooltip("退出推进的夹角余弦上界 (默认 ≈ 80°)。必须小于进入阈值以形成滞回。")]
        public float propelExitCos = 0.17f;

        [Tooltip("反向判据的短时确认时间 (s)。夹角需连续满足进入阈值达此时长才进入辅助制动, 抑制噪声抖动。")]
        public float brakeConfirmTime = 0.08f;

        // ==================== 过渡与低速退出 ====================

        [Tooltip("制动强度过渡时间 (s)。辅助制动强度的淡入/淡出时间常数, 避免阻尼突变造成顿挫。")]
        public float brakeTransitionTime = 0.25f;

        [Tooltip("制动低速退出速度 (m/s)。速度低于此值时制动强度线性渐退到 0, " +
                 "避免卡在零速附近、反向启动困难或来回抖动。")]
        public float brakeLowSpeedExit = 0.35f;

        [Tooltip("残余速度归零阈值 (m/s)。仅当【没有有效平移输入】且速度低于此值时把速度归零。" +
                 "持续反向输入不受影响, 不会阻断反向启动。")]
        public float residualVelocityThreshold = 0.1f;

        // ==================== 运行时状态 (不序列化) ====================

        [System.NonSerialized] private float _brakeRamp;
        [System.NonSerialized] private float _brakeHoldTimer;
        [System.NonSerialized] private bool _inBrake;

        /// <summary>本步判定的相位</summary>
        public TranslationDampingPhase Phase { get; private set; } = TranslationDampingPhase.Disabled;

        /// <summary>本步夹角余弦 cosθ (方向判定无效时为 0)</summary>
        public float Cosine { get; private set; }

        /// <summary>本步实际生效的线性阻尼系数 (N/(m/s))</summary>
        public float EffectiveDamping { get; private set; }

        /// <summary>辅助制动强度淡入淡出系数 [0,1]</summary>
        public float BrakeRamp => _brakeRamp;

        /// <summary>本步辅助制动换算出的减速度 (m/s², 恒为不增加动能的减速)</summary>
        public float AssistBrakeDeceleration { get; private set; }

        /// <summary>本步是否存在有效平移输入 (|F_input| &gt; inputForceThreshold)</summary>
        public bool HasValidInput { get; private set; }

        /// <summary>本步是否因方向判定无效而跳过夹角判定 (速度过小)</summary>
        public bool DirectionJudgementSkipped { get; private set; }

        /// <summary>最近一次相位切换原因 (调试用, 中文可读)</summary>
        public string LastTransitionReason { get; private set; } = "功能关闭";

        /// <summary>向 XR Origin 应用速度前是否处于辅助制动相位</summary>
        public bool IsBraking => Phase == TranslationDampingPhase.Brake;

        /// <summary>清空跨帧状态 (模式切换 / 位姿重置时调用)</summary>
        public void Reset()
        {
            _brakeRamp = 0f;
            _brakeHoldTimer = 0f;
            _inBrake = false;
            Phase = TranslationDampingPhase.Disabled;
            Cosine = 0f;
            EffectiveDamping = 0f;
            AssistBrakeDeceleration = 0f;
            HasValidInput = false;
            DirectionJudgementSkipped = false;
            LastTransitionReason = "已重置";
        }

        /// <summary>
        /// 判定本步相位并给出有效线性阻尼系数。
        /// </summary>
        /// <param name="inputForce">门控后、阻尼前的净平移输入力 F_input (世界空间, N)</param>
        /// <param name="velocity">施加本步输入前的当前速度 v (世界空间, m/s); 必须与 inputForce 同坐标系</param>
        /// <param name="dt">本积分子步时长 (s)</param>
        /// <param name="baseDamping">原有线性阻尼 (N/(m/s)), 关闭开关或 Neutral 相直接返回该值</param>
        /// <param name="mass">虚拟质量 (kg), 用于把制动减速度上限换算成阻尼上限</param>
        public float Evaluate(Vector3 inputForce, Vector3 velocity, float dt, float baseDamping, float mass)
        {
            float safeBase = Mathf.Max(0f, baseDamping);
            float step = Mathf.Max(0f, dt);
            float m = Mathf.Max(1e-4f, mass);

            if (!enabled)
            {
                _brakeRamp = 0f;
                _brakeHoldTimer = 0f;
                _inBrake = false;
                Phase = TranslationDampingPhase.Disabled;
                Cosine = 0f;
                EffectiveDamping = safeBase;
                AssistBrakeDeceleration = 0f;
                HasValidInput = false;
                DirectionJudgementSkipped = false;
                LastTransitionReason = "功能关闭, 使用原有线性阻尼";
                return safeBase;
            }

            // ---- 输入量与门限 (NaN/Inf 一律当作无输入/零速, 不污染状态机) ----
            float forceMagnitude = inputForce.magnitude;
            float speed = velocity.magnitude;
            if (!IsFinite(forceMagnitude)) forceMagnitude = 0f;
            if (!IsFinite(speed)) speed = 0f;

            HasValidInput = forceMagnitude > inputForceThreshold;

            bool directionValid = HasValidInput && speed > directionMinSpeed;
            DirectionJudgementSkipped = HasValidInput && !directionValid;

            float cos = 0f;
            if (directionValid)
            {
                cos = Vector3.Dot(inputForce, velocity) / (forceMagnitude * speed);
                if (!IsFinite(cos))
                {
                    cos = 0f;
                    directionValid = false;
                    DirectionJudgementSkipped = true;
                }
                else
                {
                    cos = Mathf.Clamp(cos, -1f, 1f);
                }
            }
            Cosine = cos;

            // ---- 相位判定: 夹角阈值 + 滞回 + 短时确认 ----
            TranslationDampingPhase previous = Phase;
            string reason;

            if (!HasValidInput)
            {
                _inBrake = false;
                _brakeHoldTimer = 0f;
                Phase = TranslationDampingPhase.Glide;
                reason = $"无有效平移输入 |F|={forceMagnitude:F3}N ≤ {inputForceThreshold:F3}N (回收/停手/合力抵消/纯转向)";
            }
            else if (!directionValid)
            {
                _inBrake = false;
                _brakeHoldTimer = 0f;
                Phase = TranslationDampingPhase.Neutral;
                reason = $"速度过小 |v|={speed:F3}m/s ≤ {directionMinSpeed:F3}m/s, 方向判定无效, 允许输入力正常启动";
            }
            else if (_inBrake)
            {
                if (cos >= brakeExitCos)
                {
                    _inBrake = false;
                    _brakeHoldTimer = 0f;
                    Phase = TranslationDampingPhase.Neutral;
                    reason = $"退出辅助制动 cosθ={cos:F3} ≥ 退出阈值 {brakeExitCos:F3}";
                }
                else
                {
                    Phase = TranslationDampingPhase.Brake;
                    reason = $"维持辅助制动 cosθ={cos:F3} < 退出阈值 {brakeExitCos:F3}";
                }
            }
            else if (cos <= brakeEnterCos)
            {
                _brakeHoldTimer += step;
                if (_brakeHoldTimer >= brakeConfirmTime)
                {
                    _inBrake = true;
                    Phase = TranslationDampingPhase.Brake;
                    reason = $"进入辅助制动 cosθ={cos:F3} ≤ 进入阈值 {brakeEnterCos:F3}, " +
                             $"已持续 {_brakeHoldTimer:F3}s ≥ 确认时间 {brakeConfirmTime:F3}s";
                }
                else
                {
                    Phase = TranslationDampingPhase.Neutral;
                    reason = $"反向待确认 cosθ={cos:F3} ≤ {brakeEnterCos:F3}, " +
                             $"仅持续 {_brakeHoldTimer:F3}s < {brakeConfirmTime:F3}s";
                }
            }
            else
            {
                _brakeHoldTimer = 0f;
                Phase = TranslationDampingPhase.Neutral;
                reason = $"夹角未达反向阈值 cosθ={cos:F3} > {brakeEnterCos:F3}";
            }

            // 非制动状态下再细分「推进 / 侧向」, 同样带滞回
            if (Phase == TranslationDampingPhase.Neutral && directionValid)
            {
                bool wasPropel = previous == TranslationDampingPhase.Propel;
                float threshold = wasPropel ? propelExitCos : propelEnterCos;
                if (cos >= threshold)
                {
                    Phase = TranslationDampingPhase.Propel;
                    reason = $"推进 cosθ={cos:F3} ≥ {(wasPropel ? "退出" : "进入")}阈值 {threshold:F3}" +
                             (wasPropel ? " (维持低阻尼)" : " (切换到低阻尼)");
                }
                else if (wasPropel)
                {
                    reason = $"退出推进 cosθ={cos:F3} < 退出阈值 {threshold:F3}, 转为侧向/待判定";
                }
                else
                {
                    reason = $"侧向输入 cosθ={cos:F3} < 进入阈值 {threshold:F3}, 维持基础阻尼";
                }
            }

            // ---- 基础阻尼: 按相位取系数 ----
            float phaseDamping;
            switch (Phase)
            {
                case TranslationDampingPhase.Propel:
                    phaseDamping = Mathf.Max(0f, propelDamping);
                    break;
                case TranslationDampingPhase.Glide:
                    phaseDamping = Mathf.Max(0f, glideDamping);
                    break;
                default:   // Brake 与 Neutral 都从基础阻尼起算, 制动在其上叠加
                    phaseDamping = safeBase;
                    break;
            }

            // ---- 辅助制动强度: 平滑过渡 + 低速渐退 ----
            float rampTarget = 0f;
            if (Phase == TranslationDampingPhase.Brake)
            {
                rampTarget = 1f;
                float lowSpeed = Mathf.Max(1e-4f, brakeLowSpeedExit);
                if (speed < lowSpeed)
                    rampTarget = Mathf.Clamp01(speed / lowSpeed);   // 低速线性渐退, 避免卡在零速附近
            }
            float transition = Mathf.Max(1e-4f, brakeTransitionTime);
            _brakeRamp = Mathf.MoveTowards(_brakeRamp, rampTarget, step / transition);

            float brakeDamping = _brakeRamp * Mathf.Max(0f, assistBrakeDamping);

            // 制动减速度上限: brakeDamping·|v|/m ≤ maxAssistBrakeDeceleration
            if (speed > 1e-4f)
            {
                float dampingCap = Mathf.Max(0f, maxAssistBrakeDeceleration) * m / speed;
                brakeDamping = Mathf.Min(brakeDamping, dampingCap);
            }

            float effective = phaseDamping + brakeDamping;

            // 显式积分稳定兜底: 阻尼项稳定条件 (c1 + 2·c2·|v|)·dt/m < 2。
            // 正常参数远够不到这里, 仅防 Inspector 填入极端值。
            float stabilityCap = 1.9f * m / Mathf.Max(1e-4f, step);
            effective = Mathf.Min(effective, stabilityCap);
            effective = Mathf.Max(0f, effective);

            EffectiveDamping = effective;
            AssistBrakeDeceleration = speed > 1e-4f ? brakeDamping * speed / m : 0f;
            LastTransitionReason = reason;

            return effective;
        }

        /// <summary>相位的中文标签 (调试输出用)</summary>
        public static string PhaseLabel(TranslationDampingPhase phase)
        {
            switch (phase)
            {
                case TranslationDampingPhase.Propel: return "推进(低阻尼)";
                case TranslationDampingPhase.Glide: return "滑行";
                case TranslationDampingPhase.Neutral: return "侧向/待判定";
                case TranslationDampingPhase.Brake: return "辅助制动";
                default: return "已关闭";
            }
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
