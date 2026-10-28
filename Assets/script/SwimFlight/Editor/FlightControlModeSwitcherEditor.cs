using UnityEngine;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine.UI;

namespace SwimFlight
{
    /// <summary>
    /// FlightControlModeSwitcher 的自定义 Inspector:
    ///   上半部分: 三个控制器引用 + 初始模式 + 一键生成三按钮切换 UI。
    ///   下半部分: 分组显示三个控制器的完整参数, 免去三处 Inspector 来回切。
    ///   参数分组直接复用各控制器自带的默认 Inspector (含 Header/Tooltip/OnValidate),
    ///   因此新加的字段会自动出现在这里, 无需同步维护。
    /// </summary>
    [CustomEditor(typeof(FlightControlModeSwitcher))]
    public class FlightControlModeSwitcherEditor : Editor
    {
        private Editor _biomechanicalEditor;
        private Editor _joystickEditor;
        private Editor _intentionalEditor;

        private bool _showBiomechanical = true;
        private bool _showJoystick = true;
        private bool _showIntentional = true;

        private void OnDisable()
        {
            DisposeNestedEditors();
        }

        public override void OnInspectorGUI()
        {
            var sw = (FlightControlModeSwitcher)target;

            EditorGUILayout.LabelField("飞行控制: 切换 + 调参", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "一个组件同时负责模式切换与参数调节。Play 模式下改参实时生效; 点「选中」定位到对应控制器。",
                MessageType.Info);

            // 自动查找: 直接改组件字段, 之后 Update 一次让下方引用字段刷新
            if (GUILayout.Button("自动查找三个控制器"))
            {
                sw.AutoFindControllers();
                EditorUtility.SetDirty(sw);
            }

            serializedObject.Update();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("biomechanicalController"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("joystickController"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("intentionalHeadMotionController"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("startMode"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enableControllerModeSwitch"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("showHeadsetModePanel"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("comparison"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("headStabilization"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("xrResolutionScale"));
            if (Application.isPlaying)
            {
                EditorGUILayout.HelpBox(sw.RenderingStatus, MessageType.Info);
                Repaint();
            }
            EditorGUILayout.HelpBox("All four modes share the yaw speed and acceleration settings. The sections below contain only input mapping parameters. Restart current comparison trial resets pose and waypoint progress.", MessageType.Info);

            serializedObject.ApplyModifiedProperties();
            if (Application.isPlaying && GUILayout.Button("Restart current comparison trial"))
                sw.RestartComparisonTrial();
            if (Application.isPlaying && GUILayout.Button("切换到划手巡航")) sw.SelectStrokeCruise();

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("模式切换 UI", EditorStyles.boldLabel);
            if (GUILayout.Button("生成控制模式切换 UI (三个按钮)"))
            {
                var canvas = CreateSwitchUI(sw);
                Selection.activeGameObject = canvas;
                EditorUtility.SetDirty(sw);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("参数调节", EditorStyles.boldLabel);

            DrawSection("划水(仿生)模式", sw.biomechanicalController, ref _showBiomechanical, ref _biomechanicalEditor);
            DrawSection("遥感(摇杆)模式", sw.joystickController, ref _showJoystick, ref _joystickEditor);
            DrawSection("前倾加速模式", sw.intentionalHeadMotionController, ref _showIntentional, ref _intentionalEditor);
        }

        private void DrawSection(string label, Object controller, ref bool show, ref Editor editor)
        {
            EditorGUILayout.BeginHorizontal();
            show = EditorGUILayout.Foldout(show, label, true);
            if (controller != null)
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("选中", EditorStyles.miniButton, GUILayout.Width(40f)))
                    EditorGUIUtility.PingObject(controller);
            }
            EditorGUILayout.EndHorizontal();

            if (!show) return;

            EditorGUI.indentLevel++;

            if (controller == null)
            {
                editor = null;
                EditorGUILayout.HelpBox("未指定控制器, 点上方「自动查找」或手动拖入。", MessageType.Warning);
            }
            else
            {
                // 引用变化时重建对应的嵌套 Editor (默认 Inspector)
                if (editor == null || editor.target != controller)
                {
                    DisposeEditor(ref editor);
                    editor = Editor.CreateEditor(controller);
                }
                editor.serializedObject.Update();
                if (controller is SwimFlightControllerV2)
                    // 只排除本编辑器每帧强制覆盖的字段 (xrOrigin/headTransform/enableDebugLog 由
                    // ApplyComparisonSettings 写), 其余全部显示。
                    // 动力学参数 (mass/linearDamping/maxAcceleration/asymmetricDamping...) 曾经是死代码,
                    // 现在手臂条件回到力驱动动力学链路, 这些参数重新生效, 必须可调。
                    DrawPropertiesExcluding(editor.serializedObject, "m_Script", "xrOrigin", "headTransform",
                        "enableDebugLog", "maxAngularVelocity", "maxAngularAcceleration");
                else if (controller is JoystickFlightController)
                    EditorGUILayout.HelpBox("Joystick axes map directly to target velocity. Speed, deadzone, heading, braking and turn response come from the shared settings above.", MessageType.Info);
                else
                    DrawPropertiesExcluding(editor.serializedObject, "m_Script", "xrOrigin", "headTransform",
                        "maxAcceleration", "maxForwardSpeed", "maxBackwardSpeed", "brakingAcceleration",
                        "deadzone", "lateralSpeed", "verticalSpeed", "turnSpeed", "enableVertical", "enableTurn");
                editor.serializedObject.ApplyModifiedProperties();
            }

            EditorGUI.indentLevel--;
        }

        // ---------- 生成三按钮切换 UI ----------

        private static GameObject CreateSwitchUI(FlightControlModeSwitcher sw)
        {
            // World Space Canvas (VR 里 ScreenSpaceOverlay 不渲染)
            var canvasGO = new GameObject("ControlModeSwitchCanvas",
                typeof(Canvas), typeof(GraphicRaycaster));
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var rt = canvasGO.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(620f, 80f);
            rt.localScale = Vector3.one * 0.0015f;
            var head = sw.biomechanicalController != null ? sw.biomechanicalController.headTransform : null;
            if (head == null && Camera.main != null) head = Camera.main.transform;
            if (head != null)
            {
                rt.SetParent(head, false);
                rt.localPosition = new Vector3(0f, -0.27f, 1.2f);
                rt.localRotation = Quaternion.identity;
                canvas.worldCamera = head.GetComponent<Camera>();
            }

            // 背景面板 + 横向布局
            var panel = new GameObject("Panel", typeof(Image), typeof(HorizontalLayoutGroup));
            panel.transform.SetParent(canvasGO.transform, false);
            panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);
            panel.GetComponent<Image>().raycastTarget = false;

            var prt = panel.GetComponent<RectTransform>();
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            prt.sizeDelta = new Vector2(620f, 80f);

            var hlg = panel.GetComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(12, 12, 12, 12);
            hlg.spacing = 16f;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            CreateButton(panel.transform, "划水模式", sw.SelectBiomechanical);
            CreateButton(panel.transform, "遥感模式", sw.SelectJoystick);
            CreateButton(panel.transform, "前倾加速", sw.SelectIntentionalHeadMotion);
            CreateButton(panel.transform, "划手巡航", sw.SelectStrokeCruise);

            return canvasGO;
        }

        private static void CreateButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Button_" + label, typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(parent, false);

            var image = go.GetComponent<Image>();
            image.color = new Color(0.2f, 0.5f, 0.9f, 1f);

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = image;
            UnityEventTools.AddPersistentListener(btn.onClick, onClick);

            var le = go.GetComponent<LayoutElement>();
            le.minWidth = 130f;
            le.minHeight = 56f;

            var textGO = new GameObject("Text", typeof(Text));
            textGO.transform.SetParent(go.transform, false);
            var text = textGO.GetComponent<Text>();
            text.text = label;
            text.fontSize = 32;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.font = GetBuiltinFont();

            var trt = textGO.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
        }

        private static Font GetBuiltinFont()
        {
            var chineseFont = Resources.Load<Font>("Fonts/NotoSansSC-Regular");
            if (chineseFont != null) return chineseFont;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }

        // ---------- 嵌套 Editor 清理 ----------

        private static void DisposeEditor(ref Editor editor)
        {
            if (editor != null) DestroyImmediate(editor);
            editor = null;
        }

        private void DisposeNestedEditors()
        {
            DisposeEditor(ref _biomechanicalEditor);
            DisposeEditor(ref _joystickEditor);
            DisposeEditor(ref _intentionalEditor);
        }
    }
}
