using UnityEngine;
using System.Collections.Generic;

namespace FireSimulation
{
    public class ForestFireManager : MonoBehaviour
    {
        [Header("=== 子系统 ===")]
        public FireSpreadSystem spreadSystem;
        public WeatherManager weatherManager;
        public TerrainManager terrainManager;
        public FireExtinguishManager extinguishManager;

        [Header("=== 邻居构建 ===")]
        public float neighbourSearchRadius = 20f;
        public int maxNeighboursPerCell = 30;

        [Header("=== 初始火源 ===")]
        public List<FireCell> initialIgnitionCells = new();
        public bool autoIgniteOnStart = true;

        private List<FireCell> _allCells = new();

        // [诊断 BUG1] 每棵树被点燃次数统计
        private readonly Dictionary<FireCell, int> _igniteCounts = new();
        private float _diagTimer;
        private const float DiagInterval = 2f;

        /// <summary>
        /// [重置为默认] 强制邻居构建与初始点火参数回默认值。
        /// 防止 neighbourSearchRadius 被误改成 0（邻居图为空）或 autoIgniteOnStart 被关掉（不点火）。
        /// 将来调参请删除本方法。
        /// </summary>
        void Awake()
        {
            neighbourSearchRadius = 20f;
            maxNeighboursPerCell = 30;
            autoIgniteOnStart = true;

            Debug.Log($"[ForestFireManager] 参数已重置为默认 neighbourSearchRadius={neighbourSearchRadius} maxNeighboursPerCell={maxNeighboursPerCell} autoIgniteOnStart={autoIgniteOnStart}");
        }

        void Start()
        {
            ResolveRefs();
            DiscoverCells();

            // [诊断 BUG1] 检测是否存在多个 FireCell 共享相同/几乎相同的 WorldPosition
            DiagnoseDuplicatePositions();

            BuildNeighbours();
            WireSubsystems();
            SubscribeCellEvents();

            if (autoIgniteOnStart)
                foreach (var c in initialIgnitionCells)
                    if (c != null) spreadSystem.IgniteCell(c);

            // [诊断 BUG1] 统计 FireCell 组件数 vs 不同 GameObject 数（检测同一物体挂多个 FireCell）
            var seenGOs = new HashSet<GameObject>();
            int distinctGOs = 0;
            foreach (var c in _allCells) if (c != null && seenGOs.Add(c.gameObject)) distinctGOs++;
            if (_allCells.Count != distinctGOs)
                Debug.LogWarning($"[DuplicateComponent] FireCell组件={_allCells.Count} 个, 但只有 {distinctGOs} 个不同GameObject " +
                                 $"(有 {_allCells.Count - distinctGOs} 个多余组件挂在同一物体上)");
            Debug.Log($"[ForestFireManager] FireCell组件={_allCells.Count}, 不同GameObject={distinctGOs}, 邻居半径 {neighbourSearchRadius}m");
        }

        void ResolveRefs()
        {
            spreadSystem   ??= GetComponent<FireSpreadSystem>();
            weatherManager ??= GetComponent<WeatherManager>();
            terrainManager ??= GetComponent<TerrainManager>();
            extinguishManager ??= GetComponent<FireExtinguishManager>();
        }

        void DiscoverCells()
        {
            _allCells.Clear();
            _allCells.AddRange(FindObjectsOfType<FireCell>());
        }

        /// <summary>
        /// [诊断 BUG1] 检查不同 FireCell 是否存在相同或几乎相同的 WorldPosition。
        /// 若日志出现大量 [PositionDup]，说明火焰重叠的根因在场景数据（多棵树 transform.position 相同），
        /// 而不是 FireSpreadSystem / FireAreaSpread 的定位逻辑。
        /// </summary>
        void DiagnoseDuplicatePositions()
        {
            var seen = new Dictionary<(int x, int y, int z), FireCell>();
            foreach (var c in _allCells)
            {
                if (c == null) continue;
                var key = (
                    Mathf.RoundToInt(c.WorldPosition.x * 10f),
                    Mathf.RoundToInt(c.WorldPosition.y * 10f),
                    Mathf.RoundToInt(c.WorldPosition.z * 10f));
                if (seen.TryGetValue(key, out var other))
                {
                    bool sameGO = c.gameObject == other.gameObject;
                    Debug.LogWarning($"[PositionDup] {c.name}(id={c.GetInstanceID()}) 与 {other.name}(id={other.GetInstanceID()}) " +
                                     $"{(sameGO ? "[同一GameObject→重复FireCell组件!]" : "[不同GameObject→两树重叠]")} " +
                                     $"位置=({c.WorldPosition.x:F2},{c.WorldPosition.y:F2},{c.WorldPosition.z:F2})");
                }
                else
                {
                    seen[key] = c;
                }
            }
        }

