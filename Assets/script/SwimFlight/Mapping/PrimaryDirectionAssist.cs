using UnityEngine;
using SwimFlight.Core;

namespace SwimFlight.Mapping
{
    /// <summary>World-up net thrust assistance. Never edits hand forces, torque or velocity.</summary>
    [System.Serializable]
    public class PrimaryDirectionAssist
    {
        [Tooltip("主方向辅助开关。只衰减净推力次要分量，不制动已有速度。默认值待预实验验证。")]
        public bool enabled = true;
        [Range(1f, 40f), Tooltip("偏离水平或竖直主方向的辅助范围（度）。范围前半段保持最强抑制，后半段平滑恢复斜向比例。初值 35° 待验证。")]
        public float dominanceAngle = 35f;
        [Range(0f, 1f), Tooltip("次要分量最大衰减比例。0 不衰减，1 完全衰减；不补偿主分量。初值 0.95 待验证。")]
        public float suppressionStrength = 0.95f;
        [Range(0f, 0.5f), Tooltip("权重平滑时间常数（秒），越大切换越慢。初值 0.06 待验证。")]
        public float smoothingTime = 0.06f;

        public Vector3 InputForce { get; private set; }
        public Vector3 OutputForce { get; private set; }
        public float HorizontalSuppression { get; private set; }
        public float VerticalSuppression { get; private set; }
        [System.NonSerialized] private float _horizontalWeight;
        [System.NonSerialized] private float _verticalWeight;

        public void Reset()
        {
            InputForce = OutputForce = Vector3.zero;
            HorizontalSuppression = VerticalSuppression = 0f;
            _horizontalWeight = _verticalWeight = 0f;
        }

        public Vector3 Filter(Vector3 force, float dt)
        {
            InputForce = force;
            if (!enabled)
            {
                HorizontalSuppression = VerticalSuppression = 0f;
                _horizontalWeight = _verticalWeight = 0f;
                return OutputForce = force;
            }
            Vector3 horizontal = new Vector3(force.x, 0f, force.z);
            float h = horizontal.magnitude;
            float v = Mathf.Abs(force.y);
            // Near-zero fade avoids unstable directional weights for cancelling hands.
            float reliability = force.sqrMagnitude / (force.sqrMagnitude + 0.0001f);
            float angle = Mathf.Atan2(v, h) * Mathf.Rad2Deg;
            float range = Mathf.Clamp(dominanceAngle, 1f, 40f);
            float strength = Mathf.Clamp01(suppressionStrength) * reliability;
            // A broad near-axis plateau rejects small cross-axis errors strongly.
            // The outer half of the range continuously restores intentional diagonals.
            float verticalTarget = strength * (1f - Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(range * 0.5f, range, angle)));
            float horizontalTarget = strength * (1f - Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(range * 0.5f, range, 90f - angle)));
            float alpha = smoothingTime <= 0f ? 1f : 1f - Mathf.Exp(-Mathf.Max(0f, dt) / smoothingTime);
            _horizontalWeight = Mathf.Lerp(_horizontalWeight, horizontalTarget, alpha);
            _verticalWeight = Mathf.Lerp(_verticalWeight, verticalTarget, alpha);
            // Smooth support masks prevent old weights attenuating a new major component.
            float horizontalMask = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(45f, 90f - range, angle));
            float verticalMask = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(range, 45f, angle));
            HorizontalSuppression = _horizontalWeight * horizontalMask;
            VerticalSuppression = _verticalWeight * verticalMask;
            // Only weights have memory: zero input always gives zero thrust.
            OutputForce = horizontal * (1f - HorizontalSuppression)
                + Vector3.up * force.y * (1f - VerticalSuppression);
            return OutputForce;
        }

        public void Apply(ref FlightCommand command, float dt)
        {
            command.translationForce = Filter(command.hasTranslationForce
                ? command.translationForce : command.leftForce + command.rightForce, dt);
            command.hasTranslationForce = true;
        }
    }
}
