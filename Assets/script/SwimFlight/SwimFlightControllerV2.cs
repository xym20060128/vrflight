using UnityEngine;
using Unity.XR.CoreUtils;
using SwimFlight.Core;
using SwimFlight.Input;
using SwimFlight.Coordinate;
using SwimFlight.Mapping;
using SwimFlight.Dynamics;
using SwimFlight.Locomotion;
using SwimFlight.Diagnostics;

// 用不同名别名引用新类型, 避免与全局旧 BiomechanicalMapper / FlightState / TrackingSpaceReference 冲突
using V2Mapper = SwimFlight.Mapping.BiomechanicalMapper;
using V2State = SwimFlight.Core.FlightState;
using V2TrackingSpace = SwimFlight.Coordinate.TrackingSpaceReference;

/// <summary>
/// Swimming input adapter: tracked hands -> coordinates -> tangential forces/torque.
/// Under FlightControlModeSwitcher, input mapping feeds the shared comparison actuator.
/// Without the switcher, the legacy independent dynamics pipeline remains available.
/// </summary>
public enum StrokeDiagnosticComparison { Baseline, GateOnly, RadialOnly }

public class SwimFlightControllerV2 : MonoBehaviour
{
    [Header("=== 引用 ===")]
    [Tooltip("XR Origin Transform (会被移动的根对象)")]
    public Transform xrOrigin;

    [Tooltip("头显/Camera Transform (用于确定前方)")]
    public Transform headTransform;

    [Tooltip("可选：实际左手输入组件。该组件禁用/对象隐藏时清除左 Trigger 权限；不改变原手速映射。")]
    public Behaviour leftInputController;
    [Tooltip("可选：实际右手输入组件。该组件禁用/对象隐藏时清除右 Trigger 权限。")]
    public Behaviour rightInputController;

    [Header("=== 坐标系 ===")]
    [Tooltip("追踪空间参考系: World=不转换 | XROrigin=乘xrOrigin旋转 | HeadLocal=乘头部旋转")]
    public V2TrackingSpace trackingSpaceReference = V2TrackingSpace.XROrigin;

    [Header("=== 调试 ===")]
    [Tooltip("输出反作用力/力矩/角速度")]
    public bool enableDebugLog = false;

    [Tooltip("debug 输出间隔 (秒), 0 = 每帧")]
    public float debugLogInterval = 0.5f;

    [Header("=== 速度估计 ===")]
    [Tooltip("位置差分低通滤波时间常数 (秒), 越小响应越快/越抖; 帧率无关")]
    public float velocitySmoothingTimeConstant = 0.06f;

    [Header("=== 身体中心 ===")]
    [Tooltip("身体中心离地高度 (m), 水平位置跟随头显。力矩/门控参考点")]
    public float bodyCenterOffset = 1.1f;

    [Header("=== 空间门控 (Recovery 机制) ===")]
    [Tooltip("进入有效操作空间的距离阈值 (m)")]
    public float spatialEnterDistance = 0.30f;

    [Tooltip("退出有效操作空间的距离阈值 (m)")]
    public float spatialExitDistance = 0.18f;

    [Header("Stroke diagnostic comparison")]
    public StrokeDiagnosticComparison strokeComparison = StrokeDiagnosticComparison.Baseline;
    [Min(0f)] public float diagnosticGateEnter = 0.30f;
    [Min(0f)] public float diagnosticGateExit = 0.25f;
    [Range(0f, 1f), Tooltip("RadialOnly: retain radial velocity. Also changes hand torque and recovery forces.")]
    public float diagnosticRadialRetention = 0.15f;

    [ContextMenu("Diagnostics/Reset motion at current position")]
    public void ResetStrokeDiagnosticMotion()
    {
        if (EnsureInitialized()) ReanchorForModeSwitch();
    }


    [Header("=== 反作用力 ===")]
    [Tooltip("反作用力增益 k (各向同性标量, N/(m/s)), 作用于切向速度 v_tan")]
    public float reactionGain = 4f;

    [Tooltip("统一前后、上下平移：完整手速产生推力，共用增益和限速。转向仍使用原切向力矩。")]
    public bool uniformTranslation = true;
    [Min(0f)] public float translationPropulsionGain = 2f;

    [Header("=== 划手调节巡航速度（由模式切换器选择） ===")]
    public bool useStrokeCruise;
    public StrokeCruiseSettings cruiseSettings = new StrokeCruiseSettings();
    private readonly StrokeCruiseTranslation _cruise = new StrokeCruiseTranslation();
    private bool _appliedCruiseMode;
    private float _cruiseDebugTimer;
    public StrokeCruiseTranslation Cruise => _cruise;

    [Min(0f), Tooltip("水平净推力倍率：1 为原比例，2 为同样划动产生两倍水平推力。只放大世界 XZ 平移，不修改单手力矩或垂直力；实际位移仍受加速度、阻尼和限速约束。")]
    public float horizontalPropulsionGain = 2f;
    [System.NonSerialized] public float userHorizontalMultiplier = 1.5f;
    [System.NonSerialized] public float userVerticalMultiplier = 1f;

    [Header("=== 平滑主方向辅助（初值待预实验验证） ===")]
    public PrimaryDirectionAssist primaryDirectionAssist = new PrimaryDirectionAssist();

    [Header("=== Trigger 升降权限（初值待预实验验证） ===")]
    public VerticalFlightPermission verticalPermission = new VerticalFlightPermission();

