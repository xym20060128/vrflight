using UnityEngine;
using SwimFlight.Core;

// 用不同名别名引用新 FlightState, 避免与全局旧 FlightState 冲突
using V2State = SwimFlight.Core.FlightState;

namespace SwimFlight.Dynamics
{
    /// <summary>
    /// 飞行动力学 V2 (刚体动力学, 牛顿-欧拉, 纯函数式)
    ///
    /// 数据流:
    ///   (V2State 当前状态, FlightCommand 左右手反作用力+力臂)
    ///       → FlightDynamicsV2 → V2State (新状态)
    ///
    /// 数学模型 (反作用力驱动, 无人工 yaw 控制):
    ///
    ///   平移:  F_total = F_L + F_R
    ///          m·dv/dt = F_total - c1(v,F)·v - c2·|v|·v
    ///
    ///   c1 默认是常量 linearDamping; 挂上 asymmetricDamping 后按「输入力与当前速度的
    ///   夹角」分相取值 (推进低阻尼 / 反向辅助制动 / 无输入滑行), 二次阻尼 c2 不变。
    ///   无论 c1 取何值, 阻尼项都不会把速度推过零 (见 Integrate 内的钳位)。
    ///
    ///   旋转:  τ = r_L×F_L + r_R×F_R       (完整 3 维, 绕 bodyCenter)
    ///          I·dω/dt = τ - C·ω            (标量 I, C 为各向异性阻尼向量)
    ///
    ///   积分:  半隐式 Euler (平移) + 轴角四元数积分 (旋转)
    ///
    /// 姿态稳定 (第一阶段):
    ///   yaw        正常响应 (rotationalDamping 常规值)
    ///   pitch/roll 强阻尼   (attitudeDamping 较大), 避免身体倾斜/VR 晕动
    ///
    ///   FlightState 保存完整 orientation + angularVelocity (供分析);
    ///   应用层只取 yaw, 不直接应用完整 orientation。
    ///
    /// 单位: 角速度内部用 rad/s; orientation 为 Quaternion, angularVelocity 为 rad/s。
    /// </summary>
    public class FlightDynamicsV2
    {
        // === 动力学参数 ===
        public float mass = 1f;              // 虚拟质量 m (kg)
        public float linearDamping = 1.2f;      // 线性阻尼 c1 (N/(m/s))
        public float quadraticDamping = 0.35f;  // 二次阻尼 c2 (N/(m/s)²), 高速限速
        public float rotationalInertia = 1.5f;  // 等效转动惯量 I (kg·m², 标量各向同性)
        public float rotationalDamping = 5f;    // yaw 旋转阻尼 c_rot (N·m/(rad/s))
        public float yawTorqueGain = 1f;
        // Runtime condition, supplied by the arm controller each frame.
        [System.NonSerialized] public bool stationaryTurn;
        [System.NonSerialized] public bool gripTurnControl;
        [System.NonSerialized] public bool gripTurnHeld;
        public float attitudeDamping = 40f;     // pitch/roll 旋转阻尼 (强, 姿态稳定)
        public float maxAngularVelocity = 0.35f;   // 最大角速度硬限制 (rad/s)
        public float horizontalSpeedLimit; // Zero retains the original spherical speed limit.
        public float verticalSpeedLimit;
        public float maxSpeed = 3f;            // 最大线速度硬限制 (m/s), 与 maxAngularVelocity 对称

        /// <summary>
        /// 创建初始状态 (协调器 Start 时用 XR Origin 初始位姿调用)
        /// </summary>
        public bool comfortMode = true;
        public float maxAcceleration = 2f;
        public float maxAngularAcceleration = 0.7f;

        /// <summary>
        /// 非对称平移阻尼 (可为 null = 完全保持改动前的行为)。
        /// 每子步依据「净输入力 vs 当前速度」的夹角给出有效线性阻尼系数,
        /// 本项目只改 c1 这一个系数, 二次阻尼 c2 与旋转动力学不受影响。
        /// </summary>
        public AsymmetricTranslationDamping asymmetricDamping;
        public VerticalFlightPermission verticalPermission;
        private readonly AccelerationJerkLimiter _verticalJerkLimiter = new AccelerationJerkLimiter();

