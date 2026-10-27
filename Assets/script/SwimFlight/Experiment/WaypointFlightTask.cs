using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Unity.XR.CoreUtils;

namespace SwimFlight.Experiment
{
    /// <summary>
    /// 用户实验：按顺序飞过一组点位（Waypoint）。
    ///
    /// 功能：
    ///   1. 在点位之间自动生成一条引导线（每段一个 LineRenderer，已飞过段变绿）。
    ///   2. 用 XR Origin（或头显）与目标点位之间的 3D 距离做到达判定。
    ///   3. 到达某个点位 → 该点位变色（绿）→ 推进到下一个点位。
    ///   4. 到达最后一个点位 → 显示「任务完成」UI + 触发事件。
    ///
    /// 用法（最短配置）：
    ///   1. 在场景里放一个空物体，挂上本脚本。
    ///   2. 把你设计好的点位 Transform 按顺序拖进 waypoints 列表。
    ///   3. 直接运行：脚本会自动给没可视化的点位补一个球体、自动生成引导线。
    ///   4. 完成 UI：把任意 GameObject（通常是一个 Canvas）拖进 completionUI，
    ///      或在 Inspector 用「生成任务完成 UI」按钮一键创建。
    ///
    /// 到达判定默认用「头显(Camera)位置」，因为飞行时用户感知的是自己的头所在的位置；
    /// 若想用 XR Origin 位置，取消 useHeadForArrival 即可（会自动查找 XR Origin）。
    ///
    /// 注意：到达判定是「顺序」的——只有当前目标点位算数，飞过它之后才会判定下一个。
    /// </summary>
    public class WaypointFlightTask : MonoBehaviour
    {
        [Header("=== 点位 ===")]
        [Tooltip("按顺序飞过的点位。至少 2 个。")]
        public List<Transform> waypoints = new List<Transform>();

        [Tooltip("到达判定用的移动参考物。留空则自动查找 XR Origin。")]
        public Transform arrivalReference;

        [Tooltip("勾选 = 用头显(Camera)位置判定到达；取消 = 用 arrivalReference 位置。")]
        public bool useHeadForArrival = true;

        [Tooltip("到达判定半径 (m)。飞进该半径即算到达。")]
        public float arrivalRadius = 1.5f;

        [Tooltip("勾选 = 只比较水平距离(XZ)，忽略高度差；取消 = 3D 距离。")]
        public bool ignoreVerticalDistance = false;

        [Header("=== 引导线 ===")]
        [Tooltip("是否自动生成引导线。")]
        public bool generateGuideLine = true;

        [Tooltip("引导线宽度。")]
        public float lineWidth = 0.05f;

        [Tooltip("引导线相对点位中心的抬高量 (m)，可为负。")]
        public float lineYOffset = 0f;

        [Tooltip("未飞过段的颜色。")]
        public Color pendingLineColor = new Color(0.2f, 0.6f, 1f, 1f);

        [Tooltip("已飞过段的颜色。")]
        public Color reachedLineColor = new Color(0.2f, 1f, 0.3f, 1f);

        [Tooltip("引导线材质。留空则自动用 Sprites/Default。")]
        public Material lineMaterial;

        [Header("=== 点位显示 ===")]
        [Tooltip("自动给没有 Renderer 的点位补一个可视化球体。若点位是你自己拖入的圆盘等可见物体，请保持关闭。")]
        public bool autoCreateWaypointVisuals = false;

        [Tooltip("自动补的球体半径 (m)。")]
        public float waypointVisualRadius = 0.3f;

        [Tooltip("未到达点位的颜色。")]
        public Color waypointPendingColor = new Color(1f, 0.8f, 0.2f, 1f);

        [Tooltip("已到达点位的颜色。")]
        public Color waypointReachedColor = new Color(0.2f, 1f, 0.3f, 1f);

        [Header("=== 完成 UI ===")]
        [Tooltip("到达最后一个点位时显示的 UI 对象（会被 SetActive(true)）。")]
        public GameObject completionUI;