    [Header("=== 手臂条件: 驱动方式与平移阻尼 ===")]
    [Tooltip("手臂(划水)条件是否使用力驱动动力学链路: 输入力 → 加速度 → 速度 → 位移。\n" +
             "开启(推荐): 本控制器自己跑 FlightDynamicsV2 并写 XR Origin 位移, 不调用共享电机。\n" +
             "关闭: 退回改动前的行为 —— 输入力换算成目标速度, 交给共享电机 (与摇杆/前倾条件共用)。")]
    public bool forceDrivenDynamics = true;

    [Tooltip("非对称平移阻尼: 输入力沿速度方向时低阻尼便于加速, 明确反向时辅助制动, " +
             "回收/无输入时用独立滑行阻尼。关闭其内部 enabled 即恢复原有恒定阻尼行为。")]
    public AsymmetricTranslationDamping asymmetricDamping = new AsymmetricTranslationDamping();

    [Tooltip("输出非对称平移阻尼的逐帧调试信息 (相位/夹角/有效阻尼/辅助制动减速度/切换原因)。" +
             "独立于 enableDebugLog, 不受 FlightControlModeSwitcher 接管影响。")]
    public bool enableDampingDebugLog = false;

    [Header("=== 诊断模式 (定位抖动来源) ===")]
    [Tooltip("三种模式都保留正常 HMD 追踪, 都不对 Main Camera 做滤波/冻结/抵消。\n" +
             "Full = 完整飞行; TranslationOnly = 有平移但无手臂虚拟 yaw; " +
             "HeadTrackingOnly = 关闭全部人工运动, 只留真实头动。\n" +
             "切换时会重锚位姿并清零速度/角速度, 避免跳变。")]
    public FlightDiagnosticMode diagnosticMode = FlightDiagnosticMode.Full;

    [Tooltip("模式切换时打印一行说明 (含重锚细节), 便于在记录里对齐时间点。")]
    public bool logDiagnosticModeChanges = true;

    [Header("=== 虚拟运动稳定 (逐项独立开关) ===")]
    [Tooltip("推进力平滑: 在门控后、积分前对左右手力做帧率无关低通, 保留力驱动机制。" +
             "制动/反向的手走更短时间常数, 不拖慢停车。")]
    public InputForceSmoother forceSmoother = new InputForceSmoother();

    [Tooltip("虚拟 yaw 稳定: 对手臂力矩产生的偏航做软死区 + 轻量平滑, 无输入时衰减残余 yaw。" +
             "不改变用户真实转头追踪。")]
    public YawTorqueStabilizer yawStabilizer = new YawTorqueStabilizer();

    [Tooltip("加速度变化率 (jerk) 限制。辅助控制层, 不是物理模型的一部分。" +
             "可让起步/刹车不那么「顿」, 代价是响应变迟缓。")]
    public AccelerationJerkLimiter jerkLimiter = new AccelerationJerkLimiter();

    [Tooltip("原地转弯辅助: 识别「两手绕竖直轴同向划水」的转向手势, 扣掉孪生的净水平力, " +
             "只保留力矩 —— 用径向修正抵消净力, 力矩在数学上严格不变。\n" +
             "判据是「两手对偏航的贡献是否同向」(coherence), 不是旧版的世界坐标对抗度;\n" +
             "双手同向划水/刹车 (含推进与垂直) coherence≈0, 完全不受影响。")]
    public TurnInPlaceAssist turnInPlace = new TurnInPlaceAssist();
    [Tooltip("按住任一侧握扳机（Grip）时立即停止全部虚拟平移，只允许手臂偏航；松开后只允许移动。")]
    public bool stationaryTurning = true;
    private bool _gripTurnHeld;

    [Tooltip("虚拟 yaw 绕【头部支点】旋转 (推荐开启)。\n" +
             "关闭 = 退回旧写法「直接写 XR Origin rotation」, 会让玩家绕地面轴画弧 —— " +
             "仅用于 A/B 对比定位抖动, 不建议长期关闭。")]
    public bool yawPivotUnderHead = true;

    [Header("=== 动力学 ===")]
    [Tooltip("虚拟质量 (kg)")]
    public float mass = 1f;
    [Tooltip("线性阻尼 (N/(m/s))")]
    public float linearDamping = 1.2f;
    [Tooltip("二次阻尼 (N/(m/s)²)")]
    public float quadraticDamping = 0.35f;
    [Tooltip("等效转动惯量 (kg·m², 标量各向同性)")]
    public float rotationalInertia = 1.5f;
    [Tooltip("yaw 旋转阻尼 (N·m/(rad/s))")]
    public float rotationalDamping = 5f;
    [Min(0f), Tooltip("手臂偏航力矩增益：1 为原比例，1.5 为同样划动产生 1.5 倍转向力矩；仍受共同旋转上限约束。")]
    public float yawTorqueGain = 6f;
    [Tooltip("pitch/roll 旋转阻尼 (N·m/(rad/s)), 较强用于姿态稳定")]
    public float attitudeDamping = 40f;
    [Tooltip("最大角速度硬限制 (rad/s)")]
    public float maxAngularVelocity = 0.35f;
    [Tooltip("最大线速度硬限制 (m/s), 与 maxAngularVelocity 对称")]
    public float maxSpeed = 3f;

    [Header("=== 数值稳定 ===")]
    [Tooltip("单个积分子步的最大时长 (秒)。掉帧时把一帧拆成多个子步, " +
             "避免显式阻尼过冲导致速度发散成 NaN。0.02 = 至少按 50Hz 积分")]
    public float maxTimeStep = 0.02f;

