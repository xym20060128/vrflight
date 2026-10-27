using UnityEngine;
using System.Collections.Generic;

namespace FireSimulation
{
    /// <summary>
    /// 对象化元胞自动机的单个 Cell — 轻量数据容器。
    ///
    /// 职责边界：
    ///   ✅ 保存状态、温度、燃料、含水率、邻居
    ///   ✅ 状态转换方法 (Ignite / Extinguish / BurnOut)
    ///   ✅ 统一状态变化事件 OnStateChanged
    ///   ✅ 温度修改接口 (SetTemperature / AddHeat / RemoveHeat)
    ///   ✅ 燃料消耗接口 (ConsumeFuel)
    ///
    ///   ❌ 不计算温度演化 → FireSpreadSystem
    ///   ❌ 不主动修改 moisture → WeatherManager / FuelDatabase
    ///   ❌ 不判断 Extinguish 条件 → FireSpreadSystem
    ///   ❌ 不发现邻居 → ForestFireManager
    ///   ❌ 不调用任何视觉层 → 只发事件
    /// </summary>
    public class FireCell : MonoBehaviour
    {
        // ═══════════════════════════════════════════
        // Inspector 可配置参数 (初始化后只读)
        // ═══════════════════════════════════════════
        [Header("=== 燃料属性 ===")]
        [Tooltip("可燃物类型，决定基础传播速率和燃点")]
        public FuelType fuelType = FuelType.Hardwood;

        [Range(0f, 1f)]
        [Tooltip("含水率 (0=全干, 1=饱和)。被动属性，由 WeatherManager 或 FuelDatabase 外部修改。FireCell 自身不修改此值。")]
        public float moisture = 0.3f;

        [Tooltip("燃点温度 (摄氏度)，达到此温度则可被点燃")]
        public float ignitionThreshold = 260f;

        [Tooltip("最大燃烧时间 (秒)，燃料耗尽则 BurnOut。默认 300 秒 = 5 分钟。")]
        public float maxBurnTime = 300f;

        [Header("=== 传播参数 ===")]
        [Tooltip("该树燃烧时对邻居的辐射热强度系数 (0~2)")]
        [Range(0f, 2f)]
        public float heatRadiation = 1.0f;

        [Tooltip("火锋最大有效传播距离 (m)，超过此距离的邻居不会被直接点燃")]
        public float maxFireReach = 15f;

        [Header("=== 垂直燃烧 (树内) ===")]
        [Tooltip("树的总高度 (m)。0 = 自动从 Renderer 包围盒检测")]
        public float treeHeight = 0f;

        [Tooltip("自动检测高度的缩放系数。碰撞体偏大时调小，例如 0.6 = 火焰只升到检测高度的 60%")]
        [Range(0.1f, 2f)]
        public float treeHeightScale = 0.6f;

        [Tooltip("火焰在树内的垂直攀升速率 (高度比例/秒, 0~1)。0 = 使用燃料类型默认值")]
        [Range(0f, 0.5f)]
        public float verticalBurnRate = 0f;

        [Header("=== 树冠结构 ===")]
        [Tooltip("树冠底部高度占总高度的比例 (0~1)。0.4 = 最低枝条在树高40%处")]
        [Range(0f, 1f)]
        public float crownBaseHeightRatio = 0.4f;

        // ═══════════════════════════════════════════
        // 运行时状态 (Inspector 只读)
        // ═══════════════════════════════════════════
        [Header("=== 运行时状态 (只读) ===")]
        [SerializeField] private FireCellState _currentState = FireCellState.Idle;
        [SerializeField] private float _temperature = 25f;
        [SerializeField] private float _fuelRemaining = 1f;
        [SerializeField] private float _burnHeight = 0f;       // 0=底部刚燃, 1=完全吞没
        [SerializeField] private int   _torchingTickRemaining = 0; // torching 爆发剩余 Tick 数
        private float _detectedBaseY;                            // 从 Renderer 包围盒检测的真正树底 Y

        // ═══════════════════════════════════════════
        // 公开属性
        // ═══════════════════════════════════════════
        public FireCellState CurrentState => _currentState;
        public float Temperature => _temperature;
        public float FuelRemaining => _fuelRemaining;
        public float BurnHeight => _burnHeight;
        public bool IsFullyEngulfed => _burnHeight >= 1f;
        /// <summary>是否处于 torching 爆发态（刚达到完全吞没，热脉冲输出 3x）</summary>
        public bool IsTorching => _torchingTickRemaining > 0;
        public Vector3 WorldPosition => transform.position;

        /// <summary>
        /// 本 Tick 是否收到过外部冷却（喷水）。由 FireSpreadSystem 读取并每 Tick 重置。
        /// </summary>
        internal bool _hasExternalCooling = false;
        internal void MarkExternalCooling() { _hasExternalCooling = true; }

