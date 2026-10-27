using UnityEngine;

namespace SwimFlight
{
    /// <summary>三种飞行控制方式。</summary>
    public enum FlightControlMode
    {
        /// <summary>划水(仿生): SwimFlightControllerV2</summary>
        Biomechanical,
        /// <summary>遥感(摇杆): JoystickFlightController</summary>
        Joystick,
        /// <summary>前倾加速辅助: IntentionalHeadMotionFlightController</summary>
        IntentionalHeadMotion,
        /// <summary>划手事件调节巡航目标速度；复用手臂控制器的原地转向和执行器。</summary>
        StrokeCruise
    }

    /// <summary>
    /// 飞行控制统一入口 —— 既切换模式, 又聚合调参 (一个组件搞定)。
    ///
    /// 切换 (供 UI 按钮调用):
    ///   SelectBiomechanical() / SelectJoystick() / SelectIntentionalHeadMotion() / Toggle()(循环下一模式)
    ///   切换只改三个控制器的 enabled (不销毁), 切回划水模式时 ResetState 对齐位姿。
    ///
    /// 调参:
    ///   本组件的 Inspector (FlightControlModeSwitcherEditor) 里分组显示三个控制器的
    ///   全部参数, 可直接调 (Play 模式实时生效)。
    ///
    /// 用法:
    ///   1. 挂到任意空物体, 三个控制器留空可点 Inspector 里的「自动查找」。
    ///   2. 游戏内三个按钮 OnClick 分别指向 SelectBiomechanical / SelectJoystick /
    ///      SelectIntentionalHeadMotion; 或用「生成控制模式切换 UI」一键创建。
    ///   3. 在 Inspector 下方分组里调各模式参数。
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class FlightControlModeSwitcher : MonoBehaviour
    {
        [Header("=== 三个控制器 ===")]
        [Tooltip("划水(仿生)控制器")]
        public SwimFlightControllerV2 biomechanicalController;

        [Tooltip("遥感(摇杆)控制器")]
        public JoystickFlightController joystickController;

        [Tooltip("前倾加速辅助控制器")]
        public IntentionalHeadMotionFlightController intentionalHeadMotionController;

        [Header("=== 初始模式 ===")]
        public FlightControlMode startMode = FlightControlMode.Biomechanical;

        [Header("Headset mode switching")]
        [Tooltip("Press the left controller X button to cycle flight modes.")]
        public bool enableControllerModeSwitch = true;
        public bool showHeadsetModePanel = true;
        private UnityEngine.XR.InputDevice _modeSwitchDevice;
        private bool _modeButtonReady;
        private bool _modeButtonWasPressed;

        [Header("Shared comparison conditions (all three modes)")]
        [Range(0.5f, 3f)] public float horizontalSpeedMultiplier = 1.5f;
        [Range(0.5f, 3f)] public float verticalSpeedMultiplier = 1f;

        public void AdjustFlightSpeed(bool vertical, float change)
        {
            if (ModeSelectionLocked || MotionPaused) return;
            if (CurrentMode == FlightControlMode.StrokeCruise && biomechanicalController != null)
            {
                var settings = biomechanicalController.cruiseSettings;
                if (vertical) settings.maximumVerticalSpeed = Mathf.Clamp(settings.maximumVerticalSpeed + change, 0.1f, 3f);
                else settings.maximumHorizontalSpeed = Mathf.Clamp(settings.maximumHorizontalSpeed + change, 0.1f, 5f);
                settings.maximumCombinedSpeed = Mathf.Max(settings.maximumCombinedSpeed,
                    Mathf.Max(settings.maximumHorizontalSpeed, settings.maximumVerticalSpeed));
                return;
            }
            if (biomechanicalController != null && biomechanicalController.uniformTranslation)
            {
                horizontalSpeedMultiplier = Mathf.Clamp(horizontalSpeedMultiplier + change, 0.5f, 3f);
                verticalSpeedMultiplier = horizontalSpeedMultiplier;
            }
            else if (vertical) verticalSpeedMultiplier = Mathf.Clamp(verticalSpeedMultiplier + change, 0.5f, 3f);
            else horizontalSpeedMultiplier = Mathf.Clamp(horizontalSpeedMultiplier + change, 0.5f, 3f);
            ApplyComparisonSettings();
        }

        public ComparisonMotionSettings comparison = new ComparisonMotionSettings();
        private readonly ComparisonMotionMotor _motor = new ComparisonMotionMotor();

        [Header("Head stabilization (control-side signal only)")]
        [Tooltip("清洗「头部位置 → 控制量」这条路: 身体中心 / 前倾位移 / 偏航支点。" +
                 "只读头部, 永不修改相机显示位姿。")]
        public HeadStabilizerSettings headStabilization = new HeadStabilizerSettings();
        private readonly HeadStabilizer _headStabilizer = new HeadStabilizer();
        private int _stabilizerFrame = -1;
        private bool _selected;
        public bool ModeSelectionLocked { get; private set; }
        public bool MotionPaused { get; private set; }

        public void SetExperimentLock(bool locked) => ModeSelectionLocked = locked;

        public void SetMotionPaused(bool paused)
        {
            MotionPaused = paused;
            _motor.Reset();
            _headStabilizer.Reset();
            biomechanicalController?.ResetState();
            if (biomechanicalController != null) biomechanicalController.enabled = !paused && IsArmMode(CurrentMode);
            if (joystickController != null) joystickController.enabled = !paused && CurrentMode == FlightControlMode.Joystick;
            if (intentionalHeadMotionController != null) intentionalHeadMotionController.enabled = !paused && CurrentMode == FlightControlMode.IntentionalHeadMotion;
        }
        private Vector3 _trialStartPosition;
        private Quaternion _trialStartRotation;
        private bool _hasTrialStart;
        [Header("Shared XR image quality")]
        [Range(1f, 1.5f)] public float xrResolutionScale = 1f;
        private float _renderSettingsTimer;
        private float _frameSampleTime;
        private int _frameSampleCount;
        public float ObservedRenderFps { get; private set; }
        public string RenderingStatus =>
            $"XR eye buffer: {UnityEngine.XR.XRSettings.eyeTextureWidth} x {UnityEngine.XR.XRSettings.eyeTextureHeight}\n" +
            $"Unity average: {ObservedRenderFps:F1} FPS; target: {Application.targetFrameRate} FPS\n" +
            $"XR scale: {UnityEngine.XR.XRSettings.eyeTextureResolutionScale:F2}; MSAA: {QualitySettings.antiAliasing}x\n" +
            "These are Unity-side values, not the stream encoder resolution or headset-delivered FPS.";
        private readonly System.Collections.Generic.List<UnityEngine.XR.XRDisplaySubsystem> _displays
            = new System.Collections.Generic.List<UnityEngine.XR.XRDisplaySubsystem>();

        private void LateUpdate()
        {
            // 手臂条件在力驱动模式下自己积分并写 XR Origin 位移 (XRLocomotionApplierV2)。
            // 此时共享电机若照常 ApplyPendingMotion, 会变成两条链路同时移动同一个 Transform;
            // 而 Step 已不再被调用, pending 本来就是零, 让它路只是在模式切换那一帧兜底。
            // 摇杆与前倾条件不受影响, 继续走共享电机。
            if (IsArmMode(CurrentMode)
                && biomechanicalController != null
                && biomechanicalController.enabled
                && biomechanicalController.UsesForceDrivenDynamics)
                return;

            _motor.ApplyPendingMotion();
        }

        private void ApplyRenderSettings()
        {
            QualitySettings.antiAliasing = 4;
            QualitySettings.globalTextureMipmapLimit = 0;
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            float resolutionScale = Mathf.Clamp(xrResolutionScale, 1f, 1.5f);
            if (!Mathf.Approximately(UnityEngine.XR.XRSettings.eyeTextureResolutionScale, resolutionScale))
                UnityEngine.XR.XRSettings.eyeTextureResolutionScale = resolutionScale;
            if (!Mathf.Approximately(UnityEngine.XR.XRSettings.renderViewportScale, 1f))
                UnityEngine.XR.XRSettings.renderViewportScale = 1f;
            if (_motor.Head != null)
            {
                var camera = _motor.Head.GetComponent<Camera>();
                if (camera != null)
                {
                    camera.allowMSAA = true;
                    camera.allowDynamicResolution = false;
                }
            }
            SubsystemManager.GetInstances(_displays);
            foreach (var display in _displays)
                if (display.running && display.TryGetDisplayRefreshRate(out float refreshRate) && refreshRate > 0f)
                    Application.targetFrameRate = Mathf.RoundToInt(refreshRate);
        }

        /// <summary>
        /// 用合成信号自检头部稳定器 (无需头显)。结果打到 Console。
        /// </summary>
        [ContextMenu("运行头部稳定器自检")]
        public void RunHeadStabilizerSelfTest()
        {
            Debug.Log(HeadStabilizer.SelfTest());
        }

        [ContextMenu("Restart current comparison trial")]
        public void RestartComparisonTrial()
        {
            if (ModeSelectionLocked) return;
            if (!_hasTrialStart || _motor.Origin == null) return;
            var mode = CurrentMode;
            if (biomechanicalController != null) biomechanicalController.enabled = false;
            if (joystickController != null) joystickController.enabled = false;
            if (intentionalHeadMotionController != null) intentionalHeadMotionController.enabled = false;
            _motor.Origin.SetPositionAndRotation(_trialStartPosition, _trialStartRotation);
            _selected = false;
            SelectMode(mode);
            foreach (var task in FindObjectsOfType<Experiment.WaypointFlightTask>())
                task.ResetProgress();
        }

        private void Update()
        {
            PollModeSwitchButton();
            ApplyComparisonSettings();
            _frameSampleCount++;
            _frameSampleTime += Time.unscaledDeltaTime;
            if (_frameSampleTime >= 2f)
            {
                ObservedRenderFps = _frameSampleCount / _frameSampleTime;
                _frameSampleCount = 0;
                _frameSampleTime = 0f;
            }
            _renderSettingsTimer -= Time.unscaledDeltaTime;
            if (_renderSettingsTimer <= 0f)
            {
                _renderSettingsTimer = 2f;
                ApplyRenderSettings();
            }
        }

        private void PollModeSwitchButton()
        {
            if (!enableControllerModeSwitch)
            {
                _modeButtonReady = false;
                return;
            }
            if (!_modeSwitchDevice.isValid)
            {
                _modeSwitchDevice = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
                _modeButtonReady = false;
            }
            if (!_modeSwitchDevice.isValid || !_modeSwitchDevice.TryGetFeatureValue(
                    UnityEngine.XR.CommonUsages.primaryButton, out bool pressed))
            {
                _modeButtonReady = false;
                return;
            }
            // A controller connected while held must be released before it can switch modes.
            if (_modeButtonReady && pressed && !_modeButtonWasPressed) Toggle();
            _modeButtonWasPressed = pressed;
            _modeButtonReady = true;
        }

        private void ApplyComparisonSettings()
        {
            if (comparison == null) comparison = new ComparisonMotionSettings();
            comparison.Validate();
            Transform origin = biomechanicalController != null ? biomechanicalController.xrOrigin : null;
            Transform head = biomechanicalController != null ? biomechanicalController.headTransform : null;
            if (origin == null && joystickController != null) origin = joystickController.xrOrigin;
            if (head == null && joystickController != null) head = joystickController.headTransform;
            if (origin == null && intentionalHeadMotionController != null) origin = intentionalHeadMotionController.xrOrigin;
            if (head == null && intentionalHeadMotionController != null) head = intentionalHeadMotionController.headTransform;
            if (origin == null)
            {
                var rig = FindObjectOfType<Unity.XR.CoreUtils.XROrigin>();
                if (rig != null) origin = rig.transform;
            }
            if (head == null && Camera.main != null) head = Camera.main.transform;
            _motor.Settings = comparison;
            _motor.Origin = origin;
            _motor.Head = head;

            // 头部稳定器: 每帧 tick 一次, 三条消费路径共享同一份采样 (口径一致)。
            // 用 frameCount 守卫, 避免 SelectMode 在同一帧重复推进。
            if (headStabilization == null) headStabilization = new HeadStabilizerSettings();
            headStabilization.Validate();
            _headStabilizer.Settings = headStabilization;
            _motor.HeadStabilizer = _headStabilizer;
            if (Time.frameCount != _stabilizerFrame)
            {
                _stabilizerFrame = Time.frameCount;
                _headStabilizer.Tick(origin, head, Time.deltaTime);
            }

            if (biomechanicalController != null)
            {
                biomechanicalController.maxSpeed = comparison.maxSpeed;
                biomechanicalController.userHorizontalMultiplier = horizontalSpeedMultiplier;
                biomechanicalController.userVerticalMultiplier = verticalSpeedMultiplier;
                biomechanicalController.maxAcceleration = comparison.acceleration;
                biomechanicalController.maxAngularVelocity = comparison.maxYawSpeed * Mathf.Deg2Rad;
                biomechanicalController.maxAngularAcceleration = comparison.yawAcceleration * Mathf.Deg2Rad;
                biomechanicalController.HeadStabilizer = _headStabilizer;
                biomechanicalController.ComparisonMotor = _motor;
                biomechanicalController.xrOrigin = origin;
                biomechanicalController.headTransform = head;
                biomechanicalController.enableDebugLog = false;
            }
            if (joystickController != null)
            {
                joystickController.ComparisonMotor = _motor;
                joystickController.xrOrigin = origin;
                joystickController.headTransform = head;
                joystickController.moveSpeed = comparison.maxSpeed;
                joystickController.verticalSpeed = comparison.maxSpeed;
                joystickController.turnSpeed = comparison.maxYawSpeed;
                joystickController.deadzone = comparison.stickDeadzone;
                joystickController.enableTurn = comparison.enableTurn;
                joystickController.enableVertical = comparison.enableVertical;
            }
            if (intentionalHeadMotionController != null)
            {
                intentionalHeadMotionController.ComparisonMotor = _motor;
                intentionalHeadMotionController.HeadStabilizer = _headStabilizer;
                intentionalHeadMotionController.xrOrigin = origin;
                intentionalHeadMotionController.headTransform = head;
                intentionalHeadMotionController.maxForwardSpeed = comparison.maxSpeed;
                intentionalHeadMotionController.maxBackwardSpeed = comparison.maxSpeed;
                intentionalHeadMotionController.lateralSpeed = comparison.maxSpeed;
                intentionalHeadMotionController.verticalSpeed = comparison.maxSpeed;
                intentionalHeadMotionController.maxAcceleration = comparison.acceleration;
                intentionalHeadMotionController.brakingAcceleration = comparison.acceleration;
                intentionalHeadMotionController.turnSpeed = comparison.maxYawSpeed;
                intentionalHeadMotionController.deadzone = comparison.stickDeadzone;
                intentionalHeadMotionController.enableTurn = comparison.enableTurn;
                intentionalHeadMotionController.enableVertical = comparison.enableVertical;
            }
        }

        private void OnValidate()
        {
            if (comparison == null) comparison = new ComparisonMotionSettings();
            comparison.Validate();
            if (headStabilization == null) headStabilization = new HeadStabilizerSettings();
            headStabilization.Validate();
            xrResolutionScale = Mathf.Clamp(xrResolutionScale, 1f, 1.5f);
        }

        /// <summary>当前模式。</summary>
        public FlightControlMode CurrentMode { get; private set; }

        /// <summary>只读: 供诊断脚本读取稳定后的头部信号 (不修改任何状态)。</summary>
        public HeadStabilizer Stabilizer => _headStabilizer;

        /// <summary>只读: 稳定器实际使用的 origin / head, 保证诊断与运行口径一致。</summary>
        public Transform StabilizerOrigin => _motor != null ? _motor.Origin : null;

        /// <inheritdoc cref="StabilizerOrigin"/>
        public Transform StabilizerHead => _motor != null ? _motor.Head : null;

        private void Start()
        {
            SelectMode(startMode);
            if (showHeadsetModePanel)
                gameObject.AddComponent<FlightModeHeadsetPanel>().Initialize(this);
            if (_motor.Origin != null)
            {
                _trialStartPosition = _motor.Origin.position;
                _trialStartRotation = _motor.Origin.rotation;
                _hasTrialStart = true;
            }
        }

        /// <summary>切换到划水模式 (供 UI 按钮)。</summary>
        public void SelectBiomechanical() => SelectMode(FlightControlMode.Biomechanical);

        /// <summary>切换到遥感模式 (供 UI 按钮)。</summary>
        public void SelectJoystick() => SelectMode(FlightControlMode.Joystick);

        /// <summary>切换到前倾加速辅助模式 (供 UI 按钮)。</summary>
        public void SelectIntentionalHeadMotion() => SelectMode(FlightControlMode.IntentionalHeadMotion);

        public void SelectStrokeCruise() => SelectMode(FlightControlMode.StrokeCruise);
        public static bool IsArmMode(FlightControlMode mode)
            => mode == FlightControlMode.Biomechanical || mode == FlightControlMode.StrokeCruise;

        /// <summary>循环切换到下一模式: 划水 → 遥感 → 前倾加速 → 划水… (供 UI 按钮)。</summary>
        public void Toggle() => SelectMode((FlightControlMode)(((int)CurrentMode + 1) % 4));

        /// <summary>切到指定模式 (启用所选、禁用其余; 切回划水时 ResetState)。</summary>
        public void SelectMode(FlightControlMode mode)
        {
            if (ModeSelectionLocked || MotionPaused) return;
            AutoFindControllers();
            if (intentionalHeadMotionController == null)
            {
                var host = joystickController != null ? joystickController.gameObject : gameObject;
                intentionalHeadMotionController = host.AddComponent<IntentionalHeadMotionFlightController>();
                intentionalHeadMotionController.enabled = false;
                if (joystickController != null)
                {
                    intentionalHeadMotionController.xrOrigin = joystickController.xrOrigin;
                    intentionalHeadMotionController.headTransform = joystickController.headTransform;
                }
            }
            ApplyComparisonSettings();
            if (_selected && CurrentMode == mode) return;
            _motor.Reset();
            _headStabilizer.Reset();
            foreach (var task in FindObjectsOfType<Experiment.WaypointFlightTask>())
            {
                // Lean changes physical head position, not virtual flight progress.
                task.useHeadForArrival = false;
                task.arrivalReference = _motor.Origin;
            }
            foreach (var analyzer in FindObjectsOfType<OpticFlow.OpticFlowAnalyzer>())
                analyzer.ResetHistory();
            foreach (var feedback in FindObjectsOfType<OpticFlow.VisualFeedbackController>())
                feedback.ResetFeedback();
            foreach (var mask in FindObjectsOfType<OpticFlow.PeripheralMaskController>())
                mask.ResetFeedback();
            CurrentMode = mode;
            _selected = true;

            if (biomechanicalController != null)
            {
                bool active = IsArmMode(mode);
                biomechanicalController.useStrokeCruise = mode == FlightControlMode.StrokeCruise;
                biomechanicalController.enabled = active;
                if (active) biomechanicalController.ResetState();
            }
            if (joystickController != null)
                joystickController.enabled = mode == FlightControlMode.Joystick;
            if (intentionalHeadMotionController != null)
                intentionalHeadMotionController.enabled = mode == FlightControlMode.IntentionalHeadMotion;
        }

        /// <summary>自动查找三个控制器 (只填空缺的引用, 不覆盖已拖入的)。</summary>
        [ContextMenu("自动查找三个控制器")]
        public void AutoFindControllers()
        {
            if (biomechanicalController == null)
                biomechanicalController = FindObjectOfType<SwimFlightControllerV2>(true);
            if (joystickController == null)
                joystickController = FindObjectOfType<JoystickFlightController>(true);
            if (intentionalHeadMotionController == null)
                intentionalHeadMotionController = FindObjectOfType<IntentionalHeadMotionFlightController>(true);
        }
    }

