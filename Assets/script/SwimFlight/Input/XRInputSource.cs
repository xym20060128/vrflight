using UnityEngine;
using UnityEngine.XR;

namespace SwimFlight.Input
{
    /// <summary>
    /// 原始 XR 设备采样 (tracking space): 位置 + 旋转 + 运行时速度(仅 debug) + 有效性
    /// 这是 XRInputSource 的输出, 不含任何速度估计。
    /// </summary>
    public struct XRDeviceSample
    {
        public Vector3 leftPosition;           // 左手追踪空间位置
        public Vector3 rightPosition;          // 右手追踪空间位置
        public Quaternion leftRotation;        // 左手追踪空间旋转
        public Quaternion rightRotation;       // 右手追踪空间旋转
        public Vector3 leftRuntimeVelocity;    // deviceVelocity, 仅 debug, 不参与计算
        public Vector3 rightRuntimeVelocity;   // deviceVelocity, 仅 debug, 不参与计算
        public bool leftValid;                 // 左手设备是否有效
        public bool rightValid;                // 右手设备是否有效
        public bool leftGripPressed;
        public bool rightGripPressed;
        public bool leftTriggerAvailable;
        public bool rightTriggerAvailable;
        public float leftTrigger;
        public float rightTrigger;
        public bool headTracked, leftTracked, rightTracked;
        public bool leftSecondaryButton, rightStickClick, leftStickClick;
    }

    /// <summary>
    /// 坐标层输入 (tracking space): 位置 + 估计速度 + 有效性
    /// HandVelocityEstimator 的输出, FlightCoordinateSystem 的输入。
    /// </summary>
    public struct XRRawInput
    {
        public Vector3 leftTrackingVelocity;    // 左手追踪空间估计速度 (m/s)
        public Vector3 rightTrackingVelocity;   // 右手追踪空间估计速度 (m/s)
        public Vector3 leftTrackingPosition;    // 左手追踪空间位置 (m)
        public Vector3 rightTrackingPosition;   // 右手追踪空间位置 (m)
        public bool leftValid;                  // 左手设备是否有效
        public bool rightValid;                 // 右手设备是否有效
    }

    /// <summary>
    /// XR 输入源 (XRInputSource)
    ///
    /// 职责:
    ///   - 只读左右手 devicePosition / deviceRotation / 设备有效性
    ///   - deviceVelocity 仅作为 runtimeVelocity 保留供 debug, 不参与核心计算
    ///   - 持有 Head Transform 引用
    ///
    /// 不做:
    ///   - 速度估计 (留给 HandVelocityEstimator)
    ///   - 坐标转换 (留给 FlightCoordinateSystem)
    ///   - 运动映射 (留给 BiomechanicalMapper)
    /// </summary>
    public class XRInputSource
    {
        private const float DeviceRequeryInterval = 5f;

        private InputDevice _leftDevice;
        private InputDevice _rightDevice;
        private InputDevice _headDevice;
        private float _deviceRequeryTimer;

        /// <summary>头部 Transform (由协调器注入, 供坐标层计算世界方向)</summary>
        public Transform headTransform;

        public XRInputSource()
        {
            RequeryDevices();
        }

        /// <summary>
        /// 每帧调用: 定期重取设备句柄 + 失效时立即重取
        /// </summary>
        public void Tick(float dt)
        {
            _deviceRequeryTimer -= dt;
            if (_deviceRequeryTimer <= 0f)
            {
                _deviceRequeryTimer = DeviceRequeryInterval;
                RequeryDevices();
            }

            if (!_leftDevice.isValid)
                _leftDevice = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (!_rightDevice.isValid)
                _rightDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        }

        /// <summary>读取左右手原始设备数据 (tracking space)</summary>
        public XRDeviceSample Read()
        {
            XRDeviceSample sample = new XRDeviceSample();
            sample.leftValid = ReadDevice(_leftDevice, out sample.leftPosition, out sample.leftRotation, out sample.leftRuntimeVelocity);
            sample.rightValid = ReadDevice(_rightDevice, out sample.rightPosition, out sample.rightRotation, out sample.rightRuntimeVelocity);
            sample.leftGripPressed = ReadGrip(_leftDevice);
            sample.rightGripPressed = ReadGrip(_rightDevice);
            sample.leftTriggerAvailable = ReadTrigger(_leftDevice, out sample.leftTrigger);
            sample.rightTriggerAvailable = ReadTrigger(_rightDevice, out sample.rightTrigger);
            if (!_headDevice.isValid) _headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!_headDevice.isValid) _headDevice = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            sample.headTracked = IsTracked(_headDevice);
            sample.leftTracked = sample.leftValid && IsTracked(_leftDevice);
            sample.rightTracked = sample.rightValid && IsTracked(_rightDevice);
            sample.leftSecondaryButton = ReadButton(_leftDevice, CommonUsages.secondaryButton);
            sample.rightStickClick = ReadButton(_rightDevice, CommonUsages.primary2DAxisClick);
            sample.leftStickClick = ReadButton(_leftDevice, CommonUsages.primary2DAxisClick);
            return sample;
        }

        private static bool ReadButton(InputDevice device, InputFeatureUsage<bool> usage)
            => device.isValid && device.TryGetFeatureValue(usage, out bool pressed) && pressed;

        private static bool IsTracked(InputDevice device)
        {
            if (!device.isValid) return false;
            if (device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked)) return tracked;
            return device.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state)
                && (state & (InputTrackingState.Position | InputTrackingState.Rotation))
                    == (InputTrackingState.Position | InputTrackingState.Rotation);
        }

        private static bool ReadTrigger(InputDevice device, out float value)
        {
            value = 0f;
            if (!device.isValid) return false;
            bool tracked;
            if (!device.TryGetFeatureValue(CommonUsages.isTracked, out tracked))
            {
                tracked = device.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state)
                    && (state & InputTrackingState.Position) != 0;
            }
            if (!tracked) return false;
            if (device.TryGetFeatureValue(CommonUsages.trigger, out value))
                return !float.IsNaN(value) && !float.IsInfinity(value);
            // Boolean-only runtimes still use Trigger, never Grip.
            if (!device.TryGetFeatureValue(CommonUsages.triggerButton, out bool pressed)) return false;
            value = pressed ? 1f : 0f;
            return true;
        }

        private static bool ReadGrip(InputDevice device)
        {
            if (!device.isValid) return false;
            if (device.TryGetFeatureValue(CommonUsages.gripButton, out bool pressed)) return pressed;
            return device.TryGetFeatureValue(CommonUsages.grip, out float grip) && grip >= 0.65f;
        }

        /// <summary>获取头部 Transform (供坐标层使用)</summary>
        public Transform GetHeadTransform() => headTransform;

        private bool ReadDevice(InputDevice device, out Vector3 position, out Quaternion rotation, out Vector3 runtimeVelocity)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            runtimeVelocity = Vector3.zero;

            if (!device.isValid) return false;

            device.TryGetFeatureValue(CommonUsages.devicePosition, out position);
            device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
            device.TryGetFeatureValue(CommonUsages.deviceVelocity, out runtimeVelocity);  // 仅 debug, 不参与计算
            return true;
        }

        private void RequeryDevices()
        {
            _leftDevice = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            _rightDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        }
    }
}
