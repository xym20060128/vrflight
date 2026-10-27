using UnityEngine;
using SwimFlight.Core;
using SwimFlight.Input;

namespace SwimFlight.Coordinate
{
    /// <summary>
    /// 追踪空间参考系: 决定 deviceVelocity 属于哪个空间, 以及如何转成世界空间
    /// </summary>
    public enum TrackingSpaceReference
    {
        /// <summary>追踪空间 = XR Origin 空间 (OpenXR LOCAL, 真机本地运行)</summary>
        XROrigin,
        /// <summary>追踪空间 = 头显局部空间 (PDC/Live Preview 串流可能)</summary>
        HeadLocal,
        /// <summary>追踪空间已等于世界空间 (不做旋转转换)</summary>
        World
    }

    /// <summary>
    /// 飞行坐标系 (FlightCoordinateSystem)
    ///
    /// 职责:
    ///   - 将 XR tracking space 转换为 world space
    ///   - 生成 SwimFlight.Core.FlightInput
    ///   - 输出手部世界速度/世界位置/身体中心
    ///
    /// 坐标转换 (速度是向量, 只受旋转影响):
    ///   worldVelocity = trackingToWorldRotation · trackingVelocity
    ///
    /// 身体中心 (统一身体参考, 力矩参考点, 不是肩关节):
    ///   水平位置取头显, 高度由 bodyCenterOffset 决定 (以 XR Origin 地面为基准)
    ///   详见 ComputeBodyCenter。
    ///
    /// 本模块不做运动映射、不做动力学、不读 InputDevice。
    /// </summary>
    public class FlightCoordinateSystem
    {
        /// <summary>追踪空间参考系 (实验验证用, 默认 XROrigin)</summary>
        public TrackingSpaceReference reference = TrackingSpaceReference.XROrigin;

        /// <summary>XR Origin Transform (由协调器注入)</summary>
        public Transform xrOrigin;

        /// <summary>头部 Transform (由协调器注入, 供 HeadLocal 模式)</summary>
        public Transform headTransform;

        /// <summary>
        /// 控制侧头部稳定器 (可选, 由协调器注入)。非空时身体中心用稳定后的头部位置,
        /// 避免非自主晃动经力臂 r 传成力/力矩抖动。为 null 时退化为原始头显位置。
        /// </summary>
        public HeadStabilizer headStabilizer;

        /// <summary>身体中心离地高度 (m, 以 XR Origin 地面为基准, Inspector 可调, 默认约 1.1)</summary>
        public float bodyCenterOffset = 1.1f;

        /// <summary>
        /// 身体中心 = 头显的水平位置 + 沿 XR Origin up 方向抬高 bodyCenterOffset
        ///
        /// 为什么水平位置要取头显, 而不是 xrOrigin.position:
        ///   xrOrigin.position 是追踪空间原点 (房间地板上那个固定点)。玩家在房间里
        ///   走动或身体前倾时, 手到这根竖轴的水平距离会平白多出一段偏移, 于是
        ///   SpatialActionGate 的 |r_xz| 永远大于 exitDistance, 复原动作退不出 Active,
        ///   收手照样出力。身体中心必须跟着人走, 门控量的才是"手离身体多远"。
        ///
        ///   同时 r = hand - bodyCenter 也是力矩的力臂, 参考点跟着人走之后,
        ///   站位偏移不再产生虚假力矩。
        ///
        /// 头显不可用时退化为原来的 xrOrigin 行为。
        /// 供本类与协调器的 debug/gizmo 共用, 保证三处口径一致。
        /// </summary>
        public static Vector3 ComputeBodyCenter(Transform xrOrigin, Transform headTransform,
            float bodyCenterOffset)
        {
            return ComputeBodyCenter(xrOrigin,
                headTransform != null ? headTransform.position : (Vector3?)null,
                bodyCenterOffset);
        }

