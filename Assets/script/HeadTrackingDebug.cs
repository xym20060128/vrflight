using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// 最小化 Head Tracking 诊断脚本
/// 目的: 判断"头显旋转没有进入 Main Camera"故障在哪一层
///
/// 只诊断,不修改任何移动/飞行逻辑。
/// 挂到任意 GameObject 上即可(建议挂在 XR Origin 上,或单独空物体)。
///
/// 每秒打印一次:
///   - XRNode.Head 设备是否有效
///   - XRNode.CenterEye 设备是否有效 (TrackedPoseDriver 实际绑的是 centerEye)
///   - deviceRotation / devicePosition (追踪空间下的头部位姿)
///   - Camera.main 的 rotation / forward
///   - 头部追踪状态 (trackingState)
///   - 原始 vs 稳定后的头部水平偏移区间峰峰值, 以及当前前倾量
///     (验证头部稳定器: 原始 ~2cm 应塌缩到 <0.5cm, 有意前倾仍能推动前倾量)
/// </summary>
public class HeadTrackingDebug : MonoBehaviour
{
    [Tooltip("打印间隔(秒)")]
    public float logInterval = 1f;

    private float _timer;
    private InputDevice _headDevice;
    private InputDevice _centerEyeDevice;

    // 记录上一次 deviceRotation,用于判断旋转是否"在变化"
    private Quaternion _lastHeadRot = Quaternion.identity;
    private bool _hasLastRot = false;

    // ---- 稳定器对比 (只读) ----
    [Tooltip("留空自动查找。仅用于读取稳定后的头部信号,不修改任何状态。")]
    public SwimFlight.FlightControlModeSwitcher switcher;
    private Vector2 _rawMin, _rawMax, _stabMin, _stabMax;
    private bool _hasInterval;

    /// <summary>
    /// 区间峰峰值: 原始头部偏移 vs 稳定后偏移。判据是「原始 ~2cm 塌缩到 <0.5cm,
    /// 而有意前倾仍能推动前倾量」——这是唯一能在真机上确认抖动被压掉的方法。
    /// </summary>
    private string DescribeStabilization()
    {
        if (switcher == null) switcher = FindObjectOfType<SwimFlight.FlightControlModeSwitcher>();
        if (switcher == null || switcher.Stabilizer == null) return "stabilizer=不可用";

        Transform origin = switcher.StabilizerOrigin;
        Transform head = switcher.StabilizerHead;
        if (origin == null || head == null) return "stabilizer=origin/head 未解析";

        Vector3 rawLocal = origin.InverseTransformPoint(head.position);
        Vector2 rawXZ = new Vector2(rawLocal.x, rawLocal.z);
        Vector3 stabLocal = switcher.Stabilizer.GetStabilizedLocalPosition(origin, head);
        Vector2 stabXZ = new Vector2(stabLocal.x, stabLocal.z);

        if (!_hasInterval)
        {
            _rawMin = _rawMax = rawXZ;
            _stabMin = _stabMax = stabXZ;
            _hasInterval = true;
        }
        else
        {
            _rawMin = Vector2.Min(_rawMin, rawXZ);
            _rawMax = Vector2.Max(_rawMax, rawXZ);
            _stabMin = Vector2.Min(_stabMin, stabXZ);
            _stabMax = Vector2.Max(_stabMax, stabXZ);
        }

        float rawPeakToPeak = Mathf.Max(_rawMax.x - _rawMin.x, _rawMax.y - _rawMin.y);
        float stabPeakToPeak = Mathf.Max(_stabMax.x - _stabMin.x, _stabMax.y - _stabMin.y);

        // 开新区间
        _rawMin = _rawMax = rawXZ;
        _stabMin = _stabMax = stabXZ;

        string lean = "";
        var leanController = switcher.intentionalHeadMotionController;
        if (leanController != null) lean = $" 前倾量={leanController.LeanAmount:F3}";

        return $"追踪={switcher.Stabilizer.IsTracking} " +
               $"raw峰峰值={rawPeakToPeak * 100f:F2}cm stab峰峰值={stabPeakToPeak * 100f:F2}cm " +
               $"稳定偏移=({stabXZ.x * 100f:F1},{stabXZ.y * 100f:F1})cm{lean}";
    }

    private void Update()
    {
        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        _timer = logInterval;

        // 每帧(每次打印)都重新获取设备,避免持有失效句柄
        _headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        _centerEyeDevice = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);

        // ---- 读 Head 设备 ----
        bool headValid = _headDevice.isValid;
        Quaternion headRot = Quaternion.identity;
        Vector3 headPos = Vector3.zero;
        bool hasRot = false, hasPos = false;
        if (headValid)
        {
            hasRot = _headDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out headRot);
            hasPos = _headDevice.TryGetFeatureValue(CommonUsages.devicePosition, out headPos);
        }

        // ---- 读 CenterEye 设备 (TrackedPoseDriver 实际用的) ----
        bool centerValid = _centerEyeDevice.isValid;
        Quaternion centerRot = Quaternion.identity;
        if (centerValid)
        {
            _centerEyeDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out centerRot);
        }

        // ---- 读追踪状态 ----
        InputTrackingState trackingState = InputTrackingState.None;
        if (headValid)
        {
            _headDevice.TryGetFeatureValue(CommonUsages.trackingState, out trackingState);
        }

        // ---- Camera ----
        Camera cam = Camera.main;
        string camInfo = (cam != null)
            ? $"rot={Euler(cam.transform.rotation)} fwd=({cam.transform.forward.x:F2},{cam.transform.forward.y:F2},{cam.transform.forward.z:F2})"
            : "Camera.main = null!";

        // ---- 判断头部旋转是否在变化 ----
        string rotChange = "?";
        if (hasRot)
        {
            if (_hasLastRot)
            {
                float delta = Quaternion.Angle(_lastHeadRot, headRot);
                rotChange = delta > 0.5f ? $"变化Δ={delta:F1}°" : "不变";
            }
            else
            {
                rotChange = "首帧";
            }
            _lastHeadRot = headRot;
            _hasLastRot = true;
        }

        Debug.Log(
            $"[HeadDebug] Head有效={headValid} 有Rot={hasRot} 有Pos={hasPos} | " +
            $"CenterEye有效={centerValid} | " +
            $"HeadRot.euler={Euler(headRot)} ({rotChange}) | " +
            $"HeadPos=({headPos.x:F2},{headPos.y:F2},{headPos.z:F2}) | " +
            $"CenterEyeRot.euler={Euler(centerRot)} | " +
            $"trackingState={trackingState} | " +
            $"Camera: {camInfo} | " +
            DescribeStabilization()
        );
    }

    private static string Euler(Quaternion q)
    {
        Vector3 e = q.eulerAngles;
        return $"({e.x:F1},{e.y:F1},{e.z:F1})";
    }
}
