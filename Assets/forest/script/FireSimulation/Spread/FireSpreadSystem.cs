using UnityEngine;
using System.Collections.Generic;

namespace FireSimulation
{
    /// <summary>
    /// 火灾蔓延引擎 — 每 Tick 驱动燃烧→传播→熄灭的完整生命周期。
    ///
    /// Tick 流水线（按序执行）：
    ///   Phase 0  清理过期灭火免疫（8 秒后恢复可燃）
    ///   Phase 1  燃料消耗 burnRate / maxBurnTime × dt
    ///   Phase 2  温度更新（被喷水冷却的 cell 跳过自动升温）
    ///   Phase 3  BurnOut（燃料归零）或 Extinguish（温度 &lt; 燃点）
    ///   Phase 4  邻居传播：调用 SimplifiedRothermel 计算火锋推进
    ///   Phase 5  清理已点燃目标的冗余火锋记录
    ///
    /// 火锋模型：_frontProgress[源][目标] = 累计推进距离。
    ///   多火源独立推进同一目标，累计距离 ≥ 实际距离时点燃。
    ///   点燃后清除所有指向该目标的火锋。
    ///
    /// 灭火免疫：被 ExtinguishManager 扑灭的树在 immuneDuration 秒内
    ///   不会被邻居重新点燃，且所有指向它的火锋被清除。
    /// </summary>
    public class FireSpreadSystem : MonoBehaviour
    {
        [Header("=== Tick ===")]
        public float tickInterval = 0.5f;
        public bool autoTick = true;

        [Header("=== 温度 ===")]
        public float ambientTemperature = 25f;
        public float peakTemperatureMultiplier = 2.5f;

        [Header("=== 燃料 ===")]
        public float burnRateMultiplier = 1.0f;

        [Header("=== 蔓延速度 ===")]
        [Tooltip("火锋推进总倍率。1 = Rothermel 原速（此值即语义基准）。\n" +
                 "标定依据：Hardwood 无风 3m 邻居原速约 217s、顺风(3m/s)约 88s（含爬升期更长）；\n" +
                 "若要达到 Rothermel 注释里「顺风 3m 邻居 30~60s」的目标，可设 3~5。\n" +
                 "防御：< 0.1 会在 Awake 被判定为历史误设并重置为 1.0。")]
        public float spreadRateMultiplier = 1.0f;

        [Tooltip("「火焰舔到就秒燃」的判定距离倍率。1 = 原判定, 0 = 完全关闭直接点燃, " +
                 "只走火锋累积。这是密林里火爆炸式蔓延的主因")]
        [Range(0f, 1f)]
        public float directIgniteReachScale = 1.0f;

        [Header("=== 灭火免疫 ===")]
        [Tooltip("树被扑灭后免疫重新点燃的时间 (秒)")]
        public float extinguishImmunityDuration = 8f;

        [HideInInspector] public WeatherManager weatherManager;
        [HideInInspector] public TerrainManager terrainManager;

        [Header("=== 运行时 ===")]
        [SerializeField] private int burningCount;
        [SerializeField] private int totalIgnitions;
        [SerializeField] private int activeFrontCount;

        private readonly HashSet<FireCell> _activeBurningCells = new();
        private float _tickTimer;

        // 火锋进度: _frontProgress[source][target] = 累计推进距离
        // 多火源指向同一目标时，各自独立推进。目标点燃后所有指向它的火锋一并清理。
        // 注意：这是「成员级」状态，跨 Tick 持久化——acc 不会在每 Tick 重建归零。
        private Dictionary<FireCell, Dictionary<FireCell, float>> _frontProgress = new();

        // 灭火免疫：被扑灭的树在一段时间内拒绝重新点燃
        private Dictionary<FireCell, float> _immuneUntil = new();
        private List<FireCell> _immuneExpired = new();

        // 临时集合复用，避免 per-Tick GC
        private List<FireCell> _burningSnapshot = new();
        private List<(FireCell, FireCell, float)> _toCleanFronts = new();
        private List<FireCell> _emptyFrontSources = new();

        // [诊断] 火锋推进进度日志限流 + 实例/Tick 身份
        private float _lastFrontLogTime;
        private float _lastStateLogTime;
        private int _tickCounter;
        private int _instanceId;

