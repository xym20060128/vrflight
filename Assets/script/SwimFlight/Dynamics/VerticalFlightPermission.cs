using UnityEngine;

namespace SwimFlight.Dynamics
{
    /// <summary>Trigger intent and world-vertical permission; stepped only by the active motion integrator.</summary>
    [System.Serializable]
    public class VerticalFlightPermission
    {
        [Tooltip("按住任意 Trigger 才允许划动升降。关闭恢复原有三维划动，不留下新增阻尼。初值待头显预实验验证。")]
        public bool enabled = true;
        [Range(0.01f, 1f), Tooltip("Trigger 模拟量按下阈值，必须高于松开阈值。初值 0.6 待验证。")]
        public float pressThreshold = 0.6f;
        [Range(0f, 0.99f), Tooltip("Trigger 模拟量松开阈值。按住后，中间区间保持状态。初值 0.4 待验证。")]
        public float releaseThreshold = 0.4f;
        [Min(0.01f), Tooltip("垂直权限完整开启/关闭的时间（秒），同时平滑退出/进入额外垂直阻尼。初值 0.2 待验证。")]
        public float transitionTime = 0.2f;
        [Min(0f), Tooltip("松开后的额外垂直阻尼 N/(m/s)，只作用于世界 Y。越大越快收住，仍有短暂位移。初值 2 待验证。")]
        public float extraVerticalDamping = 2f;

        public bool LeftHeld { get; private set; }
        public bool RightHeld { get; private set; }
        public bool Held => LeftHeld || RightHeld;
        public float Weight => enabled ? Smooth(_phase) : 1f;
        public float StepWeight { get; private set; }
        public float StepDamping => enabled ? Mathf.Max(0f, extraVerticalDamping) * (1f - StepWeight) : 0f;
        [System.NonSerialized] private float _phase;

        public void Reset()
        {
            LeftHeld = RightHeld = false;
            _phase = 0f;
            StepWeight = 0f;
        }

        public void SetInput(bool leftAvailable, float leftTrigger, bool rightAvailable, float rightTrigger)
        {
            if (!enabled) { Reset(); return; }
            float release = Mathf.Clamp(releaseThreshold, 0f, 0.99f);
            float press = Mathf.Clamp(pressThreshold, release + 0.01f, 1f);
            LeftHeld = ReadHeld(LeftHeld, leftAvailable, leftTrigger, press, release);
            RightHeld = ReadHeld(RightHeld, rightAvailable, rightTrigger, press, release);
        }

        public static bool ReadHeld(bool previous, bool available, float value, float press, float release)
        {
            if (!available || float.IsNaN(value) || float.IsInfinity(value)) return false;
            return previous ? value > release : value >= press;
        }

        // Smoothstep has zero slope at fully open/closed; integrate its area over the
        // actual substep, including any time remaining after reaching an endpoint.
        public float BeginStep(float dt)
        {
            if (!enabled) { Reset(); return StepWeight = 1f; }
            if (dt <= 0f) return StepWeight = Weight;
            float duration = Mathf.Max(0.01f, transitionTime);
            float previous = _phase;
            _phase = Mathf.MoveTowards(previous, Held ? 1f : 0f, dt / duration);
            float distance = _phase - previous;
            if (Mathf.Abs(distance) < 1e-7f) return StepWeight = Weight;
            float movingTime = Mathf.Min(dt, Mathf.Abs(distance) * duration);
            float movingAverage = (Primitive(_phase) - Primitive(previous)) / distance;
            StepWeight = Mathf.Clamp01((movingTime * movingAverage + (dt - movingTime) * Weight) / dt);
            return StepWeight;
        }

        public Vector3 FilterForce(Vector3 worldForce)
        {
            if (enabled) worldForce.y *= StepWeight;
            return worldForce;
        }

        // Exact decay of this extra damping alone: always retains the candidate's sign.
        public float DampVertical(float candidateVelocity, float mass, float dt)
            => candidateVelocity * Mathf.Exp(-StepDamping * Mathf.Max(0f, dt) / Mathf.Max(1e-4f, mass));

        private static float Smooth(float p) => p * p * (3f - 2f * p);
        private static float Primitive(float p) => p * p * p * (1f - 0.5f * p);
    }
}
