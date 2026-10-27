using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 火灾视觉效果 — 优化的粒子渲染层。
///
/// 性能策略 (按优先级):
///   1. 视锥剔除:    屏幕外的树不生成粒子
///   2. 距离 LOD:    近(0-60m)全粒子, 中(60-150m)减半, 远(150m+)发光代替
///   3. 对象池:      预热复用, 零运行时 Instantiate/Destroy
///   4. 持久粒子:    每树维持固定粒子系统, 只更新位置/参数
///   5. 火锋预算:    每帧上限, 防止爆发态粒子海
/// </summary>
public class FireAreaSpread : MonoBehaviour
{
    [Header("=== 预制体 ===")]
    public GameObject firePrefab;

    [Header("=== 树冠火 ===")]
    [Tooltip("近处每棵树最多粒子系统数")]
    public int maxParticlesPerTree = 2;
    public float yOffset = 0.5f;
    [Tooltip("火焰粒子在树干上的最低分布比例 (0=树底, 1=树顶)。" +
             "原固定 0.3 导致火刚爬升时新旧粒子挤在一起，改为 0 让粒子沿树干拉开上下间距")]
    [Range(0f, 1f)]
    public float flameBottomRatio = 0f;
    [Tooltip("火焰粒子尺寸缩放 (1=预制体原始尺寸，不缩小)。")]
    [Range(0.05f, 1f)]
    public float flameSizeScale = 1.0f;

    [Header("=== 距离 LOD ===")]
    [Tooltip("全粒子距离 (m)")]
    public float fullParticleDistance = 60f;
    [Tooltip("减半粒子距离 (m)")]
    public float halfParticleDistance = 150f;
    [Tooltip("超过此距离完全不生成粒子 (靠材质发光代替)")]
    public float maxRenderDistance = 250f;

    [Header("=== 火锋 ===")]
    public float frontParticleLifetime = 1f;
    [Tooltip("火锋粒子间距 (m)")]
    public float frontParticleSpacing = 3f;
    [Tooltip("每帧最多生成的火锋粒子数")]
    public int maxFrontParticlesPerFrame = 40;

    [Header("=== 预算 ===")]
    [Tooltip("更新间隔 (秒)")]
    public float updateInterval = 0.3f;
    [Tooltip("池最大容量")]
    public int poolCapacity = 150;

    // ── 对象池 ──
    private Queue<GameObject> _pool = new();
    private List<GameObject> _activeParticles = new();

    // ── 每棵树追踪 ──
    private Dictionary<FireSimulation.FireCell, List<GameObject>> _treeParticles = new();
    // 树的 LOD 级别缓存
    private Dictionary<FireSimulation.FireCell, int> _treeLodCache = new();

    // ── 缓存 ──
    private FireSimulation.FireSpreadSystem _spread;
    private FireSimulation.FireCell[] _allCells;
    private Camera _mainCam;
    private Plane[] _frustumPlanes = new Plane[6];
    private float _timer;
    private float _lodRefreshTimer;
    private const float LodRefreshInterval = 0.6f;

    // [诊断 BUG1] 火焰对象创建统计
    private readonly Dictionary<FireSimulation.FireCell, int> _spawnCountPerCell = new();
    private int _spawnsThisInterval;
    private int _totalSpawns;
    private float _rateTimer;
    private float _lastVisualLogTime; // [诊断] 火焰视觉日志限流

    /// <summary>
    /// [重置为默认] 把所有影响「是否产生火焰」的公共参数强制回默认值。
    /// 用户要求调回默认，防止 Inspector 里某个参数被误改后不产生火焰。
    /// 若将来需要调参，请删除/注释本方法。
    /// </summary>
    void Awake()
    {
        maxParticlesPerTree = 2;
        yOffset = 0.5f;
        flameBottomRatio = 0f;
        flameSizeScale = 1.0f;
        fullParticleDistance = 60f;
        halfParticleDistance = 150f;
        maxRenderDistance = 250f;
        frontParticleLifetime = 1f;
        frontParticleSpacing = 3f;
        maxFrontParticlesPerFrame = 40;
        updateInterval = 0.3f;
        poolCapacity = 150;

        Debug.Log($"[FireAreaSpread] 参数已重置为默认 maxParticlesPerTree={maxParticlesPerTree} flameSizeScale={flameSizeScale} updateInterval={updateInterval}");
    }

