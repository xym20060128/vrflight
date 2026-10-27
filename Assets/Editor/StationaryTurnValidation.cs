using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using SwimFlight.Core;
using SwimFlight.Dynamics;

[InitializeOnLoad]
public static class StationaryTurnValidation
{
    static StationaryTurnValidation() { EditorApplication.update += Poll; }
    private static void Poll()
    {
        const string request = "Temp/StationaryTurnValidation.request";
        if (!File.Exists(request) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode) return;
        File.Delete(request);
        try { Check(); File.WriteAllText("Temp/StationaryTurnValidation.status", "passed"); }
        catch (Exception e) { File.WriteAllText("Temp/StationaryTurnValidation.status", "failed: " + e); }
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
    }

    private static void Check()
    {
        var dynamics = new FlightDynamicsV2 { stationaryTurn = true, maxAcceleration = 2f,
            maxAngularAcceleration = 40f * Mathf.Deg2Rad, maxAngularVelocity = 20f * Mathf.Deg2Rad };
        var state = dynamics.Initialize(Vector3.zero, Quaternion.identity);
        state.velocity = new Vector3(1f, 0f, 0f);
        var command = new FlightCommand { leftArm = new Vector3(0f, 0f, 1f),
            leftForce = new Vector3(1f, 0.2f, 0f) };
        bool turnedAfterStopping = false;
        for (int i = 0; i < 200; i++)
        {
            var previous = state;
            state = dynamics.Integrate(state, ref command, 0.02f);
            float horizontal = new Vector2(state.velocity.x, state.velocity.z).magnitude;
            Require(horizontal <= new Vector2(previous.velocity.x, previous.velocity.z).magnitude + 1e-5f,
                "Turning added horizontal speed");
            Require((state.velocity - previous.velocity).magnitude <= 2f * 0.02f + 1e-5f,
                "Braking exceeded shared acceleration budget");
            if (horizontal > 1e-5f)
                Require(Mathf.Abs(Mathf.DeltaAngle(previous.orientation.eulerAngles.y,
                    state.orientation.eulerAngles.y)) < 1e-4f, "Yaw began before horizontal braking completed");
            else if (Mathf.Abs(state.angularVelocity.y) > 0.01f) turnedAfterStopping = true;
        }
        Require(turnedAfterStopping, "Stationary yaw did not resume after braking");
        Require(state.velocity.y > 0f, "Vertical input was suppressed");
        dynamics.stationaryTurn = false;
        state = dynamics.Integrate(state, ref command, 0.02f);
        Require(state.velocity.x > 0f, "Horizontal propulsion did not resume after turning");
        dynamics.gripTurnControl = true;
        dynamics.gripTurnHeld = true;
        state.velocity = new Vector3(1f, 0.5f, 0.3f);
        Vector3 lockedPosition = state.position;
        for (int i = 0; i < 50; i++) state = dynamics.Integrate(state, ref command, 0.02f);
        Require(state.velocity == Vector3.zero && state.position == lockedPosition,
            "Grip turning must stop horizontal and vertical translation");
        Require(Mathf.Abs(state.angularVelocity.y) > 0.01f, "Grip turning did not allow arm yaw");
        dynamics.gripTurnHeld = false;
        float yawAtRelease = state.orientation.eulerAngles.y;
        for (int i = 0; i < 50; i++) state = dynamics.Integrate(state, ref command, 0.02f);
        Require(state.velocity.x > 0f && state.velocity.y > 0f, "Release did not restore translation");
        Require(Mathf.Abs(Mathf.DeltaAngle(yawAtRelease, state.orientation.eulerAngles.y)) < 1e-4f,
            "Virtual yaw continued without grip");
    }
}
