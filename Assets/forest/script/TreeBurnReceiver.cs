using UnityEngine;
using System.Collections.Generic;

public class TreeBurnReceiver : MonoBehaviour
{
    [Header("烧焦进度0~1")]
    [Range(0, 1)] public float currentBurnAmount = 0f;
    public float maxBurn = 1f;
    [Header("离开火焰冷却恢复速度，0=永久烧焦不恢复")]
    public float coolSpeed = 0.03f;
    [Header("消融噪声贴图（全局共用一张）")]
    public Texture2D burnNoiseTex;

    [Header("=== 烧焦贴图替换 ===")]
    [Tooltip("烧焦后的 Albedo 贴图，替换原树皮/树叶贴图")]
    public Texture2D burnedAlbedo;
    [Tooltip("需要替换贴图的子物体名称过滤，默认 _LOD0（最高细节层）")]
    public string lodNameFilter = "_LOD0";
    [Tooltip("Shader 主贴图属性名")]
    public string mainTexProperty = "_MainTex";

    [Header("=== 垂直燃烧 ===")]
    [Tooltip("是否由 FireCell.burnHeight 驱动烧焦（从树底向上蔓延）。关闭则退回到均匀溶解。")]
    public bool useVerticalBurn = true;

    [Header("=== 火焰发光 (Emission) ===")]
    [Tooltip("是否启用火焰发射光。燃烧时树材质自发光，远距离代替粒子。已按需求禁用。")]
    public bool enableFireEmission = false;
    [Tooltip("发射光颜色")]
    public Color emissionColor = new Color(1f, 0.35f, 0.05f);
    [Tooltip("发射光峰值强度 (完全吞没时, 尼特)")]
    public float emissionPeakIntensity = 8f;
    [Tooltip("burnHeight 达到此比例后才开始发光 (0~1)。避免刚点燃全树发光。")]
    [Range(0f, 1f)]
    public float emissionActivationThreshold = 0.25f;

    [Header("行为设置")]
    [Tooltip("一旦被火焰灼烧，是否保持烧焦状态直到显式灭火（ForceExtinguish）")]
    public bool lockBurnOnContact = true;
    [Tooltip("调试开关：关闭则材质面板滑块可直接生效，不受脚本覆盖")]
    public bool autoSyncBurnToRenderer = true;

    [Header("粒子交互设置")]
    [Tooltip("每次粒子碰撞增长的灼烧值（按碰撞事件计数）")]
    public float burnPerParticle = 0.05f;
    [Tooltip("是否在控制台输出详细调试/错误信息")]
    public bool enableDebugLogs = false;

    // 同步新Shader的参数名称
    private const string DissolveParam = "_Dissolve";
    private const string BurnNoiseParam = "_BurnNoise";
    private const string BurnHeightParam = "_BurnHeight";
    private const string BurnBaseYParam = "_BurnBaseWorldY";
    private const string BurnTopYParam = "_BurnTopWorldY";
    private const string EmissionColorParam = "_EmissionColor";
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    private List<Renderer> _allTreeRenderers = new List<Renderer>();
    private MaterialPropertyBlock _mpb;
    // 静态列表供给后处理或其他系统读取所有燃烧树木
    public static List<TreeBurnReceiver> AllBurningTrees = new List<TreeBurnReceiver>();

    // 锁定标志：一旦被火焰灼烧并且 lockBurnOnContact 为 true，冷却将被禁用
    private bool _isBurnLocked = false;
    private bool _textureSwapped = false;
    private bool _emissionEnabled = false;