    [Tooltip("单帧最多子步数, 防止极端掉帧时越积越慢的死亡螺旋")]
    public int maxSubSteps = 8;

    // === 模块 ===
    [Header("Comfort settings")]
    [Tooltip("Integrate yaw only, matching the applied XR rotation.")]
    public bool comfortMode = true;
    [Min(0.1f), Tooltip("Maximum linear acceleration/deceleration (m/s squared).")]
    public float maxAcceleration = 2f;
    [Min(0.1f), Tooltip("Maximum yaw acceleration (rad/s squared).")]
    public float maxAngularAcceleration = 0.7f;

    [System.NonSerialized] public SwimFlight.ComparisonMotionMotor ComparisonMotor;

    /// <summary>
    /// 供 FlightControlModeSwitcher 查询: 本控制器是否自己积分并写 XR Origin 位移。
    /// 为 true 时共享电机必须让路, 否则同一帧会有两条链路同时移动 XR Origin。
    /// </summary>
    public bool UsesForceDrivenDynamics => useStrokeCruise || forceDrivenDynamics;

    /// <summary>本帧生效的速度更新模块 (调试/验证用, 区分实际运行链路)。</summary>
    public string ActiveVelocitySource
    {
        get
        {
            if (useStrokeCruise) return "StrokeCruiseTranslation (事件目标速度，无被动阻尼)";
            if (ComparisonMotor != null && !forceDrivenDynamics)
                return "ComparisonMotionMotor (共享电机, 目标速度跟随)";
            return "FlightDynamicsV2 (力驱动动力学积分)";
        }
    }

    /// <summary>本帧位移由谁写到 XR Origin 上 (调试/验证用)。</summary>
    public string ActivePositionApplier
    {
        get
        {
            if (ComparisonMotor != null && !forceDrivenDynamics && !useStrokeCruise)
                return "ComparisonMotionMotor.ApplyPendingMotion (LateUpdate)";
            return "XRLocomotionApplierV2.Apply";
        }
    }

    /// <summary>
    /// 控制侧头部稳定器 (由 FlightControlModeSwitcher 注入)。仅供参考 debug/gizmo
    /// 与身体中心口径一致; 为空时一切退回原始头显位置。
    /// </summary>
    [System.NonSerialized] public SwimFlight.HeadStabilizer HeadStabilizer;
    [Min(0.01f), Tooltip("Input force (N) mapped to full shared speed; mapping calibration only.")]
    public float forceForFullSpeed = 6.75f;
    [Min(0.01f), Tooltip("Input yaw torque (Nm) mapped to full shared turn speed.")]
    public float torqueForFullTurn = 1.75f;

    private XRInputSource _inputSource;
    private HandVelocityEstimator _velocityEstimator;
    private FlightCoordinateSystem _coordinateSystem;
    private V2Mapper _mapper;
    private FlightDynamicsV2 _dynamics;
    private XRLocomotionApplierV2 _locomotion;

    // === 状态 (由协调器维护) ===
    private V2State _state;
    private bool _started;        // 初始化是否已完成 (ResetState 用, 避免在初始化前调用)
    private bool _initialized;    // EnsureInitialized 的幂等标志

    // === Debug 计时器 ===
    private float _debugTimer;
    private float _spatialDebugTimer;
    private float _dampingDebugTimer;

    // === 诊断 ===
    private FlightDiagSnapshot _diag;
    private FlightDiagnosticMode _appliedMode;
    private bool _hasAppliedMode;

    /// <summary>最近一帧诊断快照 (供 FlightMotionDiagnostics 记录, 每帧填充, 无分配)。</summary>
    public FlightDiagSnapshot Diag => _diag;

    private void Start()
    {
        EnsureInitialized();
    }

    /// <summary>
    /// 幂等初始化。Start 与 Update 都会调用。
    ///
    /// 为什么不能只放在 Start: 各模块 (_mapper/_dynamics/_coordinateSystem/...) 都是普通 C# 类,
    /// 一旦 Update 先于 Start 跑到 (运行时被 FlightControlModeSwitcher 重新 enable、
    /// 或 Start 中途抛异常), ApplyParameters 每帧都会对空引用抛 NullReferenceException,
    /// 刷满 Console。把初始化收敛成一个可重入的方法后, 这个失败模式从根上消失。
    /// </summary>
    private bool EnsureInitialized()
    {
        if (_initialized) return true;

        // 自动查找引用
        if (xrOrigin == null)
        {
            var origin = FindObjectOfType<XROrigin>();
            if (origin != null) xrOrigin = origin.transform;
        }
        if (headTransform == null && Camera.main != null)
            headTransform = Camera.main.transform;

        if (xrOrigin == null)
        {
            // 还没法初始化; 不置 _initialized, 下一帧再试
            return false;
        }

        if (_inputSource == null)
        {
            _inputSource = new XRInputSource();
            _inputSource.headTransform = headTransform;
        }

        if (_velocityEstimator == null)
        {
            _velocityEstimator = new HandVelocityEstimator();
            _velocityEstimator.smoothingTimeConstant = velocitySmoothingTimeConstant;
        }

        if (_coordinateSystem == null)
            _coordinateSystem = new FlightCoordinateSystem();
        _coordinateSystem.xrOrigin = xrOrigin;
        _coordinateSystem.headTransform = headTransform;
        _coordinateSystem.reference = trackingSpaceReference;

        if (_mapper == null) _mapper = new V2Mapper();
        if (_dynamics == null) _dynamics = new FlightDynamicsV2();
        if (_locomotion == null) _locomotion = new XRLocomotionApplierV2(xrOrigin);

        ApplyParameters();

        // 初始状态对齐 XR Origin 当前位姿
        _state = _dynamics.Initialize(xrOrigin.position, xrOrigin.rotation);

        _initialized = true;
        _started = true;
        return true;
    }

