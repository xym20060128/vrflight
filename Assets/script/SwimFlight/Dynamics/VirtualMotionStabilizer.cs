using UnityEngine;
using SwimFlight.Core;

namespace SwimFlight.Dynamics
{
    /// <summary>
    /// 推进力平滑 (输入力层)。
    ///
    /// 手部速度估计是位置差分的, 抖动会直接被 ReactionForceModel (F=-k·v_tan) 放大成力,
    /// 再经动力学变成可见的加速度抖动。这里在【门控之后、积分之前】做一次帧率无关的
    /// 轻量低通, 保留力驱动机制 (不改成目标速度)。
    ///
    /// 左右手各自独立滤波: 合成力的方向不被改变, 力矩随之一致, 不会因为平滑而
    /// 凭空造出/抹掉左右手差异。
    ///
    /// 分级时间常数: 制动脉冲需要快响应, 否则会拖慢停车与反向启动,
    /// 因此明确判定为反向的那只手用更短的时间常数。
    ///
    /// 单位: 时间常数 s; 夹角余弦无量纲。
    /// </summary>
    [System.Serializable]
    public class InputForceSmoother
    {
        [Tooltip("推进力平滑总开关。关闭时输入力原样透传 (改动前行为)。")]
        public bool enabled = true;

        [Tooltip("同向(推进)时的时间常数 (s)。越大越平滑、延迟越大。")]
        public float propelTimeConstant = 0.05f;

        [Tooltip("反向/制动时的时间常数 (s)。应≤推进值, 避免拖慢停车与反向启动。")]
        public float brakeTimeConstant = 0.02f;

        [Tooltip("判定该手进入「反向/制动」的夹角余弦上界 (默认 ≈ 100°)。")]
        public float reverseDetectCos = -0.17f;

        [System.NonSerialized] private Vector3 _left;
        [System.NonSerialized] private Vector3 _right;
        [System.NonSerialized] private Vector3 _translation;
        [System.NonSerialized] private bool _initialized;

        /// <summary>本帧滤波后的左右手力 (调试)</summary>
        public Vector3 FilteredLeft => _left;
        public Vector3 FilteredRight => _right;

        /// <summary>本帧该手是否走的是制动时间常数 (调试)</summary>
        public bool LeftOnBrakeResponse { get; private set; }
        public bool RightOnBrakeResponse { get; private set; }

        public void Reset()
        {
            _left = Vector3.zero;
            _right = Vector3.zero;
            _translation = Vector3.zero;
            _initialized = false;
            LeftOnBrakeResponse = false;
            RightOnBrakeResponse = false;
        }

        /// <summary>
        /// 就地平滑 command 里的左右手力。必须在动力学积分之前调用。
        /// hasValidVelocity: velocity 是否可用于夹角判定 (速度过小时按推进处理)。
        /// </summary>
        public void Apply(ref FlightCommand command, Vector3 velocity, float dt)
        {
            if (!enabled)
            {
                _left = command.leftForce;
                _right = command.rightForce;
                _translation = command.translationForce;
                _initialized = true;
                LeftOnBrakeResponse = false;
                RightOnBrakeResponse = false;
                return;
            }

            if (!_initialized)
            {
                // 首帧直接对齐, 避免从零开始爬升造成的起步迟滞
                _left = command.leftForce;
                _right = command.rightForce;
                _translation = command.translationForce;
                _initialized = true;
                return;
            }

            if (command.hasTranslationForce)
            {
                command.translationForce = Smooth(_translation, command.translationForce, velocity, dt, out _);
                _translation = command.translationForce;
            }
            command.leftForce = Smooth(_left, command.leftForce, velocity, dt, out bool leftBrake);
            command.rightForce = Smooth(_right, command.rightForce, velocity, dt, out bool rightBrake);
            _left = command.leftForce;
            _right = command.rightForce;
            LeftOnBrakeResponse = leftBrake;
            RightOnBrakeResponse = rightBrake;
        }

        private Vector3 Smooth(Vector3 previous, Vector3 raw, Vector3 velocity, float dt, out bool onBrake)
        {
            onBrake = false;
            float tau = Mathf.Max(0f, propelTimeConstant);

            float speed = velocity.magnitude;
            float forceMagnitude = raw.magnitude;
            if (speed > 1e-4f && forceMagnitude > 1e-6f)
            {
                float cos = Vector3.Dot(raw, velocity) / (forceMagnitude * speed);
                if (cos <= reverseDetectCos)
                {
                    tau = Mathf.Max(0f, brakeTimeConstant);
                    onBrake = true;
                }
            }

            if (tau <= 1e-5f || dt <= 0f) return raw;
            float alpha = 1f - Mathf.Exp(-dt / tau);
            return Vector3.Lerp(previous, raw, alpha);
        }
    }

