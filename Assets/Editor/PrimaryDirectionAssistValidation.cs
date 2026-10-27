using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using SwimFlight.Core;
using SwimFlight.Mapping;
using SwimFlight.Dynamics;

[InitializeOnLoad]
public static class PrimaryDirectionAssistValidation
{
    static PrimaryDirectionAssistValidation() { EditorApplication.update += Poll; }
    [MenuItem("Tools/SwimFlight/Validate Primary Direction Assist")]
    public static void Run()
    {
        string result;
        try { Check(); result = "passed"; }
        catch (Exception e) { result = "failed: " + e; }
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/PrimaryDirectionAssistValidation.status", result);
        Debug.Log("PrimaryDirectionAssistValidation: " + result);
    }
    private static void Poll()
    {
        const string request = "Temp/PrimaryDirectionAssistValidation.request";
        if (!File.Exists(request) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode) return;
        File.Delete(request);
        Run();
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    private static void Close(Vector3 a, Vector3 b, string message)
        => Require((a - b).magnitude < 1e-5f, message);
    private static Vector3 Settle(PrimaryDirectionAssist assist, Vector3 force)
    {
        Vector3 result = force;
        for (int i = 0; i < 120; i++) result = assist.Filter(force, 1f / 90f);
        return result;
    }
    private static void Check()
    {
        var assist = new PrimaryDirectionAssist();
        foreach (float sign in new[] { -1f, 1f })
        {
            Close(Settle(assist, new Vector3(0f, 0f, sign)), new Vector3(0f, 0f, sign), "Pure horizontal changed");
            Close(Settle(assist, Vector3.up * sign), Vector3.up * sign, "Pure vertical changed");
            Vector3 h = new Vector3(0f, 0.1f * sign, sign);
            Vector3 hOut = Settle(assist, h);
            Require(Mathf.Abs(hOut.y) < 0.006f && hOut.z == h.z, "Horizontal bias not strongly suppressed");
            Vector3 v = new Vector3(sign * 0.1f, sign, 0f);
            Vector3 vOut = Settle(assist, v);
            Require(Mathf.Abs(vOut.x) < 0.006f && vOut.y == v.y, "Vertical bias not strongly suppressed");
            Vector3 diagonal = new Vector3(sign, sign, 0f);
            Close(assist.Filter(diagonal, 1f / 90f), diagonal, "Diagonal changed on transition");
            Close(Settle(assist, diagonal), diagonal, "Steady diagonal changed");
        }
        Settle(assist, Vector3.up);
        Close(assist.Filter(Vector3.forward, 1f / 90f), Vector3.forward, "Old weights suppressed new major axis");
        Close(assist.Filter(Vector3.zero, 1f / 90f), Vector3.zero, "Recovery generated thrust tail");
        Require(assist.Filter(new Vector3(1e-9f, -1e-9f, 0f), 0.01f).magnitude < 2e-9f, "Near-zero unstable");
        assist.enabled = false;
        Vector3 sample = new Vector3(-0.1f, 1f, 0.3f);
        Close(assist.Filter(sample, 0.01f), sample, "Disabled not passthrough");
        assist.enabled = true;
        assist.smoothingTime = 0f;
        Vector3 previous = Vector3.zero;
        for (int i = 0; i <= 900; i++)
        {
            float angle = i * 0.1f * Mathf.Deg2Rad;
            Vector3 force = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
            Vector3 result = assist.Filter(force, 0.01f);
            Require(result.magnitude <= force.magnitude + 1e-6f, "Assist increased thrust");
            if (i > 0) Require((result - previous).magnitude < 0.005f, "Direction sweep discontinuity");
            previous = result;
        }
        var slow = new PrimaryDirectionAssist();
        var fast = new PrimaryDirectionAssist();
        Vector3 input = new Vector3(0f, 0.1f, 1f);
        Vector3 slowOut = Vector3.zero, fastOut = Vector3.zero;
        for (int i = 0; i < 50; i++) slowOut = slow.Filter(input, 0.02f);
        for (int i = 0; i < 100; i++) fastOut = fast.Filter(input, 0.01f);
        Close(slowOut, fastOut, "Weight smoothing frame-rate dependence");
        foreach (float boundary in new[] { slow.dominanceAngle * 0.5f, slow.dominanceAngle })
        {
            previous = Settle(slow, new Vector3(0f, Mathf.Sin(boundary * Mathf.Deg2Rad), Mathf.Cos(boundary * Mathf.Deg2Rad)));
            for (int i = 0; i < 180; i++)
            {
                float angle = (boundary + Mathf.Sin(i * 0.4f) * 0.5f) * Mathf.Deg2Rad;
                Vector3 result = slow.Filter(new Vector3(0f, Mathf.Sin(angle), Mathf.Cos(angle)), 1f / 90f);
                if (i > 0) Require((result - previous).magnitude < 0.01f, "Boundary oscillation amplified");
                previous = result;
            }
        }

        var turn = new FlightCommand { leftArm = new Vector3(-0.5f, 0f, 0.4f),
            rightArm = new Vector3(0.5f, 0f, 0.4f),
            leftForce = new Vector3(1f, 0.1f, 0f), rightForce = new Vector3(1f, -0.1f, 0f) };
        new TurnInPlaceAssist().Apply(ref turn);
        var baseline = turn;
        assist.Apply(ref turn, 0.02f);
        Close(turn.leftForce, baseline.leftForce, "Assist changed left torque source");
        Close(turn.rightForce, baseline.rightForce, "Assist changed right torque source");
        Require(turn.translationForce.magnitude <= (baseline.leftForce + baseline.rightForce).magnitude + 1e-5f,
            "Assist reintroduced turn translation");
        var dynamics = new FlightDynamicsV2();
        var initial = dynamics.Initialize(Vector3.zero, Quaternion.identity);
        var expected = dynamics.Integrate(initial, ref baseline, 0.02f);
        var actual = dynamics.Integrate(initial, ref turn, 0.02f);
        Close(actual.angularVelocity, expected.angularVelocity, "Turning strength changed");
        Close(turn.rawTotalTorque, baseline.rawTotalTorque, "Raw torque changed");

        var mapper = new BiomechanicalMapper();
        var stroke = mapper.Map(new FlightInput { leftHandPosition = Vector3.left * 0.5f,
            rightHandPosition = Vector3.right * 0.5f,
            leftHandVelocity = new Vector3(0f, -0.1f, -1f),
            rightHandVelocity = new Vector3(0f, -0.1f, -1f) });
        Require(stroke.leftForce.y + stroke.rightForce.y > 0f, "Biased stroke did not expose vertical thrust");
        assist.Reset();
        assist.Apply(ref stroke, 0.02f);
        Require(stroke.translationForce.y < stroke.leftForce.y + stroke.rightForce.y,
            "Mapped stroke vertical bias not reduced");
        mapper = new BiomechanicalMapper();
        var recovery = mapper.Map(new FlightInput { leftHandPosition = Vector3.zero,
            rightHandPosition = Vector3.zero, leftHandVelocity = Vector3.forward,
            rightHandVelocity = Vector3.up });
        assist.Apply(ref recovery, 0.02f);
        Close(recovery.translationForce, Vector3.zero, "Inactive gate generated force");
        initial.velocity = Vector3.up * 0.5f;
        actual = dynamics.Integrate(initial, ref recovery, 0.02f);
        Require(actual.velocity.y > 0f && actual.position.y > 0f, "Assist cleared accumulated velocity");
        dynamics.gripTurnControl = true;
        dynamics.gripTurnHeld = true;
        actual = dynamics.Integrate(initial, ref turn, 0.02f);
        Close(actual.velocity, Vector3.zero, "Existing grip lock changed");

        var rig = new GameObject("DirectionValidationRig");
        var head = new GameObject("DirectionValidationHead");
        try
        {
            head.transform.SetParent(rig.transform);
            head.transform.localRotation = Quaternion.Euler(60f, 0f, 0f);
            var coordinates = new SwimFlight.Coordinate.FlightCoordinateSystem
                { xrOrigin = rig.transform, headTransform = head.transform };
            var world = coordinates.ToWorld(new SwimFlight.Input.XRRawInput
                { leftTrackingVelocity = Vector3.forward }, 0.02f);
            Close(world.leftHandVelocity, Vector3.forward, "Head pitch changed world horizontal");
            var applier = new SwimFlight.Locomotion.XRLocomotionApplierV2(rig.transform);
            applier.Reset(initial);
            applier.Apply(expected, true, false, null);
            Close(rig.transform.position, expected.position - initial.position, "Final translation application changed");
        }
        finally { UnityEngine.Object.DestroyImmediate(head); UnityEngine.Object.DestroyImmediate(rig); }
        typeof(StationaryTurnValidation).GetMethod("Check",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, null);
    }
}