        void Awake()
        {
            _instanceId = GetInstanceID();

            // [重置为默认] 把所有影响「传播是否发生」的公共参数强制回默认值，
            // 防止 Inspector 里某个参数被误改后火锋不推进 / 不 Tick。将来调参请删除本段。
            tickInterval = 0.5f;
            autoTick = true;
            ambientTemperature = 25f;
            peakTemperatureMultiplier = 2.5f;
            burnRateMultiplier = 1.0f;
            spreadRateMultiplier = 1.0f;
            directIgniteReachScale = 1.0f;
            extinguishImmunityDuration = 8f;

            Debug.Log($"[FireSpreadSystem:{_instanceId}] 参数已重置为默认 " +
                      $"tickInterval={tickInterval} autoTick={autoTick} spreadRateMultiplier={spreadRateMultiplier} " +
                      $"directIgniteReachScale={directIgniteReachScale} burnRateMultiplier={burnRateMultiplier}");
        }

        void Update()
        {
            if (!autoTick) return;
            _tickTimer += Time.deltaTime;
            if (_tickTimer >= tickInterval) { _tickTimer -= tickInterval; Tick(tickInterval); }
        }

        public void Tick(float dt)
        {
            _tickCounter++;

            // Phase 0: 清理过期免疫 + 清理 _activeBurningCells 中的非燃烧状态 cell
            _immuneExpired.Clear();
            foreach (var (cell, until) in _immuneUntil)
                if (Time.time >= until) _immuneExpired.Add(cell);
            foreach (var c in _immuneExpired) _immuneUntil.Remove(c);

            // 安全网：清理 _activeBurningCells 中已不再是 Burning 的 cell
            _burningSnapshot.Clear();
            foreach (var c in _activeBurningCells)
                if (c == null || c.CurrentState != FireCellState.Burning)
                    _burningSnapshot.Add(c);
            foreach (var c in _burningSnapshot)
                _activeBurningCells.Remove(c);

            // 清理 _frontProgress 中「源已熄灭/销毁」的残留火锋。
            // 防止死火锋在源恢复后把已经 Idle/Burned 的目标重新点燃 → 闪烁。
            _emptyFrontSources.Clear();
            foreach (var src in _frontProgress.Keys)
                if (src == null || src.CurrentState != FireCellState.Burning)
                    _emptyFrontSources.Add(src);
            foreach (var s in _emptyFrontSources)
                _frontProgress.Remove(s);

            _burningSnapshot.Clear();
            _burningSnapshot.AddRange(_activeBurningCells);
            _toCleanFronts.Clear();

            // 提前获取当前风场 (避免每个 cell 重复查询)
            float wSpd = weatherManager != null ? weatherManager.WindSpeed : 0f;

            foreach (var cell in _burningSnapshot)
            {
                if (cell.CurrentState != FireCellState.Burning) continue;

                // Phase 1: 燃料消耗
                float burnRate = burnRateMultiplier / Mathf.Max(0.1f, cell.maxBurnTime);
                cell.ConsumeFuel(burnRate * dt);

                // Phase 1.5: 树内垂直火焰攀升
                // 风加速火焰在树内的攀升: 风速越大, 火焰倾向越陡, 树内攀升越快
                float verticalRate = SimplifiedRothermel.GetVerticalBurnRate(cell.fuelType);
                if (cell.verticalBurnRate > 0f) verticalRate = cell.verticalBurnRate; // Inspector 覆盖
                float windBoost = 1f + (wSpd * 0.05f); // 10m/s 风 → 1.5× 攀升速度
                cell.AdvanceBurnHeight(verticalRate * windBoost * dt);

                // Phase 2: 温度更新
                // Fix: 收到外部冷却（喷水）的 cell 不强制覆写温度，
                // 让 RemoveHeat 的降温效果保留，Phase 3 才能正确触发 Extinguish。
                if (!cell._hasExternalCooling)
                {
                    float peak = cell.ignitionThreshold * peakTemperatureMultiplier;
                    float t = Mathf.Lerp(ambientTemperature, peak, cell.FuelRemaining);
                    cell.SetTemperature(Mathf.Max(t, cell.ignitionThreshold));
                }
                cell._hasExternalCooling = false;
                cell.treeBurnReceiver?.SyncBurnHeight(cell.BurnHeight, cell.TreeBaseY, cell.treeHeight);

                // Phase 3: BurnOut / Extinguish
                if (cell.FuelRemaining <= 0f)
                {
                    cell.BurnOut();
                    _activeBurningCells.Remove(cell);
                    _frontProgress.Remove(cell);   // 源烧尽 → 它发出的火锋失去热源，物理上应当终止
                    ClearFrontsToTarget(cell);     // 清除所有指向该 cell 的火锋
                    continue;
                }
                if (cell.Temperature < cell.ignitionThreshold)
                {
                    cell.Extinguish();
                    _activeBurningCells.Remove(cell);
                    _frontProgress.Remove(cell);
                    ClearFrontsToTarget(cell);     // 防止残留火锋下一 Tick 复燃 → 闪烁
                    _immuneUntil[cell] = Time.time + extinguishImmunityDuration;
                    continue;
                }

                // Phase 4: 邻居传播
                SpreadToNeighbours(cell, dt);

                // 新机制 3: Torching 爆发态 Tick 消费
                cell.ConsumeTorchingTick();
            }

            // Phase 5: 清理所有指向已点燃目标的火锋（多火源情况）
            foreach (var (src, tgt, acc) in _toCleanFronts)
            {
                if (_frontProgress.TryGetValue(src, out var tgtDict))
                {
                    tgtDict.Remove(tgt);
                    if (tgtDict.Count == 0)
                        _frontProgress.Remove(src);
                }
            }

            burningCount = _activeBurningCells.Count;
            activeFrontCount = CountActiveFronts();

            // [诊断] 周期性输出所有燃烧 cell 状态，用于区分「状态闪烁」vs「视觉闪烁」
            if (Time.time - _lastStateLogTime >= 1f)
            {
                _lastStateLogTime = Time.time;
                foreach (var c in _activeBurningCells)
                {
                    if (c == null) continue;
                    Debug.Log($"[FireStateDebug] Cell={c.name} State={c.CurrentState} BurnHeight={c.BurnHeight:F2} Fuel={c.FuelRemaining:F2} Temperature={c.Temperature:F0}");
                }
            }
        }