    /// <summary>
    /// 虚拟 yaw 稳定 (力矩层)。
    ///
    /// 问题: 左右手反作用力的微小不对称经 τ = r_L×F_L + r_R×F_R 直接变成虚拟偏航,
    /// 世界跟着转, 是 VR 中最容易引起不适的一类被动旋转。
    ///
    /// 手段 (都不改用户真实转头追踪, 只作用于手臂力矩产生的虚拟 yaw):
    ///   1. 软死区: 小幅力矩平滑压到 0, 大幅主动转向完全保留 (不做硬截断, 避免手感突变)
    ///   2. 轻量低通: 抑制力矩高频抖动
    ///   3. 无输入时衰减残余 yaw: 回收/停手后不再有主动转向意图, 放大 yaw 阻尼让残余角速度尽快衰减
    ///
    /// 单位: 力矩 N·m; 时间常数 s。
    /// </summary>
    [System.Serializable]
    public class YawTorqueStabilizer
    {
        [Tooltip("虚拟 yaw 稳定总开关。关闭时力矩原样透传。")]
        public bool enabled = true;

        [Tooltip("力矩软死区 (N·m)。|τ_y| 低于此值被平滑压到 0, 用于吃掉两手不对称带来的无意扭矩。" +
                 "必须远低于「有意但轻的转向」扭矩, 否则会把转向一起吃掉 —— " +
                 "实测 0.15 会把最轻的转弯划水 (τ_y≈0.29 N·m) 削掉 17%, 故降到 0.04。")]
        public float torqueDeadzone = 0.04f;

        [Tooltip("软死区过渡宽度 (N·m)。死区边缘到此宽度之间平滑放行, 避免硬截断的手感突变。")]
        public float torqueSoftness = 0.04f;

        [Tooltip("力矩低通时间常数 (s)。")]
        public float smoothingTimeConstant = 0.06f;

        [Tooltip("有效输入阈值 (N)。判据是【双手力幅之和】|F_L|+|F_R|, 不是净力 —— " +
                 "原地转弯辅助会把净力抵消到约 0, 用净力当判据会把转弯误当成「回收/停手」, " +
                 "从而放大 yaw 阻尼把转向吃掉。低于此值视为回收/停手, 触发残余 yaw 衰减。")]
        public float inputForceThreshold = 0.15f;

        [Tooltip("无有效输入时 yaw 旋转阻尼的放大倍数 (≥1)。" +
                 "默认 1 = 不做残余衰减, 松手后按动力学自身的 yaw 阻尼 (时间常数约 0.3s) 自然收住。" +
                 "实测调到 3 会把整个转弯划水的偏航量削掉 7~17% (转得越轻削得越多), " +
                 "因为松手后角速度被立刻掐掉。想让松手后停得更干脆才调大, 代价就是转向量变少。")]
        public float idleYawDampingScale = 1f;

        [System.NonSerialized] private float _filtered;
        [System.NonSerialized] private bool _initialized;

        /// <summary>本帧软死区对力矩的放行系数 [0,1] (调试)</summary>
        public float DeadzoneFactor { get; private set; }

        /// <summary>本帧施加在 yaw 旋转阻尼上的倍数 (调试)</summary>
        public float RotationalDampingScale { get; private set; } = 1f;

        public void Reset()
        {
            _filtered = 0f;
            _initialized = false;
            DeadzoneFactor = 1f;
            RotationalDampingScale = 1f;
        }

        /// <summary>对绕身体 Y 轴的力矩做稳定化; 返回稳定后的 yaw 力矩 (N·m)。</summary>
        public float Stabilize(float rawYawTorque, bool hasValidTranslationInput, float dt)
        {
            if (!enabled)
            {
                DeadzoneFactor = 1f;
                RotationalDampingScale = 1f;
                _filtered = rawYawTorque;
                _initialized = true;
                return rawYawTorque;
            }

            // 残余 yaw 衰减: 没有平移输入时按「回收/停手」处理
            RotationalDampingScale = hasValidTranslationInput
                ? 1f
                : Mathf.Max(1f, idleYawDampingScale);

            if (!_initialized)
            {
                _filtered = rawYawTorque;
                _initialized = true;
                DeadzoneFactor = 1f;
                return rawYawTorque;
            }

            // 软死区: 死区内→0, 死区+宽度外→原值, 中间平滑过渡
            float magnitude = Mathf.Abs(rawYawTorque);
            float deadzone = Mathf.Max(0f, torqueDeadzone);
            float softness = Mathf.Max(1e-4f, torqueSoftness);
            DeadzoneFactor = Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(deadzone, deadzone + softness, magnitude));
            float shaped = rawYawTorque * DeadzoneFactor;