    /// <summary>
    /// 重新把动力学状态对齐到当前 XR Origin 位姿 (速度归零), 并重置手部速度估计器。
    /// 供 FlightControlModeSwitcher 从遥感模式切回划水模式时调用,
    /// 避免位置跳变, 以及速度估计器因跨模式保留旧手位而造成的首帧速度尖峰。
    /// </summary>
    public void ResetState()
    {
        if (!_started) return;
        ReanchorForModeSwitch();
    }

    private void Update()
    {
        // 若 Start 尚未跑完/被跳过, 这里补一次初始化, 避免对空模块每帧抛 NullReferenceException
        if (!EnsureInitialized()) return;
        _diag.valid = false;

        if (_appliedCruiseMode != useStrokeCruise)
        {
            _appliedCruiseMode = useStrokeCruise;
            ReanchorForModeSwitch();
        }

        ApplyParameters();
        ApplyDiagnosticMode();

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // === 核心数据流 ===
        _inputSource.Tick(dt);

        XRDeviceSample device = _inputSource.Read();
        if (!ValidateCruiseTracking(device, dt)) return;
        UpdateVerticalPermissionInput(device);
        bool gripHeld = stationaryTurning && (device.leftGripPressed || device.rightGripPressed);
        if (gripHeld != _gripTurnHeld)
        {
            _state.velocity = Vector3.zero;
            _state.angularVelocity = Vector3.zero;
            _velocityEstimator.Reset();
            forceSmoother?.Reset();
            primaryDirectionAssist?.Reset();
            yawStabilizer?.Reset();
            jerkLimiter?.Reset();
            ComparisonMotor?.Reset();
            _cruise.Reset();
        }
        _gripTurnHeld = gripHeld;
        _dynamics.gripTurnControl = stationaryTurning;
        _dynamics.gripTurnHeld = gripHeld;
        XRRawInput raw = _velocityEstimator.Estimate(device, dt);

        FlightInput input = _coordinateSystem.ToWorld(raw, dt);

        FlightCommand command = _mapper.Map(input);
        if (ComparisonMotor != null && !forceDrivenDynamics && !useStrokeCruise)
        {
            primaryDirectionAssist?.Apply(ref command, dt);
            ApplyUserSpeed(ref command);
            Vector3 force = command.hasTranslationForce
                ? command.translationForce : command.leftForce + command.rightForce;
            Vector3 torque = Vector3.Cross(command.leftArm, command.leftForce)
                + Vector3.Cross(command.rightArm, command.rightForce);
            Vector3 desired = force / Mathf.Max(0.01f, forceForFullSpeed)
                * ComparisonMotor.Settings.maxSpeed;
            float yaw = torque.y * Mathf.Max(0f, yawTorqueGain) / Mathf.Max(0.01f, torqueForFullTurn)
                * ComparisonMotor.Settings.maxYawSpeed;
            if (stationaryTurning)
            {
                if (gripHeld) desired = Vector3.zero;
                else yaw = 0f;
            }
            ComparisonMotor.Step(desired, yaw, dt, verticalPermission, mass);
            return;
        }

        // 推进力平滑: 在门控/映射之后、积分之前就地滤波 (左右手独立, 不改变合力方向)。
        // 只作用于力驱动链路; 共享电机那条 (摇杆/前倾) 不受影响。
        if (forceSmoother != null)
            forceSmoother.Apply(ref command, _state.velocity, dt);

        // 原地转弯辅助: 在滤波之后、积分之前。只改左右手力的同向分量, 力矩不受影响。
        Vector3 handForceBeforeTurn = command.leftForce + command.rightForce;
        if (!stationaryTurning) turnInPlace?.Apply(ref command);
        else turnInPlace?.Reset();
        if (command.hasTranslationForce)
            command.translationForce += command.leftForce + command.rightForce - handForceBeforeTurn;
        _dynamics.stationaryTurn = false;

        if (useStrokeCruise)
        {
            bool brake = cruiseSettings.brakeButton == CruiseBrakeButton.RightStickClick ? device.rightStickClick
                : cruiseSettings.brakeButton == CruiseBrakeButton.LeftStickClick ? device.leftStickClick
                : device.leftSecondaryButton;
            bool verticalAllowed = verticalPermission == null || !verticalPermission.enabled || verticalPermission.Held;
            bool assistedTurn = !stationaryTurning && turnInPlace != null && turnInPlace.enabled
                && turnInPlace.LastTranslationScale < 0.99f;
            if (assistedTurn) _cruise.CancelStrokes();
            _cruise.Sample(_mapper.TangentialLeft, _mapper.TangentialRight,
                _mapper.LeftActive && !assistedTurn, _mapper.RightActive && !assistedTurn, xrOrigin.forward,
                verticalAllowed, brake, gripHeld, dt);
        }

        // Net translation only, after existing turn cancellation. Preserve all hand torque.
        if (!useStrokeCruise)
        {
            primaryDirectionAssist?.Apply(ref command, dt);
            ApplyUserSpeed(ref command);
        }

        // 固定上限的子步积分。
        // 阻尼是显式积分的, 稳定条件 (c1 + 2·c2·|v|)·dt/m < 2 —— dt 越大、mass 越小越危险。
        // 掉帧时直接用整帧 dt 积分会过冲发散成 NaN, 拆成 ≤maxTimeStep 的子步既保稳定又不丢时间。
        // 注意上面的手部速度估计仍用真实 dt (Δpos/dt), 不能钳, 否则掉帧时手速会被算大。
        Vector3 velocityBeforeIntegration = _state.velocity;
        float remaining = dt;
        int subSteps = 0;
        int stepLimit = Mathf.Max(1, maxSubSteps);
        float stepSize = Mathf.Max(1e-4f, maxTimeStep);
        while (remaining > 0f && subSteps < stepLimit)
        {
            float step = Mathf.Min(remaining, stepSize);
            _state = _dynamics.Integrate(_state, ref command, step, rotationOnly: useStrokeCruise);
            if (useStrokeCruise)
            {
                verticalPermission?.BeginStep(step);
                _cruise.Advance(step);
                _state.velocity = _cruise.Velocity;
                _state.position += _state.velocity * step;
            }
            remaining -= step;
            subSteps++;
        }

        // 按诊断模式决定是否施加人工平移 / 人工 yaw; yaw 绕稳定后的头部支点转。
        bool applyPosition = diagnosticMode != FlightDiagnosticMode.HeadTrackingOnly;
        bool applyYaw = diagnosticMode == FlightDiagnosticMode.Full;
        // 关闭 yawPivotUnderHead 时传 null → 退回绕 XR Origin 原点旋转 (旧行为), 供 A/B 对比
        Vector3? yawPivot = _gripTurnHeld && headTransform != null
            ? headTransform.position
            : yawPivotUnderHead
            ? FlightCoordinateSystem.GetControlHeadWorld(xrOrigin, headTransform, HeadStabilizer)
            : (Vector3?)null;

        // 直接量测本帧实际写到 XR Origin 上的运动 (含绕支点旋转带来的补偿位移), 用于诊断
        Vector3 originBefore = xrOrigin.position;
        float yawBefore = xrOrigin.rotation.eulerAngles.y;

        _locomotion.Apply(_state, applyPosition, applyYaw, yawPivot);
        // Remove only velocity into a contacted surface, so descent does not build up
        // invisible momentum while the head is blocked. Preserve tangential flight.
        var collisionGuard = HeadCollisionGuard.Find(xrOrigin);
        if (applyPosition && collisionGuard != null && collisionGuard.enabled
            && collisionGuard.LastCorrection.sqrMagnitude > 1e-10f)
        {
            Vector3 normal = collisionGuard.LastCorrection.normalized;
            float inward = Vector3.Dot(_state.velocity, normal);
            if (inward < 0f) _state.velocity -= normal * inward;
            if (useStrokeCruise)
            {
                _cruise.RemoveIntoSurface(normal);
                _state.velocity = _cruise.Velocity;
            }
        }

        Vector3 appliedTranslation = xrOrigin.position - originBefore;
        float appliedYawDelta = Mathf.DeltaAngle(yawBefore, xrOrigin.rotation.eulerAngles.y);

        FillDiagSnapshot(dt, appliedTranslation, appliedYawDelta, command);
        if (useStrokeCruise) _diag.acceleration = (_state.velocity - velocityBeforeIntegration) / dt;
        if (useStrokeCruise && cruiseSettings.enableDebugLog)
        {
            _cruiseDebugTimer -= dt;
            if (_cruiseDebugTimer <= 0f)
            {
                _cruiseDebugTimer = cruiseSettings.debugInterval;
                Debug.Log($"[StrokeCruise] L={_cruise.LeftPhase} R={_cruise.RightPhase} " +
                    $"events={_cruise.EventCount} lastDelta={_cruise.LastIncrement:F3} " +
                    $"target={_cruise.TargetVelocity:F3} actual={_cruise.Velocity:F3} " +
                    $"Trigger=({verticalPermission?.LeftHeld},{verticalPermission?.RightHeld}) " +
                    $"verticalAllowed={_cruise.VerticalAllowed} brake={_cruise.BrakeHeld}");
            }
        }

        // === 非对称平移阻尼 debug ===
        // 独立开关: FlightControlModeSwitcher 会把 enableDebugLog 强制置 false, 若复用那个
        // 开关则在实际场景里永远看不到输出。这里单独一个, 只打非对称阻尼的关键量。
        if (!useStrokeCruise && enableDampingDebugLog && asymmetricDamping != null)
        {
            _dampingDebugTimer -= dt;
            if (_dampingDebugTimer <= 0f)
            {
                _dampingDebugTimer = Mathf.Max(0.05f, debugLogInterval);

                Vector3 v = _state.velocity;
                Vector3 fInput = command.totalForce;
                float speed = v.magnitude;
                float forceMag = fInput.magnitude;
                string angle = asymmetricDamping.DirectionJudgementSkipped
                    ? "N/A(速度过小/无输入)"
                    : $"{Mathf.Acos(Mathf.Clamp(asymmetricDamping.Cosine, -1f, 1f)) * Mathf.Rad2Deg:F1}°";

                Debug.Log(
                    $"[AsymDamp] 速度来源={ActiveVelocitySource} | 位移应用={ActivePositionApplier} | " +
                    $"相位={AsymmetricTranslationDamping.PhaseLabel(asymmetricDamping.Phase)} | " +
                    $"|v|={speed:F3}m/s v=({v.x:F2},{v.y:F2},{v.z:F2}) | " +
                    $"|F_in|={forceMag:F3}N F=({fInput.x:F2},{fInput.y:F2},{fInput.z:F2}) | " +
                    $"夹角={angle} cosθ={asymmetricDamping.Cosine:F3} | " +
                    $"有效阻尼c1={asymmetricDamping.EffectiveDamping:F3}N/(m/s) (基础{linearDamping:F2}) | " +
                    $"制动强度={asymmetricDamping.BrakeRamp * asymmetricDamping.assistBrakeDamping:F3}N/(m/s) " +
                    $"制动减速度={asymmetricDamping.AssistBrakeDeceleration:F3}m/s² | " +
                    $"原因={asymmetricDamping.LastTransitionReason}");
            }
        }

        // === Debug 输出 (每帧或按间隔) ===
        if (enableDebugLog)
        {
            _debugTimer -= dt;
            if (_debugTimer <= 0f)
            {
                _debugTimer = debugLogInterval;
                Debug.Log(
                    $"[SFV2] estV=({raw.leftTrackingVelocity.magnitude:F2},{raw.rightTrackingVelocity.magnitude:F2}) " +
                    $"F=({command.leftForce.magnitude:F2},{command.rightForce.magnitude:F2}) " +
                    $"active=({_mapper.LeftSpatialGate.CurrentState},{_mapper.RightSpatialGate.CurrentState}) | " +
                    $"Ftot=({command.totalForce.x:F2},{command.totalForce.y:F2},{command.totalForce.z:F2}) " +
                    $"tau=({command.totalTorque.x:F2},{command.totalTorque.y:F2},{command.totalTorque.z:F2}) | " +
                    $"omega=({command.omegaLeft:F2},{command.omegaRight:F2}) " +
                    $"angVel=({_state.angularVelocity.x:F2},{_state.angularVelocity.y:F2},{_state.angularVelocity.z:F2})");
            }
        }

        // === Spatial Gate debug (每 0.3 秒) ===
        if (enableDebugLog)
        {
            _spatialDebugTimer -= dt;
            if (_spatialDebugTimer <= 0f)
            {
                _spatialDebugTimer = 0.3f;
                Vector3 bodyCenter = ComputeBodyCenterForDisplay();
                Debug.Log(
                    $"[SpatialGate] L dist={_mapper.DebugSpatialDistanceLeft:F2} state={_mapper.LeftSpatialGate.CurrentState} " +
                    $"R dist={_mapper.DebugSpatialDistanceRight:F2} state={_mapper.RightSpatialGate.CurrentState} | " +
                    $"body=({bodyCenter.x:F2},{bodyCenter.y:F2},{bodyCenter.z:F2})");
            }
        }
    }

