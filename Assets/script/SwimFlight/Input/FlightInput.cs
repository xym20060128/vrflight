using UnityEngine;

namespace SwimFlight.Core
{
    /// <summary>
    /// 飞行输入: 经过 CoordinateTransform 坐标归一化后的运动输入
    ///
    /// 【重要】本结构不是 XR/OpenXR 的原始输入。
    /// 它是 CoordinateTransform 模块把追踪空间(tracking space)数据
    /// 统一转换到世界空间(World Space)之后的产物。
    ///
    /// 数据流位置:
    ///   CoordinateTransform 的输出 → BiomechanicalMapper 的输入
    ///
    /// 坐标系约定: 手位置/速度/身体中心均为【世界空间 (World Space)】。
    ///
    /// 本结构是纯数据容器, 不包含任何计算逻辑。
    /// </summary>
    public struct FlightInput
    {
        // === 手部位置 (世界空间, 单位: m) ===
        public Vector3 leftHandPosition;    // 左手世界位置
        public Vector3 rightHandPosition;   // 右手世界位置

        // === 手部速度 (世界空间, 单位: m/s) ===
        public Vector3 leftHandVelocity;    // 左手世界速度
        public Vector3 rightHandVelocity;   // 右手世界速度

        // === 身体中心 (世界空间, = xrOrigin.position + xrOrigin.up·bodyCenterOffset) ===
        public Vector3 bodyCenter;          // 身体中心 (力矩/门控参考点)

        // === 时间 ===
        public float deltaTime;   // 帧间隔时间 (单位: 秒)
    }
}
