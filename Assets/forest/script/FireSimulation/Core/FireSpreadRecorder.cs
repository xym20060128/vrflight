using UnityEngine;
using System.Collections.Generic;
using System.IO;

namespace FireSimulation
{
    /// <summary>
    /// 火灾蔓延数据记录器 — 定期快照全场景火焰状态，导出 JSON 供外部 Python 可视化。
    ///
    /// 使用方式:
    ///   1. 挂到场景中任意 GameObject 上
    ///   2. 运行场景，火灾开始后自动记录
    ///   3. 所有树熄灭或手动按 S 键停止记录，输出 JSON 到 persistentDataPath
    ///
    /// 输出文件: Application.persistentDataPath/fire_spread_log_yyyyMMdd_HHmmss.json
    /// </summary>
    public class FireSpreadRecorder : MonoBehaviour
    {
        [Header("=== 记录设置 ===")]
        [Tooltip("快照间隔 (秒)")]
        public float snapshotInterval = 1f;
        [Tooltip("最大快照数 (防止文件过大)")]
        public int maxSnapshots = 1000;
        [Tooltip("是否在 Start 时自动开始记录")]
        public bool autoStart = true;
        [Tooltip("停止记录的快捷键")]
        public KeyCode stopKey = KeyCode.S;

        [Header("=== 子系统引用 ===")]
        public FireSpreadSystem spreadSystem;

        private List<Snapshot> _snapshots = new();
        private float _timer;
        private bool _recording;
        private FireCell[] _allCells;
        private int _snapshotIndex;

        // JSON 序列化数据结构
        [System.Serializable]
        public struct Snapshot
        {
            public int snapshotIndex;
            public float time;
            public int burningCount;
            public int burnedCount;
            public int totalIgnitions;
            public float windSpeed;
            public Vector3Ser windDirection;
            public List<TreeRecord> trees;
            public List<FrontDetail> frontDetails;
        }

        [System.Serializable]
        public struct FrontDetail
        {
            public int sourceId;
            public int targetId;
            public float distance;
            public float advance;
            public float progressRatio;   // advance / distance
        }

        [System.Serializable]
        public struct TreeRecord
        {
            public int id;
            public string name;
            public Vector3Ser position;
            public float treeHeight;
            public string fuelType;
            public string state;        // idle / burning / burned
            public float burnHeight;    // 0-1
            public float temperature;
            public float fuelRemaining;
            public float moisture;
            public int torching;        // 0/1
        }

        [System.Serializable]
        public struct FrontRecord
        {
            public int sourceId;
            public int targetId;
            public float distance;
            public float advance;
            public float spreadRate;    // m/s
            public float firelineIntensity;
        }

        [System.Serializable]
        public struct Vector3Ser
        {
            public float x, y, z;
            public Vector3Ser(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        }

        void Start()
        {
            spreadSystem ??= FindObjectOfType<FireSpreadSystem>();
            _allCells = FindObjectsOfType<FireCell>();

            if (autoStart) StartRecording();
        }

        void Update()
        {
            if (!_recording) return;

            if (Input.GetKeyDown(stopKey))
            {
                StopRecording();
                return;
            }

            if (_snapshots.Count >= maxSnapshots)
            {
                Debug.LogWarning("[FireSpreadRecorder] 达到最大快照数，自动停止记录");
                StopRecording();
                return;
            }

            _timer += Time.deltaTime;
            if (_timer >= snapshotInterval)
            {
                _timer -= snapshotInterval;
                TakeSnapshot(Time.time);
            }
        }

        public void StartRecording()
        {
            _snapshots.Clear();
            _snapshotIndex = 0;
            _recording = true;
            _timer = 0f;
            Debug.Log("[FireSpreadRecorder] 开始记录火灾蔓延数据");
        }

        public void StopRecording()
        {
            _recording = false;
            ExportJson();
        }