    [System.Serializable]
    public class ComparisonMotionSettings
    {
        [Min(0.1f), Tooltip("Total 3D speed cap; includes diagonal motion, all axes and both directions (m/s).")]
        public float maxSpeed = 3f;
        [Min(0.1f), Tooltip("Common acceleration, braking and direction-change limit (m/s squared).")]
        public float acceleration = 2f;
        [Min(0.1f), Tooltip("Yaw speed limit (degrees/s).")]
        public float maxYawSpeed = 20f;
        [Min(0.1f), Tooltip("Yaw acceleration and braking (degrees/s squared).")]
        public float yawAcceleration = 40f;
        [Range(0f, 0.5f)] public float stickDeadzone = 0.15f;
        public bool enableVertical = true;
        public bool enableTurn = true;

        public void Validate()
        {
            maxSpeed = Mathf.Max(0.1f, maxSpeed);
            acceleration = Mathf.Max(0.1f, acceleration);
            maxYawSpeed = Mathf.Max(0.1f, maxYawSpeed);
            yawAcceleration = Mathf.Max(0.1f, yawAcceleration);
            stickDeadzone = Mathf.Clamp(stickDeadzone, 0f, 0.5f);
        }
    }

    /// <summary>One actuator shared by all input mappings. Only the selected controller calls Step.</summary>
    public class ComparisonMotionMotor
    {
        public ComparisonMotionSettings Settings;
        public Transform Origin, Head;

