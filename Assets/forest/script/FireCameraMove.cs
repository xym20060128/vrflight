//using UnityEngine;

//public class FireCameraMove : MonoBehaviour
//{
//    [Header("移动速度")]
//    public float moveSpeed = 5f;
//    [Header("鼠标灵敏度")]
//    public float mouseSensitivity = 2f;

//    private float rotX;
//    private float rotY;

//    void Start()
//    {
//        // 开局直接禁用移动，不会乱跑飞走
//        enabled = false;
//    }

//    void Update()
//    {
//        // 鼠标旋转视角
//        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
//        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

//        rotY += mouseX;
//        rotX -= mouseY;
//        rotX = Mathf.Clamp(rotX, -85f, 85f);
//        transform.localRotation = Quaternion.Euler(rotX, rotY, 0);

//        // WASD前后左右移动
//        float horizontal = Input.GetAxis("Horizontal");
//        float vertical = Input.GetAxis("Vertical");
//        Vector3 moveDir = transform.right * horizontal + transform.forward * vertical;
//        transform.position += moveDir.normalized * moveSpeed * Time.deltaTime;
//    }

//    // 对外开关方法，名字统一 SetActiveControl
//    public void SetActiveControl(bool open)
//    {
//        enabled = open;
//        if (open)
//        {
//            Cursor.lockState = CursorLockMode.Locked;
//            Cursor.visible = false;
//        }
//        else
//        {
//            Cursor.lockState = CursorLockMode.None;
//            Cursor.visible = true;
//        }
//    }
//}

//using UnityEngine;

//public class FireCameraMove : MonoBehaviour
//{
//    [Header("移动速度")]
//    public float moveSpeed = 5f;
//    [Header("旋转灵敏度")]
//    public float rotateSens = 2f;

//    [Header("喷水灭火设置")]
//    public Transform waterMuzzle; // 喷水枪口空物体，放在相机前方
//    public float waterRange = 15f;
//    public KeyCode sprayKey = KeyCode.Mouse0; // 鼠标左键喷水

//    private bool canControl = false;
//    private float rotX, rotY;

//    void Start()
//    {
//        rotX = transform.localEulerAngles.x;
//        rotY = transform.localEulerAngles.y;
//    }

//    public void SetActiveControl(bool active)
//    {
//        canControl = active;
//        if (!canControl)
//        {
//            rotX = transform.localEulerAngles.x;
//            rotY = transform.localEulerAngles.y;
//        }
//    }

//    void Update()
//    {
//        // 没开启灭火视角操控，直接全部跳过
//        if (!canControl) return;

//        // 鼠标旋转相机
//        rotX -= Input.GetAxis("Mouse Y") * rotateSens;
//        rotY += Input.GetAxis("Mouse X") * rotateSens;
//        transform.localRotation = Quaternion.Euler(rotX, rotY, 0);

//        // WASD平移
//        float h = Input.GetAxis("Horizontal");
//        float v = Input.GetAxis("Vertical");
//        Vector3 dir = transform.right * h + transform.forward * v;
//        transform.position += dir * moveSpeed * Time.deltaTime;

//        // ========== 喷水射线检测 ==========
//        if (Input.GetMouseButton(0)) // 按住鼠标左键持续喷水
//        {
//            if (waterMuzzle == null) return;

//            Ray ray = new Ray(waterMuzzle.position, waterMuzzle.forward);
//            if (Physics.Raycast(ray, out RaycastHit hit, waterRange))
//            {
//                FireExtinguish fire = hit.collider.GetComponent<FireExtinguish>();
//                if (fire != null)
//                {
//                    fire.PutOutFire();
//                }
//            }
//        }
//    }
//}

//using UnityEngine;
//using System.Collections.Generic;

//public class FireCameraMove : MonoBehaviour
//{
//    [Header("移动速度")]
//    public float moveSpeed = 5f;
//    [Header("旋转灵敏度")]
//    public float rotateSens = 2f;

//    [Header("喷水灭火设置")]
//    public Transform waterMuzzle; // 喷水枪口空物体，放在相机前方
//    public float waterRange = 15f;
//    public KeyCode sprayKey = KeyCode.Mouse0; // 鼠标左键喷水

//    [Header("直升机模式切换")]
//    public bool isHelicopterMode = false;

//    private bool canControl = false;
//    private float rotX, rotY;
//    private FireExtinguish currentHitFire = null;

//    void Start()
//    {
//        rotX = transform.localEulerAngles.x;
//        rotY = transform.localEulerAngles.y;
//    }