        void SpreadToNeighbours(FireCell source, float dt)
        {
            if (source.neighbours == null) return;
            Vector3 wDir = weatherManager != null ? weatherManager.WindDirection : Vector3.forward;
            float wSpd = weatherManager != null ? weatherManager.WindSpeed : 0f;

            if (!_frontProgress.TryGetValue(source, out var tgtDict))
                _frontProgress[source] = tgtDict = new Dictionary<FireCell, float>();

            foreach (var target in source.neighbours)
            {
                if (target == null) continue;
                if (target == source || target.gameObject == source.gameObject) continue; // 防御：排除自己 + 同一物体上的重复组件
                if (target.CurrentState != FireCellState.Idle) continue;
                if (_immuneUntil.ContainsKey(target)) continue; // 免疫期内，拒绝重新点燃
                float dist = Vector3.Distance(source.WorldPosition, target.WorldPosition);
                if (dist <= 0.01f) continue;     // 防御：重叠到几乎同一点的目标不推进（避免 dist≈0 被秒燃）
                if (dist > source.maxFireReach) continue;

                float slope = terrainManager != null
                    ? terrainManager.GetSlopeAngle(source.WorldPosition, target.WorldPosition) : 0f;

                SpreadResult r = SimplifiedRothermel.Compute(source, target, wDir, wSpd, slope, dt,
                    source.BurnHeight, source.IsTorching);

                // 新机制 1: 直接火焰接触点燃
                // 火焰高度超过目标树冠底部 → 火焰可直接舔到目标枝叶, 立即点燃
                // directIgniteReachScale 收紧这条捷径: 它绕过火锋累积直接点燃,
                // 是密林里火蔓延过快的主要来源。0 = 关闭, 全部走火锋累积。
                bool directIgnite = r.canDirectIgnite
                    && directIgniteReachScale > 0f
                    && dist < r.flameHeight * 1.5f * directIgniteReachScale;

                if (directIgnite)
                {
                    // 计算火焰接触目标的高度比例
                    // 火焰从源树底部延伸到 r.flameHeight 处
                    float sourceFlameTopY = source.TreeBaseY + r.flameHeight;
                    float targetTreeH = target.treeHeight > 0f ? target.treeHeight : 10f;
                    // 火焰顶端在目标树上的相对位置 (0=目标底部, 1=目标顶部)
                    float contactHeight01 = Mathf.Clamp01(
                        (sourceFlameTopY - target.TreeBaseY) / Mathf.Max(0.1f, targetTreeH));
                    // 至少从树冠底部开始烧 (火焰舔的是树冠)
                    float crownRatio = target.crownBaseHeightRatio > 0f
                        ? target.crownBaseHeightRatio : 0.4f;
                    contactHeight01 = Mathf.Max(contactHeight01, crownRatio);

                    target.SetTemperature(target.ignitionThreshold);
                    target.IgniteAtHeight(contactHeight01);
                    _activeBurningCells.Add(target);
                    totalIgnitions++;

                    // 清理所有指向该目标的火锋
                    foreach (var (otherSrc, otherTgts) in _frontProgress)
                        if (otherTgts.ContainsKey(target))
                            _toCleanFronts.Add((otherSrc, target, 0f));
                    continue; // 已点燃, 无需累积火锋
                }

                // 新机制 2: 对流柱干燥 (热气柱上升, 烘干上方树木)
                if (r.convectiveMoistureDelta > 0f)
                    target.SetMoisture(Mathf.Max(0f, target.moisture - r.convectiveMoistureDelta));

                // Fix #1: 辐射预干燥
                if (r.preHeatMoistureDelta > 0f)
                    target.SetMoisture(Mathf.Max(0f, target.moisture - r.preHeatMoistureDelta));

                // 火锋推进：acc 跨 Tick 持续累积，绝不重建归零
                if (!tgtDict.TryGetValue(target, out float acc)) acc = 0f;
                acc += r.frontAdvance * spreadRateMultiplier;
                tgtDict[target] = acc;

                // [诊断] 火锋推进进度（限流：每 2 秒记录一次；neighbours 按距离升序，首个即最近邻居）
                if (Time.time - _lastFrontLogTime >= 2f)
                {
                    _lastFrontLogTime = Time.time;
                    Debug.Log($"[FrontProgress] sys={_instanceId} tick={_tickCounter} " +
                              $"Src={source.name}({source.GetInstanceID()}) -> Tgt={target.name}({target.GetInstanceID()}) " +
                              $"same={source == target} dist={dist:F2} " +
                              $"advanceThisTick={r.frontAdvance:F4} acc={acc:F4} " +
                              $"progress={(acc / Mathf.Max(dist, 0.001f) * 100f):F1}% " +
                              $"R={r.spreadRate:F4} BurnHeight={source.BurnHeight:F2} Torching={source.IsTorching}");
                }

                if (acc >= dist)
                {
                    target.SetTemperature(target.ignitionThreshold);
                    target.Ignite();
                    _activeBurningCells.Add(target);
                    totalIgnitions++;

                    Debug.Log($"[Ignite] sys={_instanceId} tick={_tickCounter} {target.name} 被 {source.name} 点燃 (acc={acc:F2} >= dist={dist:F2})");

                    // 不在 foreach 中修改字典，延迟到 Phase 5 统一清理
                    _toCleanFronts.Add((source, target, acc));

                    // 同时清理所有其他源→同一目标的火锋
                    foreach (var (otherSrc, otherTgts) in _frontProgress)
                    {
                        if (otherSrc == source) continue;
                        if (otherTgts.ContainsKey(target))
                            _toCleanFronts.Add((otherSrc, target, 0f));
                    }
                }
            }
        }

