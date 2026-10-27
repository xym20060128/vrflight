using UnityEngine;

namespace FireSimulation
{
    /// <summary>
    /// 天气管理器 — 封装风场和湿度环境数据。
    ///
    /// 第一版：直接读取场景中的 NM_Wind 组件获取风速和风向。
    ///         如果场景中不存在 NM_Wind，则使用用户手动设置的 fallback 值。
    ///
    /// 后续扩展方向：
    ///   - 动态风速变化 (阵风模型)
    ///   - 昼夜湿度循环
    ///   - 降水事件
    ///   - 区域风场 (多个 WindZone)
    /// </summary>
    public class WeatherManager : MonoBehaviour
    {
        [Header("=== 风场来源 ===")]
        [Tooltip("场景中的 NM_Wind 组件引用。留空则自动查找。")]
        public NM_Wind windComponent;

        [Tooltip("若找不到 NM_Wind，使用此风速 (m/s)")]
        public float fallbackWindSpeed = 3f;

        [Tooltip("若找不到 NM_Wind，使用此风向 (归一化)")]
        public Vector3 fallbackWindDirection = new Vector3(1f, 0f, 0f);

        [Header("=== 湿度设置 ===")]
        [Range(0f, 1f)]
        [Tooltip("全局环境湿度 (0=极干, 1=饱和)。影响所有 FireCell 的默认含水率。")]
        public float ambientMoisture = 0.3f;

        [Tooltip("是否在 Start 时将 ambientMoisture 同步到所有 FireCell")]
        public bool applyMoistureToAllCells = true;

        // ═══════════════════════════════════════════
        // 运行时缓存
        // ═══════════════════════════════════════════
        private Vector3 _cachedWindDir = Vector3.right;
        private float _cachedWindSpeed = 0f;

        public Vector3 WindDirection => _cachedWindDir;
        public float WindSpeed => _cachedWindSpeed;
        public float AmbientMoisture => ambientMoisture;

        // ═══════════════════════════════════════════
        // Unity 生命周期
        // ═══════════════════════════════════════════

        void Start()
        {
            if (windComponent == null)
                windComponent = FindObjectOfType<NM_Wind>();

            if (applyMoistureToAllCells)
            {
                foreach (var cell in FindObjectsOfType<FireCell>())
                    cell.SetMoisture(ambientMoisture);
            }
        }

        void Update()
        {
            RefreshWind();
        }

        void RefreshWind()
        {
            if (windComponent != null)
            {
                // NM_Wind.WindSpeed 单位是 km/h，transform.forward 是风向
                _cachedWindDir = windComponent.transform.forward.normalized;
                _cachedWindSpeed = windComponent.WindSpeed * 0.277778f; // km/h → m/s
            }
            else
            {
                _cachedWindDir = fallbackWindDirection.normalized;
                _cachedWindSpeed = fallbackWindSpeed;
            }
        }

        // ═══════════════════════════════════════════
        // 调试
        // ═══════════════════════════════════════════

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Vector3 dir = _cachedWindDir.magnitude > 0.01f
                ? _cachedWindDir : fallbackWindDirection.normalized;
            Gizmos.DrawRay(transform.position, dir * 5f);
            Gizmos.DrawWireSphere(transform.position + dir * 5f, 0.2f);
        }
    }
}