        void TakeSnapshot(float time)
        {
            var snap = new Snapshot
            {
                time = time,
                snapshotIndex = _snapshotIndex,
                burningCount = spreadSystem != null ? spreadSystem.GetBurningCount() : 0,
                burnedCount = 0,
                totalIgnitions = 0,
                trees = new List<TreeRecord>(),
                frontDetails = new List<FrontDetail>()
            };

            if (spreadSystem != null)
            {
                var wm = spreadSystem.weatherManager;
                snap.windSpeed = wm != null ? wm.WindSpeed : 0f;
                var wd = wm != null ? wm.WindDirection : Vector3.forward;
                snap.windDirection = new Vector3Ser(wd.x, wd.y, wd.z);

                // 树数据
                int id = 0;
                foreach (var cell in _allCells)
                {
                    if (cell == null) continue;

                    var tree = new TreeRecord
                    {
                        id = id++,
                        name = cell.name,
                        position = new Vector3Ser(
                            cell.WorldPosition.x,
                            cell.TreeBaseY,
                            cell.WorldPosition.z),
                        treeHeight = cell.treeHeight > 0f ? cell.treeHeight : 10f,
                        fuelType = cell.fuelType.ToString(),
                        state = cell.CurrentState.ToString(),
                        burnHeight = cell.CurrentState == FireCellState.Burning ? cell.BurnHeight : 0f,
                        temperature = cell.Temperature,
                        fuelRemaining = cell.FuelRemaining,
                        moisture = cell.moisture,
                        torching = cell.IsTorching ? 1 : 0
                    };

                    if (cell.CurrentState == FireCellState.Burned)
                        snap.burnedCount++;

                    snap.trees.Add(tree);
                }

                // 火锋数据
                snap.frontDetails = new List<FrontDetail>();
                foreach (var (src, tgt, dist, advance) in spreadSystem.GetActiveFronts())
                {
                    int srcId = System.Array.IndexOf(_allCells, src);
                    int tgtId = System.Array.IndexOf(_allCells, tgt);
                    if (srcId < 0 || tgtId < 0) continue;

                    snap.frontDetails.Add(new FrontDetail
                    {
                        sourceId = srcId,
                        targetId = tgtId,
                        distance = dist,
                        advance = advance,
                        progressRatio = dist > 0.01f ? advance / dist : 0f
                    });
                }
            }

            _snapshots.Add(snap);
            _snapshotIndex++;
        }

        [System.Serializable]
        public class ExportRoot
        {
            public string exportTime;
            public float snapshotInterval;
            public int totalSnapshots;
            public List<CellSummary> cells;
            public List<Snapshot> snapshots;
        }

        [System.Serializable]
        public class CellSummary
        {
            public int id;
            public string name;
            public float x, y, z;
            public float treeHeight;
            public string fuelType;
        }