    /// <summary>
    /// 诊断模式切换。切换时重锚位姿并清零速度/角速度, 避免切换瞬间跳变。
    /// </summary>
    private void ApplyDiagnosticMode()
    {
        if (_hasAppliedMode && _appliedMode == diagnosticMode) return;

        bool first = !_hasAppliedMode;
        _appliedMode = diagnosticMode;
        _hasAppliedMode = true;

        if (first) return;   // Start 已初始化过, 首次不进重锚

        if (logDiagnosticModeChanges)
        {
            Debug.Log($"[SFDiag] 诊断模式切换 → {diagnosticMode} | " +
                      $"人工平移={(diagnosticMode != FlightDiagnosticMode.HeadTrackingOnly)} " +
                      $"人工yaw={(diagnosticMode == FlightDiagnosticMode.Full)} | " +
                      "已重锚到 XR Origin 当前姿态并清零速度/角速度 (避免切换跳变)");
        }
        ReanchorForModeSwitch();
    }

    /// <summary>
    /// 把动力学状态对齐到 XR Origin 当前位姿, 清零速度/角速度与全部辅助模块状态。
    /// 任何模式切换与 ResetState 都走这里, 保证不出现位置跳变、也不带上一模式的残余速度。
    /// 副作用: SpatialActionGate 重置为 Inactive, 切换后手需要重新进入有效操作空间。
    /// </summary>
    private void ReanchorForModeSwitch()
    {
        if (xrOrigin == null) return;

        _state = _dynamics.Initialize(xrOrigin.position, xrOrigin.rotation);
        _locomotion.Reset(_state);
        _velocityEstimator.Reset();
        _mapper = new V2Mapper();

        // 清掉全部辅助模块的跨帧状态 (滞回/计时/滤波/上一帧加速度)
        asymmetricDamping?.Reset();
        forceSmoother?.Reset();
        turnInPlace?.Reset();
        primaryDirectionAssist?.Reset();
        verticalPermission?.Reset();
        _gripTurnHeld = false;
        _dynamics.gripTurnHeld = false;
        _dynamics.stationaryTurn = false;
        yawStabilizer?.Reset();
        jerkLimiter?.Reset();
        _cruise.Reset();

        // 共享电机可能残留待施加位移, 清零避免它和本控制器同时移动 XR Origin
        ComparisonMotor?.Reset();

        ApplyParameters();
    }

