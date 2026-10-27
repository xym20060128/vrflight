using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

public class TreePrefabReplaceTool
{
    // 缓存项目内所有树预制体 名称→预制体
    private static Dictionary<string, GameObject> treePrefabCache = new Dictionary<string, GameObject>();

    [MenuItem("Tools/树木工具/一键替换全场景(Clone)树为最新预制体")]
    static void ReplaceAllCloneTreeToNewPrefab()
    {
        // 1. 预加载项目内所有树预制体（不带(Clone)）
        LoadAllTreePrefabs();
        if (treePrefabCache.Count == 0)
        {
            EditorUtility.DisplayDialog("错误", "项目内未找到任何树预制体", "确定");
            return;
        }

        // 2. 收集场景所有顶层(Clone)树
        List<GameObject> treeTargets = new List<GameObject>();
        GameObject[] allObjs = Object.FindObjectsOfType<GameObject>(includeInactive: true);
        foreach (GameObject go in allObjs)
        {
            string name = go.name;
            if (name.Contains("(Clone)") && !name.Contains("_LOD"))
            {
                treeTargets.Add(go);
            }
        }

        if (treeTargets.Count == 0)
        {
            EditorUtility.DisplayDialog("提示", "场景没有找到(Clone)实体树", "确定");
            return;
        }

        bool confirm = EditorUtility.DisplayDialog(
            "确认替换",
            $"共检测到 {treeTargets.Count} 棵实体树\n原地替换为新版预制体，复制燃烧脚本、碰撞体",
            "开始替换",
            "取消"
        );
        if (!confirm) return;

        int successCount = 0;
        int failCount = 0;
        List<string> failLog = new List<string>();

        // 3. 逐个原地替换
        foreach (GameObject oldTree in treeTargets)
        {
            if (oldTree == null) continue;
            Undo.RecordObject(oldTree.transform, "替换树木预制体");

            // 提取原始预制体名称
            string cloneName = oldTree.name;
            string prefabName = cloneName.Replace("(Clone)", "");

            if (!treePrefabCache.ContainsKey(prefabName))
            {
                failCount++;
                failLog.Add($"未匹配预制体：{cloneName}");
                continue;
            }

            GameObject newPrefab = treePrefabCache[prefabName];
            Transform parent = oldTree.transform.parent;
            Vector3 pos = oldTree.transform.position;
            Quaternion rot = oldTree.transform.rotation;
            Vector3 scale = oldTree.transform.localScale;

            // 实例化新树
            GameObject newTree = PrefabUtility.InstantiatePrefab(newPrefab) as GameObject;
            if (newTree == null)
            {
                failCount++;
                failLog.Add($"实例化失败：{cloneName}");
                continue;
            }
            newTree.transform.SetParent(parent);
            newTree.transform.position = pos;
            newTree.transform.rotation = rot;
            newTree.transform.localScale = scale;

            // 复制组件（修复空引用）
            CopyAllComponentsSafe(oldTree, newTree);

            // 删除旧树
            Undo.DestroyObjectImmediate(oldTree);
            successCount++;
            Debug.Log($"<color=cyan>替换成功：{cloneName} → {prefabName}新版预制体</color>");
        }

        // 结果弹窗
        string msg = $"替换完成\n成功：{successCount} 棵\n匹配失败：{failCount} 棵";
        if (failLog.Count > 0) msg += "\n失败详情查看Console";
        EditorUtility.DisplayDialog("执行完毕", msg, "确定");
        foreach (var log in failLog)
        {
            Debug.LogWarning($"<color=yellow>{log}</color>");
        }
    }

    // 加载项目内所有原版树预制体
    static void LoadAllTreePrefabs()
    {
        treePrefabCache.Clear();
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
        foreach (string guid in prefabGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) continue;

            string name = prefab.name;
            // 过滤烧焦、克隆后缀预制体
            if (name.EndsWith("(burned)") || name.EndsWith("(Clone)"))
                continue;

            if (!treePrefabCache.ContainsKey(name))
            {
                treePrefabCache.Add(name, prefab);
            }
        }
    }

    // 【修复版】安全复制组件，杜绝NullReference
    static void CopyAllComponentsSafe(GameObject source, GameObject dest)
    {
        if (source == null || dest == null)
        {
            Debug.LogError("源物体或目标物体为空，跳过组件复制");
            return;
        }

        Component[] sourceComps = source.GetComponents<Component>();
        foreach (Component comp in sourceComps)
        {
            if (comp == null) continue;
            // Transform自带，无需复制
            if (comp is Transform) continue;

            System.Type compType = comp.GetType();
            // 先判断目标是否已有同类型组件，避免重复
            Component existComp = dest.GetComponent(compType);
            if (existComp != null) continue;

            try
            {
                Component newComp = dest.AddComponent(compType);
                EditorUtility.CopySerialized(comp, newComp);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"跳过无法复制的组件 {compType.Name}：{e.Message}");
            }
        }
    }
}