        /// <summary>
        /// 邻居列表，由 ForestFireManager 在初始化阶段通过 SetNeighbours() 设置。
        /// </summary>
        [HideInInspector] public List<FireCell> neighbours = new List<FireCell>();

        /// <summary>
        /// 同一 GameObject 上的 TreeBurnReceiver 引用 (Awake 自动获取)
        /// </summary>
        [HideInInspector] public TreeBurnReceiver treeBurnReceiver;

        // ═══════════════════════════════════════════
        // 统一状态变化事件
        // ═══════════════════════════════════════════
        /// <summary>
        /// 统一事件：(触发者, 旧状态, 新状态)
        /// 仅 ForestFireManager 订阅，由它协调视觉层和外部系统。
        /// </summary>
        public event System.Action<FireCell, FireCellState, FireCellState> OnStateChanged;

        // ═══════════════════════════════════════════
        // Unity 生命周期
        // ═══════════════════════════════════════════
        void Awake()
        {
            // [重置为默认] 防止这些「传播关键」参数被误改成 0 导致无法传播/无火焰。
            // 只纠正非法值(≤0)，不覆盖正常调参。fuelType/moisture/treeHeight 属每树配置，不在此重置。
            if (maxFireReach <= 0f) maxFireReach = 15f;
            if (maxBurnTime <= 0f) maxBurnTime = 300f;
            if (ignitionThreshold <= 0f) ignitionThreshold = 260f;
            if (treeHeightScale <= 0f) treeHeightScale = 0.6f;

            treeBurnReceiver = GetComponent<TreeBurnReceiver>();
            _temperature = 25f;
            _fuelRemaining = 1f;

            // 始终从 Renderer 包围盒检测树底 Y (即使 treeHeight 已手动指定)
            // 否则 TreeBaseY 会回退到 transform.position.y (模型 pivot, 偏上)
            var renderers = GetComponentsInChildren<Renderer>();
            if (renderers != null && renderers.Length > 0)
            {
                float minY = float.MaxValue, maxY = float.MinValue;
                foreach (var r in renderers)
                {
                    var b = r.bounds;
                    if (b.min.y < minY) minY = b.min.y;
                    if (b.max.y > maxY) maxY = b.max.y;
                }
                _detectedBaseY = minY;

                // 树高: 手动指定优先, 否则从包围盒高度检测
                if (treeHeight <= 0f)
                    treeHeight = Mathf.Max(0.5f, maxY - minY);
            }
            else
            {
                _detectedBaseY = transform.position.y;
                if (treeHeight <= 0f) treeHeight = 10f;
            }

            // 应用缩放: 碰撞体偏大时包围盒偏高, 调小 treeHeightScale 即可
            treeHeight *= treeHeightScale;
        }

        // ═══════════════════════════════════════════
        // 温度接口 (仅修改数值，不做状态判断)
        // ═══════════════════════════════════════════

        /// <summary>
        /// 直接设置温度值。由 FireSpreadSystem 在每 Tick 调用。
        /// </summary>
        public void SetTemperature(float value)
        {
            _temperature = Mathf.Max(value, 0f);
        }

        /// <summary>
        /// 加热 (辐射热、自身燃烧放热等)。上限为 ignitionThreshold × 3。
        /// </summary>
        public void AddHeat(float amount)
        {
            _temperature = Mathf.Min(_temperature + amount, ignitionThreshold * 3f);
        }

        /// <summary>
        /// 降温 (喷水、风冷等)。由 FireSpreadSystem 计算并调用。
        /// 不直接触发 Extinguish — 由 FireSpreadSystem 判断是否低于 ignitionThreshold。
        /// </summary>
        public void RemoveHeat(float amount)
        {
            _temperature -= amount;
            if (_temperature < 0f) _temperature = 0f;
        }

        // ═══════════════════════════════════════════
        // 燃料消耗接口
        // ═══════════════════════════════════════════

        /// <summary>
        /// 消耗燃料 (0~1 比例)。由 FireSpreadSystem 每 Tick 根据燃烧速率调用。
        /// </summary>
        public void ConsumeFuel(float amount)
        {
            _fuelRemaining -= amount;
            if (_fuelRemaining < 0f) _fuelRemaining = 0f;
        }

        /// <summary>
        /// 含水量被动设置。由 WeatherManager 或场景外部环境调用。
        /// FireCell 自身不在任何方法中修改此值。
        /// </summary>
        public void SetMoisture(float value)
        {
            moisture = Mathf.Clamp01(value);
        }

