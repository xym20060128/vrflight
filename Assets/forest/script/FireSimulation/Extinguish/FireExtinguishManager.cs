using UnityEngine;
using System.Collections.Generic;

namespace FireSimulation
{
    /// <summary>
    /// 灭火协调器 — 接收喷水输入，对范围内 FireCell 降温。
    ///
    /// 职责：
    ///   - 接收 FireCameraMove 的喷水命中位置
    ///   - 查找喷水范围内的 FireCell
    ///   - 调用 cell.RemoveHeat(amount)，由 FireSpreadSystem 后续判断是否熄灭
    ///
    /// 不负责：
    ///   - 灭火粒子特效 → FireAreaSpread (已有 waterParticleObj)
    ///   - 火焰熄灭判断 → FireSpreadSystem (T < ignitionThreshold → Extinguish)
    /// </summary>
    public class FireExtinguishManager : MonoBehaviour
    {
        [Header("=== 喷水参数 ===")]
        [Tooltip("喷水作用半径 (米)")]
        public float sprayRadius = 2.0f;

        [Tooltip("每秒降温量 (摄氏度/秒)。默认 1500，约 0.25 秒可扑灭一棵树。")]
        public float coolingPerSecond = 1500f;

        [Header("=== 直升机喷水 (更高效) ===")]
        [Tooltip("直升机喷水半径 (米)，比地面大")]
        public float helicopterSprayRadius = 5f;
        [Tooltip("直升机每秒降温量，比地面高")]
        public float helicopterCoolingPerSecond = 4000f;

        [Header("=== 引用 ===")]
        [Tooltip("FireSpreadSystem 引用，用于获取燃烧 Cell 的快捷查找")]
        public FireSpreadSystem spreadSystem;

        // ═══════════════════════════════════════════
        // 公共接口
        // ═══════════════════════════════════════════

        /// <summary>
        /// 在指定位置喷水。由 FireCameraMove 每帧调用。
        /// </summary>
        /// <param name="worldPosition">水枪命中的世界坐标</param>
        /// <param name="deltaTime">本帧时间增量</param>
        /// <returns>本次影响到的 FireCell 数量</returns>
        public int SprayAt(Vector3 worldPosition, float deltaTime)
        {
            float coolingThisFrame = coolingPerSecond * deltaTime;

            // 物理查找范围内所有 FireCell
            Collider[] hits = Physics.OverlapSphere(worldPosition, sprayRadius);
            int affectedCount = 0;

            foreach (var col in hits)
            {
                FireCell cell = col.GetComponent<FireCell>();
                if (cell == null)
                {
                    // 检查父物体上是否有 FireCell（树的结构可能是 collider 在子物体上）
                    cell = col.GetComponentInParent<FireCell>();
                }
                if (cell == null) continue;
                if (cell.CurrentState != FireCellState.Burning) continue;

                // 距离衰减：越远降温越少
                float dist = DistanceToTreeAxis(worldPosition, cell);
                float attenuation = 1f - (dist / sprayRadius);
                attenuation = Mathf.Clamp01(attenuation);

                cell.RemoveHeat(coolingThisFrame * attenuation);
                cell.MarkExternalCooling();

                // 不自行 Extinguish — 统一由 FireSpreadSystem.Tick Phase 3 处理
                // Phase 2 看到 _hasExternalCooling → 跳过温度覆写
                // Phase 3 看到 T < ignitionThreshold → Extinguish + 免疫 + 火锋清理

                affectedCount++;
            }

            return affectedCount;
        }

        /// <summary>
        /// 直升机喷水 — 比地面灭火范围更大、降温更快。
        /// 由 HelicopterSprayer 每帧调用。
        /// </summary>
        /// <returns>本次影响到的 FireCell 数量</returns>
        public int SprayFromHelicopter(Vector3 worldPosition, float deltaTime)
        {
            float coolingThisFrame = helicopterCoolingPerSecond * deltaTime;
            Collider[] hits = Physics.OverlapSphere(worldPosition, helicopterSprayRadius);
            int affectedCount = 0;

            foreach (var col in hits)
            {
                FireCell cell = col.GetComponent<FireCell>();
                if (cell == null) cell = col.GetComponentInParent<FireCell>();
                if (cell == null) continue;
                if (cell.CurrentState != FireCellState.Burning) continue;

                float dist = DistanceToTreeAxis(worldPosition, cell);
                float attenuation = 1f - (dist / helicopterSprayRadius);
                attenuation = Mathf.Clamp01(attenuation);

                cell.RemoveHeat(coolingThisFrame * attenuation);
                cell.MarkExternalCooling();
                // 不自行 Extinguish — 统一由 Tick Phase 3 处理

                affectedCount++;
            }

            return affectedCount;
        }

        /// <summary>
        /// 喷水点到"树干竖轴线段"的距离 (不是到 FireCell.WorldPosition 那个点)。
        ///
        /// 为什么不能直接用 Vector3.Distance(hit, cell.WorldPosition):
        ///   cell.WorldPosition = transform.position, 是树的 pivot, 基本在树底。
        ///   而水枪射线打中的是树干/树冠, 命中点往往在 pivot 上方 5~15m。
        ///   于是 dist 远大于 sprayRadius(2m), attenuation 被 Clamp01 压成 0,
        ///   RemoveHeat(0) —— 水打上去了, 温度一点没降, 表现就是"水灭不了火"。
        ///
        ///   改成量到树的竖轴线段 [TreeBaseY, TreeBaseY + treeHeight] 之后,
        ///   打树干任意高度都算贴脸命中, 衰减接近 1。
        /// </summary>
        private static float DistanceToTreeAxis(Vector3 point, FireCell cell)
        {
            Vector3 axis = cell.WorldPosition;
            float baseY = cell.TreeBaseY;
            float height = cell.treeHeight > 0f ? cell.treeHeight : 10f;

            // 把喷水点的高度夹到树的高度区间内, 得到轴线上最近的点
            float nearestY = Mathf.Clamp(point.y, baseY, baseY + height);
            Vector3 nearest = new Vector3(axis.x, nearestY, axis.z);

            return Vector3.Distance(point, nearest);
        }

        /// <summary>
        /// 检查某位置是否在现有喷水可覆盖范围内 (供 UI 提示)
        /// </summary>
        public bool IsInSprayRange(Vector3 worldPosition)
        {
            // 简单检查范围内有没有 Burning 的 FireCell
            Collider[] hits = Physics.OverlapSphere(worldPosition, sprayRadius);
            foreach (var col in hits)
            {
                FireCell cell = col.GetComponent<FireCell>();
                if (cell != null && cell.CurrentState == FireCellState.Burning)
                    return true;
            }
            return false;
        }

        // ═══════════════════════════════════════════
        // 调试
        // ═══════════════════════════════════════════

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position, sprayRadius);
        }
    }
}