        void BuildNeighbours()
        {
            // Fix #4: 空间哈希网格加速。cellSize = searchRadius，每个格子只查相邻 9 格。
            float cellSize = neighbourSearchRadius;
            var grid = new Dictionary<long, List<FireCell>>();

            foreach (var cell in _allCells)
            {
                if (cell == null) continue;
                int gx = Mathf.FloorToInt(cell.WorldPosition.x / cellSize);
                int gz = Mathf.FloorToInt(cell.WorldPosition.z / cellSize);
                long key = ((long)gx << 32) | (uint)gz;
                if (!grid.TryGetValue(key, out var list))
                    grid[key] = list = new List<FireCell>();
                list.Add(cell);
            }

            int links = 0;
            foreach (var cell in _allCells)
            {
                if (cell == null) continue;
                int gx = Mathf.FloorToInt(cell.WorldPosition.x / cellSize);
                int gz = Mathf.FloorToInt(cell.WorldPosition.z / cellSize);

                var cand = new List<(FireCell c, float d)>();
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        long key = ((long)(gx + dx) << 32) | (uint)(gz + dz);
                        if (!grid.TryGetValue(key, out var neighbors)) continue;
                        foreach (var o in neighbors)
                        {
                            // 排除自己（引用相等）+ 同一 GameObject 上的重复 FireCell 组件（GameObject 相等）
                            if (o == cell || o.gameObject == cell.gameObject) continue;
                            float d = Vector3.Distance(cell.WorldPosition, o.WorldPosition);
                            if (d <= neighbourSearchRadius)
                                cand.Add((o, d));
                        }
                    }
                }

                cand.Sort((a, b) => a.d.CompareTo(b.d));
                int take = Mathf.Min(cand.Count, maxNeighboursPerCell);
                var nb = new List<FireCell>(take);
                for (int k = 0; k < take; k++) nb.Add(cand[k].c);
                cell.SetNeighbours(nb);
                links += nb.Count;
            }

            Debug.Log($"[ForestFireManager] 邻居图: {links} 边");
        }

        void WireSubsystems()
        {
            if (spreadSystem != null)
            {
                spreadSystem.weatherManager ??= weatherManager;
                spreadSystem.terrainManager ??= terrainManager;
            }
            if (extinguishManager != null)
                extinguishManager.spreadSystem ??= spreadSystem;
        }

        void SubscribeCellEvents()
        {
            foreach (var cell in _allCells)
            {
                if (cell == null) continue;
                cell.OnStateChanged += (c, old, New) =>
                {
                    if (New == FireCellState.Burning)
                    {
                        int n = _igniteCounts.TryGetValue(c, out var v) ? v + 1 : 1;
                        _igniteCounts[c] = n;
                        if (n == 1)
                            Debug.Log($"[BurnStart] Cell={c.name} WorldPosition=({c.WorldPosition.x:F2},{c.WorldPosition.y:F2},{c.WorldPosition.z:F2}) (点燃 #1)");
                        else
                            Debug.LogWarning($"[ReIgnite] Cell={c.name} 第 {n} 次被点燃 WorldPosition=({c.WorldPosition.x:F2},{c.WorldPosition.y:F2},{c.WorldPosition.z:F2})");
                    }
                    else if (New == FireCellState.Idle)
                    {
                        // 被喷水扑灭 → 恢复绿色
                        c.treeBurnReceiver?.ForceExtinguish();
                        int hist = _igniteCounts.TryGetValue(c, out var v) ? v : 0;
                        Debug.Log($"[🧯] {c.name} 已扑灭 (历史点燃 {hist} 次)");
                    }
                    else if (New == FireCellState.Burned)
                    {
                        // 燃料耗尽 → 完全烧焦
                        c.treeBurnReceiver?.SetBurnProgress(1f);
                        int hist = _igniteCounts.TryGetValue(c, out var v) ? v : 0;
                        Debug.Log($"[💀] {c.name} 已烧尽 (历史点燃 {hist} 次)");
                    }
                };
            }
        }

        void Update()
        {
            _diagTimer += Time.deltaTime;
            if (_diagTimer < DiagInterval) return;
            _diagTimer = 0f;

            int burning = spreadSystem != null ? spreadSystem.GetBurningCount() : 0;
            int reIgnited = 0;
            int maxIgnites = 0;
            FireCell worst = null;
            foreach (var kv in _igniteCounts)
            {
                if (kv.Value >= 2) reIgnited++;
                if (kv.Value > maxIgnites) { maxIgnites = kv.Value; worst = kv.Key; }
            }

            Debug.Log($"[Diag-Ignite] 总FireCell={_allCells.Count} 当前Burning={burning} " +
                      $"累计被点燃过的树={_igniteCounts.Count} 复燃树(≥2次)={reIgnited} " +
                      (worst != null ? $"最高复燃={worst.name}(x{maxIgnites})" : ""));
        }

        public void IgniteCell(FireCell cell) => spreadSystem?.IgniteCell(cell);
    }
}
