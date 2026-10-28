using UnityEngine;
using UnityEngine.XR;
using Unity.XR.CoreUtils;

namespace SwimFlight
{
    /// <summary>
    /// Joystick input adapter. Under FlightControlModeSwitcher, inputs produce target
    /// velocity/yaw for the shared comparison actuator; heading is XR Origin yaw.
    /// Legacy direct-motion settings below apply only when running without the switcher.
    /// </summary>
    public class JoystickFlightController : MonoBehaviour
    {
        [Header("=== 引用 ===")]
        [Tooltip("XR Origin Transform (会被移动的根对象)")]
        public Transform xrOrigin;

        [Tooltip("头显/Camera Transform (用于确定前进方向)")]
        public Transform headTransform;

        [Header("=== 速度 ===")]
        [Tooltip("水平移动最大速度 (m/s), 作用于前进/后退与左右平移")]
        public float moveSpeed = 3f;

        [Tooltip("垂直移动最大速度 (m/s), 作用于上升/下降")]
        public float verticalSpeed = 2f;

        [Tooltip("转向最大角速度 (deg/s), 作用于右摇杆 X")]
        public float turnSpeed = 20f;

        [Tooltip("摇杆死区 (0~1)。小于该幅度的输入按 0 处理, 避免漂移")]
        [Range(0f, 0.5f)] public float deadzone = 0.15f;

        [Tooltip("速度平滑系数 (越大越跟手, 0 = 直接输出目标速度)")]
        public float smoothing = 8f;

        [Header("=== 启用开关 ===")]
        [Tooltip("是否用右摇杆 X 转向")]
        public bool enableTurn = true;

        [Tooltip("是否用右摇杆 Y 上升/下降")]
        public bool enableVertical = true;

        // === 运行时 ===
        [System.NonSerialized] public ComparisonMotionMotor ComparisonMotor;
        private InputDevice _leftDevice;
        private InputDevice _rightDevice;
        private float _requeryTimer;
        private Vector3 _velocity;

        private void Start()
        {
            ResolveReferences();
        }

        private void OnEnable()
        {
            // 重新启用(切回遥感模式)时清掉旧速度, 避免带着惯性继续飞
            _velocity = Vector3.zero;
        }

        private void Update()
        {
            ResolveReferences();

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            TickDevices(dt);

            Vector2 left = ReadStick(_leftDevice);
            Vector2 right = ReadStick(_rightDevice);

            Vector3 desired = ComputeDesiredVelocity(left, right);
            float yawRate = ComputeYawRate(right);
            if (ComparisonMotor != null)
            {
                ComparisonMotor.Step(desired, yawRate, dt);
                return;
            }

            // 平滑 (帧率无关)
            if (smoothing > 0f)
            {
                float alpha = 1f - Mathf.Exp(-smoothing * dt);
                _velocity = Vector3.Lerp(_velocity, desired, alpha);
            }
            else
            {
                _velocity = desired;
            }

            Apply(_velocity, yawRate, dt);
        }

        private void Apply(Vector3 velocity, float yawRate, float dt)
        {
            if (xrOrigin == null) return;

            xrOrigin.position += velocity * dt;

            if (Mathf.Abs(yawRate) > 1e-4f)
            {
                // 绕世界 Y 轴转向
                xrOrigin.rotation = Quaternion.Euler(0f, yawRate * dt, 0f) * xrOrigin.rotation;
            }
        }

        private Vector3 ComputeDesiredVelocity(Vector2 left, Vector2 right)
        {
            Vector3 forward = ComparisonMotor != null ? ComparisonMotor.Forward : Flatten(headTransform != null ? headTransform.forward : Vector3.forward);
            Vector3 rightDir = ComparisonMotor != null ? ComparisonMotor.Right : Flatten(headTransform != null ? headTransform.right : Vector3.right);

            Vector3 v = forward * (left.y * moveSpeed)
                      + rightDir * (left.x * moveSpeed);

            if (enableVertical)
                v += Vector3.up * (right.y * verticalSpeed);

            return v;
        }

        private float ComputeYawRate(Vector2 right)
        {
            return enableTurn ? right.x * turnSpeed : 0f;
        }

        // ---------- 输入读取 ----------

        private void TickDevices(float dt)
        {
            _requeryTimer -= dt;
            if (_requeryTimer <= 0f)
            {
                _requeryTimer = 5f;
                _leftDevice = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                _rightDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            }

            if (!_leftDevice.isValid) _leftDevice = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (!_rightDevice.isValid) _rightDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        }

        private Vector2 ReadStick(InputDevice device)
        {
            if (!device.isValid) return Vector2.zero;

            Vector2 v = Vector2.zero;
            if (!device.TryGetFeatureValue(CommonUsages.primary2DAxis, out v))
                device.TryGetFeatureValue(CommonUsages.secondary2DAxis, out v);

            if (ComparisonMotor != null)
                return new Vector2(FilterAxis(v.x), FilterAxis(v.y));
            return ApplyDeadzone(v);
        }

        private float FilterAxis(float value)
        {
            float magnitude = Mathf.Abs(value);
            return magnitude <= deadzone ? 0f : Mathf.Sign(value) *
                Mathf.Clamp01((magnitude - deadzone) / Mathf.Max(0.001f, 1f - deadzone));
        }

        private Vector2 ApplyDeadzone(Vector2 v)
        {
            float m = v.magnitude;
            if (m < deadzone) return Vector2.zero;
            float scaled = (m - deadzone) / Mathf.Max(1e-4f, 1f - deadzone);
            return v.normalized * scaled;
        }

        // ---------- 工具 ----------

        private void ResolveReferences()
        {
            if (xrOrigin == null)
            {
                var origin = FindObjectOfType<XROrigin>();
                if (origin != null) xrOrigin = origin.transform;
            }
            if (headTransform == null)
            {
                if (Camera.main != null) headTransform = Camera.main.transform;
            }
        }

        /// <summary>去掉竖直分量并归一化; 头显正对竖直方向时退回世界正前。</summary>
        private static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            if (v.sqrMagnitude < 1e-6f) return Vector3.forward;
            return v.normalized;
        }
    }
}