        /// <summary>
        /// 推进火焰在树内的垂直攀升高度。
        /// amount: 高度比例增量 (0~1 归一化)
        /// 由 FireSpreadSystem 每 Tick 调用。
        ///
        /// 当 burnHeight 首次达到 1.0 时，触发 torching 爆发态：
        ///   瞬间释放大量热脉冲，持续 2 个 Tick (1秒),
        ///   期间对邻居的辐射热和火锋推进速率 ×3。
        /// </summary>
        public void AdvanceBurnHeight(float amount)
        {
            if (_currentState != FireCellState.Burning) return;
            bool wasFullyEngulfed = _burnHeight >= 1f;
            _burnHeight = Mathf.Min(1f, _burnHeight + amount);
            if (!wasFullyEngulfed && _burnHeight >= 1f)
            {
                _torchingTickRemaining = 2; // 2 Tick = 1秒的热脉冲
            }
        }

        /// <summary>
        /// 消费 1 个 Tick 的 torching 爆发态。
        /// 由 FireSpreadSystem 在每个 Tick 末尾调用。
        /// </summary>
        public void ConsumeTorchingTick()
        {
            if (_torchingTickRemaining > 0)
                _torchingTickRemaining--;
        }

        /// <summary>
        /// 获取树底部的世界 Y 坐标。
        /// 从 Renderer 包围盒检测，而非依赖 transform.position.y（模型 pivot 可能在树干中间）。
        /// </summary>
        public float TreeBaseY => _detectedBaseY;

        /// <summary>
        /// 获取火焰当前到达的世界 Y 高度
        /// </summary>
        public float BurnWorldY
        {
            get
            {
                float h = treeHeight > 0f ? treeHeight : 10f;
                return TreeBaseY + _burnHeight * h;
            }
        }

        // ═══════════════════════════════════════════
        // 状态转换方法 (仅做状态切换 + 发事件)
        // ═══════════════════════════════════════════

        /// <summary>
        /// Idle → Burning (从底部开始)。
        /// 由 FireSpreadSystem 在火锋推进积累完成后调用。
        /// 触发 OnStateChanged(Idle → Burning)。
        /// </summary>
        public void Ignite()
        {
            IgniteAtHeight(0f);
        }

        /// <summary>
        /// Idle → Burning (从指定高度比例开始)。
        ///
        /// 用于直接火焰接触点燃场景:
        ///   当源树的火焰舔到目标树冠时, 目标从树冠高度开始燃烧
        ///   (而非从底部), 这样视觉上火焰不会"从顶部突然冒出来"。
        ///
        /// initialBurnHeight: 初始燃烧高度比例 (0=底部, 1=顶部)。
        /// </summary>
        public void IgniteAtHeight(float initialBurnHeight)
        {
            if (_currentState != FireCellState.Idle) return;

            var oldState = _currentState;
            _currentState = FireCellState.Burning;
            _fuelRemaining = 1f;
            _burnHeight = Mathf.Clamp01(initialBurnHeight);

            OnStateChanged?.Invoke(this, oldState, _currentState);
        }

        /// <summary>
        /// Burning → Idle。
        /// 由 FireSpreadSystem 在温度低于 ignitionThreshold 时调用。
        /// 触发 OnStateChanged(Burning → Idle)。
        /// </summary>
        public void Extinguish()
        {
            if (_currentState != FireCellState.Burning) return;

            var oldState = _currentState;
            _currentState = FireCellState.Idle;
            _temperature = 25f;

            OnStateChanged?.Invoke(this, oldState, _currentState);
        }

        /// <summary>
        /// Burning → Burned。
        /// 由 FireSpreadSystem 在 fuelRemaining == 0 时调用。
        /// 触发 OnStateChanged(Burning → Burned)。
        /// </summary>
        public void BurnOut()
        {
            if (_currentState != FireCellState.Burning) return;

            var oldState = _currentState;
            _currentState = FireCellState.Burned;

            OnStateChanged?.Invoke(this, oldState, _currentState);
        }

        // ═══════════════════════════════════════════
        // 邻居设置
        // ═══════════════════════════════════════════

        /// <summary>
        /// 仅由 ForestFireManager 在初始化时调用一次。
        /// </summary>
        public void SetNeighbours(List<FireCell> list)
        {
            neighbours = list;
        }

        // ═══════════════════════════════════════════
        // Editor 辅助
        // ═══════════════════════════════════════════
        void OnDrawGizmosSelected()
        {
            // 树底标记 — 验证 TreeBaseY 是否正确
            Gizmos.color = Color.yellow;
            Vector3 basePos = WorldPosition;
            basePos.y = TreeBaseY;
            Gizmos.DrawWireSphere(basePos, 0.15f);
            Gizmos.DrawLine(basePos, basePos + Vector3.up * treeHeight);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(basePos + Vector3.up * treeHeight, $"{gameObject.name}\nH={treeHeight:F1}m");
#endif

            if (neighbours == null || neighbours.Count == 0) return;

            Gizmos.color = _currentState switch
            {
                FireCellState.Burning => Color.red,
                FireCellState.Burned  => Color.gray,
                _                     => Color.green
            };

            foreach (var n in neighbours)
            {
                if (n != null)
                    Gizmos.DrawLine(WorldPosition, n.WorldPosition);
            }
        }
    }
}
