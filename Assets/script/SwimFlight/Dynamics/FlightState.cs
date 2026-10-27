using UnityEngine;

namespace SwimFlight.Core
{
    /// <summary>
    /// 飞行状态: 虚拟飞行器/身体的刚体动力学状态
    ///
    /// 数据流位置:
    ///   FlightDynamicsV2 的输出 → XRLocomotionApplierV2 的输入
    ///
    /// 物理意义: 动力学状态向量
    ///   X = [ position, velocity, orientation, angularVelocity ]
    ///
    /// 坐标系约定: position / velocity / angularVelocity 均为世界空间。
    ///
    /// 姿态:
    ///   orientation 保存完整 3 维姿态 (Quaternion), 供分析/后续阶段使用。
    ///   第一阶段姿态稳定: pitch/roll 强阻尼, yaw 正常响应;
    ///   应用层只取 yaw 写回 XR Origin, 不直接应用完整 orientation (避免 VR 晕动)。
    ///
    /// 本结构是纯数据容器, 不包含任何动力学计算逻辑。
    /// </summary>
    public struct FlightState
    {
        // === 位置 (世界空间, 单位: m) ===
        public Vector3 position;             // 世界空间位置

        // === 速度 (世界空间, 单位: m/s) ===
        public Vector3 velocity;             // 世界空间速度

        // === 姿态 ===
        public Quaternion orientation;       // 完整 3 维姿态 (世界空间)
        public Vector3 angularVelocity;      // 角速度 (世界空间, rad/s)
    }
}