        void ExportJson()
        {
            if (_snapshots.Count == 0)
            {
                Debug.LogWarning("[FireSpreadRecorder] 无数据可导出");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"exportTime\": \"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}\",");
            sb.AppendLine($"  \"snapshotInterval\": {snapshotInterval},");
            sb.AppendLine($"  \"totalSnapshots\": {_snapshots.Count},");

            // cells 摘要
            sb.AppendLine("  \"cells\": [");
            int cid = 0;
            for (int i = 0; i < _allCells.Length; i++)
            {
                var c = _allCells[i];
                if (c == null) continue;
                if (cid > 0) sb.AppendLine(",");
                sb.Append("    {");
                sb.Append($"\"id\":{cid},");
                sb.Append($"\"name\":\"{c.name}\",");
                sb.Append($"\"x\":{c.WorldPosition.x:F3},\"y\":{c.TreeBaseY:F3},\"z\":{c.WorldPosition.z:F3},");
                sb.Append($"\"treeHeight\":{(c.treeHeight > 0f ? c.treeHeight : 10f):F2},");
                sb.Append($"\"fuelType\":\"{c.fuelType}\"");
                sb.Append("}");
                cid++;
            }
            sb.AppendLine();
            sb.AppendLine("  ],");

            // snapshots
            sb.AppendLine("  \"snapshots\": [");
            for (int si = 0; si < _snapshots.Count; si++)
            {
                if (si > 0) sb.AppendLine(",");
                var snap = _snapshots[si];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"snapshotIndex\": {snap.snapshotIndex},");
                sb.AppendLine($"      \"time\": {snap.time:F2},");
                sb.AppendLine($"      \"burningCount\": {snap.burningCount},");
                sb.AppendLine($"      \"burnedCount\": {snap.burnedCount},");
                sb.AppendLine($"      \"totalIgnitions\": {snap.totalIgnitions},");
                sb.AppendLine($"      \"windSpeed\": {snap.windSpeed:F2},");
                sb.Append($"      \"windDirection\": {{\"x\":{snap.windDirection.x.ToString("F2")},\"y\":{snap.windDirection.y.ToString("F2")},\"z\":{snap.windDirection.z.ToString("F2")}}},");
                sb.AppendLine();

                // trees
                sb.AppendLine("      \"trees\": [");
                for (int ti = 0; ti < snap.trees.Count; ti++)
                {
                    if (ti > 0) sb.AppendLine(",");
                    var t = snap.trees[ti];
                    sb.Append("        {");
                    sb.Append($"\"id\":{t.id},");
                    sb.Append($"\"name\":\"{t.name}\",");
                    sb.Append($"\"position\":{{\"x\":{t.position.x.ToString("F3")},\"y\":{t.position.y.ToString("F3")},\"z\":{t.position.z.ToString("F3")}}},");
                    sb.Append($"\"treeHeight\":{t.treeHeight:F2},");
                    sb.Append($"\"fuelType\":\"{t.fuelType}\",");
                    sb.Append($"\"state\":\"{t.state}\",");
                    sb.Append($"\"burnHeight\":{t.burnHeight:F3},");
                    sb.Append($"\"temperature\":{t.temperature:F1},");
                    sb.Append($"\"fuelRemaining\":{t.fuelRemaining:F3},");
                    sb.Append($"\"moisture\":{t.moisture:F3},");
                    sb.Append($"\"torching\":{t.torching}");
                    sb.Append("}");
                }
                if (snap.trees.Count > 0) sb.AppendLine();
                sb.AppendLine("      ],");

                // frontDetails
                sb.AppendLine("      \"frontDetails\": [");
                for (int fi = 0; fi < snap.frontDetails.Count; fi++)
                {
                    if (fi > 0) sb.AppendLine(",");
                    var f = snap.frontDetails[fi];
                    sb.Append("        {");
                    sb.Append($"\"sourceId\":{f.sourceId},");
                    sb.Append($"\"targetId\":{f.targetId},");
                    sb.Append($"\"distance\":{f.distance:F2},");
                    sb.Append($"\"advance\":{f.advance:F2},");
                    sb.Append($"\"progressRatio\":{f.progressRatio:F3}");
                    sb.Append("}");
                }
                if (snap.frontDetails.Count > 0) sb.AppendLine();
                sb.Append("      ]");

                sb.AppendLine();
                sb.Append("    }");
            }
            sb.AppendLine();
            sb.AppendLine("  ]");
            sb.AppendLine("}");

            string filename = $"fire_spread_log_{System.DateTime.Now:yyyyMMdd_HHmmss}.json";
            // 输出到项目根目录下的 fire_data 文件夹
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string outDir = Path.Combine(projectRoot, "fire_data");
            Directory.CreateDirectory(outDir);
            string path = Path.Combine(outDir, filename);
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[FireSpreadRecorder] 已导出 {_snapshots.Count} 个快照 ({_allCells.Length} 棵树) → {path}");
        }

        void OnDestroy()
        {
            if (_recording) ExportJson();
        }
    }
}
