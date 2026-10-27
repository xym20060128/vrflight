using UnityEngine;
using UnityEngine.UI;

namespace SwimFlight
{
    // World-space UI follows the tracked camera without changing the camera pose.
    public sealed class FlightModeHeadsetPanel : MonoBehaviour
    {
        private FlightControlModeSwitcher _switcher;
        private Canvas _canvas;
        private Text _status;
        private Font _font;
        private Text _horizontalSpeed, _verticalSpeed;
        private Text _strokeStatus;
        private readonly string[] _strokeLabels = { "原算法", "仅放宽门控", "仅保留径向" };
        private Diagnostics.FlightMotionDiagnostics _strokeRecorder;
        private FlightControlMode _displayedMode = (FlightControlMode)(-1);
        private bool _displayedControllerSwitch;
        private readonly string[] _labels = { "划水模式", "摇杆模式", "前倾加速", "划手巡航" };

        public void Initialize(FlightControlModeSwitcher switcher)
        {
            _switcher = switcher;
            _font = Resources.Load<Font>("Fonts/NotoSansSC-Regular");
            if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var existing = GameObject.Find("ControlModeSwitchCanvas");
            _canvas = existing != null ? existing.GetComponent<Canvas>() : null;
            if (_canvas == null)
            {
                var root = new GameObject("ControlModeSwitchCanvas", typeof(RectTransform), typeof(Canvas));
                _canvas = root.GetComponent<Canvas>();
                for (int i = 0; i < 4; i++)
                {
                    var button = new GameObject("ModeButton", typeof(RectTransform), typeof(Image), typeof(Button));
                    button.transform.SetParent(root.transform, false);
                    var rect = button.GetComponent<RectTransform>();
                    rect.sizeDelta = new Vector2(180f, 56f);
                    rect.anchoredPosition = new Vector2((i - 1) * 196f, 0f);
                    var label = CreateText(button.transform, "Label");
                    label.rectTransform.anchorMin = Vector2.zero;
                    label.rectTransform.anchorMax = Vector2.one;
                    label.rectTransform.sizeDelta = Vector2.zero;
                }
            }
            _canvas.renderMode = RenderMode.WorldSpace;
            // The legacy background stretched over the enlarged canvas, covering
            // controls above the mode row. Keep its visible area on that row only.
            var background = _canvas.transform.Find("Panel") as RectTransform;
            if (background != null)
            {
                background.anchorMin = background.anchorMax = new Vector2(0.5f, 0.5f);
                background.pivot = new Vector2(0.5f, 0.5f);
                background.anchoredPosition = Vector2.zero;
                background.sizeDelta = new Vector2(620f, 80f);
                var image = background.GetComponent<Image>();
                if (image != null) image.raycastTarget = false;
            }
            if (_canvas.GetComponent<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>() == null)
                _canvas.gameObject.AddComponent<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>();
            var buttons = _canvas.GetComponentsInChildren<Button>(true);
            // Existing scenes serialize three mode buttons. Add the fourth without
            // treating speed/diagnostic controls as mode buttons.
            if (buttons.Length == 3)
            {
                var cruiseButton = Instantiate(buttons[0], buttons[0].transform.parent);
                cruiseButton.name = "StrokeCruiseModeButton";
                buttons = _canvas.GetComponentsInChildren<Button>(true);
            }
            for (int i = 0; i < Mathf.Min(4, buttons.Length); i++)
            {
                int mode = i;
                var modeRect = buttons[i].GetComponent<RectTransform>();
                modeRect.anchorMin = modeRect.anchorMax = new Vector2(0.5f, 0.5f);
                modeRect.sizeDelta = new Vector2(144f, 56f);
                modeRect.anchoredPosition = new Vector2((i - 1.5f) * 154f, 0f);
                var layout = buttons[i].GetComponent<LayoutElement>();
                if (layout != null) { layout.minWidth = 130f; layout.preferredWidth = 144f; }
                // Replace serialized callbacks, including the obsolete SelectGazeGuided binding.
                buttons[i].onClick = new Button.ButtonClickedEvent();
                buttons[i].onClick.AddListener(() => _switcher.SelectMode((FlightControlMode)mode));
                var text = buttons[i].GetComponentInChildren<Text>(true);
                if (text != null)
                {
                    text.font = _font;
                    text.fontSize = 26;
                    text.text = _labels[i];
                    text.color = Color.white;
                    text.material = null;
                    text.raycastTarget = false;
                }
            }
            _status = CreateText(_canvas.transform, "HeadsetModeStatus");
            _status.rectTransform.sizeDelta = new Vector2(620f, 48f);
            _status.rectTransform.anchoredPosition = new Vector2(0f, -66f);
            _horizontalSpeed = CreateSpeedRow(false, -118f);
            _verticalSpeed = CreateSpeedRow(true, -184f);
            CreateStrokeDiagnostics();
            // Only button surfaces receive pointer hits; canvas backgrounds and labels
            // must not intercept rays aimed at the experiment controls behind them.
            foreach (var graphic in _canvas.GetComponentsInChildren<Graphic>(true))
                graphic.raycastTarget = graphic.GetComponent<Button>() != null;
            AttachToHead();
        }