        /// <summary>
        /// 位置版本重载: 头部世界位置由调用方给出。稳定器存在时传入稳定后的位置,
        /// 即可让身体中心不再跟着非自主晃动抖。headWorldPosition 为 null 时
        /// 退化为「身体中心 = XR Origin + 抬升」。
        /// </summary>
        public static Vector3 ComputeBodyCenter(Transform xrOrigin, Vector3? headWorldPosition,
            float bodyCenterOffset)
        {
            Vector3 originPos = xrOrigin != null ? xrOrigin.position : Vector3.zero;
            Vector3 originUp = xrOrigin != null ? xrOrigin.up : Vector3.up;

            if (headWorldPosition == null)
                return originPos + originUp * bodyCenterOffset;

            // 头显相对 XR Origin 的偏移, 去掉沿 up 的竖直分量, 只保留水平分量
            Vector3 headOffset = headWorldPosition.Value - originPos;
            Vector3 horizontal = headOffset - originUp * Vector3.Dot(headOffset, originUp);

            return originPos + horizontal + originUp * bodyCenterOffset;
        }

        /// <summary>
        /// 取「用于控制」的头部世界位置: 有稳定器走稳定值, 否则原始头显位置,
        /// 无头部时返回 null (交给 ComputeBodyCenter 走退化分支)。
        /// 供本类的 ToWorld 与协调器的 debug/gizmo 共用, 保证各处口径一致。
        /// </summary>
        public static Vector3? GetControlHeadWorld(Transform xrOrigin, Transform headTransform,
            HeadStabilizer headStabilizer)
        {
            if (headTransform == null) return null;
            if (headStabilizer != null)
                return headStabilizer.GetStabilizedHeadPosition(xrOrigin, headTransform);
            return headTransform.position;
        }

        /// <inheritdoc cref="GetControlHeadWorld(Transform, Transform, HeadStabilizer)"/>
        public Vector3? GetControlHeadWorld()
            => GetControlHeadWorld(xrOrigin, headTransform, headStabilizer);

        /// <summary>
        /// 追踪空间原始数据 → 世界空间 FlightInput
        /// </summary>
        public FlightInput ToWorld(XRRawInput raw, float dt)
        {
            Quaternion trackingToWorld = GetTrackingToWorldRotation();

            FlightInput input = new FlightInput();

            // 速度转换 (向量, 只旋转)
            input.leftHandVelocity = trackingToWorld * raw.leftTrackingVelocity;
            input.rightHandVelocity = trackingToWorld * raw.rightTrackingVelocity;

            // 手部世界位置 (绝对位置)
            input.leftHandPosition = ToWorldPosition(raw.leftTrackingPosition);
            input.rightHandPosition = ToWorldPosition(raw.rightTrackingPosition);

            // 身体中心 = 头显水平位置 + 抬高 bodyCenterOffset (见 ComputeBodyCenter)
            // 用稳定后的头部位置: 非自主晃动不再经力臂 r 传成力/力矩抖动。
            input.bodyCenter = ComputeBodyCenter(xrOrigin, GetControlHeadWorld(), bodyCenterOffset);

            input.deltaTime = dt;

            return input;
        }

        /// <summary>
        /// 追踪空间手部位置 → 世界空间绝对位置
        /// </summary>
        private Vector3 ToWorldPosition(Vector3 trackingPosition)
        {
            switch (reference)
            {
                case TrackingSpaceReference.World:
                    return trackingPosition;

                case TrackingSpaceReference.XROrigin:
                    return (xrOrigin != null ? xrOrigin.position : Vector3.zero)
                         + (xrOrigin != null ? xrOrigin.rotation : Quaternion.identity) * trackingPosition;

                case TrackingSpaceReference.HeadLocal:
                    return (headTransform != null ? headTransform.position : Vector3.zero)
                         + (headTransform != null ? headTransform.rotation : Quaternion.identity) * trackingPosition;

                default:
                    return trackingPosition;
            }
        }

        /// <summary>
        /// 获取 Tracking Space → World Space 的旋转
        /// </summary>
        private Quaternion GetTrackingToWorldRotation()
        {
            switch (reference)
            {
                case TrackingSpaceReference.XROrigin:
                    return xrOrigin != null ? xrOrigin.rotation : Quaternion.identity;

                case TrackingSpaceReference.HeadLocal:
                    return headTransform != null ? headTransform.rotation : Quaternion.identity;

                case TrackingSpaceReference.World:
                default:
                    return Quaternion.identity;
            }
        }
    }
}