            // 轻量低通
            float tau = Mathf.Max(0f, smoothingTimeConstant);
            if (tau > 1e-5f && dt > 0f)
            {
                float alpha = 1f - Mathf.Exp(-dt / tau);
                _filtered = Mathf.Lerp(_filtered, shaped, alpha);
            }
            else
            {
                _filtered = shaped;
            }

            return _filtered;
        }
    }

    /// <summary>
    /// 人工平移的加速度变化率 (jerk) 限制。
    ///
    /// 这是【辅助控制层】, 不是物理模型的一部分: 幅值限制 (comfortMode.maxAcceleration)
    /// 只能约束加速度的大小, 约束不了它变化的快慢; 力的突变仍会表现为加速度台阶,
    /// 在 VR 里就是画面一"顿"。
    ///
    /// 起步 / 刹车 / 反向 / 模式切换的处理:
    ///   - 初始化与 Reset 时直接对齐目标, 避免从 0 缓慢爬升造成新的起步迟滞
    ///   - 输入力与速度反向 (刹车/反向) 时放宽到 maxJerkOnBrake, 不拖慢停车
    ///   - 速度越低限制越松 (低速时本来就更敏感于迟滞)
    ///
    /// 单位: jerk m/s³; 速度 m/s。
    /// </summary>
    [System.Serializable]
    public class AccelerationJerkLimiter
    {
        [Tooltip("jerk 限制总开关。这是辅助控制层, 可随时关闭回到纯幅值限制。")]
        public bool enabled = true;

        [Tooltip("最大加速度变化率 (m/s³)。越小越平滑、响应越迟滞。调试初值, 非最优值。")]
        public float maxJerk = 25f;

        [Tooltip("刹车/反向时的最大 jerk (m/s³)。应≥ maxJerk, 避免拖慢停车与反向。")]
        public float maxJerkOnBrake = 80f;

        [Tooltip("低于此速度 (m/s) 时不再限制 jerk, 避免低速下无法启动或停住。")]
        public float jerkFreeSpeed = 0.10f;

        [System.NonSerialized] private Vector3 _lastAcceleration;
        [System.NonSerialized] private bool _initialized;

        /// <summary>本帧实际使用的 jerk 上限 (调试)</summary>
        public float ActiveJerkLimit { get; private set; }

        public void Reset()
        {
            _lastAcceleration = Vector3.zero;
            _initialized = false;
            ActiveJerkLimit = 0f;
        }

        public Vector3 Apply(Vector3 targetAcceleration, Vector3 velocity, Vector3 inputForce, float dt)
        {
            if (!enabled || dt <= 0f)
            {
                _lastAcceleration = targetAcceleration;
                _initialized = true;
                ActiveJerkLimit = 0f;
                return targetAcceleration;
            }

            if (!_initialized)
            {
                _lastAcceleration = targetAcceleration;
                _initialized = true;
                ActiveJerkLimit = 0f;
                return targetAcceleration;
            }

            float limit = Mathf.Max(0f, maxJerk);

            // 刹车 / 反向: 放宽, 不拖慢停车
            float speed = velocity.magnitude;
            float forceMagnitude = inputForce.magnitude;
            if (speed > 1e-4f && forceMagnitude > 1e-6f)
            {
                float cos = Vector3.Dot(inputForce, velocity) / (forceMagnitude * speed);
                if (cos < 0f) limit = Mathf.Max(limit, maxJerkOnBrake);
            }

            // 低速区完全不限, 保证能起步、能停住
            if (speed < Mathf.Max(0f, jerkFreeSpeed)) limit = float.PositiveInfinity;

            ActiveJerkLimit = limit;

            if (float.IsPositiveInfinity(limit))
            {
                _lastAcceleration = targetAcceleration;
                return targetAcceleration;
            }

            Vector3 delta = targetAcceleration - _lastAcceleration;
            _lastAcceleration += Vector3.ClampMagnitude(delta, limit * dt);
            return _lastAcceleration;
        }
    }
}