    /// <summary>填充本帧诊断快照 (无分配)。</summary>
    private void FillDiagSnapshot(float dt, Vector3 appliedTranslation, float appliedYawDelta,
        FlightCommand command)
    {
        Vector3 originPos = xrOrigin != null ? xrOrigin.position : Vector3.zero;
        float originYaw = xrOrigin != null ? xrOrigin.rotation.eulerAngles.y : 0f;

        _diag.strokeComparison = (int)strokeComparison;
        _diag.gateEnter = _mapper.LeftSpatialGate.enterDistance;
        _diag.gateExit = _mapper.LeftSpatialGate.exitDistance;
        _diag.radialRetention = _mapper.radialRetention;
        _diag.leftGateActive = _mapper.LeftActive ? 1f : 0f;
        _diag.rightGateActive = _mapper.RightActive ? 1f : 0f;
        _diag.leftGateSeconds = _mapper.LeftActive ? dt : 0f;
        _diag.rightGateSeconds = _mapper.RightActive ? dt : 0f;
        _diag.leftDistance = _mapper.DebugSpatialDistanceLeft;
        _diag.rightDistance = _mapper.DebugSpatialDistanceRight;
        _diag.gripTurnHeld = _gripTurnHeld ? 1f : 0f;
        _diag.rawLeft = _mapper.RawLeft; _diag.rawRight = _mapper.RawRight;
        _diag.tanLeft = _mapper.TangentialLeft; _diag.tanRight = _mapper.TangentialRight;
        _diag.effectiveLeft = _mapper.EffectiveLeft; _diag.effectiveRight = _mapper.EffectiveRight;
        _diag.valid = true;
        _diag.frameTime = dt;
        _diag.mode = (int)diagnosticMode;

        if (xrOrigin != null && headTransform != null)
        {
            _diag.hmdLocalPosition = xrOrigin.InverseTransformPoint(headTransform.position);
            _diag.hmdLocalEuler = (Quaternion.Inverse(xrOrigin.rotation)
                                   * headTransform.rotation).eulerAngles;
        }
        else
        {
            _diag.hmdLocalPosition = Vector3.zero;
            _diag.hmdLocalEuler = Vector3.zero;
        }

        _diag.originPosition = originPos;
        _diag.originYaw = originYaw;
        _diag.leftForceMagnitude = command.leftForce.magnitude;
        _diag.rightForceMagnitude = command.rightForce.magnitude;
        _diag.totalForce = command.totalForce;
        _diag.directionInputForce = primaryDirectionAssist != null
            ? primaryDirectionAssist.InputForce : command.leftForce + command.rightForce;
        _diag.directionOutputForce = command.hasTranslationForce
            ? command.translationForce : command.leftForce + command.rightForce;
        _diag.horizontalSuppression = primaryDirectionAssist != null ? primaryDirectionAssist.HorizontalSuppression : 0f;
        _diag.verticalSuppression = primaryDirectionAssist != null ? primaryDirectionAssist.VerticalSuppression : 0f;
        _diag.verticalPermissionEnabled = verticalPermission != null && verticalPermission.enabled ? 1f : 0f;
        _diag.verticalPermissionWeight = verticalPermission != null ? verticalPermission.Weight : 1f;
        _diag.verticalPermissionStepWeight = verticalPermission != null ? verticalPermission.StepWeight : 1f;
        _diag.verticalPermissionDamping = verticalPermission != null ? verticalPermission.StepDamping : 0f;
        _diag.leftTriggerHeld = verticalPermission != null && verticalPermission.LeftHeld ? 1f : 0f;
        _diag.rightTriggerHeld = verticalPermission != null && verticalPermission.RightHeld ? 1f : 0f;
        _diag.rawTorqueY = command.rawTotalTorque.y;
        _diag.torqueY = command.totalTorque.y;
        _diag.velocity = _state.velocity;
        _diag.speed = _state.velocity.magnitude;
        _diag.acceleration = _dynamics.LastAcceleration;
        _diag.yawRate = _state.angularVelocity.y;
        _diag.yawAcceleration = _dynamics.LastYawAcceleration;
        _diag.appliedTranslationDelta = appliedTranslation;
        _diag.appliedYawDelta = appliedYawDelta;
        _diag.turnOpposition = turnInPlace != null ? turnInPlace.LastOpposition : 0f;
        _diag.turnCoherence = turnInPlace != null ? turnInPlace.LastCoherence : 0f;
        _diag.turnTranslationScale = turnInPlace != null ? turnInPlace.LastTranslationScale : 1f;
    }

