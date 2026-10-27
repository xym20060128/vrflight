using System;
using System.IO;
using UnityEngine;
using SwimFlight.Locomotion;

public static class HeadCollisionValidation
{
    public static void Run()
    {
        string result;
        try { Check(); result = "passed"; } catch (Exception e) { result = "failed: " + e; }
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/HeadCollisionValidation.status", result);
        Debug.Log("HeadCollisionValidation: " + result);
    }
    public static void Check()
    {
        var rig = new GameObject("CollisionValidationRig");
        var camera = new GameObject("CollisionValidationHead");
        var floor = new GameObject("CollisionValidationFloor");
        var wall = new GameObject("CollisionValidationWall");
        var target = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var targetChild = new GameObject("TargetChild");
        var taskObject = new GameObject("PassThroughValidationTask");
        try
        {
            // Far from city geometry, so checks target only our test surfaces.
            rig.transform.position = new Vector3(10000f, 0f, 10000f);
            camera.transform.SetParent(rig.transform);
            camera.transform.localPosition = Vector3.up * 2f;
            floor.transform.position = new Vector3(10000f, -0.5f, 10000f);
            floor.AddComponent<BoxCollider>().size = new Vector3(20f, 1f, 20f);
            wall.transform.position = new Vector3(10002f, 2f, 10000f);
            wall.AddComponent<BoxCollider>().size = new Vector3(0.1f, 10f, 20f);
            var guard = rig.AddComponent<HeadCollisionGuard>();
            guard.head = camera.transform;
            guard.Initialize();
            guard.Move(new Vector3(0f, -10f, 0f));
            Require(camera.transform.position.y >= 0.159f, "Fast descent crossed floor");
            float height = camera.transform.position.y;
            guard.Move(new Vector3(0.5f, -1f, 0f));
            Require(camera.transform.position.x > 10000.4f, "Ground contact blocked horizontal motion");
            Require(camera.transform.position.y >= 0.159f, "Repeated descent crossed floor");
            guard.Move(Vector3.up);
            Require(camera.transform.position.y > height + 0.99f, "Floor blocked ascent");
            target.transform.position = new Vector3(10001.1f, camera.transform.position.y, 10000f);
            target.transform.localScale = Vector3.one * 0.5f;
            targetChild.transform.SetParent(target.transform, false);
            targetChild.AddComponent<BoxCollider>();
            var task = taskObject.AddComponent<SwimFlight.Experiment.WaypointFlightTask>();
            task.waypoints.Add(target.transform);
            task.MakeWaypointsPassThrough();
            Require(!target.GetComponent<Collider>().enabled && !targetChild.GetComponent<Collider>().enabled,
                "Configured waypoint and child colliders must not block motion");
            float beforeTarget = camera.transform.position.x;
            guard.Move(Vector3.right * 0.8f);
            Require(camera.transform.position.x > beforeTarget + 0.79f, "Waypoint stopped flight");
            Require(floor.GetComponent<Collider>().enabled && wall.GetComponent<Collider>().enabled,
                "Waypoint setup must preserve environment collisions");
            guard.Move(new Vector3(10f, 0f, 0f));
            Require(camera.transform.position.x < 10001.81f, "Sweep crossed wall");
            camera.transform.position = new Vector3(10000f, -2f, 10000f);
            guard.Move(Vector3.zero);
            Require(camera.transform.position.y >= 0.159f, "Below-ground tracking reset not recovered");
            Require(camera.GetComponent<SphereCollider>() != null, "Head collider missing");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(taskObject); UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(wall); UnityEngine.Object.DestroyImmediate(floor);
            UnityEngine.Object.DestroyImmediate(camera); UnityEngine.Object.DestroyImmediate(rig);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
