using UnityEngine;

namespace SwimFlight.Input
{
    /// <summary>
    /// 手部速度估计器 (Hand Velocity Estimator)
    ///
    /// 职责: 位置差分 + 低通滤波 → 稳定的手部速度 (tracking space)
    ///
    /// 输入: XRDeviceSample (当前帧位置 + 有效性) + deltaTime
    /// 输出: XRRawInput (位置 + 估计速度 + 有效性)
    ///
    /// 数学:
    ///   alpha       = 1 - exp(-dt / smoothingTimeConstant)  帧率无关低通系数
    ///   filteredPos = Lerp(prevFilteredPos, rawPos, alpha)   低通滤波
    ///   velocity    = (filteredPos - prevFilteredPos) / dt   帧间差分
    ///
    /// 边界:
    ///   - 速度估计在 tracking space 完成 (不做坐标转换)
    ///   - 不做动作映射 / 不参与动力学
    ///   - 设备失效时速度归零, 滤波状态重置 (下次有效时重新初始化)
    /// </summary>
    public class HandVelocityEstimator
    {
        /// <summary>低通滤波时间常数 (秒), 越小响应越快/越抖, 越大越平滑/越滞后; 帧率无关</summary>
        public float smoothingTimeConstant = 0.06f;

        private Vector3 _filteredLeftPos;
        private Vector3 _filteredRightPos;
        private bool _leftInitialized;
        private bool _rightInitialized;

        /// <summary>
        /// 原始设备采样 + dt → 坐标层输入 (位置 + 估计速度)
        /// </summary>
        public XRRawInput Estimate(XRDeviceSample sample, float dt)
        {
            XRRawInput raw = new XRRawInput();
            raw.leftValid = sample.leftValid;
            raw.rightValid = sample.rightValid;
            raw.leftTrackingPosition = sample.leftPosition;
            raw.rightTrackingPosition = sample.rightPosition;

            if (dt <= 1e-6f)
            {
                raw.leftTrackingVelocity = Vector3.zero;
                raw.rightTrackingVelocity = Vector3.zero;
                return raw;
            }

            raw.leftTrackingVelocity = EstimateHand(
                ref _filteredLeftPos, ref _leftInitialized, sample.leftValid, sample.leftPosition, dt);
            raw.rightTrackingVelocity = EstimateHand(
                ref _filteredRightPos, ref _rightInitialized, sample.rightValid, sample.rightPosition, dt);

            return raw;
        }

        private Vector3 EstimateHand(
            ref Vector3 filteredPos, ref bool initialized, bool valid, Vector3 rawPos, float dt)
        {
            if (!valid)
            {
                // 设备失效: 速度归零, 滤波状态重置为未初始化
                initialized = false;
                return Vector3.zero;
            }

            if (!initialized)
            {
                // 首帧: 滤波位置 = 原始位置, 速度 = 0 (避免首帧差分跳变)
                filteredPos = rawPos;
                initialized = true;
                return Vector3.zero;
            }

            // 低通滤波 (帧率无关: alpha 由 dt 与时间常数推导)
            // Large discontinuities are tracking resets, not intentional strokes.
            if ((rawPos - filteredPos).magnitude > 0.25f + 5f * dt)
            {
                filteredPos = rawPos;
                return Vector3.zero;
            }
            float alpha = smoothingTimeConstant > 0f ? 1f - Mathf.Exp(-dt / smoothingTimeConstant) : 1f;
            Vector3 newFiltered = Vector3.Lerp(filteredPos, rawPos, alpha);

            // 帧间差分求速度
            Vector3 velocity = (newFiltered - filteredPos) / dt;

            filteredPos = newFiltered;
            return velocity;
        }

        /// <summary>
        /// 清空滤波/初始化状态, 使下一次估计从零速度重新开始。
        /// 供 SwimFlightControllerV2.ResetState 在切回划水模式时调用, 避免首帧速度尖峰。
        /// </summary>
        public void Reset()
        {
            _leftInitialized = false;
            _rightInitialized = false;
        }
    }
}
