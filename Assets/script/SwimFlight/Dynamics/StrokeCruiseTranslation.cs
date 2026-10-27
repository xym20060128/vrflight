using System;
using UnityEngine;

namespace SwimFlight.Dynamics
{
    public enum CruiseBrakeButton { LeftSecondary, RightStickClick, LeftStickClick }
    public enum CruiseStrokePhase { AwaitRecovery, Ready, Stroking }

    /// <summary>Stroke events adjust a world-space target velocity. No Transform writes or force integration.</summary>
    [Serializable]
    public sealed class StrokeCruiseSettings
    {
        [Header("划手巡航（保守初值，需头显实测）")]
        [Min(0.001f), Tooltip("有效切向划动的最小位移（米），过滤短促抖动。")]
        public float minimumStrokeDistance = 0.08f;
        [Min(0.01f), Tooltip("进入划动的切向轴速度（m/s）。")]
        public float startSpeed = 0.35f;
        [Min(0f), Tooltip("结束划动的速度（m/s），必须低于开始值。")]
        public float endSpeed = 0.12f;
        [Min(0.01f), Tooltip("低于结束速度或改变方向持续多久才结算（秒）。")]
        public float endHoldTime = 0.10f;
        [Min(0.01f), Tooltip("手回到空间门控回收区持续多久才重新允许触发（秒）。恢复时不产生增量。")]
        public float recoveryTime = 0.08f;
        [Min(0.1f), Tooltip("单动作最长累计时间，超时只结算一次，之后必须收手。")]
        public float maximumStrokeTime = 1.2f;
        [Min(0.01f), Tooltip("全局两次巡航增量的最短间隔（秒），限制交替划手增速。")]
        public float minimumStrokeInterval = 0.35f;
        [Range(0.01f, 0.4f), Tooltip("双手开始时间差在此窗口内视作同步动作；用较大的动作量，不相加。")]
        public float mergeWindow = 0.12f;
        [Range(0.5f, 1f), Tooltip("前后/上下分量占切向速度的最小比例，排除侧向动作。")]
        public float axisDominance = 0.7f;
        [Min(0f), Tooltip("有效位移 × 此系数 = 单次目标速度增量（1/s）。")]
        public float distanceToSpeed = 2f;
        [Min(0f), Tooltip("一次划动的最大速度增量（m/s）。")]
        public float maximumStrokeIncrement = 0.45f;
        [Min(0.01f)] public float maximumHorizontalSpeed = 2f;
        [Min(0.01f)] public float maximumVerticalSpeed = 0.8f;
        [Min(0.01f), Tooltip("水平和竖直合成速度上限（m/s）。")]
        public float maximumCombinedSpeed = 2.1f;
        [Min(0.01f), Tooltip("实际速度追踪目标的加速上限（m/s²）。")]
        public float acceleration = 0.8f;
        [Min(0.01f), Tooltip("实际速度追踪目标的减速上限（m/s²）。")]
        public float deceleration = 1.5f;
        [Min(0.01f), Tooltip("按住刹车时目标速度趋零的速率（m/s²）。")]
        public float brakeRate = 1.2f;
        [Min(0.01f), Tooltip("松开 Trigger 后竖直目标速度趋零速率（m/s²）。")]
        public float verticalReleaseRate = 0.8f;
        [Min(0f), Tooltip("目标和实际速度都很小时归零（m/s）。")]
        public float stopThreshold = 0.02f;
        [Tooltip("独立按钮；默认左手 Y。可选左右摇杆按下。避开 X 切模式、A 喷水、右 B 校准/调试以及 Trigger/Grip。")]
        public CruiseBrakeButton brakeButton = CruiseBrakeButton.LeftSecondary;
        public bool enableDebugLog;
        [Min(0.1f)] public float debugInterval = 0.5f;

        public void Validate()
        {
            minimumStrokeDistance = Mathf.Max(0.001f, minimumStrokeDistance);
            startSpeed = Mathf.Max(0.02f, startSpeed);
            endSpeed = Mathf.Clamp(endSpeed, 0f, startSpeed - 0.01f);
            endHoldTime = Mathf.Max(0.01f, endHoldTime);
            recoveryTime = Mathf.Max(0.01f, recoveryTime);
            maximumStrokeTime = Mathf.Max(endHoldTime + 0.01f, maximumStrokeTime);
            minimumStrokeInterval = Mathf.Max(0.01f, minimumStrokeInterval);
            mergeWindow = Mathf.Clamp(mergeWindow, 0.01f, 0.4f);
            axisDominance = Mathf.Clamp(axisDominance, 0.5f, 1f);
            distanceToSpeed = Mathf.Max(0f, distanceToSpeed);
            maximumStrokeIncrement = Mathf.Max(0f, maximumStrokeIncrement);
            maximumHorizontalSpeed = Mathf.Max(0.01f, maximumHorizontalSpeed);
            maximumVerticalSpeed = Mathf.Max(0.01f, maximumVerticalSpeed);
            maximumCombinedSpeed = Mathf.Max(0.01f, maximumCombinedSpeed);
            acceleration = Mathf.Max(0.01f, acceleration);
            deceleration = Mathf.Max(0.01f, deceleration);
            brakeRate = Mathf.Max(0.01f, brakeRate);
            verticalReleaseRate = Mathf.Max(0.01f, verticalReleaseRate);
            stopThreshold = Mathf.Max(0f, stopThreshold);
            debugInterval = Mathf.Max(0.1f, debugInterval);
        }
    }