        /// <summary>
        /// 清除所有「指向 target」的火锋记录（来自任意源）。
        /// 用于 cell 被扑灭/烧尽/手动点燃时，防止残留火锋在下一 Tick 把它重新点燃 → 闪烁。
        /// </summary>
        private void ClearFrontsToTarget(FireCell target)
        {
            _emptyFrontSources.Clear();
            foreach (var (src, targets) in _frontProgress)
                if (targets.Remove(target) && targets.Count == 0)
                    _emptyFrontSources.Add(src);
            foreach (var src in _emptyFrontSources)
                _frontProgress.Remove(src);
        }

        int CountActiveFronts()
        {
            int n = 0;
            foreach (var kv in _frontProgress)
                n += kv.Value.Count;
            return n;
        }

        /// <summary>供 FireAreaSpread 读取当前所有活跃火锋</summary>
        public IEnumerable<(FireCell src, FireCell tgt, float dist, float advance)> GetActiveFronts()
        {
            foreach (var (src, targets) in _frontProgress)
                foreach (var (tgt, advance) in targets)
                    yield return (src, tgt, Vector3.Distance(src.WorldPosition, tgt.WorldPosition), advance);
        }

        public void IgniteCell(FireCell cell)
        {
            if (cell.CurrentState != FireCellState.Idle) return;
            _immuneUntil.Remove(cell); // 手动点燃时清除免疫
            ClearFrontsToTarget(cell); // 清除残留火锋，避免点燃后立即被旧记录干扰
            cell.SetTemperature(cell.ignitionThreshold);
            cell.Ignite();
            _activeBurningCells.Add(cell);
            totalIgnitions++;
        }

        /// <summary>
        /// 标记一棵树已被扑灭。设置免疫期并清理所有指向它的火锋。
        /// </summary>
        public void MarkExtinguished(FireCell cell)
        {
            _immuneUntil[cell] = Time.time + extinguishImmunityDuration;

            // 清理所有指向该 cell 的火锋（静默，不刷日志）
            ClearFrontsToTarget(cell);
        }

        public int GetBurningCount() => _activeBurningCells.Count;
        public List<FireCell> GetBurningCells()
        {
            var result = new List<FireCell>(_activeBurningCells.Count);
            foreach (var c in _activeBurningCells)
                if (c != null && c.CurrentState == FireCellState.Burning)
                    result.Add(c);
            return result;
        }
    }
}