        /// <summary>
        /// 控制侧头部稳定器 (由 FlightControlModeSwitcher 注入)。非空时偏航支点每帧
        /// 取自稳定后的头部位置; 为 null 时退回「Reset 时抓一次并冻结」的旧行为。
        /// </summary>
        public HeadStabilizer HeadStabilizer;

        public Vector3 Velocity { get; private set; }
        public float YawRate { get; private set; }
        private UnityEngine.XR.InputDevice _headDevice;
        private Vector3 _pendingTranslation;
        private float _pendingYaw;
        private Vector3 _rotationPivotLocal;
        private bool _hasRotationPivot;

        public void ApplyPendingMotion()
        {
            if (Origin == null) return;
            var collisionGuard = SwimFlight.Locomotion.HeadCollisionGuard.Find(Origin);
            if (collisionGuard != null)
            {
                collisionGuard.Move(_pendingTranslation);
                if (collisionGuard.enabled && collisionGuard.LastCorrection.sqrMagnitude > 1e-10f)
                {
                    Vector3 normal = collisionGuard.LastCorrection.normalized;
                    float inward = Vector3.Dot(Velocity, normal);
                    if (inward < 0f) Velocity -= normal * inward;
                }
            }
            else Origin.position += _pendingTranslation;
            if (Mathf.Abs(_pendingYaw) > 1e-6f)
            {
                Vector3 pivot;
                if (HeadStabilizer != null && Head != null)
                {
                    // 支点每帧取自稳定后的头部。
                    // 抓一次并冻结: 人一旦挪动/前倾, 头就绕着旧点画弧 → 转身时画面抖。
                    // 直接用原始头部: 原始位置每帧带噪声, 噪声直接灌进支点。
                    // 稳定值同时满足两者: 跟着人走, 但不抖。
                    // (支点是 origin 局部的, 上面的平移不影响它; 转身时头部局部偏移
                    //  本就不变, 所以这个闭环不会反过来推动稳定器基线。)
                    pivot = Origin.TransformPoint(
                        HeadStabilizer.GetStabilizedLocalPosition(Origin, Head));
                }
                else
                {
                    if (!_hasRotationPivot && Head != null) CaptureRotationPivot();
                    pivot = _hasRotationPivot
                        ? Origin.TransformPoint(_rotationPivotLocal) : Origin.position;
                }
                Origin.RotateAround(pivot, Vector3.up, _pendingYaw);
            }
            _pendingTranslation = Vector3.zero;
            _pendingYaw = 0f;
        }
        public Vector3 Forward => Origin != null
            ? Vector3.ProjectOnPlane(Origin.forward, Vector3.up).normalized : Vector3.forward;
        public Vector3 Right => Vector3.Cross(Vector3.up, Forward);
        public static float SimulationDelta(float dt) => Mathf.Clamp(dt, 0f, 0.16f);