//    public void SetActiveControl(bool active)
//    {
//        canControl = active;
//        if (!canControl)
//        {
//            rotX = transform.localEulerAngles.x;
//            rotY = transform.localEulerAngles.y;
//            // 停止所有火焰浇水状态
//            if (currentHitFire != null)
//            {
//                currentHitFire.StopWater();
//                currentHitFire = null;
//            }
//        }
//    }

//    void Update()
//    {
//        // 直升机模式 或 未开启操控 → 全部禁用
//        if (!canControl || isHelicopterMode)
//        {
//            // 松开喷水，停止火焰浇水
//            if (currentHitFire != null)
//            {
//                currentHitFire.StopWater();
//                currentHitFire = null;
//            }
//            return;
//        }

//        // 鼠标旋转相机
//        rotX -= Input.GetAxis("Mouse Y") * rotateSens;
//        rotY += Input.GetAxis("Mouse X") * rotateSens;
//        transform.localRotation = Quaternion.Euler(rotX, rotY, 0);

//        // WASD平移
//        float h = Input.GetAxis("Horizontal");
//        float v = Input.GetAxis("Vertical");
//        Vector3 dir = transform.right * h + transform.forward * v;
//        transform.position += dir * moveSpeed * Time.deltaTime;

//        // ========== 喷水射线检测 ==========
//        if (Input.GetMouseButton(0)) // 按住鼠标左键持续喷水
//        {
//            if (waterMuzzle == null) return;

//            Ray ray = new Ray(waterMuzzle.position, waterMuzzle.forward);
//            if (Physics.Raycast(ray, out RaycastHit hit, waterRange))
//            {
//                FireExtinguish fire = hit.collider.GetComponent<FireExtinguish>();
//                if (fire != null)
//                {
//                    // 不再直接瞬间灭火，改为持续浇水压制
//                    fire.StartWater();
//                    currentHitFire = fire;
//                }
//                else
//                {
//                    // 射线打到别的物体，停止之前火焰
//                    if (currentHitFire != null)
//                    {
//                        currentHitFire.StopWater();
//                        currentHitFire = null;
//                    }
//                }
//            }
//            else
//            {
//                // 射线没命中任何物体，停止浇水
//                if (currentHitFire != null)
//                {
//                    currentHitFire.StopWater();
//                    currentHitFire = null;
//                }
//            }
//        }
//        else
//        {
//            // 松开鼠标，停止浇水
//            if (currentHitFire != null)
//            {
//                currentHitFire.StopWater();
//                currentHitFire = null;
//            }
//        }
//    }
//}

//using UnityEngine;

//public class FireCameraMove : MonoBehaviour
//{
//    [Header("移动速度")]
//    public float moveSpeed = 3f;
//    [Header("鼠标旋转设置")]
//    [Tooltip("原始灵敏度，建议调低到0.8~1.2")]
//    public float rotateSens = 0.9f;
//    [Tooltip("旋转平滑缓冲，数值越大越顺滑、延迟越高")]
//    public float rotateSmooth = 12f;
//    [Tooltip("垂直视角最大上下角度，防止翻转抖动")]
//    public float maxVerticalAngle = 80f;

//    [Header("喷水灭火设置")]
//    public Transform waterMuzzle;
//    public float waterRange = 15f;

//    [Header("直升机模式切换")]
//    public bool isHelicopterMode = false;

//    private bool canControl = false;
//    private float rotX, rotY;
//    private float targetRotX, targetRotY; // 平滑目标角度
//    private FireExtinguish currentHitFire = null;

//    void Start()
//    {
//        rotX = transform.localEulerAngles.x;
//        rotY = transform.localEulerAngles.y;
//        targetRotX = rotX;
//        targetRotY = rotY;
//    }

//    public void SetActiveControl(bool active)
//    {
//        canControl = active;
//        StopSprayImmediately();
//        if (!canControl)
//        {
//            rotX = transform.localEulerAngles.x;
//            rotY = transform.localEulerAngles.y;
//            targetRotX = rotX;
//            targetRotY = rotY;
//        }
//    }

//    // 立刻停止喷水、火焰停止浇水
//    void StopSprayImmediately()
//    {
//        if (currentHitFire != null)
//        {
//            currentHitFire.StopWater();
//            currentHitFire = null;
//        }
//    }

//    void Update()
//    {
//        // ESC切换视角，关闭操控
//        if (Input.GetKeyDown(KeyCode.Escape))
//        {
//            SetActiveControl(false);
//            return;
//        }

//        // 直升机模式 / 无操控 直接退出
//        if (!canControl || isHelicopterMode)
//        {
//            StopSprayImmediately();
//            return;
//        }

