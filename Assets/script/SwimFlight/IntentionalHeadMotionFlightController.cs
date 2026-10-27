using UnityEngine;
using UnityEngine.XR;
using Unity.XR.CoreUtils;

namespace SwimFlight
{
    /// <summary>
    /// Lean-assisted joystick acceleration. HMD displacement is a torso-lean proxy,
    /// not a measured body angle. Mapping is provisional pending the target paper.
    /// Class identity is retained for existing scene and UI references.
    /// </summary>
    public class IntentionalHeadMotionFlightController : MonoBehaviour
    {
        public Transform xrOrigin;
        public Transform headTransform;
        [Header("Lean-assisted acceleration")]
        [Tooltip("Forward HMD displacement ignored as natural sway (metres).")]
        public float leanDeadzone = 0.025f;
        [Tooltip("Forward displacement producing full acceleration (metres).")]
        public float fullLeanDistance = 0.18f;
        public float maxAcceleration = 2f;
        public float maxForwardSpeed = 5f;
        public float maxBackwardSpeed = 2.5f;
        [Tooltip("Deceleration when upright or stick released (m/s squared).")]
        public float brakingAcceleration = 3f;
        public float leanSmoothingTime = 0.08f;
        [Header("Joystick")]
        [Range(0f, 0.5f)] public float deadzone = 0.15f;
        public float lateralSpeed = 1.5f;
        public float verticalSpeed = 1.5f;
        public float turnSpeed = 20f;
        public bool enableVertical = true;
        public bool enableTurn = true;

        public float ForwardSpeed => _forwardSpeed;
        public float LeanAmount => _leanAmount;
        public float ForwardAcceleration { get; private set; }

        [System.NonSerialized] public ComparisonMotionMotor ComparisonMotor;

        /// <summary>
        /// 控制侧头部稳定器 (由 FlightControlModeSwitcher 注入)。
        /// 非空时前倾位移取稳定值, 非自主晃动不再直接调制加速度。为 null 时用原始头显位置。
        /// </summary>
        [System.NonSerialized] public HeadStabilizer HeadStabilizer;
        private InputDevice _leftDevice, _rightDevice, _headDevice;
        private Vector3 _neutralPosition, _neutralForward;
        private bool _calibrated, _previousCalibrateButton;
        private float _forwardSpeed, _leanAmount, _requeryTimer;
        private Vector3 _auxiliaryVelocity;

        private void OnEnable()
        {
            ResetMotion();
            _calibrated = false;
            _previousCalibrateButton = false;
        }

        private void OnDisable()
        {
            ResetMotion();
            _calibrated = false;
        }

        private void ResetMotion()
        {
            _forwardSpeed = _leanAmount = 0f;
            ForwardAcceleration = 0f;
            _auxiliaryVelocity = Vector3.zero;
        }

        [ContextMenu("Calibrate upright posture")]
        public void CalibrateNeutralPose()
        {
            ResolveReferences();
            if (xrOrigin == null || headTransform == null) return;
            // 中立位取稳定后的位置, 避免把某个晃动瞬间当成基准
            _neutralPosition = GetControlLocalPosition();
            _neutralForward = xrOrigin.InverseTransformDirection(headTransform.forward);
            _neutralForward.y = 0f;
            _neutralForward = _neutralForward.sqrMagnitude > 1e-6f
                ? _neutralForward.normalized : Vector3.forward;
            ResetMotion();
            if (ComparisonMotor != null) ComparisonMotor.Reset();
            _calibrated = true;
        }

