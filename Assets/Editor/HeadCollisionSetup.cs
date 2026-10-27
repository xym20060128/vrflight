using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SwimFlight.Locomotion;

[InitializeOnLoad]
public static class HeadCollisionSetup
{
    static HeadCollisionSetup() { EditorApplication.update += Poll; }
    private static void Poll()
    {
        const string request = "Temp/HeadCollisionSetup.request";
        if (!File.Exists(request) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode) return;
        File.Delete(request);
        try { Configure(); Validate(); File.WriteAllText("Logs/HeadCollisionSetup.status", "passed"); }
        catch (Exception e) { File.WriteAllText("Logs/HeadCollisionSetup.status", "failed: " + e); }
    }

    public static void Configure()
    {
        const string scenePath = "Assets/JapaneseCity/Scenes/JapaneseCity_Demo.unity";
        var scene = EditorSceneManager.GetActiveScene();
        if (Application.isBatchMode && scene.path != scenePath)
            scene = EditorSceneManager.OpenScene(scenePath);
        if (scene.path != scenePath) throw new Exception("Open JapaneseCity_Demo before configuring collisions.");
        var controller = UnityEngine.Object.FindObjectOfType<SwimFlightControllerV2>();
        if (controller == null || controller.xrOrigin == null || controller.headTransform == null)
            throw new Exception("Cannot resolve the actual flight rig/head references.");
        var report = new StringBuilder();
        int grounds = 0, added = 0;
        Bounds bounds = default;
        foreach (var filter in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
        {
            if (filter.gameObject.scene != scene || filter.sharedMesh == null) continue;
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(filter.gameObject);
            string path = root != null ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root) : "";
            // Actual ground-prefab membership, not a guessed scene object label.
            if (!path.StartsWith("Assets/JapaneseCity/Prefabs/Grounds/", StringComparison.Ordinal)) continue;
            bool structural = filter.sharedMesh.name.StartsWith("jcRoad", StringComparison.Ordinal)
                || filter.sharedMesh.name.StartsWith("jcBldBase", StringComparison.Ordinal);
            if (!structural)
            {
                // Remove query colliders added by the initial setup to flat road markings;
                // preserve all colliders supplied by the original prefab.
                var extra = filter.GetComponent<MeshCollider>();
                if (extra != null && PrefabUtility.GetCorrespondingObjectFromSource(extra) == null)
                    UnityEngine.Object.DestroyImmediate(extra);
                continue;
            }
            var renderer = filter.GetComponent<MeshRenderer>();
            if (renderer == null || !renderer.enabled) continue;
            if (grounds == 0) bounds = renderer.bounds; else bounds.Encapsulate(renderer.bounds);
            if (grounds < 12) report.AppendLine(path + " mesh=" + filter.sharedMesh.name + " bounds=" + renderer.bounds);
            grounds++;
            if (filter.GetComponent<Collider>() == null)
            {
                var collider = filter.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = filter.sharedMesh;
                collider.convex = false;
                added++;
            }
        }
        if (grounds == 0) throw new Exception("No ground-prefab meshes found; aborting scene modification.");
        var guard = controller.xrOrigin.GetComponent<HeadCollisionGuard>();
        if (guard == null) guard = controller.xrOrigin.gameObject.AddComponent<HeadCollisionGuard>();
        guard.head = controller.headTransform;
        guard.groundHeight = bounds.min.y;
        guard.radius = 0.15f;
        guard.skin = 0.01f;
        guard.Initialize();
        var foundation = GameObject.Find("Flight Ground Collision Foundation");
        if (foundation == null) foundation = new GameObject("Flight Ground Collision Foundation");
        foundation.transform.position = new Vector3(bounds.center.x, bounds.min.y - 0.5f, bounds.center.z);
        var box = foundation.GetComponent<BoxCollider>();
        if (box == null) box = foundation.AddComponent<BoxCollider>();
        box.size = new Vector3(bounds.size.x + 20f, 1f, bounds.size.z + 20f);
        controller.yawTorqueGain = 6f;
        var switcher = UnityEngine.Object.FindObjectOfType<SwimFlight.FlightControlModeSwitcher>();
        if (switcher != null)
        {
            switcher.comparison.maxYawSpeed = 50f;
            switcher.comparison.yawAcceleration = 60f;
        }
        EditorUtility.SetDirty(controller);
        EditorUtility.SetDirty(guard);
        if (switcher != null) EditorUtility.SetDirty(switcher);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new Exception("Scene save failed.");
        report.AppendLine("ground meshes=" + grounds + ", colliders added=" + added + ", city bounds=" + bounds);
        report.AppendLine("ground fallback top=" + guard.groundHeight + ", head radius=" + guard.radius);
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/HeadCollisionSetup.report", report.ToString());
    }

    public static void Validate()
    {
        HeadCollisionValidation.Check();
        var controller = UnityEngine.Object.FindObjectOfType<SwimFlightControllerV2>();
        var sceneGuard = controller != null ? HeadCollisionGuard.Find(controller.xrOrigin) : null;
        Require(sceneGuard != null && sceneGuard.head == controller.headTransform, "Scene rig guard missing");
        Require(sceneGuard.head.GetComponent<SphereCollider>() != null, "Scene head collider missing");
    }
    public static void ConfigureValidateAndBuild()
    {
        try
        {
            Configure();
            Validate();
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/HeadCollisionSetup.status", "passed");
            BatchBuild.BuildAndroidIndependent();
        }
        catch (Exception e)
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/HeadCollisionSetup.status", "failed: " + e);
            Debug.LogException(e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
            throw;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
