using UnityEngine;

namespace SwimFlight.Core
{
    /// <summary>
    /// 飞行命令: BiomechanicalMapper 计算出的左右手反作用力 → FlightDynamicsV2 的输入
    ///
    /// 数据流位置:
    ///   BiomechanicalMapper 的输出 → FlightDynamicsV2 的输入
    ///
    /// 物理意义 (仿生拨水/拨空气, 牛顿第三定律):
    ///
    ///   每只手是外力施加点, 手相对流体做切向(轨道)运动产生反作用力:
    ///     F_i = -k · v_tan,i        (仅当该手 Active)
    ///
    ///   反作用力作用在手的位置, 因手相对身体中心存在力臂, 自然产生:
    ///     F_total = F_L + F_R
    ///     τ       = r_L × F_L + r_R × F_R   (绕身体中心 = bodyCenter, 完整 3 维)
    ///
    ///   r_i = handPosition_i - bodyCenter
    ///
    ///   omegaLeft/Right 是轨道角速度 (rad/s), 仅用于 debug/论文分析,
    ///   验证 τ_y 与轨道运动关系, 不参与任何动力学计算、不作控制输入。
    ///
    /// 本结构是纯数据容器, 不包含任何计算逻辑。
    /// </summary>
    public struct FlightCommand
    {
        // === 左右手反作用力 (世界空间, 单位: N) ===
        public Vector3 leftForce;    // 左手反作用力 F_L = -k·v_tan,L
        public Vector3 rightForce;   // 右手反作用力 F_R = -k·v_tan,R

        // Optional assisted net translation; hand forces remain authoritative for torque.
        public bool hasTranslationForce;
        public Vector3 translationForce;

        // === 力臂 (世界空间, 单位: m) ===
        public Vector3 leftArm;      // r_L = handPosition_L - bodyCenter
        public Vector3 rightArm;     // r_R = handPosition_R - bodyCenter

        // === 合成量 (由 FlightDynamicsV2 填充, 供协调器观察/调试) ===
        public Vector3 totalForce;   // F_L + F_R (世界空间, N)
        public Vector3 totalTorque;  // 实际参与积分的力矩: 稳定化之后的 r_L×F_L + r_R×F_R (N·m)
        public Vector3 rawTotalTorque; // 稳定化之前的原始力矩 (N·m), 仅供诊断对比

        // === 轨道角速度 (仅分析, 不参与动力学) ===
        public float omegaLeft;      // 左手绕身体中心水平面轨道角速度 (rad/s)
        public float omegaRight;     // 右手轨道角速度 (rad/s)
    }
}
