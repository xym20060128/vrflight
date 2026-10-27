using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using XRDevice = UnityEngine.XR.InputDevice;

/// <summary>
/// 手持水枪 —— 挂在 XR Origin 下、手柄握住的水枪物体上。
/// 按住 A 键（左手为 X 键）从枪口射出定向水柱，命中燃烧的树后调 FireExtinguishManager.SprayAt 降温灭火。
///
/// 输入走两条路，任意一条通了就能喷水（串流 / Live Preview 下 Input System 的设备
/// 布局有时对不上，所以额外保留一条 UnityEngine.XR 的兜底通道）：
///   1. Input System：action map "WaterGun" / action "Fire"，绑定到 primaryButton
///   2. 兜底：直接轮询所有 XR 手柄的 CommonUsages.primaryButton
///
/// 依赖：
///   - 水枪要有 waterMuzzle（枪口 Transform，朝向喷射方向）
///   - 树必须有 Collider（射线要能命中）
/// </summary>
public class WaterGunSpray : MonoBehaviour
{
    [Header("=== 枪口 ===")]
    [Tooltip("枪口 Transform（水柱起点 + 朝向）")]
    public Transform waterMuzzle;

    [Header("=== 喷水参数 ===")]
    public float sprayRange = 15f;
    [Tooltip("喷水射线忽略的层（火焰/水特效等）")]
    public LayerMask sprayLayerMask = ~0;

    [Header("=== 输入 ===")]
    [Tooltip("XR Input Action Asset，须含 action map: WaterGun / action: Fire（绑定到手柄 A / X 键）")]
    public InputActionAsset xrInputActions;
    [Tooltip("Input System 没读到时，直接轮询 XR 手柄的 A / X 键兜底（串流模式下建议开启）")]
    public bool fallbackToLegacyXRInput = true;

    [Header("=== 引用 ===")]
    public FireSimulation.FireExtinguishManager extinguishManager;

    [Header("=== 水特效 ===")]
    [Tooltip("枪口喷出的水粒子 GameObject")]
    public GameObject waterParticleObj;
    [Tooltip("是否绘制水柱 LineRenderer")]
    public bool showWaterBeam = true;

    private InputAction _fire;
    private LineRenderer _beam;

    void Start()
    {
        extinguishManager ??= FindObjectOfType<FireSimulation.FireExtinguishManager>();

        if (xrInputActions != null)
        {
            var map = xrInputActions.FindActionMap("WaterGun");
            if (map != null)
            {
                _fire = map.FindAction("Fire");
                _fire?.Enable();
                Debug.Log(_fire != null
                    ? "[WaterGunSpray] Fire action 已找到并启用"
                    : "[WaterGunSpray] 找不到名为 Fire 的 Action（action 名要精确叫 Fire，大小写敏感）");
            }
            else
            {
                Debug.LogWarning("[WaterGunSpray] 找不到名为 WaterGun 的 Action Map（map 名要精确叫 WaterGun）");
            }
        }
        else
        {
            Debug.LogWarning("[WaterGunSpray] xrInputActions 未赋值！请在 Inspector 拖入 InputActionAsset");
        }

        if (showWaterBeam)
        {
            _beam = GetComponent<LineRenderer>();
            if (_beam == null) _beam = gameObject.AddComponent<LineRenderer>();
            _beam.positionCount = 2;
            _beam.startWidth = 0.05f;
            _beam.endWidth = 0.03f;
            _beam.material = new Material(Shader.Find("Sprites/Default"));
            _beam.startColor = new Color(0.4f, 0.7f, 1f, 0.85f);
            _beam.endColor = new Color(0.4f, 0.7f, 1f, 0.3f);
            _beam.enabled = false;
        }

        if (waterParticleObj != null)
            waterParticleObj.SetActive(false);
    }

    void Update()
    {
        bool spraying = ReadFireInput();
        if (spraying) DoSpray();
        else StopSpray();

#if UNITY_EDITOR
        if (Time.frameCount % 60 == 0)
            Debug.Log($"[WaterGunSpray] Fire(InputSystem) = {(_fire != null ? _fire.ReadValue<float>() : -1f)}, " +
                      $"A键(兜底) = {ReadLegacyPrimaryButton()}");
#endif
    }

    /// <summary>Input System 优先，读不到再用 UnityEngine.XR 兜底。</summary>
    bool ReadFireInput()
    {
        if (_fire != null && _fire.ReadValue<float>() > 0.5f) return true;
        return fallbackToLegacyXRInput && ReadLegacyPrimaryButton();
    }

    static readonly List<XRDevice> s_Controllers = new List<XRDevice>();

    /// <summary>轮询所有 XR 手柄的 A（右手）/ X（左手）键。</summary>
    static bool ReadLegacyPrimaryButton()
    {
        UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(
            UnityEngine.XR.InputDeviceCharacteristics.Controller, s_Controllers);

        for (int i = 0; i < s_Controllers.Count; i++)
        {
            if (s_Controllers[i].TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool pressed)
                && pressed)
                return true;
        }
        return false;
    }

    void DoSpray()
    {
        if (waterMuzzle == null)
        {
            Debug.LogWarning("[WaterGunSpray] waterMuzzle 未赋值！请在 Inspector 拖入枪口 Transform");
            return;
        }

        if (waterParticleObj != null && !waterParticleObj.activeSelf)
            waterParticleObj.SetActive(true);

        Ray ray = new Ray(waterMuzzle.position, waterMuzzle.forward);
        Debug.DrawRay(ray.origin, ray.direction * sprayRange, Color.blue, 0.05f);

        Vector3 end = ray.origin + ray.direction * sprayRange;

        // RaycastAll 穿透水/火特效，找真正的 FireCell / TreeBurnReceiver
        RaycastHit[] hits = Physics.RaycastAll(ray, sprayRange, sprayLayerMask);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (var h in hits)
        {
            string n = h.collider.name.ToLower();
            if (n.Contains("fire") || n.Contains("flame") || n.Contains("water") || n.Contains("spray"))
                continue;

            var fc = h.collider.GetComponent<FireSimulation.FireCell>();
            if (fc == null) fc = h.collider.GetComponentInParent<FireSimulation.FireCell>();
            var tbr = h.collider.GetComponent<TreeBurnReceiver>();
            if (tbr == null) tbr = h.collider.GetComponentInParent<TreeBurnReceiver>();

            if (fc != null || tbr != null)
            {
                end = h.point;
                if (waterParticleObj != null)
                {
                    waterParticleObj.transform.position = h.point;
                    waterParticleObj.transform.forward = -ray.direction;
                }
                extinguishManager?.SprayAt(h.point, Time.deltaTime);
                break;
            }
        }

        if (_beam != null)
        {
            _beam.enabled = true;
            _beam.SetPosition(0, waterMuzzle.position);
            _beam.SetPosition(1, end);
        }
    }

    void StopSpray()
    {
        if (waterParticleObj != null) waterParticleObj.SetActive(false);
        if (_beam != null) _beam.enabled = false;
    }

    void OnDisable()
    {
        StopSpray();
        _fire?.Disable();
    }
}