        [Tooltip("完成 UI 显示在头部正前方的距离 (m)。")]
        public float completionUIDistance = 1.5f;

        [Header("=== 事件（可用于实验数据采集） ===")]
        [Tooltip("到达某个点位时触发，参数 = 点位下标。")]
        public UnityEvent<int> onWaypointReached;

        [Tooltip("到达最后一个点位时触发。")]
        public UnityEvent onTaskComplete;

        [Header("=== 调试 ===")]
        [Tooltip("到达时打印日志。")]
        public bool logArrival = true;

        // === 运行时状态 ===
        private readonly List<LineRenderer> _segments = new List<LineRenderer>();
        private readonly List<Renderer> _waypointRenderers = new List<Renderer>();

        private Transform _head;
        private Material _lineMaterialInstance;
        private int _currentIndex;
        private bool _taskComplete;
        public bool ProgressEnabled { get; set; } = true;

        /// <summary>每个点位到达时的 Time.time 秒（顺序对应点位）。供实验数据导出。 </summary>
        public readonly List<float> reachedTimes = new List<float>();

        /// <summary>Unscaled seconds from the beginning of this trial, for cross-trial comparison.</summary>
        public readonly List<float> reachedElapsedTimes = new List<float>();
        private float _trialStartedAt;
        private float _completedElapsedTime;
        public float TrialElapsedSeconds => _taskComplete ? _completedElapsedTime
            : Mathf.Max(0f, Time.unscaledTime - _trialStartedAt);

        private void ResetTrialClock()
        {
            _trialStartedAt = Time.unscaledTime;
            _completedElapsedTime = 0f;
            reachedTimes.Clear();
            reachedElapsedTimes.Clear();
        }

        /// <summary>是否已完成全部点位。 </summary>
        public bool IsComplete => _taskComplete;

        /// <summary>当前正在飞向的点位下标。 </summary>
        public int CurrentIndex => _currentIndex;

        private static readonly int ColorProp = Shader.PropertyToID("_Color");
        private static readonly int BaseColorProp = Shader.PropertyToID("_BaseColor");

        private void Start()
        {
            MakeWaypointsPassThrough();
            ResolveReferences();
            if (autoCreateWaypointVisuals) EnsureWaypointVisuals();
            CacheWaypointRenderers();
            BuildGuideLine();

            _currentIndex = 0;
            _taskComplete = false;
            ResetTrialClock();

            if (completionUI != null) completionUI.SetActive(false);
            ApplyWaypointColors();
            UpdateSegmentColors();
        }

        private void Update()
        {
            if (!ProgressEnabled) return;
            if (_taskComplete) return;
            if (waypoints == null || waypoints.Count == 0) return;
            if (_currentIndex >= waypoints.Count) return;

            Transform target = waypoints[_currentIndex];
            if (target == null)   // 空点位直接跳过
            {
                _currentIndex++;
                return;
            }

            if (DistanceTo(target.position) <= arrivalRadius)
            {
                MarkWaypointReached(_currentIndex);
                reachedTimes.Add(Time.time);
                reachedElapsedTimes.Add(TrialElapsedSeconds);

                onWaypointReached.Invoke(_currentIndex);
                if (logArrival)
                    Debug.Log($"[WaypointTask] 到达点位 {_currentIndex} : {target.name}  (t={Time.time:F2}s)");

                _currentIndex++;
                UpdateSegmentColors();

                if (_currentIndex >= waypoints.Count)
                    CompleteTask();
            }
        }

        private void CompleteTask()
        {
            _completedElapsedTime = TrialElapsedSeconds;
            _taskComplete = true;
            if (completionUI != null)
            {
                completionUI.SetActive(true);
                PlaceCompletionUIInFrontOfHead(completionUI);
            }
            onTaskComplete.Invoke();
            if (logArrival) Debug.Log("[WaypointTask] 任务完成，全部点位已到达。");
        }

