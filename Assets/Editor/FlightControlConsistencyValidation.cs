using System;
using SwimFlight;
using UnityEditor;
using UnityEngine;

public static class FlightControlConsistencyValidation
{
    [MenuItem("SwimFlight/Validate turn and directional lean")]
    public static void Run()
    {
        Require(Lean(0.18f, 1f) == 1f, "Forward lean must boost forward travel");
        Require(Lean(-0.18f, -1f) == 1f, "Backward lean must boost reverse travel");
        Require(Lean(0.18f, -1f) == 0f, "Forward lean must not boost reverse travel");
        Require(Lean(-0.18f, 1f) == 0f, "Backward lean must not boost forward travel");
        Require(Lean(-0.18f, 0f) == 0f, "Released stick must not boost");
        Require(Lean(0.02f, 1f) == 0f && Lean(-0.02f, -1f) == 0f, "Sway deadzone");
        Require(Mathf.Abs(Lean(0.1f, 1f) - Lean(-0.1f, -1f)) < 1e-6f, "Symmetric boost");
        var settings = new ComparisonMotionSettings();
        foreach (float direction in new[] { -1f, 1f })
        {
            var motor = new ComparisonMotionMotor { Settings = settings };
            for (int i = 0; i < 100; i++)
            {
                float previous = motor.YawRate;
                motor.AdvanceMotion(Vector3.zero, direction * 90f, 0.02f);
                Require(Mathf.Abs(motor.YawRate) <= 20.0001f, "Shared turn cap");
                Require(Mathf.Abs(motor.YawRate - previous) <= 0.8001f, "Shared angular acceleration");
            }
            Require(Mathf.Abs(motor.YawRate - direction * 20f) < 1e-4f, "Full turn speed");
        }
        Debug.Log("Turn and directional lean validation passed.");
    }

    private static float Lean(float displacement, float input) =>
        IntentionalHeadMotionFlightController.ComputeDirectionalLean(displacement, input, 0.025f, 0.18f);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
