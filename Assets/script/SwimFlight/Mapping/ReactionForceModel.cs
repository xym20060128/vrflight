using UnityEngine;

namespace SwimFlight.Mapping
{
    /// <summary>
    /// 反作用力模型 (Reaction Force Model)
    ///
    /// 职责: 手相对流体运动 → 反作用力 (牛顿第三定律)
    ///   F = -k · v_tan
    ///
    /// 只使用切向速度 v_tan (绕身体中心的轨道运动); 径向运动(v_rad)不产生动力。
    /// k 为各向同性标量增益 (无 forward/lateral/vertical 分轴控制)。
    ///
    /// 不做:
    ///   - 人体运动解析 (r/v/v_rad/v_tan 由 BiomechanicalMapper 负责)
    ///   - 空间门控 / 力矩 / 积分 / 坐标转换 / 动作分类
    ///
    /// 本模块是纯计算, 无跨帧状态。
    /// </summary>
    public class ReactionForceModel
    {
        /// <summary>反作用力增益 k (各向同性标量, N/(m/s))</summary>
        public float gain = 4f;
        public float velocityDeadZone = 0.03f;
        public float maxHandSpeed = 2.5f;

        /// <summary>
        /// 计算反作用力 F = -k·v_tan (世界空间)。
        /// </summary>
        /// <param name="tangentialVelocity">切向速度 v_tan (绕身体中心的轨道运动)</param>
        /// <param name="active">该手是否 Active (Inactive 时返回零力)</param>
        public Vector3 Compute(Vector3 tangentialVelocity, bool active)
        {
            if (!active) return Vector3.zero;
            float speed = tangentialVelocity.magnitude;
            if (float.IsNaN(speed) || float.IsInfinity(speed) || speed <= velocityDeadZone)
                return Vector3.zero;
            float effectiveSpeed = Mathf.Min(speed - velocityDeadZone, maxHandSpeed);
            return -Mathf.Max(0f, gain) * effectiveSpeed * (tangentialVelocity / speed);
        }
    }
}