        public void Reset()
        {
            Velocity = Vector3.zero;
            YawRate = 0f;
            _pendingTranslation = Vector3.zero;
            _pendingYaw = 0f;
            _hasRotationPivot = Origin != null && Head != null;
            if (_hasRotationPivot) CaptureRotationPivot();
        }

        private void CaptureRotationPivot()
        {
            _rotationPivotLocal = Origin.InverseTransformPoint(Head.position);
            _hasRotationPivot = true;
        }

        // Pure motion update, shared by every mode and usable without a headset in validation.
        public void AdvanceMotion(Vector3 desiredVelocity, float desiredYawRate, float dt,
            SwimFlight.Dynamics.VerticalFlightPermission verticalPermission = null, float mass = 1f)
        {
            if (!Settings.enableVertical) desiredVelocity.y = 0f;
            desiredVelocity = Vector3.ClampMagnitude(desiredVelocity, Settings.maxSpeed);
            desiredYawRate = Settings.enableTurn
                ? Mathf.Clamp(desiredYawRate, -Settings.maxYawSpeed, Settings.maxYawSpeed) : 0f;
            Vector3 previousVelocity = Velocity;
            Vector3 unrestrictedTarget = desiredVelocity;
            Velocity = Vector3.MoveTowards(Velocity, desiredVelocity, Settings.acceleration * dt);
            Velocity = Vector3.ClampMagnitude(Velocity, Settings.maxSpeed);
            verticalPermission?.BeginStep(dt);
            if (verticalPermission != null && verticalPermission.enabled)
            {
                float distance = (unrestrictedTarget - previousVelocity).magnitude;
                float fraction = distance > 1e-6f ? Mathf.Min(1f, Settings.acceleration * dt / distance) : 1f;
                float permittedY = previousVelocity.y +
                    (unrestrictedTarget.y * verticalPermission.StepWeight - previousVelocity.y) * fraction;
                float horizontalChange = new Vector2(Velocity.x - previousVelocity.x,
                    Velocity.z - previousVelocity.z).magnitude;
                float verticalChangeBudget = Mathf.Sqrt(Mathf.Max(0f,
                    Settings.acceleration * Settings.acceleration * dt * dt - horizontalChange * horizontalChange));
                float nextY = verticalPermission.DampVertical(permittedY, mass, dt);
                nextY = Mathf.Clamp(nextY, previousVelocity.y - verticalChangeBudget,
                    previousVelocity.y + verticalChangeBudget);
                float verticalSpeedBudget = Mathf.Sqrt(Mathf.Max(0f, Settings.maxSpeed * Settings.maxSpeed
                    - Velocity.x * Velocity.x - Velocity.z * Velocity.z));
                Velocity = new Vector3(Velocity.x, Mathf.Clamp(nextY, -verticalSpeedBudget, verticalSpeedBudget), Velocity.z);
            }
            YawRate = Mathf.MoveTowards(YawRate, desiredYawRate, Settings.yawAcceleration * dt);
            YawRate = Mathf.Clamp(YawRate, -Settings.maxYawSpeed, Settings.maxYawSpeed);
        }

