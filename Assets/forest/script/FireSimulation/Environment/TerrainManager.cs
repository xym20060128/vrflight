using UnityEngine;

namespace FireSimulation
{
    public class TerrainManager : MonoBehaviour
    {
        [Header("=== 坡度计算 ===")]
        public LayerMask groundLayer = ~0;
        public float rayOriginYOffset = 10f;
        public float rayMaxDistance = 50f;

        private Terrain _cachedTerrain;

        float RayGround(Vector3 pos)
        {
            if (_cachedTerrain == null)
                _cachedTerrain = Terrain.activeTerrain;

            if (_cachedTerrain != null)
                return _cachedTerrain.SampleHeight(pos) + _cachedTerrain.transform.position.y;

            Vector3 origin = pos + Vector3.up * rayOriginYOffset;
            if (Physics.Raycast(origin, Vector3.down, out var hit, rayMaxDistance, groundLayer))
                return hit.point.y;
            return pos.y;
        }

        public float GetSlopeAngle(Vector3 sourcePos, Vector3 targetPos)
        {
            float srcY = RayGround(sourcePos);
            float tgtY = RayGround(targetPos);
            float deltaH = tgtY - srcY;
            float horizDist = new Vector2(targetPos.x - sourcePos.x, targetPos.z - sourcePos.z).magnitude;
            if (horizDist < 0.01f) return 0f;
            return Mathf.Atan(deltaH / horizDist);
        }

        public float GetGroundHeight(Vector3 worldPos) => RayGround(worldPos);
    }
}