    public sealed class StrokeCruiseTranslation
    {
        private sealed class Hand
        {
            public CruiseStrokePhase phase;
            public Vector3 axis;
            public float distance, startTime, endTimer, recoveryTimer;
            public void Reset()
            {
                phase = CruiseStrokePhase.AwaitRecovery;
                axis = Vector3.zero;
                distance = endTimer = recoveryTimer = startTime = 0f;
            }
        }

        private struct Stroke
        {
            public Vector3 delta;
            public float start;
            public int hand;
        }

        private readonly Hand _left = new Hand(), _right = new Hand();
        private Stroke _pending;
        private bool _hasPending;
        private float _pendingDeadline, _time, _lastEventTime = -100f;
        public StrokeCruiseSettings Settings = new StrokeCruiseSettings();
        public Vector3 TargetVelocity { get; private set; }
        public Vector3 Velocity { get; private set; }
        public Vector3 LastIncrement { get; private set; }
        public int EventCount { get; private set; }
        public bool BrakeHeld { get; private set; }
        public bool VerticalAllowed { get; private set; }
        public CruiseStrokePhase LeftPhase => _left.phase;
        public CruiseStrokePhase RightPhase => _right.phase;

        public void Reset()
        {
            CancelStrokes();
            TargetVelocity = Velocity = LastIncrement = Vector3.zero;
            _time = 0f;
            _lastEventTime = -100f;
            EventCount = 0;
            BrakeHeld = VerticalAllowed = false;
        }

        public void CancelStrokes()
        {
            _left.Reset(); _right.Reset(); _hasPending = false;
        }

        /// <summary>Called once per input sample, not per physics substep. Gate and tangent come from the mapper.</summary>
        public void Sample(Vector3 leftTangent, Vector3 rightTangent, bool leftActive, bool rightActive,
            Vector3 rigForward, bool verticalAllowed, bool brake, bool turning, float dt)
        {
            Settings.Validate();
            if (!Finite(dt) || dt <= 0f) return;
            _time += dt;
            VerticalAllowed = verticalAllowed;
            BrakeHeld = brake;
            if (turning)
            {
                // Existing Grip rule: immediately stop translation, turn in place,
                // then require fresh strokes after release. Never resume stored cruise.
                TargetVelocity = Velocity = Vector3.zero;
                CancelStrokes();
                return;
            }
            if (brake) { CancelStrokes(); return; }
            if (!verticalAllowed)
            {
                if (_left.phase == CruiseStrokePhase.Stroking && Mathf.Abs(_left.axis.y) > 0.5f) _left.Reset();
                if (_right.phase == CruiseStrokePhase.Stroking && Mathf.Abs(_right.axis.y) > 0.5f) _right.Reset();
                if (_hasPending && Mathf.Abs(_pending.delta.y) > 0f) _hasPending = false;
            }
            Vector3 forward = Vector3.ProjectOnPlane(rigForward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.5f) forward = Vector3.forward;
            SampleHand(_left, 0, leftTangent, leftActive, forward, dt);
            SampleHand(_right, 1, rightTangent, rightActive, forward, dt);
            if (_hasPending && _time >= _pendingDeadline)
            {
                Hand other = _pending.hand == 0 ? _right : _left;
                // A synchronous other hand can finish later; wait for its settlement,
                // bounded by maximumStrokeTime. This avoids duration-dependent doubling.
                bool synchronousInProgress = other.phase == CruiseStrokePhase.Stroking
                    && Mathf.Abs(other.startTime - _pending.start) <= Settings.mergeWindow;
                if (!synchronousInProgress) CommitPending();
            }
        }

