using UnityEngine;

namespace SwimFlight.Mapping
{
    /// <summary>
    /// 空间动作状态 (手是否处于有效操作空间)
    /// </summary>
    public enum SpatialActionState
    {
        Inactive,  // 手在身体附近(回收区), 不允许主动动力
        Active     // 手在有效操作空间(足够远), 允许主动动力
    }

    /// <summary>
    /// 空间动作门控 (Spatial Action Gate)
    ///
    /// 职责: 只回答"这只手现在是否处于可以执行主动划水的空间?", 不回答动作方向。
    ///   用进入/退出两个距离阈值形成空间迟滞, 避免边界抖动。
    ///
    /// 状态机 (每只手独立实例):
    ///   Inactive → Active: horizontalDistance >= enterDistance
    ///   Active   → Inactive: horizontalDistance <= exitDistance
    ///   (enterDistance 与 exitDistance 之间保持上一状态, 形成空间迟滞)
    ///
    /// 语义: 手伸出去(离身体远)才算"进入操作空间", 允许主动动力;
    ///        手收回来(离身体近)算"退出操作空间", 恢复动作不产生任何动力。
    /// </summary>
    public class SpatialActionGate
    {
        /// <summary>进入 Active 的距离阈值 (m)</summary>
        public float enterDistance = 0.30f;

        /// <summary>退出 Active 的距离阈值 (m), 必须 < enterDistance</summary>
        public float exitDistance = 0.18f;

        private SpatialActionState _state = SpatialActionState.Inactive;

        /// <summary>
        /// 更新空间状态 (带迟滞)
        /// </summary>
        /// <param name="horizontalDistance">手到身体中心的水平距离 (m)</param>
        public SpatialActionState Update(float horizontalDistance)
        {
            switch (_state)
            {
                case SpatialActionState.Inactive:
                    if (horizontalDistance >= enterDistance)
                        _state = SpatialActionState.Active;
                    break;

                case SpatialActionState.Active:
                    if (horizontalDistance <= exitDistance)
                        _state = SpatialActionState.Inactive;
                    break;
            }

            return _state;
        }

        /// <summary>当前空间状态 (只读)</summary>
        public SpatialActionState CurrentState => _state;

        /// <summary>是否处于 Active (允许主动动力)</summary>
        public bool IsActive => _state == SpatialActionState.Active;
    }
}
