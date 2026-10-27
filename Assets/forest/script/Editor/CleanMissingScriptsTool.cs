using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class CleanMissingScriptsTool : EditorWindow
{
    private int removeCount;
    private int scannedCount;

    [MenuItem("工具/清理所有Missing丢失脚本")]
    private static void ShowWindow()
    {
        var window = GetWindow<CleanMissingScriptsTool>("清理丢失脚本");
        window.minSize = new Vector2(350, 160);
    }

    private void OnGUI()
    {
        GUILayout.Label("一键清除场景中所有 Missing (Script) 组件", EditorStyles.wordWrappedLabel);
        GUILayout.Space(10);

        if (GUILayout.Button("开始清理", GUILayout.Height(40)))
        {
            CleanAllMissingScripts();
        }

        GUILayout.Space(10);
        GUILayout.Label($"扫描对象: {scannedCount}    已移除丢失组件: {removeCount}");

        if (removeCount > 0)
        {
            GUILayout.Space(5);
            if (GUILayout.Button("撤销清理 (Ctrl+Z)", GUILayout.Height(25)))
            {
                Undo.PerformUndo();
                removeCount = 0;
            }
        }
    }

    private void CleanAllMissingScripts()
    {
        removeCount = 0;
        scannedCount = 0;

        // 记录整个场景用于撤销
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();

        // FindObjectsOfType 包含所有已激活 + 未激活的物体
        GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();

        foreach (GameObject go in allObjects)
        {
            // 只处理场景中的物体，跳过 Assets 中的预制体资源
            if (go.scene.name == null) continue;

            scannedCount++;
            int before = GetMissingScriptCount(go);
            if (before > 0)
            {
                Undo.RegisterFullObjectHierarchyUndo(go, "Clean Missing Scripts");
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                int after = GetMissingScriptCount(go);
                removeCount += (before - after);
            }

            // 递归处理子物体
            foreach (Transform child in go.transform)
                CleanChildMissing(child, ref removeCount, ref scannedCount);
        }

        if (removeCount > 0)
        {
            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorUtility.DisplayDialog("完成", $"扫描 {scannedCount} 个对象\n移除 {removeCount} 个丢失脚本组件", "确定");
        }
        else
        {
            EditorUtility.DisplayDialog("完成", $"扫描 {scannedCount} 个对象\n没有找到丢失脚本", "确定");
        }

        Debug.Log($"[CleanMissingScripts] 扫描 {scannedCount} 对象，移除 {removeCount} 丢失组件");
    }

    private void CleanChildMissing(Transform t, ref int count, ref int scanned)
    {
        if (t == null) return;
        scanned++;

        int before = GetMissingScriptCount(t.gameObject);
        if (before > 0)
        {
            Undo.RegisterFullObjectHierarchyUndo(t.gameObject, "Clean Missing Scripts");
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
            int after = GetMissingScriptCount(t.gameObject);
            count += (before - after);
        }

        foreach (Transform child in t)
            CleanChildMissing(child, ref count, ref scanned);
    }

    private static int GetMissingScriptCount(GameObject go)
    {
        int missing = 0;
        Component[] comps = go.GetComponents<Component>();
        foreach (var c in comps)
        {
            if (c == null) missing++;
        }
        return missing;
    }
}
