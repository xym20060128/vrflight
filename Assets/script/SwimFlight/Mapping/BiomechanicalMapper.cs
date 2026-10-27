using UnityEngine;
using SwimFlight.Core;

namespace SwimFlight.Mapping
{
    /// <summary>
    /// 仿生动作映射器: 人体运动解析 → 左右手反作用力 (世界空间)
    ///
    /// 数据流位置:
    ///   FlightInput (世界空间手部运动) → FlightCommand (左右手反作用力 + 力臂)
    ///
    /// 职责 (人体运动解析):
    ///   每只手:
    ///     r     = handPosition - bodyCenter     (力臂, 参考点 = bodyCenter, 不是肩关节)
    ///     v     = handVelocity                   (手部世界速度)
    ///     v_rad = ((v·r)/|r|²)·r                 (径向分量: 手臂伸出/收回)
    ///     v_tan = v - v_rad                      (切向分量: 绕身体中心的划水运动)
    ///     active = SpatialActionGate(dist_xz)    (空间门控, Recovery 机制)
    ///     ω     = (r×v)_y / |r_xz|²              (轨道角速度, 仅分析)
    ///
    ///   反作用力 F = -k·v_tan 由 ReactionForceModel 负责。
    ///   径向运动(v_rad)不产生动力, 只有切向运动(v_tan)产生反作用力。
    ///
    /// 空间门控 (Recovery 机制, 最高优先级):
    ///     dist = |r_xz|  (水平距离)
    ///     dist >  enterDistance → Active:     允许产生反作用力
    ///     dist <  exitDistance  → Inactive:    F=0, 不检测该手运动
    ///     之间滞回, 保持上一状态。
    ///
    /// 本模块不:
    ///   - 积分 / 力矩 / 阻尼 / 坐标转换 / 动作分类 / 意图检测
    ///   - 不依赖 XR Toolkit / InputDevice / Transform / MonoBehaviour
    /// </summary>
    public class BiomechanicalMapper
    {
        // Diagnostic retention affects force and therefore torque; zero preserves the original mapping.
        public float radialRetention;
        public bool uniformTranslation;
        public Vector3 RawLeft, RawRight, TangentialLeft, TangentialRight, EffectiveLeft, EffectiveRight;
        public bool LeftActive, RightActive;

        // === 左右手各自独立的空间动作门控 (Recovery 机制) ===
        private readonly SpatialActionGate _leftSpatialGate = new SpatialActionGate();
        private readonly SpatialActionGate _rightSpatialGate = new SpatialActionGate();

        // === 反作用力模型 (F = -k·v_tan) ===
        private readonly ReactionForceModel _reactionForce = new ReactionForceModel();

        // === 调试 (最近一帧) ===
        public float DebugOmegaLeft;           // 左手轨道角速度 (rad/s)
        public float DebugOmegaRight;          // 右手轨道角速度 (rad/s)
        public float DebugSpatialDistanceLeft; // 左手到身体中心水平距离
        public float DebugSpatialDistanceRight;// 右手到身体中心水平距离

        /// <summary>左手空间门控 (供配置/debug)</summary>
        public SpatialActionGate LeftSpatialGate => _leftSpatialGate;

        /// <summary>右手空间门控 (供配置/debug)</summary>
        public SpatialActionGate RightSpatialGate => _rightSpatialGate;

        /// <summary>反作用力模型 (供配置)</summary>
        public ReactionForceModel ReactionForce => _reactionForce;

        /// <summary>
        /// 人体运动解析 → 左右手反作用力 (世界空间)
        /// 注意: 因内含 SpatialActionGate, 本方法有状态 (跨帧保持)。
        /// totalForce / totalTorque 不在本层计算, 由 FlightDynamicsV2 计算并写回。
        /// </summary>
        public FlightCommand Map(FlightInput input)
        {
            FlightCommand cmd = new FlightCommand();
            Vector3 bodyCenter = input.bodyCenter;

            // === 人体运动解析: r, v ===
            Vector3 rL = input.leftHandPosition - bodyCenter;
            Vector3 rR = input.rightHandPosition - bodyCenter;
            Vector3 vL = input.leftHandVelocity;
            Vector3 vR = input.rightHandVelocity;

            // === 切向速度 (径向分量不产力) ===
            Vector3 vTanL = TangentialVelocity(rL, vL);
            Vector3 vTanR = TangentialVelocity(rR, vR);

            // === active (空间门控, Recovery 机制) ===
            float distL = HorizontalDistance(input.leftHandPosition, bodyCenter);
            float distR = HorizontalDistance(input.rightHandPosition, bodyCenter);
            _leftSpatialGate.Update(distL);
            _rightSpatialGate.Update(distR);
            bool leftActive = _leftSpatialGate.IsActive;
            bool rightActive = _rightSpatialGate.IsActive;

            // === 反作用力 F = -k·v_tan (由 ReactionForceModel 负责) ===
            RawLeft = vL; RawRight = vR;
            TangentialLeft = vTanL; TangentialRight = vTanR;
            float retention = Mathf.Clamp01(radialRetention);
            EffectiveLeft = vTanL + retention * (vL - vTanL);
            EffectiveRight = vTanR + retention * (vR - vTanR);
            LeftActive = leftActive; RightActive = rightActive;
            cmd.leftForce = _reactionForce.Compute(EffectiveLeft, leftActive);
            cmd.rightForce = _reactionForce.Compute(EffectiveRight, rightActive);
            // Translation uses full hand speed so the same stroke strength does not
            // depend on its radial angle. Torque keeps the tangential hand forces.
            if (uniformTranslation)
            {
                cmd.translationForce = _reactionForce.Compute(vL, leftActive)
                    + _reactionForce.Compute(vR, rightActive);
                cmd.hasTranslationForce = true;
            }
            cmd.leftArm = rL;
            cmd.rightArm = rR;

            // === 轨道角速度 ω (仅分析) ===
            cmd.omegaLeft = OmegaY(rL, vL);
            cmd.omegaRight = OmegaY(rR, vR);

            DebugOmegaLeft = cmd.omegaLeft;
            DebugOmegaRight = cmd.omegaRight;
            DebugSpatialDistanceLeft = distL;
            DebugSpatialDistanceRight = distR;

            return cmd;
        }

        /// <summary>切向速度 v_tan = v - v_rad, 其中 v_rad = ((v·r)/|r|²)·r</summary>
        private static Vector3 TangentialVelocity(Vector3 r, Vector3 v)
        {
            float rSq = r.sqrMagnitude;
            if (rSq < 1e-6f) return v;   // 手几乎在身体中心: 无径向方向 (此时通常 Inactive, F=0)
            Vector3 vRad = (Vector3.Dot(v, r) / rSq) * r;
            return v - vRad;
        }

        /// <summary>轨道角速度 ω = (r×v)_y / |r_xz|² (水平面, 绕身体中心 +Y, 供分析)</summary>
        private static float OmegaY(Vector3 r, Vector3 v)
        {
            float rSq = r.x * r.x + r.z * r.z;   // 水平 |r|²
            if (rSq < 1e-6f) return 0f;
            return (r.z * v.x - r.x * v.z) / rSq;   // (r×v)_y = r.z·v.x - r.x·v.z
        }

        /// <summary>两点间的水平距离 (忽略 Y 分量, 只算 XZ 平面)</summary>
        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            Vector3 ah = new Vector3(a.x, 0f, a.z);
            Vector3 bh = new Vector3(b.x, 0f, b.z);
            return Vector3.Distance(ah, bh);
        }
    }
}