        public void Step(Vector3 desiredVelocity, float desiredYawRate, float dt,
            SwimFlight.Dynamics.VerticalFlightPermission verticalPermission = null, float mass = 1f)
        {
            if (Origin == null || Head == null || Settings == null) return;
            if (!_headDevice.isValid)
            {
                _headDevice = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);
                if (!_headDevice.isValid)
                    _headDevice = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.CenterEye);
            }
            if (!_headDevice.isValid || !_headDevice.TryGetFeatureValue(
                UnityEngine.XR.CommonUsages.isTracked, out bool tracked) || !tracked)
            {
                Reset();
                return;
            }
            if (!Settings.enableVertical) desiredVelocity.y = 0f;
            desiredVelocity = Vector3.ClampMagnitude(desiredVelocity, Settings.maxSpeed);
            desiredYawRate = Settings.enableTurn
                ? Mathf.Clamp(desiredYawRate, -Settings.maxYawSpeed, Settings.maxYawSpeed) : 0f;
            float remaining = SimulationDelta(dt);
            while (remaining > 1e-6f)
            {
                float step = Mathf.Min(0.02f, remaining);
                AdvanceMotion(desiredVelocity, desiredYawRate, step, verticalPermission, mass);
                _pendingTranslation += Velocity * step;
                _pendingYaw += YawRate * step;
                remaining -= step;
            }
        }
    }

}
