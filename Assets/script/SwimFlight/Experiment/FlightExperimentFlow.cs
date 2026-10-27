using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace SwimFlight.Experiment
{
    // Runs after the switcher and waypoint task have initialized their references.
    [DefaultExecutionOrder(100)]
    public sealed class FlightExperimentFlow : MonoBehaviour
    {
        public FlightControlModeSwitcher switcher;
        public WaypointFlightTask task;
        [Min(0)] public float practiceSecondsPerMode = 60f;
        [Min(0)] public float minimumRestSeconds;
        public FlightControlMode[] order = { FlightControlMode.Biomechanical,
            FlightControlMode.Joystick, FlightControlMode.IntentionalHeadMotion };
        public enum Phase { Practice, Task, BetweenTrials, Finished }
        public Phase CurrentPhase { get; private set; }
        public string DataPath { get; private set; }
        private readonly float[] practice = new float[3];
        private Canvas canvas;
        private Text title, message;
        private Button action;
        private Font font;
        private int trial = -1;
        private float breakStarted;
        private bool ready;
        private Vector3 startPosition;
        private Quaternion startRotation;
        private Session session;
        private string saveError;
        private static readonly string[] Labels = { "手臂划水", "手柄摇杆", "前倾＋摇杆" };
        [Serializable] public sealed class TrialResult
        {
            public string mode, completedUtc;
            public int trialIndex;
            public float elapsedSeconds;
            public float[] waypointSeconds;
        }
        [Serializable] public sealed class Session
        {
            public string id, startedUtc, status = "in_progress";
            public FlightControlMode[] order;
            public ComparisonMotionSettings motion;
            public string armSettingsJson, leanSettingsJson;
            public float arrivalRadius, minimumRestSeconds;
            public bool ignoreVerticalDistance;
            public Vector3 startPosition;
            public Quaternion startRotation;
            public Vector3[] waypoints;
            public float[] practiceSeconds;
            public string questionnaireMedium = "paper";
            public List<TrialResult> trials = new List<TrialResult>();
        }

        private void Start()
        {
            if (switcher == null) switcher = GetComponent<FlightControlModeSwitcher>();
            if (task == null) task = FindObjectOfType<WaypointFlightTask>();
            if (switcher == null || task == null || switcher.StabilizerOrigin == null ||
                switcher.biomechanicalController == null || switcher.joystickController == null ||
                order == null || order.Length != 3 || new HashSet<FlightControlMode>(order).Count != 3 ||
                Array.Exists(order, mode => (int)mode < 0 || (int)mode > 2) ||
                task.waypoints == null || task.waypoints.Count == 0 || task.waypoints.Exists(w => w == null))
            {
                Debug.LogError("[Experiment] Missing controllers, origin, valid mode order or waypoints.");
                if (switcher != null) switcher.SetMotionPaused(true);
                if (task != null) task.ProgressEnabled = false;
                enabled = false;
                return;
            }
            startPosition = switcher.StabilizerOrigin.position;
            startRotation = switcher.StabilizerOrigin.rotation;
            task.ProgressEnabled = false;
            task.onTaskComplete.AddListener(OnTaskComplete);
            task.useHeadForArrival = false;
            task.arrivalReference = switcher.StabilizerOrigin;
            if (task.completionUI != null) task.completionUI.SetActive(false);
            session = new Session { id = Guid.NewGuid().ToString("N"),
                startedUtc = DateTime.UtcNow.ToString("o"), order = (FlightControlMode[])order.Clone(),
                motion = JsonUtility.FromJson<ComparisonMotionSettings>(JsonUtility.ToJson(switcher.comparison)),
                armSettingsJson = JsonUtility.ToJson(switcher.biomechanicalController),
                leanSettingsJson = JsonUtility.ToJson(switcher.intentionalHeadMotionController),
                arrivalRadius = task.arrivalRadius, ignoreVerticalDistance = task.ignoreVerticalDistance,
                startPosition = startPosition, startRotation = startRotation,
                minimumRestSeconds = minimumRestSeconds,
                waypoints = task.waypoints.ConvertAll(w => w.position).ToArray(), practiceSeconds = practice };
            DataPath = Path.Combine(Application.persistentDataPath, "FlightExperiments", session.id + ".json");
            BuildUI();
            CurrentPhase = Phase.Practice;
            ready = true;
            Save();
            Debug.Log("[Experiment] Data: " + DataPath);
        }

        private void Update()
        {
            if (!ready) return;
            if (CurrentPhase == Phase.Practice)
            {
                // Cruise is available for free practice, outside the original three-condition experiment.
                if ((int)switcher.CurrentMode < practice.Length)
                    practice[(int)switcher.CurrentMode] += Time.unscaledDeltaTime;
                title.text = "自由体验：左手 X 或模式按钮切换";
                message.text = $"手臂 {practice[0]:F0}s　摇杆 {practice[1]:F0}s　前倾 {practice[2]:F0}s\n每种方式至少体验 {practiceSecondsPerMode:F0} 秒\n手臂模式：按住侧握扳机原地转向，松开移动";
                action.interactable = Array.TrueForAll(practice, t => t >= practiceSecondsPerMode);
            }
            else if (CurrentPhase == Phase.Task)
                message.text = $"点位 {task.CurrentIndex}/{task.waypoints.Count}　时间 {task.TrialElapsedSeconds:F1}s";
            else if (CurrentPhase == Phase.BetweenTrials)
            {
                float remaining = Mathf.Max(0, minimumRestSeconds - (Time.unscaledTime - breakStarted));
                message.text = saveError ?? ("请填写本轮纸质问卷，完成后再继续。\n实验编号：" + session.id +
                    (remaining > 0 ? $"\n距下一任务至少休息 {remaining:F0} 秒" : ""));
                action.interactable = remaining <= 0;
            }
        }

        public void StartExperiment()
        {
            if (!ready || CurrentPhase != Phase.Practice ||
                !Array.TrueForAll(practice, t => t >= practiceSecondsPerMode)) return;
            BeginTrial(0);
        }

        private void BeginTrial(int index)
        {
            trial = index;
            switcher.SetExperimentLock(false);
            switcher.SetMotionPaused(true);
            switcher.StabilizerOrigin.SetPositionAndRotation(startPosition, startRotation);
            // Force a fresh input/dynamics state even if practice ended in this mode.
            switcher.SetMotionPaused(false);
            switcher.SelectMode(order[index]);
            if (switcher.CurrentMode == FlightControlMode.Biomechanical) switcher.biomechanicalController.ResetState();
            switcher.SetExperimentLock(true);
            switcher.enableControllerModeSwitch = false;
            task.ResetProgress();
            task.ProgressEnabled = true;
            action.gameObject.SetActive(false);
            CurrentPhase = Phase.Task;
            title.text = $"正式任务 {index + 1}/3：{Labels[(int)order[index]]}";
        }

        private void OnTaskComplete()
        {
            if (CurrentPhase != Phase.Task) return;
            if (task.completionUI != null) task.completionUI.SetActive(false);
            task.ProgressEnabled = false;
            switcher.SetExperimentLock(true);
            switcher.enableControllerModeSwitch = false;
            switcher.SetMotionPaused(true);
            session.trials.Add(new TrialResult { mode = order[trial].ToString(), trialIndex = trial,
                completedUtc = DateTime.UtcNow.ToString("o"), elapsedSeconds = task.TrialElapsedSeconds,
                waypointSeconds = task.reachedElapsedTimes.ToArray() });
            CurrentPhase = Phase.BetweenTrials;
            breakStarted = Time.unscaledTime;
            Save();
            title.text = $"第 {trial + 1}/3 轮完成：{Labels[(int)order[trial]]}";
            action.gameObject.SetActive(true);
            SetAction(trial < 2 ? "纸质问卷已填好，开始下一轮" : "纸质问卷已填好，完成实验", ContinueAfterPaperQuestionnaire);
        }

        public void ContinueAfterPaperQuestionnaire()
        {
            if (CurrentPhase != Phase.BetweenTrials || Time.unscaledTime - breakStarted < minimumRestSeconds) return;
            bool last = trial == order.Length - 1;
            session.status = last ? "completed" : "in_progress";
            if (!Save()) { session.status = "in_progress"; return; }
            if (!last) { BeginTrial(trial + 1); return; }
            CurrentPhase = Phase.Finished;
            title.text = "实验完成";
            message.text = "三轮任务数据已保存。纸质问卷请交给实验员。";
            SetAction("退出", ExitExperiment);
        }

        private bool Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DataPath));
                string temp = DataPath + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(session, true));
                if (File.Exists(DataPath)) File.Copy(temp, DataPath, true);
                else File.Move(temp, DataPath);
                saveError = null;
                return true;
            }
            catch (Exception e)
            {
                saveError = "保存失败，请联系实验员后重试继续。";
                Debug.LogError("[Experiment] " + e.Message);
                return false;
            }
        }

        public void ExitExperiment()
        {
            if (session != null && CurrentPhase != Phase.Finished)
            {
                session.status = "aborted";
                if (!Save()) { message.text = saveError; return; }
            }
            switcher.SetMotionPaused(true);
            task.ProgressEnabled = false;
            Application.Quit();
            #if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
            #endif
        }

        private void BuildUI()
        {
            font = Resources.Load<Font>("Fonts/NotoSansSC-Regular") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var root = new GameObject("FlightExperimentCanvas", typeof(RectTransform), typeof(Canvas), typeof(TrackedDeviceGraphicRaycaster));
            canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = switcher.StabilizerHead.GetComponent<Camera>();
            root.transform.SetParent(switcher.StabilizerHead, false);
            root.transform.localPosition = new Vector3(0, 0.12f, 1.5f);
            root.transform.localScale = Vector3.one * 0.0015f;
            ((RectTransform)root.transform).sizeDelta = new Vector2(800, 480);
            title = TextAt(root.transform, "", 0, 205, 760, 45);
            message = TextAt(root.transform, "", 0, 145, 760, 75);
            action = ButtonAt(root.transform, "开始实验", 0, 35, 320, StartExperiment);
            ButtonAt(root.transform, "终止／退出", 300, -25, 190, ExitExperiment);
        }

        private Text TextAt(Transform parent, string label, float x, float y, float width, float height)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = font; text.fontSize = 24; text.text = label;
            text.alignment = TextAnchor.MiddleCenter; text.color = Color.white; text.raycastTarget = false;
            text.rectTransform.sizeDelta = new Vector2(width, height);
            text.rectTransform.anchoredPosition = new Vector2(x, y);
            return text;
        }

        private Button ButtonAt(Transform parent, string label, float x, float y, float width, UnityEngine.Events.UnityAction callback)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(width, 42); rect.anchoredPosition = new Vector2(x, y);
            go.GetComponent<Image>().color = new Color(0.08f, 0.2f, 0.35f, 0.95f);
            var button = go.GetComponent<Button>(); button.onClick.AddListener(callback);
            TextAt(go.transform, label, 0, 0, width, 42);
            return button;
        }

        private void SetAction(string label, UnityEngine.Events.UnityAction callback)
        {
            action.GetComponentInChildren<Text>().text = label;
            action.onClick.RemoveAllListeners(); action.onClick.AddListener(callback);
            action.interactable = true;
        }

        private void OnDestroy()
        {
            if (task != null) task.onTaskComplete.RemoveListener(OnTaskComplete);
            if (canvas != null) Destroy(canvas.gameObject);
        }
    }
}