    void Start()
    {
        if (firePrefab == null)
        {
            Debug.LogError("[FireAreaSpread] firePrefab 为空！无法生成火焰。请在 Inspector 把 firePrefab 拖回 Detailed_Pyro_FX 的 Fire 预制体。");
            return;
        }

        _spread = FindObjectOfType<FireSimulation.FireSpreadSystem>();
        _allCells = FindObjectsOfType<FireSimulation.FireCell>();
        _mainCam = Camera.main;

        // 预热池
        for (int i = 0; i < poolCapacity; i++)
        {
            var go = Instantiate(firePrefab, Vector3.zero, Quaternion.identity);
            go.SetActive(false);
            go.hideFlags = HideFlags.HideInHierarchy;
            _pool.Enqueue(go);
        }
    }

    GameObject GetFromPool(Vector3 pos, Quaternion rot)
    {
        GameObject go;
        if (_pool.Count > 0)
        {
            go = _pool.Dequeue();
            go.transform.SetPositionAndRotation(pos, rot);
            go.hideFlags = HideFlags.None;
        }
        else
        {
            go = Instantiate(firePrefab, pos, rot);
        }
        go.SetActive(true);
        _activeParticles.Add(go);
        return go;
    }

    void ReturnToPool(GameObject go)
    {
        if (go == null) return;
        go.SetActive(false);
        go.hideFlags = HideFlags.HideInHierarchy;
        go.transform.position = Vector3.zero;
        _activeParticles.Remove(go);
        if (_pool.Count < poolCapacity)
            _pool.Enqueue(go);
        else
            Destroy(go);
    }

    void ReturnToPoolDelayed(GameObject go, float delay)
    {
        if (go == null) return;
        StartCoroutine(ReturnAfterDelay(go, delay));
    }

    System.Collections.IEnumerator ReturnAfterDelay(GameObject go, float delay)
    {
        yield return new WaitForSeconds(delay);
        ReturnToPool(go);
    }

    /// <summary>
    /// 计算树的 LOD 级别:
    ///   0 = 全粒子 (近)
    ///   1 = 减半粒子 (中)
    ///   2 = 仅发光 (远, 不需要粒子)
    ///   3 = 剔除 (屏幕外或超远)
    /// </summary>
    int ComputeLod(FireSimulation.FireCell cell)
    {
        if (_mainCam == null) return 0;

        Vector3 pos = cell.WorldPosition;

        // ── 视锥剔除 ──
        if (!IsInFrustum(pos)) return 3;

        // ── 距离分级 ──
        float dist = Vector3.Distance(_mainCam.transform.position, pos);

        if (dist < fullParticleDistance)  return 0; // 近: 全粒子
        if (dist < halfParticleDistance)  return 1; // 中: 减半
        if (dist < maxRenderDistance)     return 2; // 远: 仅发光
        return 3;                                    // 超远: 剔除
    }

    bool IsInFrustum(Vector3 pos)
    {
        // 用树的高度范围做粗检测 (底部和顶部)
        float radius = 2f; // 树冠半径
        for (int i = 0; i < 6; i++)
        {
            if (_frustumPlanes[i].GetDistanceToPoint(pos + Vector3.up * 5f) < -radius &&
                _frustumPlanes[i].GetDistanceToPoint(pos) < -radius)
                return false;
        }
        return true;
    }