//        // ========== 鼠标平滑旋转（解决灵敏、抖动核心） ==========
//        float mouseX = Input.GetAxis("Mouse X") * rotateSens;
//        float mouseY = Input.GetAxis("Mouse Y") * rotateSens;

//        targetRotX -= mouseY;
//        targetRotY += mouseX;

//        // 限制上下俯仰角度，防止镜头翻转剧烈抖动
//        targetRotX = Mathf.Clamp(targetRotX, -maxVerticalAngle, maxVerticalAngle);

//        // 平滑插值过渡角度，消除突变抖动
//        rotX = Mathf.Lerp(rotX, targetRotX, rotateSmooth * Time.deltaTime);
//        rotY = Mathf.Lerp(rotY, targetRotY, rotateSmooth * Time.deltaTime);

//        transform.localRotation = Quaternion.Euler(rotX, rotY, 0);

//        // ========== WASD移动 ==========
//        float h = Input.GetAxis("Horizontal");
//        float v = Input.GetAxis("Vertical");
//        Vector3 dir = transform.right * h + transform.forward * v;
//        transform.position += dir * moveSpeed * Time.deltaTime;

//        // ========== 喷水射线检测 ==========
//        if (Input.GetMouseButton(0))
//        {
//            if (waterMuzzle == null) return;
//            Ray ray = new Ray(waterMuzzle.position, waterMuzzle.forward);
//            if (Physics.Raycast(ray, out RaycastHit hit, waterRange))
//            {
//                FireExtinguish fire = hit.collider.GetComponent<FireExtinguish>();
//                if (fire != null)
//                {
//                    fire.StartWater();
//                    currentHitFire = fire;
//                }
//                else
//                {
//                    StopSprayImmediately();
//                }
//            }
//            else
//            {
//                StopSprayImmediately();
//            }
//        }
//        else
//        {
//            StopSprayImmediately();
//        }
//    }
//}

using UnityEngine;

public class FireCameraMove : MonoBehaviour
{
    [Header("移动速度")]
    public float moveSpeed = 3f;
    [Header("鼠标旋转设置")]
    public float rotateSens = 0.6f;
    public float rotateSmooth = 12f;
    public float maxVerticalAngle = 80f;

    [Header("喷水灭火设置")]
    public Transform waterMuzzle;
    public float waterRange = 15f;
    public GameObject waterParticleObj;
    [Tooltip("喷水射线忽略的层（火焰粒子等），建议排除火焰特效所在的层")]
    public LayerMask sprayLayerMask = ~0;
    [Tooltip("喷水键（直升机模式用空格，地面模式用鼠标左键也生效）")]
    public KeyCode sprayKey = KeyCode.Mouse0;

    [Header("灭火系统引用")]
    public FireSimulation.FireExtinguishManager extinguishManager;

    [Header("山地地面吸附参数")]
    public float groundCheckDist = 2.5f;
    public float groundLerpSpeed = 15f;
    public float cameraFloorOffset = 0.6f;
    [Tooltip("地面检测层蒙版")]
    public LayerMask groundLayer = -1;

    [Header("直升机模式切换")]
    public bool isHelicopterMode = false;

    private bool canControl;
    private float rotX, rotY;
    private float targetRotX, targetRotY;
    private float targetGroundHeight;
    private LineRenderer _waterBeam;

    void Start()
    {
        rotX = transform.localEulerAngles.x;
        rotY = transform.localEulerAngles.y;
        targetRotX = rotX;
        targetRotY = rotY;
        targetGroundHeight = transform.position.y;

        extinguishManager ??= FindObjectOfType<FireSimulation.FireExtinguishManager>();

        // 创建水柱 LineRenderer
        _waterBeam = GetComponent<LineRenderer>();
        if (_waterBeam == null) _waterBeam = gameObject.AddComponent<LineRenderer>();
        _waterBeam.positionCount = 2;
        _waterBeam.startWidth = 0.06f;
        _waterBeam.endWidth = 0.03f;
        _waterBeam.material = new Material(Shader.Find("Sprites/Default"));
        _waterBeam.startColor = new Color(1f, 1f, 1f, 0f);
        _waterBeam.endColor = new Color(1f, 1f, 1f, 0f);
        _waterBeam.enabled = false;

        if (waterParticleObj != null)
            waterParticleObj.SetActive(false);
    }

    public void SetActiveControl(bool active)
    {
        canControl = active;
        if (waterParticleObj != null) waterParticleObj.SetActive(false);
        if (_waterBeam != null) _waterBeam.enabled = false;

        if (!canControl)
        {
            rotX = transform.localEulerAngles.x;
            rotY = transform.localEulerAngles.y;
            targetRotX = rotX;
            targetRotY = rotY;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape)) { SetActiveControl(false); return; }

