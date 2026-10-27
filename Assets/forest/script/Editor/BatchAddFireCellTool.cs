using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using FireSimulation;

public class BatchAddFireCellTool
{
    private static readonly Dictionary<string, FuelType> NameFuelMap = new Dictionary<string, FuelType>
    {
        { "Beech",     FuelType.Hardwood },
        { "Oak",       FuelType.Hardwood },
        { "Maple",     FuelType.Hardwood },
        { "Birch",     FuelType.Hardwood },
        { "Ash",       FuelType.Hardwood },
        { "Elm",       FuelType.Hardwood },
        { "Walnut",    FuelType.Hardwood },
        { "Chestnut",  FuelType.Hardwood },
        { "Poplar",    FuelType.Hardwood },
        { "Alder",     FuelType.Hardwood },
        { "Pine",      FuelType.Softwood },
        { "Spruce",    FuelType.Softwood },
        { "Fir",       FuelType.Softwood },
        { "Cedar",     FuelType.Softwood },
        { "Larch",     FuelType.Softwood },
        { "Cypress",   FuelType.Softwood },
        { "Redwood",   FuelType.Softwood },
        { "Hemlock",   FuelType.Softwood },
        { "Bush",      FuelType.Shrub },
        { "Shrub",     FuelType.Shrub },
        { "Hedge",     FuelType.Shrub },
        { "Berry",     FuelType.Shrub },
        { "Grass",     FuelType.Grass },
        { "Weed",      FuelType.Grass },
        { "Fern",      FuelType.Grass },
        { "Reed",      FuelType.Grass },
        { "Leaf",      FuelType.Litter },
        { "Litter",    FuelType.Litter },
        { "Ground",    FuelType.Litter },
        { "Debris",    FuelType.Litter },
        { "Twig",      FuelType.Litter },
    };

    [MenuItem("Tools/批量给(Clone)树添加 FireCell 组件")]
    static void BatchAddFireCell()
    {
        GameObject[] allObjects = Object.FindObjectsOfType<GameObject>(includeInactive: true);

        List<GameObject> targets = new List<GameObject>();
        foreach (GameObject go in allObjects)
        {
            string name = go.name;
            if (name.Contains("(Clone)") && !name.Contains("_LOD"))
            {
                targets.Add(go);
            }
        }

        if (targets.Count == 0)
        {
            EditorUtility.DisplayDialog("提示", "场景中没有找到 (Clone) 树物体", "确定");
            return;
        }

        int added = 0;
        int skipped = 0;
        Dictionary<FuelType, int> typeStats = new Dictionary<FuelType, int>
        {
            { FuelType.Grass, 0 },
            { FuelType.Shrub, 0 },
            { FuelType.Hardwood, 0 },
            { FuelType.Softwood, 0 },
            { FuelType.Litter, 0 }
        };

        foreach (GameObject treeObj in targets)
        {
            FireCell existing = treeObj.GetComponent<FireCell>();
            if (existing != null)
            {
                skipped++;
                continue;
            }

            Undo.RecordObject(treeObj, "添加 FireCell");
            FireCell cell = treeObj.AddComponent<FireCell>();
            FuelType guessed = GuessFuelType(treeObj.name);
            cell.fuelType = guessed;
            typeStats[guessed]++;
            added++;
        }

        string statsMsg = "";
        foreach (var kv in typeStats)
        {
            if (kv.Value > 0)
                statsMsg += "  " + kv.Key + ": " + kv.Value + " 棵\n";
        }

        EditorUtility.DisplayDialog("执行完成",
            "目标树总数: " + targets.Count + "\n" +
            "成功添加 FireCell: " + added + " 棵\n" +
            "已存在跳过: " + skipped + " 棵\n\n" +
            "FuelType 分布:\n" + statsMsg,
            "确定");

        Debug.Log("[BatchAddFireCell] 完成。新增 " + added + "，跳过 " + skipped);
    }

    [MenuItem("Tools/批量给所有树(含非Clone)添加 FireCell 组件")]
    static void BatchAddFireCellAllTrees()
    {
        TreeBurnReceiver[] allReceivers = Object.FindObjectsOfType<TreeBurnReceiver>(includeInactive: true);

        int added = 0;
        int skipped = 0;

        foreach (TreeBurnReceiver receiver in allReceivers)
        {
            GameObject treeObj = receiver.gameObject;
            if (treeObj.name.Contains("_LOD")) continue;

            FireCell existing = treeObj.GetComponent<FireCell>();
            if (existing != null)
            {
                skipped++;
                continue;
            }

            Undo.RecordObject(treeObj, "添加 FireCell");
            FireCell cell = treeObj.AddComponent<FireCell>();
            cell.fuelType = GuessFuelType(treeObj.name);
            added++;
        }

        EditorUtility.DisplayDialog("执行完成",
            "通过 TreeBurnReceiver 找到 " + allReceivers.Length + " 棵树\n" +
            "成功添加 FireCell: " + added + " 棵\n" +
            "已存在跳过: " + skipped + " 棵",
            "确定");

        Debug.Log("[BatchAddFireCell] 全量模式完成。新增 " + added + "，跳过 " + skipped);
    }

    static FuelType GuessFuelType(string objectName)
    {
        string lower = objectName.ToLower();
        foreach (var kv in NameFuelMap)
        {
            if (lower.Contains(kv.Key.ToLower()))
                return kv.Value;
        }
        return FuelType.Hardwood;
    }

    [MenuItem("Tools/批量给 FireCell 树添加 TreeBurnReceiver")]
    static void BatchAddTreeBurnReceiver()
    {
        FireCell[] allCells = Object.FindObjectsOfType<FireCell>(includeInactive: true);

        int added = 0;
        int skipped = 0;

        foreach (FireCell cell in allCells)
        {
            if (cell == null) continue;
            GameObject treeObj = cell.gameObject;
            if (treeObj.name.Contains("_LOD")) continue;

            TreeBurnReceiver receiver = treeObj.GetComponent<TreeBurnReceiver>();
            if (receiver != null)
            {
                skipped++;
                continue;
            }

            Undo.AddComponent<TreeBurnReceiver>(treeObj);
            added++;
        }

        EditorUtility.DisplayDialog("执行完成",
            "FireCell 树总数: " + allCells.Length + "\n" +
            "成功添加 TreeBurnReceiver: " + added + " 棵\n" +
            "已存在跳过: " + skipped + " 棵\n\n" +
            "请手动对每个树种预制体设置 burnedAlbedo 烧焦贴图。",
            "确定");

        Debug.Log("[BatchAddTreeBurnReceiver] 新增 " + added + "，跳过 " + skipped);
    }
}