        /// <summary>把完成 UI 摆到头部正前方 completionUIDistance 处，并让它面向头部（仅 World Space 画布有效）。 </summary>
        private void PlaceCompletionUIInFrontOfHead(GameObject ui)
        {
            if (ui == null) return;

            if (_head == null) _head = FindHead();
            Transform head = _head != null ? _head
                : (arrivalReference != null ? arrivalReference : transform);

            Vector3 forward = head.forward;
            forward.y = 0f;   // 只取水平朝向，避免仰头时 UI 飞太高
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();

            ui.transform.position = head.position + forward * completionUIDistance;
            ui.transform.rotation = Quaternion.LookRotation(head.position - ui.transform.position, Vector3.up);
        }

        /// <summary>重置进度（重复实验用），不改变点位/引导线。 </summary>
        public void ResetProgress()
        {
            _currentIndex = 0;
            _taskComplete = false;
            ResetTrialClock();
            if (completionUI != null) completionUI.SetActive(false);
            ApplyWaypointColors();
            UpdateSegmentColors();
        }

        /// <summary>Task targets are visual markers, not solid obstacles. Arrival uses distance.</summary>
        public void MakeWaypointsPassThrough()
        {
            if (waypoints == null) return;
            foreach (var waypoint in waypoints)
            {
                if (waypoint == null) continue;
                foreach (var collider in waypoint.GetComponentsInChildren<Collider>(true))
                    collider.enabled = false;
            }
        }

        /// <summary>给没有 Renderer 的点位补一个可视化球体（幂等，编辑态/运行态均可调用）。 </summary>
        public void EnsureWaypointVisuals()
        {
            if (waypoints == null) return;
            foreach (var wp in waypoints)
            {
                if (wp == null) continue;
                if (wp.GetComponentInChildren<Renderer>() != null) continue;

                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "WaypointVisual";
                RemoveCollider(go.GetComponent<Collider>());
                go.transform.SetParent(wp, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localScale = Vector3.one * (waypointVisualRadius * 2f);
            }
        }

        /// <summary>重建引导线（清空旧段并按当前点位重建）。 </summary>
        public void RebuildGuideLine()
        {
            BuildGuideLine();
            ApplyWaypointColors();
            UpdateSegmentColors();
        }

        // ---------- 内部实现 ----------

        private void ResolveReferences()
        {
            if (arrivalReference == null)
            {
                var origin = FindObjectOfType<XROrigin>();
                if (origin != null) arrivalReference = origin.transform;
            }
            _head = FindHead();
        }

        /// <summary>解析「头」的 Transform：优先 XR Origin 下的相机，其次 Camera.main。 </summary>
        private Transform FindHead()
        {
            var origin = FindObjectOfType<XROrigin>();
            if (origin != null)
            {
                var cam = origin.GetComponentInChildren<Camera>(true);
                if (cam != null) return cam.transform;
            }
            return Camera.main != null ? Camera.main.transform : null;
        }

        private Vector3 GetArrivalPosition()
        {
            if (useHeadForArrival)
            {
                if (_head == null) _head = FindHead(); // XR 相机可能在 Start 之后才就绪，惰性解析
                if (_head != null) return _head.position;
            }
            if (arrivalReference != null) return arrivalReference.position;
            return transform.position;
        }

        private float DistanceTo(Vector3 point)
        {
            Vector3 a = GetArrivalPosition();
            if (ignoreVerticalDistance) { a.y = 0f; point.y = 0f; }
            return Vector3.Distance(a, point);
        }

        private void CacheWaypointRenderers()
        {
            _waypointRenderers.Clear();
            if (waypoints == null) return;
            foreach (var wp in waypoints)
                _waypointRenderers.Add(wp != null ? wp.GetComponentInChildren<Renderer>() : null);
        }

        private void MarkWaypointReached(int index)
        {
            if (index < 0 || index >= _waypointRenderers.Count) return;
            SetWaypointColor(_waypointRenderers[index], waypointReachedColor);
        }

        private void ApplyWaypointColors()
        {
            for (int i = 0; i < _waypointRenderers.Count; i++)
            {
                bool reached = i < _currentIndex;
                SetWaypointColor(_waypointRenderers[i],
                    reached ? waypointReachedColor : waypointPendingColor);
            }
        }

        /// <summary>直接改拖入物体（圆盘等）的材质颜色，兼容 Built-in(_Color) 与 URP(_BaseColor)。 </summary>
        private void SetWaypointColor(Renderer r, Color c)
        {
            if (r == null) return;
            // r.material 会返回该物体独立的材质实例，改它不会影响其它物体/共享材质。
            var mat = r.material;
            if (mat == null) return;
            if (mat.HasProperty(BaseColorProp)) mat.SetColor(BaseColorProp, c);
            else if (mat.HasProperty(ColorProp)) mat.SetColor(ColorProp, c);
        }

        private void BuildGuideLine()
        {
            ClearGuideLine();
            if (!generateGuideLine || waypoints == null || waypoints.Count < 2) return;

            var parent = new GameObject("GuideLine").transform;
            parent.SetParent(transform, false);

            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                var go = new GameObject($"Segment_{i}_{i + 1}");
                go.transform.SetParent(parent, false);

                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.widthMultiplier = lineWidth;
                lr.numCapVertices = 4;
                lr.numCornerVertices = 4;
                lr.sharedMaterial = GetLineMaterial();
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;

                _segments.Add(lr);
            }

            RefreshLinePositions();
            UpdateSegmentColors();
        }