    void Awake()
    {
        // [需求] 取消树材质自发光（变红）机制——强制关闭，防止场景序列化值覆盖
        enableFireEmission = false;

        if (enableDebugLogs) Debug.Log($"<color=cyan>[{gameObject.name}] Awake 初始化开始</color>");
        _mpb = new MaterialPropertyBlock();
        Renderer[] childRds = GetComponentsInChildren<Renderer>();

        if (childRds == null || childRds.Length == 0)
        {
            if (enableDebugLogs) Debug.LogError($"<color=red>[{gameObject.name}] Awake 未找到任何子 Renderer！烧焦完全无法显示</color>");
        }
        else
        {
            _allTreeRenderers.AddRange(childRds);
            if (enableDebugLogs) Debug.Log($"<color=cyan>[{gameObject.name}] Awake 成功收集 Renderer 数量：{childRds.Length}</color>");
        }

        AllBurningTrees.Add(this);
        if (enableDebugLogs) Debug.Log($"<color=cyan>[{gameObject.name}] Awake 加入全局燃烧列表，当前全局树木总数：{AllBurningTrees.Count}</color>");

        if (burnNoiseTex == null && enableDebugLogs)
            Debug.LogWarning($"<color=yellow>[{gameObject.name}] Awake burnNoiseTex 噪声贴图未赋值，烧焦无噪波图案！</color>");
        else if (enableDebugLogs)
            Debug.Log($"<color=cyan>[{gameObject.name}] Awake 噪声贴图正常：{burnNoiseTex.name}");
    }

    void OnDestroy()
    {
        AllBurningTrees.Remove(this);
        if (enableDebugLogs) Debug.Log($"<color=cyan>[{gameObject.name}] OnDestroy 从全局燃烧列表移除</color>");
    }

    void Update()
    {
        // 离开火焰冷却恢复逻辑：
        // - 如果启用了 lockBurnOnContact 且已经被灼烧（_isBurnLocked），则不恢复
        // - 否则按 coolSpeed 缓慢恢复
        if (!_isBurnLocked && coolSpeed > 0f && currentBurnAmount > 0f)
        {
            float oldVal = currentBurnAmount;
            currentBurnAmount -= coolSpeed * Time.deltaTime;
            currentBurnAmount = Mathf.Max(0f, currentBurnAmount);
            if (enableDebugLogs) Debug.Log($"<color=gray>[{gameObject.name}] Update 冷却衰减 {oldVal:F3} → {currentBurnAmount:F3}");

            if (autoSyncBurnToRenderer)
                UpdatePropertyBlocks();
        }
    }

    // Particle collision: 粒子系统开启 Send Collision Messages 才会触发
    void OnParticleCollision(GameObject other)
    {
        if (!enableDebugLogs) return;
        Debug.Log($"<color=orange>[{gameObject.name}] OnParticleCollision 收到碰撞消息，碰撞物体：{other.name}");

        ParticleSystem ps = other.GetComponent<ParticleSystem>();
        if (ps == null)
        {
            ps = other.GetComponentInParent<ParticleSystem>();
            if (ps != null && enableDebugLogs)
                Debug.Log($"<color=orange>[{gameObject.name}] 粒子组件在父物体：{ps.gameObject.name}");
        }

        if (ps == null)
        {
            Debug.LogWarning($"<color=red>[{gameObject.name}] OnParticleCollision 未找到 ParticleSystem，碰撞失效！碰撞源：{other.name}");
            return;
        }

        List<ParticleCollisionEvent> collisionEvents = new List<ParticleCollisionEvent>();
        int hitCount = ParticlePhysicsExtensions.GetCollisionEvents(ps, gameObject, collisionEvents);

        if (hitCount <= 0)
        {
            Debug.Log($"<color=orange>[{gameObject.name}] OnParticleCollision 无有效碰撞事件，hitCount = {hitCount}");
            return;
        }

        float delta = hitCount * burnPerParticle;
        Debug.Log($"<color=orange>[{gameObject.name}] 有效撞击次数 {hitCount}，单次增量 {burnPerParticle:F3}，本次总灼烧增量 {delta:F3}");

        BurnTick(delta);
        UpdatePropertyBlocks();
        Debug.Log($"<color=orange>[{gameObject.name}] 碰撞处理完成，当前 burnAmount = {currentBurnAmount:F3}");
    }

    /// 火焰持续灼烧调用（火焰触发器、粒子碰撞共用）
    public void BurnTick(float deltaBurn)
    {
        float old = currentBurnAmount;
        currentBurnAmount += deltaBurn;
        currentBurnAmount = Mathf.Min(currentBurnAmount, maxBurn);
        if (enableDebugLogs) Debug.Log($"<color=lime>[{gameObject.name}] BurnTick {old:F3} + {deltaBurn:F3} → {currentBurnAmount:F3}");

        if (lockBurnOnContact && currentBurnAmount > 0f)
        {
            if (!_isBurnLocked && enableDebugLogs)
                Debug.Log($"<color=lime>[{gameObject.name}] BurnTick 触发灼烧锁定，停止自动冷却");
            _isBurnLocked = true;
        }
    }