    void Update()
    {
        if (firePrefab == null) return;

        // [诊断 BUG1] 每秒火焰对象创建速率统计（分项拆开，不再用混合指标）
        _rateTimer += Time.deltaTime;
        if (_rateTimer >= 1f)
        {
            // Active Flame GameObjects = 树冠火焰对象（在 _treeParticles 中）
            int activeFlameGO = 0;
            foreach (var kv in _treeParticles) activeFlameGO += kv.Value.Count;

            // Active FireFront objects = 火锋对象（在 _activeParticles 中但不在 _treeParticles 中）
            int activeFrontGO = _activeParticles.Count - activeFlameGO;

            // Active ParticleSystems + Total Particle Count（遍历所有活跃火焰对象）
            int activePS = 0;
            int totalParticleCount = 0;
            foreach (var go in _activeParticles)
            {
                if (go == null) continue;
                var pss = go.GetComponentsInChildren<ParticleSystem>();
                activePS += pss.Length;
                foreach (var p in pss) totalParticleCount += p.particleCount;
            }

            int burning = _spread != null ? _spread.GetBurningCount() : 0;
            int repeatSpawn = 0;
            foreach (var kv in _spawnCountPerCell) if (kv.Value >= 3) repeatSpawn++;

            Debug.Log($"[FlameRate] 本秒新建火焰对象={_spawnsThisInterval} 累计新建={_totalSpawns} | " +
                      $"ActiveFlameGO={activeFlameGO} ActiveFrontGO={activeFrontGO} " +
                      $"ActiveParticleSystems={activePS} TotalParticleCount={totalParticleCount} | " +
                      $"BurningCells={burning} 追踪树={_treeParticles.Count} 反复创建树(≥3)={repeatSpawn}");
            _spawnsThisInterval = 0;
            _rateTimer = 0f;
        }

        _timer += Time.deltaTime;
        if (_timer < updateInterval) return;
        _timer = 0f;

        // ── 定期刷新视锥和 LOD ──
        _lodRefreshTimer += updateInterval;
        if (_lodRefreshTimer >= LodRefreshInterval && _mainCam != null)
        {
            _lodRefreshTimer = 0f;
            GeometryUtility.CalculateFrustumPlanes(_mainCam, _frustumPlanes);
        }

        // ── 清理已停止燃烧的树 ──
        var deadKeys = new List<FireSimulation.FireCell>();
        foreach (var (cell, particles) in _treeParticles)
        {
            if (cell == null || cell.CurrentState != FireSimulation.FireCellState.Burning)
            {
                // [诊断 BUG1] 记录火焰对象因「状态改变」被回收（区别于 LOD 回收）
                int spawnN = 0;
                if (cell != null) _spawnCountPerCell.TryGetValue(cell, out spawnN);
                Debug.Log($"[FlameRecycle-State] Cell={(cell != null ? cell.name : "null")} " +
                          $"状态={(cell != null ? cell.CurrentState.ToString() : "null")} " +
                          $"回收火焰对象x{particles.Count} (历史spawn#{spawnN})");
                foreach (var p in particles) ReturnToPool(p);
                deadKeys.Add(cell);
            }
        }
        foreach (var k in deadKeys)
        {
            _treeParticles.Remove(k);
            _treeLodCache.Remove(k);
        }

        // ── 更新燃烧的树 ──
        foreach (var cell in _allCells)
        {
            if (cell == null || cell.CurrentState != FireSimulation.FireCellState.Burning) continue;

            int lod = ComputeLod(cell);
            _treeLodCache[cell] = lod;

            // LOD 2/3: 不需要粒子, 仅靠材质发光
            if (lod >= 2)
            {
                // 如果有遗留粒子, 回收
                if (_treeParticles.TryGetValue(cell, out var oldPs))
                {
                    // [诊断 BUG1] 记录火焰对象因「LOD 距离剔除」被回收（区别于状态回收）
                    int spawnN = 0;
                    _spawnCountPerCell.TryGetValue(cell, out spawnN);
                    Debug.Log($"[FlameRecycle-LOD] Cell={cell.name} lod={lod} " +
                              $"回收火焰对象x{oldPs.Count} (历史spawn#{spawnN})");
                    foreach (var p in oldPs) ReturnToPool(p);
                    _treeParticles.Remove(cell);
                }
                continue;
            }

            float burnH = cell.BurnHeight;
            float treeH = cell.treeHeight > 0f ? cell.treeHeight : 10f;

            if (!_treeParticles.TryGetValue(cell, out var particles))
                _treeParticles[cell] = particles = new List<GameObject>(maxParticlesPerTree);

            // LOD 0: 全粒子数, LOD 1: 减半
            int desired = lod == 0
                ? Mathf.Min(maxParticlesPerTree, 1 + Mathf.RoundToInt(burnH * (maxParticlesPerTree - 1)))
                : 1; // LOD 1: 只留 1 个

            while (particles.Count < desired)
            {
                var go = GetFromPool(cell.WorldPosition, Quaternion.identity);
                // 初始位置贴地
                Vector3 basePos = cell.WorldPosition;
                basePos.y = cell.TreeBaseY + yOffset;
                go.transform.position = basePos;
                particles.Add(go);

                // [诊断 BUG1] 火焰对象创建计数
                _spawnsThisInterval++;
                _totalSpawns++;
                _spawnCountPerCell.TryGetValue(cell, out int prevSpawn);
                int spawnN = prevSpawn + 1;
                _spawnCountPerCell[cell] = spawnN;

                Debug.Log($"[FireSpawn] Cell={cell.name} spawn#{spawnN} " +
                          $"WorldPosition=({cell.WorldPosition.x:F2},{cell.WorldPosition.y:F2},{cell.WorldPosition.z:F2}) " +
                          $"TreeBaseY={cell.TreeBaseY:F2} " +
                          $"FlamePosition=({go.transform.position.x:F2},{go.transform.position.y:F2},{go.transform.position.z:F2})");
            }
            while (particles.Count > desired)
            {
                var last = particles[particles.Count - 1];
                particles.RemoveAt(particles.Count - 1);
                ReturnToPool(last);
            }

            // 更新粒子位置 — 用 TreeBaseY (真正的树底) 而非 WorldPosition.y (可能偏上)
            for (int i = 0; i < particles.Count; i++)
            {
                if (particles[i] == null) continue;
                float t = particles.Count > 1 ? (float)i / (particles.Count - 1) : 0.5f;
                // 火焰沿树干从底部(flameBottomRatio)铺到当前燃烧高度(burnH)，均匀分布。
                // 原实现下界固定 0.3，火刚爬升(burnH≈0.3)时新增粒子与旧粒子几乎重叠 → 上下间距太小。
                float bottom = flameBottomRatio * treeH;
                float top = Mathf.Max(bottom, burnH * treeH);
                float h = Mathf.Lerp(bottom, top, t);
                Vector3 pos = cell.WorldPosition;
                pos.y = cell.TreeBaseY + yOffset + h;
                particles[i].transform.position = pos;

                // [需求] 不缩小火焰（该多大就多大）。flameSizeScale 默认 1.0 = 原始尺寸。
                // 缩放所有子发射器（GetComponentsInChildren），不随 burnHeight 缩小。
                var allPs = particles[i].GetComponentsInChildren<ParticleSystem>();
                foreach (var ps in allPs)
                {
                    var m = ps.main;
                    // 只确保 Local 空间，使粒子跟随被移动的池对象。
                    if (m.simulationSpace != ParticleSystemSimulationSpace.Local)
                    {
                        m.simulationSpace = ParticleSystemSimulationSpace.Local;
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                        ps.Play();
                    }

                    m.startSizeMultiplier = flameSizeScale;
                    var sz = ps.sizeOverLifetime;
                    sz.sizeMultiplier = flameSizeScale;
                }
            }

            // [诊断] 周期性输出火焰视觉状态，用于区分「FireCell 状态闪烁」vs「ParticleSystem 视觉闪烁」
            if (Time.time - _lastVisualLogTime >= 1f && particles.Count > 0)
            {
                _lastVisualLogTime = Time.time;
                var go0 = particles[0];
                var ps0 = go0 != null ? go0.GetComponent<ParticleSystem>() : null;
                float scale = ps0 != null ? ps0.main.startSizeMultiplier : 0f;
                float emission = ps0 != null ? ps0.emission.rateOverTime.constant : 0f;
                int pCount = ps0 != null ? ps0.particleCount : 0;
                Debug.Log($"[FlameVisualDebug] Cell={cell.name} Active={go0 != null && go0.activeSelf} Scale={scale:F2} Emission={emission:F1} ParticleCount={pCount}");
            }
        }

        // ── 火锋 — 有预算上限 ──
        if (_spread == null) return;
        int frontBudget = 0;
        foreach (var (src, tgt, dist, advance) in _spread.GetActiveFronts())
        {
            if (frontBudget >= maxFrontParticlesPerFrame) break;

            // 剔除: 源或目标在屏幕外则跳过
            if (!IsInFrustum(src.WorldPosition) && !IsInFrustum(tgt.WorldPosition))
                continue;

            Vector3 from = src.WorldPosition;
            from.y = src.TreeBaseY + yOffset;
            Vector3 to   = tgt.WorldPosition;
            to.y = tgt.TreeBaseY + yOffset;
            Vector3 dir  = (to - from).normalized;
            float frontLen = Mathf.Min(advance, dist);

            int count = Mathf.Min(
                Mathf.Max(1, Mathf.RoundToInt(frontLen / frontParticleSpacing)),
                maxFrontParticlesPerFrame - frontBudget);
            for (int i = 1; i <= count; i++)
            {
                float t = (float)i / Mathf.Max(1, count);
                Vector3 pos = from + dir * (t * frontLen);
                pos.x += Random.Range(-0.15f, 0.15f);
                pos.z += Random.Range(-0.15f, 0.15f);
                var go = GetFromPool(pos, Quaternion.identity);
                ReturnToPoolDelayed(go, frontParticleLifetime);
                frontBudget++;
            }
        }
    }

    void OnDestroy()
    {
        StopAllCoroutines();
        foreach (var go in _activeParticles)
        {
            if (go != null) Destroy(go);
        }
        _activeParticles.Clear();
        foreach (var go in _pool)
        {
            if (go != null) Destroy(go);
        }
        _pool.Clear();
        foreach (var (_, particles) in _treeParticles)
            particles.Clear();
        _treeParticles.Clear();
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (_mainCam == null) return;
        var camPos = _mainCam.transform.position;
        Gizmos.color = Color.green;  Gizmos.DrawWireSphere(camPos, fullParticleDistance);
        Gizmos.color = Color.yellow; Gizmos.DrawWireSphere(camPos, halfParticleDistance);
        Gizmos.color = Color.red;    Gizmos.DrawWireSphere(camPos, maxRenderDistance);
    }
#endif
}
