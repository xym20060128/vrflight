using UnityEngine;

namespace SwimFlight.Locomotion
{
    /// <summary>Protect the tracked head by moving the rig; never overwrite tracked camera pose.</summary>
    [DefaultExecutionOrder(2000)]
    public class HeadCollisionGuard : MonoBehaviour
    {
        public Transform head;
        [Min(0.01f)] public float radius = 0.15f;
        [Min(0.001f)] public float skin = 0.01f;
        public LayerMask collisionLayers = ~0;
        public bool useGroundFloor = true;
        public float groundHeight = 0f;
        private SphereCollider _sphere;
        private readonly Collider[] _overlaps = new Collider[64];
        private Vector3 _lastHeadPosition;
        private bool _hasHeadPosition;
        public Vector3 LastCorrection { get; private set; }

        public static HeadCollisionGuard Find(Transform rig)
            => rig != null ? rig.GetComponent<HeadCollisionGuard>() : null;

        public void Initialize()
        {
            if (head == null) return;
            _sphere = head.GetComponent<SphereCollider>();
            if (_sphere == null) _sphere = head.gameObject.AddComponent<SphereCollider>();
            _sphere.radius = radius;
            _sphere.center = Vector3.zero;
            // A query shape: transform-driven motion must use casts, not physics callbacks.
            _sphere.isTrigger = true;
        }

        private void OnEnable()
        {
            Initialize(); _hasHeadPosition = false;
            Application.onBeforeRender += ProtectTrackedHead;
        }
        private void OnDisable() { Application.onBeforeRender -= ProtectTrackedHead; }

        private bool IsEnvironment(Collider other)
            => other != null && !other.transform.IsChildOf(transform) && !other.isTrigger;

        public Vector3 ConstrainDisplacement(Vector3 start, Vector3 displacement)
        {
            Vector3 position = start;
            Vector3 remaining = displacement;
            for (int step = 0; step < 4 && remaining.sqrMagnitude > 1e-10f; step++)
            {
                float length = remaining.magnitude;
                RaycastHit nearest = default;
                bool found = false;
                foreach (RaycastHit hit in Physics.SphereCastAll(position, radius,
                    remaining / length, length + skin, collisionLayers, QueryTriggerInteraction.Ignore))
                {
                    if (!IsEnvironment(hit.collider) || Vector3.Dot(remaining, hit.normal) >= -1e-6f) continue;
                    if (!found || hit.distance < nearest.distance) { nearest = hit; found = true; }
                }
                if (!found) { position += remaining; break; }
                float travel = Mathf.Clamp(nearest.distance - skin, 0f, length);
                position += remaining / length * travel;
                remaining = Vector3.ProjectOnPlane(remaining * (1f - travel / length), nearest.normal);
            }
            // Catch tracking teleports or starting below a one-sided ground mesh.
            if (useGroundFloor) position.y = Mathf.Max(position.y, groundHeight + radius + skin);
            return position - start;
        }

        public Vector3 Move(Vector3 requested)
        {
            if (!enabled || head == null) { transform.position += requested; return requested; }
            Physics.SyncTransforms();
            RecoverPenetration();
            Vector3 actual = ConstrainDisplacement(head.position, requested);
            transform.position += actual;
            LastCorrection = actual - requested;
            _lastHeadPosition = head.position;
            _hasHeadPosition = true;
            return actual;
        }

        private void RecoverPenetration()
        {
            if (_sphere == null) Initialize();
            if (_sphere == null) return;
            for (int iteration = 0; iteration < 6; iteration++)
            {
                Physics.SyncTransforms();
                int count = Physics.OverlapSphereNonAlloc(head.position, radius, _overlaps,
                    collisionLayers, QueryTriggerInteraction.Ignore);
                bool corrected = false;
                for (int i = 0; i < count; i++)
                {
                    Collider other = _overlaps[i];
                    if (!IsEnvironment(other)) continue;
                    if (Physics.ComputePenetration(_sphere, head.position, head.rotation,
                        other, other.transform.position, other.transform.rotation, out Vector3 direction, out float distance))
                    {
                        transform.position += direction * (distance + skin);
                        corrected = true;
                    }
                }
                if (!corrected) break;
            }
            if (useGroundFloor && head.position.y < groundHeight + radius + skin)
                transform.position += Vector3.up * (groundHeight + radius + skin - head.position.y);
        }

        private void LateUpdate() { ProtectTrackedHead(); }

        [BeforeRenderOrder(10000)]
        private void ProtectTrackedHead()
        {
            if (head == null) return;
            Physics.SyncTransforms();
            // Tracked physical head motion can cross a surface after the flight update.
            if (_hasHeadPosition)
            {
                Vector3 desired = head.position - _lastHeadPosition;
                transform.position += ConstrainDisplacement(_lastHeadPosition, desired) - desired;
            }
            RecoverPenetration();
            _lastHeadPosition = head.position;
            _hasHeadPosition = true;
        }
    }
}