        // === 喷水：两种模式下都能工作 ===
        bool spraying = Input.GetKey(sprayKey) || Input.GetMouseButton(0);
        if (spraying && canControl)
        {
            DoSpray();
        }
        else
        {
            if (waterParticleObj != null) waterParticleObj.SetActive(false);
            if (_waterBeam != null) _waterBeam.enabled = false;
        }

        // === 地面灭火模式：WASD + 鼠标旋转 ===
        if (!canControl || isHelicopterMode) return;

        float mouseX = Input.GetAxis("Mouse X") * rotateSens;
        float mouseY = Input.GetAxis("Mouse Y") * rotateSens;
        targetRotX -= mouseY;
        targetRotY += mouseX;
        targetRotX = Mathf.Clamp(targetRotX, -maxVerticalAngle, maxVerticalAngle);
        rotX = Mathf.Lerp(rotX, targetRotX, rotateSmooth * Time.deltaTime);
        rotY = Mathf.Lerp(rotY, targetRotY, rotateSmooth * Time.deltaTime);
        transform.localRotation = Quaternion.Euler(rotX, rotY, 0);

        // Fix #9: 地面射线加 LayerMask，避免打到树上
        Ray groundRay = new Ray(transform.position, Vector3.down);
        if (Physics.Raycast(groundRay, out RaycastHit groundHit, groundCheckDist, groundLayer))
            targetGroundHeight = groundHit.point.y + cameraFloorOffset;

        Vector3 horizontalForward = transform.forward; horizontalForward.y = 0; horizontalForward.Normalize();
        Vector3 horizontalRight = transform.right; horizontalRight.y = 0; horizontalRight.Normalize();
        Vector3 newPos = transform.position + (horizontalRight * Input.GetAxis("Horizontal") + horizontalForward * Input.GetAxis("Vertical")) * moveSpeed * Time.deltaTime;
        newPos.y = Mathf.Lerp(transform.position.y, targetGroundHeight, groundLerpSpeed * Time.deltaTime);
        transform.position = newPos;
    }

    /// <summary>
    /// 喷水逻辑，地面模式和直升机模式共用。
    /// 地面模式：沿 waterMuzzle.forward 射线；直升机模式：从飞机下方垂直喷水。
    /// </summary>
    void DoSpray()
    {
        if (waterMuzzle == null)
        {
            Debug.LogWarning("[FireCameraMove] waterMuzzle 未赋值！请在 Inspector 拖入喷水枪口 Transform");
            return;
        }

        if (waterParticleObj != null && !waterParticleObj.activeSelf)
            waterParticleObj.SetActive(true);

        // 直升机模式：向下喷水；地面模式：沿枪口朝向喷水
        Vector3 sprayOrigin = isHelicopterMode ? transform.position : waterMuzzle.position;
        Vector3 sprayDir = isHelicopterMode ? Vector3.down : waterMuzzle.forward;

        Ray sprayRay = new Ray(sprayOrigin, sprayDir);
        Debug.DrawRay(sprayRay.origin, sprayRay.direction * waterRange, Color.blue, 0.1f);

        // 使用 RaycastAll 穿透火焰粒子等特效，找到真正的 FireCell
        RaycastHit[] allHits = Physics.RaycastAll(sprayRay, waterRange, sprayLayerMask);
        System.Array.Sort(allHits, (a, b) => a.distance.CompareTo(b.distance));

        RaycastHit? treeHit = null;
        RaycastHit? fallbackHit = null;

        foreach (var h in allHits)
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
                treeHit = h;
                break;
            }

            if (!fallbackHit.HasValue && !n.Contains("terrain") && !n.Contains("ground"))
                fallbackHit = h;
        }

        var finalHit = treeHit ?? fallbackHit;
        Vector3 endPoint = sprayOrigin + sprayDir * waterRange;

        if (finalHit.HasValue)
        {
            var hit = finalHit.Value;
            endPoint = hit.point;
            if (waterParticleObj != null)
            {
                waterParticleObj.transform.position = hit.point;
                waterParticleObj.transform.forward = -sprayRay.direction;
            }
            extinguishManager?.SprayAt(hit.point, Time.deltaTime);
        }

        // 绘制水柱 LineRenderer
        _waterBeam.enabled = true;
        _waterBeam.SetPosition(0, sprayOrigin);
        _waterBeam.SetPosition(1, endPoint);
    }
}