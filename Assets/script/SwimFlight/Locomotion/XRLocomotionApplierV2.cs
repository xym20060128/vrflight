using UnityEngine;
using SwimFlight.Core;

// 用不同名别名引用新 FlightState, 避免与全局旧 FlightState 冲突
using V2State = SwimFlight.Core.FlightState;

namespace SwimFlight.Locomotion
{
    /// <summary>
    /// XR 运动执行器 V2 (Locomotion Applier)
    ///
    /// 数据流:
    ///   FlightState (绝对状态) → XRLocomotionApplierV2 → XR Origin Transform
    ///
    /// 职责: 只负责运动应用, 把 FlightState 写到 XR Origin。
    ///   position: 按【增量】施加 (不是写绝对位置), 见下
    ///   rotation: 姿态稳定阶段只应用 yaw, 且【绕头部支点】旋转
    ///
    /// 为什么 yaw 必须绕头部支点:
    ///   XR Origin 的原点在地面, 而玩家实际站位可能离它半米。直接写
    ///   xrOrigin.rotation 会让玩家绕地面那根轴画弧 —— 转身时人被甩出去,
    ///   画面跟着横向平移, 是 VR 里很明显的一类晃动。
    ///   绕头部支点转, 等价于「世界绕观察者转」, 头部保持不动。
    ///
    /// 为什么平移只加增量:
    ///   绕支点旋转本身会改变 XR Origin 的 position (这正是"让头部不动"的补偿量)。
    ///   若随后把 position 覆盖成 state.position, 就把这份补偿又抹掉了, 绕支点等于白做。
    ///   因此平移只施加本帧的仿真增量, 旋转的补偿量得以保留。
    ///   增量与绝对位置不会累积漂移: 增量 = state.position 相邻帧之差, 由动力学唯一决定。
    ///
    /// 禁止:
    ///   - 读取 InputDevice
    ///   - 计算 velocity / 动力学 / 坐标转换
    /// </summary>
    public class XRLocomotionApplierV2
    {
        private readonly Transform _xrOrigin;

        /// <summary>上一帧动力学给出的绝对位置 (用于取增量, 不是 Transform 的实际位置)</summary>
        private Vector3 _lastStatePosition;
        private bool _hasLastStatePosition;

        public XRLocomotionApplierV2(Transform xrOrigin)
        {
            _xrOrigin = xrOrigin;
        }

        /// <summary>
        /// 应用飞行状态。
        /// </summary>
        /// <param name="state">飞行状态 (position 绝对位置, orientation 完整姿态)</param>
        /// <param name="applyPosition">是否施加人工平移 (诊断模式可关闭)</param>
        /// <param name="applyYaw">是否施加人工 yaw (诊断模式可关闭)</param>
        /// <param name="yawPivot">yaw 旋转支点 (世界空间, 通常为稳定后的头部位置); null 时退回绕原点</param>
        public void Apply(V2State state, bool applyPosition, bool applyYaw, Vector3? yawPivot)
        {
            if (_xrOrigin == null) return;

            // ---- 旋转: 绕头部支点施加 yaw 增量 ----
            if (applyYaw)
            {
                float currentYaw = _xrOrigin.rotation.eulerAngles.y;
                float targetYaw = state.orientation.eulerAngles.y;
                float deltaYaw = Mathf.DeltaAngle(currentYaw, targetYaw);
                if (Mathf.Abs(deltaYaw) > 1e-5f)
                {
                    Vector3 pivot = yawPivot ?? _xrOrigin.position;
                    _xrOrigin.RotateAround(pivot, Vector3.up, deltaYaw);
                }
            }

            // ---- 平移: 只加仿真增量 ----
            if (applyPosition)
            {
                // 重锚后首帧 delta 为 0 (Reset 已把 _lastStatePosition 对齐到当前 state),
                // 因此从这里恢复平移不会产生跳变。
                if (_hasLastStatePosition)
                {
                    Vector3 movement = state.position - _lastStatePosition;
                    HeadCollisionGuard guard = HeadCollisionGuard.Find(_xrOrigin);
                    if (guard != null) guard.Move(movement);
                    else _xrOrigin.position += movement;
                }
                _lastStatePosition = state.position;
                _hasLastStatePosition = true;
            }
            else
            {
                // 不施加平移: 丢掉锚点, 恢复施加时首帧不补跳
                _hasLastStatePosition = false;
            }
        }

        /// <summary>
        /// 重新对齐锚点。模式切换 / 位姿重置时调用, 使下一次 Apply 的平移增量为 0,
        /// 避免切换瞬间跳动。
        /// </summary>
        public void Reset(V2State state)
        {
            _lastStatePosition = state.position;
            _hasLastStatePosition = true;
        }
    }
}