        /// <summary>虚拟 yaw 稳定 (可为 null = 不做任何 yaw 稳定, 力矩原样使用)。</summary>
        public YawTorqueStabilizer yawStabilizer;

        /// <summary>人工平移的 jerk 限制 (可为 null = 只做幅值限制, 与改动前一致)。辅助控制层。</summary>
        public AccelerationJerkLimiter jerkLimiter;

        /// <summary>最近一次子步实际使用的线加速度 (m/s²), 仅诊断用。</summary>
        public Vector3 LastAcceleration { get; private set; }

        /// <summary>最近一次子步实际使用的 yaw 角加速度 (rad/s²), 仅诊断用。</summary>
        public float LastYawAcceleration { get; private set; }

        public V2State Initialize(Vector3 initialPosition, Quaternion initialRotation)
        {
            _verticalJerkLimiter.Reset();
            V2State state = new V2State();
            state.position = initialPosition;
            state.velocity = Vector3.zero;
            state.orientation = comfortMode
                ? Quaternion.Euler(0f, initialRotation.eulerAngles.y, 0f) : initialRotation;
            state.angularVelocity = Vector3.zero;
            return state;
        }

        /// <summary>
        /// 牛顿-欧拉积分: 当前状态 + 左右手反作用力/力臂 → 新状态
        /// 同时把计算出的 totalForce / totalTorque 写回 command (供协调器观察/调试)。
        /// </summary>
        /// <param name="state">当前动力学状态 (V2State)</param>
        /// <param name="command">左右手反作用力 + 力臂 (FlightCommand); 本层填充 totalForce/totalTorque</param>
        /// <param name="dt">帧间隔 (秒)</param>
        public V2State Integrate(V2State state, ref FlightCommand command, float dt, bool rotationOnly = false)
        {
            // ---- 刚体合成力 / 力矩 ----
            Vector3 totalForce = command.hasTranslationForce
                ? command.translationForce : command.leftForce + command.rightForce;
            if (gripTurnControl && gripTurnHeld) totalForce = Vector3.zero;
            Vector3 unrestrictedForce = totalForce;
            bool verticalGate = verticalPermission != null && verticalPermission.enabled;
            if (!rotationOnly) verticalPermission?.BeginStep(dt);
            if (!verticalGate) _verticalJerkLimiter.Reset();
            if (verticalGate) totalForce = verticalPermission.FilterForce(totalForce);

            // 力矩 = 力臂 × 力 (绕身体中心 = bodyCenter)。完整 3 维。
            Vector3 rawTorque = Vector3.Cross(command.leftArm, command.leftForce)
                              + Vector3.Cross(command.rightArm, command.rightForce);
            Vector3 torque = rawTorque;
            torque.y *= Mathf.Max(0f, yawTorqueGain);

            // 力矩稳定 (虚拟 yaw): 只作用于手臂力矩产生的偏航, 不改用户真实转头追踪。
            // 关掉 yawStabilizer 时这里是纯透传, 行为与改动前一致。
            if (yawStabilizer != null)
            {
                float inputThreshold = yawStabilizer.inputForceThreshold;
                // 「用户还在划」的判据要用**双手力幅之和**, 不能用净力 |F_L+F_R|:
                // 原地转弯辅助会把净力抵消到 ~0 (那正是它的职责), 若拿净力当判据,
                // 转弯会被误判成「回收/停手」→ 放大 yaw 阻尼 → 转向被吃掉。
                bool hasTranslationInput =
                    command.leftForce.magnitude + command.rightForce.magnitude > inputThreshold;
                float stabilizedYaw = yawStabilizer.Stabilize(torque.y, hasTranslationInput, dt);
                torque = new Vector3(torque.x, stabilizedYaw, torque.z);
            }

            if (gripTurnControl && !gripTurnHeld) torque = Vector3.zero;
            command.rawTotalTorque = rawTorque;
            command.totalForce = totalForce;
            command.totalTorque = torque;

            Vector3 nextVelocity = state.velocity;
            Vector3 nextPosition = state.position;
            if (!rotationOnly)
            {

                // ---- 平移动力学: m·a = F_total - F_drag ----
                Vector3 v = state.velocity;
                float speed = v.magnitude;
                float m = Mathf.Max(1e-4f, mass);   // 防止 Inspector 填 0 直接除爆

                // 有效线性阻尼 c1: 关掉非对称阻尼时恒等于 linearDamping, 行为与改动前一致。
                // F_input 用门控后、阻尼前的净平移合力 totalForce, v 用本步施加输入前的速度,
                // 两者都是世界空间 (同坐标系), 夹角判定才成立。
                float dampingCoefficient = linearDamping;
                if (asymmetricDamping != null)
                    // Keep the original damping classifier independent of Trigger projection.
                    dampingCoefficient = asymmetricDamping.Evaluate(unrestrictedForce, v, dt, linearDamping, m);

                // 阻尼合力 (与 v 反向), 取负号后才成为加速度贡献: m·a = F_total - F_drag
                Vector3 dampingDrag = dampingCoefficient * v + quadraticDamping * speed * v;
                Vector3 dampingAcceleration = -dampingDrag / m;

                if (asymmetricDamping != null && asymmetricDamping.enabled)
                {
                    // 阻尼项单独不得把速度推过零。显式 Euler 在大步长/大阻尼下会过冲反号,
                    // 那是「阻尼导致的数值反向」, 会凭空造出一个反向速度, 既违反不可增能、
                    // 又会被误读成用户的反向意图。这里只钳阻尼项: 用户输入力那一路不受限,
                    // 所以速度归零后真实的反向输入依然能建立反向速度, 不会被挡住。
                    if (speed > 1e-6f)
                    {
                        Vector3 direction = v / speed;
                        float alongVelocity = Vector3.Dot(dampingAcceleration, direction);
                        if (alongVelocity < 0f && -alongVelocity * dt > speed)
                            dampingAcceleration = -direction * (speed / Mathf.Max(1e-6f, dt));
                    }
                }

                Vector3 acceleration = unrestrictedForce / m + dampingAcceleration;

                // 半隐式 Euler (平移)
                if (comfortMode)
                    acceleration = Vector3.ClampMagnitude(acceleration, Mathf.Max(0.1f, maxAcceleration));

                // jerk 限制 (辅助控制层, 默认关闭): 约束加速度变化快慢, 不只是幅值。
                // 放在幅值钳位之后 —— 先保证不超上限, 再保证变化不突兀。
                if (jerkLimiter != null)
                    acceleration = jerkLimiter.Apply(acceleration, v, unrestrictedForce, dt);

                LastAcceleration = acceleration;   // 诊断用: 实际进入积分的加速度

                nextVelocity = v + acceleration * dt;
                if (gripTurnControl && gripTurnHeld)
                {
                    nextVelocity = Vector3.zero;
                    LastAcceleration = -v / Mathf.Max(1e-6f, dt);
                }
                if (stationaryTurn)
                {
                    // Ignore horizontal thrust while turning; brake existing momentum within
                    // the same 3D acceleration budget, without affecting the vertical input.
                    float verticalDelta = Mathf.Clamp(nextVelocity.y - v.y,
                        -maxAcceleration * dt, maxAcceleration * dt);
                    float horizontalBudget = Mathf.Sqrt(Mathf.Max(0f,
                        maxAcceleration * maxAcceleration * dt * dt - verticalDelta * verticalDelta));
                    Vector3 horizontal = Vector3.MoveTowards(new Vector3(v.x, 0f, v.z),
                        Vector3.zero, horizontalBudget);
                    nextVelocity = new Vector3(horizontal.x, v.y + verticalDelta, horizontal.z);
                    acceleration = (nextVelocity - v) / Mathf.Max(1e-6f, dt);
                    LastAcceleration = acceleration;
                }

                // 线速度硬限制。阻尼项是显式积分的, 稳定条件为 (c1 + 2·c2·|v|)·dt/m < 2,
                // 掉帧(dt 变大)或 mass 偏小时会过冲反号并逐帧放大 → Inf → NaN。
                // 角速度本来就有钳位, 线速度之前没有, 补上。
                if (horizontalSpeedLimit > 0f && verticalSpeedLimit > 0f)
                {
                    Vector3 horizontal = Vector3.ClampMagnitude(new Vector3(nextVelocity.x, 0f, nextVelocity.z), horizontalSpeedLimit);
                    nextVelocity = new Vector3(horizontal.x, Mathf.Clamp(nextVelocity.y, -verticalSpeedLimit, verticalSpeedLimit), horizontal.z);
                }
                else if (nextVelocity.sqrMagnitude > maxSpeed * maxSpeed)
                    nextVelocity = nextVelocity.normalized * maxSpeed;

                // 残余速度归零: 只在【没有有效平移输入】且速度足够小时才允许。
                // 有输入时永不归零 —— 否则持续反向输入会被反复清零, 反向启动不了。
                if (asymmetricDamping != null && asymmetricDamping.enabled
                    && !asymmetricDamping.HasValidInput)
                {
                    float residual = Mathf.Max(0f, asymmetricDamping.residualVelocityThreshold);
                    if (nextVelocity.sqrMagnitude <= residual * residual)
                        nextVelocity = Vector3.zero;
                }

                if (verticalGate && !(gripTurnControl && gripTurnHeld))
                {
                    // Preserve the existing horizontal solver, damping classification and
                    // limits. Update only world-Y from permitted thrust and existing drag.
                    // The remaining 3D acceleration/speed budget bounds Y without changing XZ.
                    float horizontalAcceleration = new Vector2(acceleration.x, acceleration.z).magnitude;
                    float verticalBudget = comfortMode ? Mathf.Sqrt(Mathf.Max(0f,
                        maxAcceleration * maxAcceleration - horizontalAcceleration * horizontalAcceleration))
                        : float.PositiveInfinity;
                    float verticalDrag = dampingAcceleration.y;
                    if (v.y * verticalDrag < 0f && Mathf.Abs(verticalDrag) * dt > Mathf.Abs(v.y))
                        verticalDrag = -v.y / Mathf.Max(1e-6f, dt);
                    float permittedAcceleration = totalForce.y / m + verticalDrag;
                    permittedAcceleration = Mathf.Clamp(permittedAcceleration, -verticalBudget, verticalBudget);
                    if (jerkLimiter != null && jerkLimiter.enabled)
                    {
                        _verticalJerkLimiter.enabled = true;
                        _verticalJerkLimiter.maxJerk = jerkLimiter.maxJerk;
                        _verticalJerkLimiter.maxJerkOnBrake = jerkLimiter.maxJerkOnBrake;
                        _verticalJerkLimiter.jerkFreeSpeed = jerkLimiter.jerkFreeSpeed;
                        permittedAcceleration = _verticalJerkLimiter.Apply(Vector3.up * permittedAcceleration,
                            Vector3.up * v.y, Vector3.up * totalForce.y, dt).y;
                        permittedAcceleration = Mathf.Clamp(permittedAcceleration, -verticalBudget, verticalBudget);
                    }
                    float candidateY = v.y + permittedAcceleration * dt;
                    float dampedY = verticalPermission.DampVertical(candidateY, m, dt);
                    // Extra damping cannot steal the horizontal acceleration budget.
                    dampedY = Mathf.Clamp(dampedY, v.y - verticalBudget * dt, v.y + verticalBudget * dt);
                    float verticalSpeedBudget = verticalSpeedLimit > 0f ? verticalSpeedLimit : Mathf.Sqrt(Mathf.Max(0f,
                        maxSpeed * maxSpeed - nextVelocity.x * nextVelocity.x - nextVelocity.z * nextVelocity.z));
                    nextVelocity.y = Mathf.Clamp(dampedY, -verticalSpeedBudget, verticalSpeedBudget);
                    LastAcceleration = (nextVelocity - v) / Mathf.Max(1e-6f, dt);
                }

                nextPosition = state.position + nextVelocity * dt;
            }
            else LastAcceleration = Vector3.zero;

            // ---- 旋转动力学: I·α = τ - C·ω ----
            // 各向异性阻尼: yaw 常规(rotationalDamping), pitch/roll 强(attitudeDamping)。
            Vector3 omega = state.angularVelocity;
            // 无有效平移输入时 (回收/停手) 放大 yaw 阻尼, 让残余偏航角速度尽快衰减
            float yawDampingScale = yawStabilizer != null ? yawStabilizer.RotationalDampingScale : 1f;
            Vector3 dampingVec = new Vector3(attitudeDamping, rotationalDamping * yawDampingScale, attitudeDamping);

            float inertia = Mathf.Max(1e-4f, rotationalInertia);
            Vector3 alpha = new Vector3(
                (torque.x - dampingVec.x * omega.x) / inertia,
                (torque.y - dampingVec.y * omega.y) / inertia,
                (torque.z - dampingVec.z * omega.z) / inertia);

            if (comfortMode)
            {
                omega = new Vector3(0f, omega.y, 0f);
                alpha = new Vector3(0f, Mathf.Clamp(alpha.y,
                    -Mathf.Max(0.1f, maxAngularAcceleration),
                    Mathf.Max(0.1f, maxAngularAcceleration)), 0f);
            }
            LastYawAcceleration = alpha.y;   // 诊断用
            Vector3 nextOmega = omega + alpha * dt;
            if (gripTurnControl && !gripTurnHeld) nextOmega = Vector3.zero;
            bool brakingForTurn = stationaryTurn &&
                (nextVelocity.x * nextVelocity.x + nextVelocity.z * nextVelocity.z) > 1e-10f;
            if (brakingForTurn)
            {
                // Do not accumulate unseen yaw during braking, which would jump on release.
                nextOmega = Vector3.zero;
                LastYawAcceleration = 0f;
            }

            // 硬限制角速度 (全轴)
            float maxAng = maxAngularVelocity;
            if (nextOmega.sqrMagnitude > maxAng * maxAng)
                nextOmega = nextOmega.normalized * maxAng;

            // 轴角四元数积分 (世界空间角速度)
            Quaternion dq = Quaternion.identity;
            float angle = nextOmega.magnitude * dt;   // rad
            if (angle > 1e-6f)
            {
                Vector3 axis = nextOmega / nextOmega.magnitude;
                dq = Quaternion.AngleAxis(angle * Mathf.Rad2Deg, axis);
            }
            Quaternion orientation = comfortMode
                ? Quaternion.Euler(0f, state.orientation.eulerAngles.y, 0f) : state.orientation;
            Quaternion nextOrientation = Quaternion.Normalize(dq * orientation);

            // ---- 组装输出 ----
            V2State next = new V2State();
            next.position = nextPosition;
            next.velocity = nextVelocity;
            next.orientation = nextOrientation;
            next.angularVelocity = nextOmega;

            // NaN/Inf 兜底。
            // 状态一旦被污染, XRLocomotionApplierV2 会把它无条件写进 XR Origin 的 Transform,
            // 之后满屏 "Invalid localAABB / worldAABB / Invalid AABB", 且自己恢复不了。
            // 注意 maxSpeed/maxAngularVelocity 的钳位挡不住 NaN —— NaN 的比较恒为 false,
            // 会直接穿过 if 继续传播, 所以必须在这里显式拦一道。
            if (!IsFinite(next.position) || !IsFinite(next.velocity)
                || !IsFinite(next.angularVelocity) || !IsFinite(next.orientation))
            {
                Debug.LogWarning("[FlightDynamicsV2] 状态出现 NaN/Inf, 已回退到上一帧并清零速度。" +
                                 "通常是掉帧导致积分步长过大, 检查 maxTimeStep / mass。");
                next.position = IsFinite(state.position) ? state.position : Vector3.zero;
                next.velocity = Vector3.zero;
                next.orientation = IsFinite(state.orientation) ? state.orientation : Quaternion.identity;
                next.angularVelocity = Vector3.zero;
            }

            return next;
        }

        private static bool IsFinite(Vector3 v)
            => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
              || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        private static bool IsFinite(Quaternion q)
            => !(float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w)
              || float.IsInfinity(q.x) || float.IsInfinity(q.y) || float.IsInfinity(q.z)
              || float.IsInfinity(q.w));
    }
}
