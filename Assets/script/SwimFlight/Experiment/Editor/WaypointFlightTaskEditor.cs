using UnityEngine;
using UnityEditor;
using UnityEngine.UI;

namespace SwimFlight.Experiment
{
    /// <summary>
    /// WaypointFlightTask 的 Inspector 辅助按钮：
    ///   - 为所有点位补全可视化球体
    ///   - 生成一个「任务完成」UI Canvas 并自动填入 completionUI
    /// </summary>
    [CustomEditor(typeof(WaypointFlightTask))]
    public class WaypointFlightTaskEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var task = (WaypointFlightTask)target;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("设置工具", EditorStyles.boldLabel);

            if (GUILayout.Button("为所有点位补全可视化球体"))
            {
                task.EnsureWaypointVisuals();
                EditorUtility.SetDirty(task);
            }

            if (GUILayout.Button("生成任务完成 UI"))
            {
                task.completionUI = CreateCompletionUI();
                EditorUtility.SetDirty(task);
            }

            if (GUILayout.Button("把完成 UI 设为不显示"))
            {
                if (task.completionUI != null) task.completionUI.SetActive(false);
            }
        }

        private static GameObject CreateCompletionUI()
        {
            // Canvas (World Space: VR 头显里 ScreenSpaceOverlay 不渲染)
            var canvasGO = new GameObject("TaskCompleteCanvas",
                typeof(Canvas), typeof(GraphicRaycaster));
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGO.SetActive(false);

            // 逻辑像素尺寸 200x60，缩小 0.005 → 世界尺寸约 1m x 0.3m
            var canvasRT = canvasGO.GetComponent<RectTransform>();
            canvasRT.sizeDelta = new Vector2(200f, 60f);
            canvasRT.localScale = new Vector3(0.005f, 0.005f, 0.005f);

            // 居中大字
            var textGO = new GameObject("TaskCompleteText", typeof(Text));
            textGO.transform.SetParent(canvasGO.transform, false);
            var text = textGO.GetComponent<Text>();
            text.text = "任务完成";
            text.fontSize = 40;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.font = GetBuiltinFont();

            var rt = textGO.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            return canvasGO;
        }

        private static Font GetBuiltinFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }
    }
}