        private void ClearGuideLine()
        {
            foreach (var seg in _segments)
                if (seg != null) Destroy(seg.gameObject);
            _segments.Clear();
        }

        private void RefreshLinePositions()
        {
            for (int i = 0; i < _segments.Count; i++)
            {
                if (i + 1 >= waypoints.Count) break;
                if (waypoints[i] == null || waypoints[i + 1] == null) continue;
                Vector3 a = waypoints[i].position + Vector3.up * lineYOffset;
                Vector3 b = waypoints[i + 1].position + Vector3.up * lineYOffset;
                _segments[i].SetPosition(0, a);
                _segments[i].SetPosition(1, b);
            }
        }

        private void UpdateSegmentColors()
        {
            for (int i = 0; i < _segments.Count; i++)
            {
                Color c = i < _currentIndex ? reachedLineColor : pendingLineColor;
                _segments[i].startColor = c;
                _segments[i].endColor = c;
            }
        }

        private Material GetLineMaterial()
        {
            if (lineMaterial != null) return lineMaterial;
            if (_lineMaterialInstance == null)
            {
                var shader = Shader.Find("Sprites/Default");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                _lineMaterialInstance = new Material(shader) { name = "WaypointLineMaterial" };
            }
            return _lineMaterialInstance;
        }

        private static void RemoveCollider(Collider col)
        {
            if (col == null) return;
            if (Application.isPlaying) Destroy(col);
            else DestroyImmediate(col);
        }

        private void OnDrawGizmos()
        {
            if (waypoints == null) return;

            // 到达判定半径球
            Vector3 refPos = Application.isPlaying
                ? GetArrivalPosition()
                : (arrivalReference != null ? arrivalReference.position : transform.position);
            Gizmos.color = new Color(0f, 1f, 1f, 0.35f);
            Gizmos.DrawWireSphere(refPos, arrivalRadius);

            // 路径线
            Gizmos.color = pendingLineColor;
            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                if (waypoints[i] == null || waypoints[i + 1] == null) continue;
                Gizmos.DrawLine(waypoints[i].position, waypoints[i + 1].position);
            }

            // 点位
            for (int i = 0; i < waypoints.Count; i++)
            {
                if (waypoints[i] == null) continue;
                bool reached = Application.isPlaying && i < _currentIndex;
                Gizmos.color = reached ? waypointReachedColor : waypointPendingColor;
                Gizmos.DrawWireSphere(waypoints[i].position, waypointVisualRadius);
            }
        }

        private void OnValidate()
        {
            arrivalRadius = Mathf.Max(0.05f, arrivalRadius);
            waypointVisualRadius = Mathf.Max(0.05f, waypointVisualRadius);
        }
    }
}