        private void SampleHand(Hand hand, int index, Vector3 tangent, bool active, Vector3 forward, float dt)
        {
            if (!Finite(tangent)) { hand.Reset(); return; }
            if (!active)
            {
                if (hand.phase == CruiseStrokePhase.Stroking) Finish(hand, index);
                hand.recoveryTimer += dt;
                if (hand.recoveryTimer >= Settings.recoveryTime) hand.phase = CruiseStrokePhase.Ready;
                return;
            }
            hand.recoveryTimer = 0f;
            if (hand.phase == CruiseStrokePhase.AwaitRecovery) return;
            if (hand.phase == CruiseStrokePhase.Ready)
            {
                float horizontal = -Vector3.Dot(tangent, forward);
                float vertical = VerticalAllowed ? -tangent.y : 0f;
                float strongest = Mathf.Max(Mathf.Abs(horizontal), Mathf.Abs(vertical));
                if (strongest < Settings.startSpeed || strongest < tangent.magnitude * Settings.axisDominance) return;
                hand.axis = Mathf.Abs(horizontal) >= Mathf.Abs(vertical)
                    ? forward * Mathf.Sign(horizontal) : Vector3.up * Mathf.Sign(vertical);
                hand.phase = CruiseStrokePhase.Stroking;
                hand.startTime = _time;
                hand.distance = hand.endTimer = 0f;
            }
            float along = -Vector3.Dot(tangent, hand.axis);
            if (along > Settings.endSpeed)
            {
                // Never integrate return motion or a velocity spike without a bound.
                hand.distance += Mathf.Min(along, 3f) * dt;
                hand.endTimer = 0f;
            }
            else hand.endTimer += dt;
            if (hand.endTimer >= Settings.endHoldTime || _time - hand.startTime >= Settings.maximumStrokeTime)
                Finish(hand, index);
        }

        private void Finish(Hand hand, int index)
        {
            if (hand.distance >= Settings.minimumStrokeDistance)
            {
                var stroke = new Stroke { hand = index, start = hand.startTime,
                    delta = hand.axis * Mathf.Min(Settings.maximumStrokeIncrement, hand.distance * Settings.distanceToSpeed) };
                if (_hasPending && _pending.hand != index &&
                    Mathf.Abs(stroke.start - _pending.start) <= Settings.mergeWindow)
                {
                    // Stronger hand wins (first completed wins ties), never sum synchronous strokes.
                    if (stroke.delta.sqrMagnitude > _pending.delta.sqrMagnitude) _pending.delta = stroke.delta;
                    CommitPending();
                }
                else
                {
                    if (_hasPending) CommitPending();
                    _pending = stroke;
                    _pendingDeadline = _time + Settings.mergeWindow;
                    _hasPending = true;
                }
            }
            hand.Reset();
        }

        private void CommitPending()
        {
            if (!_hasPending) return;
            _hasPending = false;
            if (BrakeHeld || _time - _lastEventTime < Settings.minimumStrokeInterval) return;
            Vector3 delta = _pending.delta;
            if (!VerticalAllowed) delta.y = 0f;
            if (delta.sqrMagnitude < 1e-10f) return;
            Vector3 axis = delta.normalized;
            float current = Vector3.Dot(TargetVelocity, axis);
            float amount = delta.magnitude;
            // An opposite event may reach zero, but cannot cross it. A later recovered
            // stroke is required to start the opposite cruise direction.
            if (current < 0f) amount = Mathf.Min(amount, -current);
            Vector3 before = TargetVelocity;
            TargetVelocity = Limit(TargetVelocity + axis * amount);
            LastIncrement = TargetVelocity - before;
            EventCount++;
            _lastEventTime = _time;
        }

        /// <summary>Acceleration bounded tracking of target; intentionally no passive drag.</summary>
        public void Advance(float dt)
        {
            if (!Finite(dt) || dt <= 0f) return;
            if (BrakeHeld) TargetVelocity = Vector3.MoveTowards(TargetVelocity, Vector3.zero, Settings.brakeRate * dt);
            if (!VerticalAllowed)
            {
                Vector3 target = TargetVelocity;
                target.y = Mathf.MoveTowards(target.y, 0f, Settings.verticalReleaseRate * dt);
                TargetVelocity = target;
            }
            TargetVelocity = Limit(TargetVelocity);
            if (TargetVelocity.magnitude < Settings.stopThreshold) TargetVelocity = Vector3.zero;
            Vector3 difference = TargetVelocity - Velocity;
            bool slowing = Vector3.Dot(difference, Velocity) < 0f;
            Velocity = Vector3.MoveTowards(Velocity, TargetVelocity,
                (slowing ? Settings.deceleration : Settings.acceleration) * dt);
            Velocity = Limit(Velocity);
            // MoveTowards reaches an exactly zero target in finite time. Snapping
            // again after this step could exceed the configured deceleration budget.
        }

        public void RemoveIntoSurface(Vector3 normal)
        {
            if (!Finite(normal) || normal.sqrMagnitude < 1e-8f) return;
            normal.Normalize();
            float actualInward = Vector3.Dot(Velocity, normal);
            float targetInward = Vector3.Dot(TargetVelocity, normal);
            if (actualInward < 0f) Velocity -= normal * actualInward;
            if (targetInward < 0f) TargetVelocity -= normal * targetInward;
            CancelStrokes();
        }

        private Vector3 Limit(Vector3 velocity)
        {
            Vector3 horizontal = Vector3.ClampMagnitude(new Vector3(velocity.x, 0f, velocity.z), Settings.maximumHorizontalSpeed);
            return Vector3.ClampMagnitude(new Vector3(horizontal.x,
                Mathf.Clamp(velocity.y, -Settings.maximumVerticalSpeed, Settings.maximumVerticalSpeed), horizontal.z),
                Settings.maximumCombinedSpeed);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
