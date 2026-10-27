using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// 直升机广域洒水灭火 — 挂直升机 GameObject 上。
/// 按空格向下洒水，同时播放水粒子 + 绘制水柱 LineRenderer。
/// </summary>
public class HelicopterSprayer : MonoBehaviour
{
    [Header("=== 洒水范围 ===")]
    [Tooltip("洒水覆盖半径 (米)")]
    public float sprayRadius = 15f;
    [Tooltip("洒水最大高度差 (米)")]
    public float sprayDepth = 80f;

    [Header("=== 降温效率 ===")]
    public float coolingPerSecond = 5000f;

    [Header("=== 输入 ===")]
    [Tooltip("XR Input Action Asset，须含 action map: Helicopter / action: Fire，绑定到手柄扳机")]
    public InputActionAsset xrInputActions;

    private InputAction _xrFire;

    [Header("=== 引用 ===")]
    public FireSimulation.FireSpreadSystem spreadSystem;
    public FireSimulation.FireExtinguishManager extinguishManager;

    [Header("=== 水粒子 ===")]
    [Tooltip("直升机下方的水粒子 GameObject（粒子系统 Prefab 实例）")]
    public GameObject waterParticleObj;
    [Tooltip("粒子挂载点 Transform，粒子跟随此位置")]
    public Transform particleMountPoint;

    [Header("=== 水柱 ===")]
    [Tooltip("是否启用 LineRenderer 水柱")]
    public bool showWaterBeam = true;

    private List<FireSimulation.FireCell> _burningCache = new();
    private LineRenderer _waterBeam;
    private float _logTimer;
    private bool _wasSpraying;
    private float _groundY;

    void Start()
    {
        spreadSystem ??= FindObjectOfType<FireSimulation.FireSpreadSystem>();
        extinguishManager ??= FindObjectOfType<FireSimulation.FireExtinguishManager>();

        if (xrInputActions != null)
        {
            var map = xrInputActions.FindActionMap("Helicopter");
            if (map != null)
            {
                _xrFire = map.FindAction("Fire");
                _xrFire?.Enable();
            }
        }

        if (showWaterBeam)
        {
            _waterBeam = GetComponent<LineRenderer>();
            if (_waterBeam == null) _waterBeam = gameObject.AddComponent<LineRenderer>();
            _waterBeam.positionCount = 2;
            _waterBeam.startWidth = 0.15f;
            _waterBeam.endWidth = 0.08f;
            _waterBeam.material = new Material(Shader.Find("Sprites/Default"));
            _waterBeam.startColor = new Color(1f, 1f, 1f, 0f);
            _waterBeam.endColor = new Color(1f, 1f, 1f, 0f);
            _waterBeam.enabled = false;
        }

        if (waterParticleObj != null)
            waterParticleObj.SetActive(false);
    }

    void Update()
    {
        bool spraying = _xrFire != null && _xrFire.IsPressed();

        if (spraying)
        {
            DoWideSpray();
            UpdateParticles(true);

            // 水柱
            if (_waterBeam != null)
            {
                _waterBeam.enabled = true;
                Vector3 origin = particleMountPoint != null ? particleMountPoint.position : transform.position;
                _waterBeam.SetPosition(0, origin);
                _waterBeam.SetPosition(1, new Vector3(origin.x, _groundY, origin.z));
            }
        }
        else if (_wasSpraying)
        {
            UpdateParticles(false);
            if (_waterBeam != null) _waterBeam.enabled = false;
        }

        _wasSpraying = spraying;
    }

    void DoWideSpray()
    {
        if (spreadSystem == null) return;

        _burningCache.Clear();
        _burningCache.AddRange(spreadSystem.GetBurningCells());

        float coolingThisFrame = coolingPerSecond * Time.deltaTime;
        Vector3 heliPos = transform.position;
        int cooledCount = 0;
        float rayGround = heliPos.y - sprayDepth;

        foreach (var cell in _burningCache)
        {
            if (cell == null || cell.CurrentState != FireSimulation.FireCellState.Burning) continue;

            Vector3 treePos = cell.WorldPosition;

            float dy = heliPos.y - treePos.y;
            if (dy < 0f || dy > sprayDepth) continue;

            float dx = heliPos.x - treePos.x;
            float dz = heliPos.z - treePos.z;
            float hDist = Mathf.Sqrt(dx * dx + dz * dz);
            if (hDist > sprayRadius) continue;

            float attenuation = 1f - (hDist / sprayRadius);
            attenuation = Mathf.Clamp01(attenuation);

            cell.RemoveHeat(coolingThisFrame * attenuation);
            cell.MarkExternalCooling();
            cooledCount++;

            // 记录最低地面高度用于水柱终点
            if (treePos.y < rayGround) rayGround = treePos.y;
        }

        // 射线找地面高度用于水柱落点
        if (Physics.Raycast(transform.position, Vector3.down, out RaycastHit hit, sprayDepth))
            _groundY = hit.point.y;

        _logTimer += Time.deltaTime;
        if (_logTimer >= 1f)
        {
            _logTimer = 0f;
            Debug.Log($"[HelicopterSprayer] 全局燃烧 {_burningCache.Count} 棵 | 覆盖 {cooledCount} 棵 | 降温 {coolingThisFrame:F0}°C/frame");
        }
    }

    void UpdateParticles(bool on)
    {
        if (waterParticleObj == null) return;

        if (on)
        {
            // 粒子位置跟随挂载点或直升机
            Vector3 spawnPos = particleMountPoint != null
                ? particleMountPoint.position
                : transform.position - Vector3.up * 2f;

            if (!waterParticleObj.activeSelf)
                waterParticleObj.SetActive(true);

            waterParticleObj.transform.position = spawnPos;
        }
        else
        {
            waterParticleObj.SetActive(false);
        }
    }

    void OnDisable()
    {
        if (waterParticleObj != null) waterParticleObj.SetActive(false);
        if (_waterBeam != null) _waterBeam.enabled = false;
        _xrFire?.Disable();
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.3f);
        Vector3 pos = particleMountPoint != null ? particleMountPoint.position : transform.position;
        Gizmos.DrawWireSphere(pos, sprayRadius);
        Gizmos.DrawLine(pos, pos + Vector3.down * sprayDepth);
    }
}