        private void Update()
        {
            ResolveReferences();
            if (xrOrigin == null || headTransform == null || Time.deltaTime <= 0f) return;
            float dt = ComparisonMotor != null ? ComparisonMotionMotor.SimulationDelta(Time.deltaTime)
                : Mathf.Min(Time.deltaTime, 0.05f);
            TickDevices(dt);
            // Do not capture a stale/default headset pose before tracking starts.
            if (!_headDevice.isValid ||
                !_headDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || !tracked)
            {
                ResetMotion();
                if (ComparisonMotor != null) ComparisonMotor.Reset();
                _calibrated = false;
                return;
            }
            bool calibrateButton = _rightDevice.isValid &&
                _rightDevice.TryGetFeatureValue(CommonUsages.secondaryButton, out bool pressed) && pressed;
            if (!_calibrated || (calibrateButton && !_previousCalibrateButton))
                CalibrateNeutralPose();
            _previousCalibrateButton = calibrateButton;

            Vector2 left = ReadStick(_leftDevice);
            Vector2 right = ReadStick(_rightDevice);
            Vector3 localPosition = GetControlLocalPosition();
            float displacement = Vector3.Dot(localPosition - _neutralPosition, _neutralForward);
            float targetLean = Mathf.Clamp01((displacement - leanDeadzone) /
                Mathf.Max(0.001f, fullLeanDistance - leanDeadzone));
            float alpha = 1f - Mathf.Exp(-dt / Mathf.Max(0.001f, leanSmoothingTime));
            _leanAmount = Mathf.Lerp(_leanAmount, targetLean, alpha);
            if (ComparisonMotor != null)
            {
                Vector3 sharedForward = ComparisonMotor.Forward;
                float actualSpeed = Vector3.Dot(ComparisonMotor.Velocity, sharedForward);
                float desiredSpeed = 0f;
                if (Mathf.Abs(left.y) > 0f && targetLean > 0f && actualSpeed * left.y >= 0f)
                    desiredSpeed = actualSpeed + left.y * _leanAmount * maxAcceleration * dt;
                Vector3 desired = sharedForward * desiredSpeed
                    + ComparisonMotor.Right * (left.x * lateralSpeed)
                    + Vector3.up * (enableVertical ? right.y * verticalSpeed : 0f);
                ComparisonMotor.Step(desired, enableTurn ? right.x * turnSpeed : 0f, dt);
                _forwardSpeed = Vector3.Dot(ComparisonMotor.Velocity, sharedForward);
                ForwardAcceleration = (_forwardSpeed - actualSpeed) / Mathf.Max(dt, 0.0001f);
                return;
            }
            float previousSpeed = _forwardSpeed;
            if (Mathf.Abs(left.y) > 0f && targetLean > 0f)
            {
                float acceleration = left.y * _leanAmount * Mathf.Max(0f, maxAcceleration);
                // Brake before reversing, rather than instantly changing velocity sign.
                if (_forwardSpeed * left.y < 0f)
                    _forwardSpeed = Mathf.MoveTowards(_forwardSpeed, 0f,
                        Mathf.Max(0f, brakingAcceleration) * dt);
                else
                    _forwardSpeed += acceleration * dt;
            }
            else
                _forwardSpeed = Mathf.MoveTowards(_forwardSpeed, 0f,
                    Mathf.Max(0f, brakingAcceleration) * dt);
            _forwardSpeed = Mathf.Clamp(_forwardSpeed, -Mathf.Max(0f, maxBackwardSpeed),
                Mathf.Max(0f, maxForwardSpeed));
            ForwardAcceleration = (_forwardSpeed - previousSpeed) / dt;

            // Heading follows rig yaw, so looking around does not redirect accumulated speed.
            Vector3 forward = Vector3.ProjectOnPlane(xrOrigin.TransformDirection(_neutralForward), Vector3.up).normalized;
            Vector3 rightDirection = Vector3.Cross(Vector3.up, forward);
            Vector3 desiredAuxiliary = rightDirection * (left.x * lateralSpeed)
                + Vector3.up * (enableVertical ? right.y * verticalSpeed : 0f);
            _auxiliaryVelocity = Vector3.MoveTowards(_auxiliaryVelocity, desiredAuxiliary,
                Mathf.Max(0f, brakingAcceleration) * dt);
            xrOrigin.position += (forward * _forwardSpeed + _auxiliaryVelocity) * dt;
            if (enableTurn)
                xrOrigin.rotation = Quaternion.AngleAxis(right.x * turnSpeed * dt, Vector3.up) * xrOrigin.rotation;
        }

        private Vector2 ReadStick(InputDevice device)
        {
            if (!device.isValid) return Vector2.zero;
            if (!device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis))
                device.TryGetFeatureValue(CommonUsages.secondary2DAxis, out axis);
            // Per-axis deadzones prevent sideways input from causing forward creep.
            return new Vector2(FilterAxis(axis.x), FilterAxis(axis.y));
        }

        private float FilterAxis(float value)
        {
            float magnitude = Mathf.Abs(value);
            return magnitude <= deadzone ? 0f : Mathf.Sign(value) *
                Mathf.Clamp01((magnitude - deadzone) / Mathf.Max(0.001f, 1f - deadzone));
        }

        private void TickDevices(float dt)
        {
            _requeryTimer -= dt;
            if (_requeryTimer <= 0f || !_leftDevice.isValid || !_rightDevice.isValid || !_headDevice.isValid)
            {
                _requeryTimer = 2f;
                _leftDevice = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                _rightDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
                _headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
                if (!_headDevice.isValid) _headDevice = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            }
        }

        /// <summary>
        /// 用于控制的头部 origin 局部位置: 有稳定器走稳定值, 否则原始头显位置。
        /// </summary>
        private Vector3 GetControlLocalPosition()
        {
            if (HeadStabilizer != null)
                return HeadStabilizer.GetStabilizedLocalPosition(xrOrigin, headTransform);
            return xrOrigin.InverseTransformPoint(headTransform.position);
        }

        private void ResolveReferences()
        {
            if (xrOrigin == null)
            {
                var origin = FindObjectOfType<XROrigin>();
                if (origin != null) xrOrigin = origin.transform;
            }
            if (headTransform == null && Camera.main != null) headTransform = Camera.main.transform;
        }

        private void OnValidate()
        {
            leanDeadzone = Mathf.Max(0f, leanDeadzone);
            fullLeanDistance = Mathf.Max(leanDeadzone + 0.001f, fullLeanDistance);
            maxAcceleration = Mathf.Max(0f, maxAcceleration);
            brakingAcceleration = Mathf.Max(0f, brakingAcceleration);
            maxForwardSpeed = Mathf.Max(0f, maxForwardSpeed);
            maxBackwardSpeed = Mathf.Max(0f, maxBackwardSpeed);
            leanSmoothingTime = Mathf.Max(0.001f, leanSmoothingTime);
            lateralSpeed = Mathf.Max(0f, lateralSpeed);
            verticalSpeed = Mathf.Max(0f, verticalSpeed);
            turnSpeed = Mathf.Max(0f, turnSpeed);
        }
    }
}