    /// <summary>
    /// 把 Inspector 参数同步到各模块 (Start 初始化 + Update 每帧同步, 支持 Play 模式实时调参)
    /// </summary>
    private void ApplyParameters()
    {
        // 兜底: 模块尚未建好时直接返回 (EnsureInitialized 会先建好再调用)。
        // 少这一道时, 任何 Update 早于 Start 的时序都会变成每帧刷屏的 NullReferenceException。
        if (_velocityEstimator == null || _coordinateSystem == null
            || _mapper == null || _dynamics == null)
            return;

        // 速度估计
        _velocityEstimator.smoothingTimeConstant = velocitySmoothingTimeConstant;

        // 身体中心
        _coordinateSystem.bodyCenterOffset = bodyCenterOffset;
        _coordinateSystem.headStabilizer = HeadStabilizer;

        // 空间门控 (Recovery 机制)
        float gateEnter = strokeComparison == StrokeDiagnosticComparison.GateOnly
            ? Mathf.Max(0f, diagnosticGateEnter) : spatialEnterDistance;
        float gateExit = strokeComparison == StrokeDiagnosticComparison.GateOnly
            ? Mathf.Clamp(diagnosticGateExit, 0f, gateEnter) : spatialExitDistance;
        _mapper.radialRetention = strokeComparison == StrokeDiagnosticComparison.RadialOnly
            ? Mathf.Clamp01(diagnosticRadialRetention) : 0f;
        _mapper.uniformTranslation = uniformTranslation;
        if (cruiseSettings == null) cruiseSettings = new StrokeCruiseSettings();
        cruiseSettings.Validate();
        _cruise.Settings = cruiseSettings;
        _mapper.LeftSpatialGate.enterDistance = gateEnter;
        _mapper.LeftSpatialGate.exitDistance = gateExit;
        _mapper.RightSpatialGate.enterDistance = gateEnter;
        _mapper.RightSpatialGate.exitDistance = gateExit;

        // 反作用力
        _mapper.ReactionForce.gain = reactionGain;

        // 动力学
        // 引用也每帧同步: xrOrigin/headTransform 允许在 Play 中被重新赋值
        _coordinateSystem.xrOrigin = xrOrigin;
        _coordinateSystem.headTransform = headTransform;
        _coordinateSystem.reference = trackingSpaceReference;
        _dynamics.comfortMode = comfortMode;
        _dynamics.maxAcceleration = Mathf.Max(0.1f, maxAcceleration);
        _dynamics.maxAngularAcceleration = Mathf.Max(0.1f, maxAngularAcceleration);
        _dynamics.mass = mass;
        _dynamics.linearDamping = linearDamping;
        _dynamics.quadraticDamping = quadraticDamping;
        _dynamics.rotationalInertia = rotationalInertia;
        _dynamics.rotationalDamping = rotationalDamping;
        _dynamics.yawTorqueGain = Mathf.Max(0f, yawTorqueGain);
        _dynamics.attitudeDamping = attitudeDamping;
        _dynamics.maxAngularVelocity = maxAngularVelocity;
        _dynamics.maxSpeed = maxSpeed;
        _dynamics.horizontalSpeedLimit = maxSpeed * userHorizontalMultiplier;
        _dynamics.verticalSpeedLimit = maxSpeed * (uniformTranslation
            ? userHorizontalMultiplier : userVerticalMultiplier);

        // 非对称平移阻尼: 把本组件的配置对象交给动力学层, 每子步由它给出有效 c1。
        // 传 null / 内部 enabled=false 都退化为改动前的恒定 linearDamping。
        _dynamics.asymmetricDamping = asymmetricDamping;
        _dynamics.verticalPermission = verticalPermission;
    }