        private Text CreateSpeedRow(bool vertical, float y)
        {
            var label = CreateText(_canvas.transform, vertical ? "VerticalSpeed" : "HorizontalSpeed");
            label.rectTransform.sizeDelta = new Vector2(380f, 56f);
            label.rectTransform.anchoredPosition = new Vector2(0f, y);
            for (int i = 0; i < 2; i++)
            {
                float delta = i == 0 ? -0.25f : 0.25f;
                var go = new GameObject((vertical ? "Vertical" : "Horizontal") + (i == 0 ? "Slower" : "Faster"),
                    typeof(RectTransform), typeof(Image), typeof(Button));
                go.transform.SetParent(_canvas.transform, false);
                var rect = go.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(88f, 56f);
                rect.anchoredPosition = new Vector2(i == 0 ? -246f : 246f, y);
                go.GetComponent<Image>().color = new Color(0.15f, 0.3f, 0.45f, 0.95f);
                go.GetComponent<Button>().onClick.AddListener(() => _switcher.AdjustFlightSpeed(vertical, delta));
                var text = CreateText(go.transform, "Label");
                text.text = i == 0 ? "-" : "+";
                text.rectTransform.anchorMin = Vector2.zero;
                text.rectTransform.anchorMax = Vector2.one;
                text.rectTransform.sizeDelta = Vector2.zero;
            }
            return label;
        }

        private void CreateStrokeDiagnostics()
        {
            _strokeRecorder = FindObjectOfType<Diagnostics.FlightMotionDiagnostics>();
            if (_strokeRecorder == null)
                _strokeRecorder = _switcher.gameObject.AddComponent<Diagnostics.FlightMotionDiagnostics>();
            _strokeRecorder.controller = _switcher.biomechanicalController;
            _strokeRecorder.sampleInterval = 0f;
            _strokeRecorder.outputPath = NewStrokeRecordingPath();
            string[] labels = { "原算法", "仅放宽门控", "仅保留径向" };
            for (int i = 0; i < 3; i++)
            {
                int group = i;
                CreateStrokeButton(labels[i], new Vector2((i - 1) * 196f, -250f), 180f, () =>
                {
                    SaveAndResetStroke();
                    var controller = _switcher.biomechanicalController;
                    if (controller == null) return;
                    controller.strokeComparison = (StrokeDiagnosticComparison)group;
                    controller.ResetStrokeDiagnosticMotion();
                    _strokeRecorder.outputPath = NewStrokeRecordingPath();
                });
            }
            CreateStrokeButton("保存记录并清零", new Vector2(0f, -316f), 300f, SaveAndResetStroke);
            _strokeStatus = CreateText(_canvas.transform, "StrokeStatus");
            _strokeStatus.rectTransform.sizeDelta = new Vector2(620f, 48f);
            _strokeStatus.rectTransform.anchoredPosition = new Vector2(0f, -368f);
        }

        private string NewStrokeRecordingPath()
        {
            var controller = _switcher.biomechanicalController;
            return "stroke_" + (controller != null ? controller.strokeComparison.ToString() : "Baseline")
                + "_" + System.DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + ".csv";
        }

        private void SaveAndResetStroke()
        {
            if (_strokeRecorder != null)
            {
                _strokeRecorder.Dump();
                _strokeRecorder.ClearRecording();
                _strokeRecorder.outputPath = NewStrokeRecordingPath();
            }
            _switcher.biomechanicalController?.ResetStrokeDiagnosticMotion();
        }

        private void CreateStrokeButton(string label, Vector2 position, float width, UnityEngine.Events.UnityAction action)
        {
            var go = new GameObject("StrokeDiagnosticButton", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(_canvas.transform, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, 56f);
            rect.anchoredPosition = position;
            go.GetComponent<Image>().color = new Color(0.15f, 0.3f, 0.45f, 0.95f);
            go.GetComponent<Button>().onClick.AddListener(action);
            var text = CreateText(go.transform, "Label");
            text.text = label;
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.sizeDelta = Vector2.zero;
        }

        private Text CreateText(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = 24;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }

        private void AttachToHead()
        {
            var head = _switcher.StabilizerHead;
            if (head == null) return;
            _canvas.worldCamera = head.GetComponent<Camera>();
            var rect = (RectTransform)_canvas.transform;
            rect.SetParent(head, false);
            rect.localPosition = new Vector3(0f, -0.27f, 1.2f);
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one * 0.0015f;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(620f, 800f);
        }

        private void LateUpdate()
        {
            if (_canvas == null || _switcher == null) return;
            var stroke = _switcher.biomechanicalController;
            if (_strokeStatus != null && stroke != null)
                _strokeStatus.text = "诊断：" + _strokeLabels[(int)stroke.strokeComparison];
            foreach (var button in _canvas.GetComponentsInChildren<Button>())
                button.interactable = !_switcher.ModeSelectionLocked && !_switcher.MotionPaused;
            if (_canvas.transform.parent != _switcher.StabilizerHead) AttachToHead();
            _horizontalSpeed.text = "前后速度  " + _switcher.horizontalSpeedMultiplier.ToString("0.00") + " 倍";
            _verticalSpeed.text = "升降速度  " + _switcher.verticalSpeedMultiplier.ToString("0.00") + " 倍";
            if (_switcher.CurrentMode == FlightControlMode.StrokeCruise && stroke != null)
            {
                _horizontalSpeed.text = $"巡航水平上限  {stroke.cruiseSettings.maximumHorizontalSpeed:F2} m/s";
                _verticalSpeed.text = $"巡航竖直上限  {stroke.cruiseSettings.maximumVerticalSpeed:F2} m/s";
            }
            if (_displayedMode == _switcher.CurrentMode &&
                _displayedControllerSwitch == _switcher.enableControllerModeSwitch) return;
            _displayedMode = _switcher.CurrentMode;
            _displayedControllerSwitch = _switcher.enableControllerModeSwitch;
            _status.text = "当前：" + _labels[(int)_displayedMode] +
                (_displayedControllerSwitch ? "  |  左手 X 切换" : "");
        }

        private void OnDestroy()
        {
            if (_canvas != null) Destroy(_canvas.gameObject);
        }
    }
}