    /// 水流/灭火调用，清除消融效果但保留烧焦贴图。
    public void ForceExtinguish()
    {
        if (enableDebugLogs) Debug.Log($"<color=blue>[{gameObject.name}] ForceExtinguish 灭火，烧焦贴图保留</color>");
        currentBurnAmount = 0f;
        _isBurnLocked = false;
        // 不恢复贴图 — 烧焦纹理永久保留
        UpdatePropertyBlocks();
    }

    /// <summary>
    /// 直接设置烧焦进度并同步到 Shader。由 ForestFireManager / FireSpreadSystem 调用。
    /// </summary>
    public void SetBurnProgress(float value)
    {
        currentBurnAmount = Mathf.Clamp01(value);
        _isBurnLocked = true;

        if (!_textureSwapped && burnedAlbedo != null && currentBurnAmount > 0f)
            SwapToBurnedTexture();

        UpdatePropertyBlocks();
    }

    /// <summary>
    /// 由 FireSpreadSystem 每 Tick 调用，驱动垂直燃烧效果。
    ///
    /// burnHeight01: 火焰已攀升的高度比例 (0=底部刚燃, 1=完全吞没)
    /// treeBaseY:    树底部的世界 Y 坐标
    /// treeHeight:   树的总高度 (m)
    ///
    /// 效果：
    ///   1. _Dissolve 跟随 burnHeight 从 0→1，制造"逐渐烧焦"的时间感
    ///   2. 传入 _BurnHeight/_BurnBaseWorldY/_BurnTopWorldY 让 Shader
    ///      可在世界空间做底部→顶部的裁剪，实现真正的从下往上烧焦。
    ///      如果 Shader 不支持这些参数，则仅退化为均匀溶解。
    /// </summary>
    public void SyncBurnHeight(float burnHeight01, float treeBaseY, float treeHeight)
    {
        if (!useVerticalBurn) return;

        // 烧焦进度跟随火焰攀升高度
        currentBurnAmount = Mathf.Clamp01(burnHeight01);
        _isBurnLocked = true;

        if (!_textureSwapped && burnedAlbedo != null && currentBurnAmount > 0f)
            SwapToBurnedTexture();

        // 发射光: 延迟激活, burnHeight 超过阈值才发光
        // 这样火焰从底部爬升时, 树冠不会提前发亮
        if (enableFireEmission)
        {
            if (currentBurnAmount >= emissionActivationThreshold && !_emissionEnabled)
                EnableEmissionOnMaterials();
            else if (currentBurnAmount < emissionActivationThreshold && _emissionEnabled)
                DisableEmissionOnMaterials();
        }

        // 计算世界 Y 边界
        float burnWorldY = treeBaseY + burnHeight01 * treeHeight;

        // 发射光强度: 从 0 开始线性爬升 (而非从基础值开始)
        float emissionIntensity = 0f;
        if (currentBurnAmount >= emissionActivationThreshold)
        {
            float t = Mathf.InverseLerp(emissionActivationThreshold, 1f, currentBurnAmount);
            emissionIntensity = Mathf.Lerp(0.5f, emissionPeakIntensity, t * t); // 平方加速
        }
        Color currentEmission = emissionColor * emissionIntensity;

        foreach (Renderer rd in _allTreeRenderers)
        {
            if (rd == null) continue;
            rd.GetPropertyBlock(_mpb);

            // 基础溶解值
            _mpb.SetFloat(DissolveParam, currentBurnAmount);
            if (burnNoiseTex != null)
                _mpb.SetTexture(BurnNoiseParam, burnNoiseTex);

            // 垂直燃烧的世界空间参数 (Shader 可选支持)
            _mpb.SetFloat(BurnHeightParam, burnWorldY);
            _mpb.SetFloat(BurnBaseYParam, treeBaseY);
            _mpb.SetFloat(BurnTopYParam, treeBaseY + treeHeight);

            // 发射光
            if (enableFireEmission)
                _mpb.SetColor(EmissionColorId, currentEmission);

            rd.SetPropertyBlock(_mpb);
        }
    }