    private void OnDisable()
    {
        _cruise.Reset();
        if (_initialized) ReanchorForModeSwitch();
        verticalPermission?.Reset();
        _diag.valid = false;
    }

    private void OnEnable()
    {
        if (_initialized) ReanchorForModeSwitch();
    }

    private void ApplyUserSpeed(ref FlightCommand command)
    {
        if (uniformTranslation)
        {
            Vector3 force = command.hasTranslationForce
                ? command.translationForce : command.leftForce + command.rightForce;
            command.translationForce = force * Mathf.Max(0f, translationPropulsionGain)
                * Mathf.Max(0f, userHorizontalMultiplier);
            command.hasTranslationForce = true;
            return;
        }
        ApplyHorizontalPropulsionGain(ref command, horizontalPropulsionGain * userHorizontalMultiplier);
        command.translationForce.y *= userVerticalMultiplier;
    }

    public static void ApplyHorizontalPropulsionGain(ref FlightCommand command, float gain)
    {
        Vector3 force = command.hasTranslationForce
            ? command.translationForce : command.leftForce + command.rightForce;
        float multiplier = Mathf.Max(0f, gain);
        command.translationForce = new Vector3(force.x * multiplier, force.y, force.z * multiplier);
        command.hasTranslationForce = true;
    }

    private void UpdateVerticalPermissionInput(XRDeviceSample device)
    {
        verticalPermission?.SetInput(device.leftTriggerAvailable && IsInputControllerActive(leftInputController),
            device.leftTrigger, device.rightTriggerAvailable && IsInputControllerActive(rightInputController),
            device.rightTrigger);
    }

    private bool ValidateCruiseTracking(XRDeviceSample device, float dt)
    {
        if (!useStrokeCruise) return true;
        if (device.headTracked && device.leftTracked && device.rightTracked
            && IsInputControllerActive(leftInputController) && IsInputControllerActive(rightInputController)
            && dt > 0f && dt <= 0.16f) return true;
        ReanchorForModeSwitch();
        return false;
    }

    private static bool IsInputControllerActive(Behaviour controller)
        => ReferenceEquals(controller, null) || (controller != null && controller.isActiveAndEnabled);

    /// <summary>
    /// debug/gizmo 用的身体中心: 与 ToWorld 走同一条「取控制用头部位置」的路,
    /// 保证三处口径一致。HeadStabilizer 为空时退回原始头显位置 (即旧行为);
    /// OnDrawGizmos 可能在 Start 之前跑, 故不从 _coordinateSystem 取。
    /// </summary>
    private Vector3 ComputeBodyCenterForDisplay()
    {
        return FlightCoordinateSystem.ComputeBodyCenter(xrOrigin,
            FlightCoordinateSystem.GetControlHeadWorld(xrOrigin, headTransform, HeadStabilizer),
            bodyCenterOffset);
    }

    private void OnDrawGizmos()
    {
        if (xrOrigin == null) return;
        Vector3 bodyCenter = ComputeBodyCenterForDisplay();
        Gizmos.color = Color.green;
        DrawCircleGizmo(bodyCenter, spatialEnterDistance);
        Gizmos.color = Color.red;
        DrawCircleGizmo(bodyCenter, spatialExitDistance);
    }

    private void DrawCircleGizmo(Vector3 center, float radius, int segments = 48)
    {
        Vector3 prev = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * (360f / segments) * Mathf.Deg2Rad;
            Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
}
