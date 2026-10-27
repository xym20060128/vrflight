using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(Terrain))]
public class TerrainTreeConverter : MonoBehaviour
{
    [Header("地形树种原型与预制体一一对应，顺序和Paint Trees从上到下保持一致")]
    public List<TerrainTreePair> treeBindList = new List<TerrainTreePair>();

    [Header("转换后清空地形原生树实例")]
    public bool wipeTerrainTree = true;

    [ContextMenu("一键转换地形树为独立实体")]
    public void RunConvert()
    {
        Terrain terr = GetComponent<Terrain>();
        TerrainData tData = terr.terrainData;
        TreeInstance[] allTreeIns = tData.treeInstances;

        if (allTreeIns.Length == 0)
        {
            Debug.LogWarning("当前地形无树木，无需转换");
            return;
        }

        int spawnCount = 0;
        Vector3 terrWorldPos = transform.position;
        Vector3 terrSize = tData.size;

        foreach (TreeInstance ins in allTreeIns)
        {
            int protoId = ins.prototypeIndex;
            GameObject targetPrefab = GetTargetPrefab(protoId);
            if (targetPrefab == null)
            {
                Debug.LogWarning($"树种原型ID {protoId} 未配置预制体，跳过");
                continue;
            }

            Vector3 localPos = new Vector3(
                ins.position.x * terrSize.x,
                ins.position.y * terrSize.y,
                ins.position.z * terrSize.z
            );
            Vector3 worldPos = terrWorldPos + localPos;

            GameObject newTreeObj = Instantiate(targetPrefab, worldPos, Quaternion.identity, transform);
            newTreeObj.transform.localScale = Vector3.one * ins.widthScale;
            spawnCount++;
        }

        if (wipeTerrainTree)
        {
            tData.treeInstances = new TreeInstance[0];
            Debug.Log("已清空地形原生TreeInstance");
        }

        Debug.Log($"转换完成，总计生成 {spawnCount} 棵独立树木");
    }

    private GameObject GetTargetPrefab(int index)
    {
        if (index < 0 || index >= treeBindList.Count)
            return null;
        return treeBindList[index].realTreePrefab;
    }
}

// 拆分到脚本最底部，纯序列化数据类，消除ExtensionOfNativeClass警告
[System.Serializable]
public class TerrainTreePair
{
    public TreePrototype terrainProto;
    public GameObject realTreePrefab;
}