    /// <summary>
    /// 首次着火时, 在所有 Renderer 的材质上启用 _EMISSION 关键字。
    /// 这样 MPB 设置的 _EmissionColor 才能生效。
    /// </summary>
    void EnableEmissionOnMaterials()
    {
        _emissionEnabled = true;
        foreach (Renderer rd in _allTreeRenderers)
        {
            if (rd == null) continue;
            Material mat = rd.material;
            if (mat == null) continue;
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
    }

    void DisableEmissionOnMaterials()
    {
        _emissionEnabled = false;
        foreach (Renderer rd in _allTreeRenderers)
        {
            if (rd == null) continue;
            Material mat = rd.material;
            if (mat == null) continue;
            mat.DisableKeyword("_EMISSION");
            // 把 emission 归零写回 MPB
            rd.GetPropertyBlock(_mpb);
            _mpb.SetColor(EmissionColorId, Color.black);
            rd.SetPropertyBlock(_mpb);
        }
    }

    /// <summary>
    /// 将匹配 lodNameFilter 的子 Renderer 的主贴图替换为 burnedAlbedo。
    /// 贴图永久保留，灭火不恢复。
    /// </summary>
    void SwapToBurnedTexture()
    {
        _textureSwapped = true;

        foreach (Renderer rd in _allTreeRenderers)
        {
            if (rd == null) continue;
            if (!rd.name.Contains(lodNameFilter)) continue;

            Material mat = rd.material;
            if (mat == null || !mat.HasProperty(mainTexProperty)) continue;

            mat.SetTexture(mainTexProperty, burnedAlbedo);

            if (enableDebugLogs)
                Debug.Log($"<color=orange>[{gameObject.name}] {rd.name} 贴图已替换</color>");
        }
    }

    // 同步灼烧数值、噪声贴图到所有Renderer的MPB
    private void UpdatePropertyBlocks()
    {
        if (_allTreeRenderers == null || _allTreeRenderers.Count == 0)
        {
            if (enableDebugLogs) Debug.LogWarning($"<color=yellow>[{gameObject.name}] UpdatePropertyBlocks 无可用 Renderer，跳过写入MPB");
            return;
        }

        if (enableDebugLogs) Debug.Log($"<color=magenta>[{gameObject.name}] UpdatePropertyBlocks 开始同步，目标灼烧值：{currentBurnAmount:F3}");

        foreach (Renderer rd in _allTreeRenderers)
        {
            if (rd == null)
            {
                if (enableDebugLogs) Debug.LogWarning($"<color=yellow>[{gameObject.name}] UpdatePropertyBlocks 存在空Renderer，跳过");
                continue;
            }

            Material sharedMat = rd.sharedMaterial;
            if (sharedMat == null)
            {
                if (enableDebugLogs) Debug.LogError($"<color=red>[{gameObject.name}] Renderer {rd.name} 无材质，无法写入消融参数");
                continue;
            }

            // 检测Shader是否存在灼烧进度参数
            if (!sharedMat.HasProperty(DissolveParam) && enableDebugLogs)
                Debug.LogWarning($"<color=yellow>[{gameObject.name}] 材质 {sharedMat.name} 缺少属性 {DissolveParam}，灼烧数值传不进Shader！");

            // 检测噪声贴图参数
            if (burnNoiseTex != null && !sharedMat.HasProperty(BurnNoiseParam) && enableDebugLogs)
                Debug.LogWarning($"<color=yellow>[{gameObject.name}] 材质 {sharedMat.name} 缺少噪声纹理槽 {BurnNoiseParam}");

            rd.GetPropertyBlock(_mpb);
            // 写入灼烧进度
            _mpb.SetFloat(DissolveParam, currentBurnAmount);
            // 写入噪声贴图
            if (burnNoiseTex != null)
            {
                _mpb.SetTexture(BurnNoiseParam, burnNoiseTex);
                if (enableDebugLogs) Debug.Log($"<color=magenta>[{gameObject.name}] 成功写入噪声贴图到MPB");
            }

            rd.SetPropertyBlock(_mpb);
        }

        if (enableDebugLogs) Debug.Log($"<color=magenta>[{gameObject.name}] UpdatePropertyBlocks MPB同步全部完成");
    }
}