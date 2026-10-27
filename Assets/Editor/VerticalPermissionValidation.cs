using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using SwimFlight.Core;
using SwimFlight.Input;
using SwimFlight.Mapping;
using SwimFlight.Dynamics;

[InitializeOnLoad]
public static class VerticalPermissionValidation
{
    static VerticalPermissionValidation() { EditorApplication.update += PollBuildRequest; }

    private static void PollBuildRequest()
    {
        const string request = "Temp/VerticalPermissionBuild.request";
        const string horizontalRequest = "Temp/ExperimentAccessBuild.request";
        if ((!File.Exists(request) && !File.Exists(horizontalRequest)) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer) return;
        if (File.Exists(horizontalRequest)) File.Delete(horizontalRequest);
        if (File.Exists(request)) File.Delete(request);
        File.WriteAllText("Logs/VerticalPermissionBuild.status", "building");
        try { ValidateSceneAndBuild(); File.WriteAllText("Logs/VerticalPermissionBuild.status", "succeeded"); }
        catch (Exception e) { File.WriteAllText("Logs/VerticalPermissionBuild.status", "failed: " + e); Debug.LogException(e); }
    }
    [MenuItem("Tools/SwimFlight/Validate Trigger Vertical Permission")]
    public static void Run()
    {
        var panelSource = File.ReadAllText("Assets/script/SwimFlight/FlightModeHeadsetPanel.cs");
        Require(panelSource.Contains("前后速度") && panelSource.Contains("升降速度") && !panelSource.Contains("????"),
            "Headset speed labels must contain intact Chinese text");
        var chineseFont = Resources.Load<Font>("Fonts/NotoSansSC-Regular");
        Require(chineseFont != null, "Bundled Chinese font must load");
        foreach (char character in "前后速度升降倍")
            Require(chineseFont.HasCharacter(character), "Missing Chinese glyph: " + character);
        Directory.CreateDirectory("Logs");
        try
        {
            Check();
            PrimaryDirectionAssistValidation.Run();
            Require(File.ReadAllText("Logs/PrimaryDirectionAssistValidation.status").Trim() == "passed",
                "Primary direction / existing stationary-turn regression failed");
            HeadCollisionValidation.Check();
            File.WriteAllText("Logs/VerticalPermissionValidation.status", "passed");
            Debug.Log("VerticalPermissionValidation: passed (input, force, damping, compatibility, lifecycle, regressions)");
        }
        catch (Exception e)
        {
            File.WriteAllText("Logs/VerticalPermissionValidation.status", "failed: " + e);
            Debug.LogException(e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
            throw;
        }
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    public static void ValidateSceneAndBuild()
    {
        Run();
        const string scenePath = "Assets/JapaneseCity/Scenes/JapaneseCity_Demo.unity";
        var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);
        bool opened = !scene.isLoaded;
        if (opened) scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath,
            UnityEditor.SceneManagement.OpenSceneMode.Additive);
        try
        {
        SwimFlightControllerV2 controller = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            controller = root.GetComponentInChildren<SwimFlightControllerV2>(true);
            if (controller != null) break;
        }
        Require(controller != null && controller.verticalPermission != null && controller.verticalPermission.enabled,
            "Actual build scene missing enabled vertical permission");
        Require(controller.forceDrivenDynamics, "Build scene no longer uses force-driven dynamics");
        Require(controller.leftInputController != null && controller.rightInputController != null,
            "Actual left/right controller lifecycle references missing");
        Require(controller.headTransform != null && controller.xrOrigin != null, "Actual tracked rig references missing");
        Require(SwimFlight.Locomotion.HeadCollisionGuard.Find(controller.xrOrigin) != null,
            "Existing head/ground collision guard lost");
        foreach (var root in scene.GetRootGameObjects())
        foreach (var task in root.GetComponentsInChildren<SwimFlight.Experiment.WaypointFlightTask>(true))
        foreach (var waypoint in task.waypoints)
        {
            Require(waypoint != null, "Missing task waypoint");
            foreach (var collider in waypoint.GetComponentsInChildren<Collider>(true))
                Require(!collider.enabled, "Build scene waypoint remains a solid obstacle: " + waypoint.name);
        }
        File.WriteAllText("Logs/VerticalPermissionScene.status", "passed: force-driven; both input controllers bound; collision guard retained");
        }
        finally { if (opened) UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true); }
        BatchBuild.BuildAndroidIndependent();
    }
    private static void Close(float actual, float expected, string message, float tolerance = 1e-5f)
        => Require(Mathf.Abs(actual - expected) <= tolerance, message + ": " + actual + " vs " + expected);
    private static void Close(Vector3 actual, Vector3 expected, string message)
        => Require((actual - expected).magnitude < 1e-5f, message + ": " + actual + " vs " + expected);
    private static void Prime(VerticalFlightPermission gate, bool left, bool right)
    {
        gate.Reset();
        gate.SetInput(true, left ? 1f : 0f, true, right ? 1f : 0f);
        for (int i = 0; i < 30; i++) gate.BeginStep(0.02f);
    }
    private static FlightDynamicsV2 Dynamics(VerticalFlightPermission permission)
        => new FlightDynamicsV2 { verticalPermission = permission,
            asymmetricDamping = new AsymmetricTranslationDamping { enabled = true } };
    private static FlightCommand Stroke(float handY)
        => new BiomechanicalMapper().Map(new FlightInput { leftHandPosition = Vector3.left * 0.5f,
            rightHandPosition = Vector3.right * 0.5f,
            leftHandVelocity = new Vector3(0f, handY, -1f), rightHandVelocity = new Vector3(0f, handY, -1f) });

    private static void Check()
    {
        CheckHorizontalPropulsionGain();
        var speedObject = new GameObject("SpeedControlValidation");
        var speedSwitcher = speedObject.AddComponent<SwimFlight.FlightControlModeSwitcher>();
        speedSwitcher.AdjustFlightSpeed(false, 0.25f);
        Require(Mathf.Approximately(speedSwitcher.horizontalSpeedMultiplier, 1.75f) &&
            Mathf.Approximately(speedSwitcher.verticalSpeedMultiplier, 1f), "Horizontal speed adjustment must be independent");
        speedSwitcher.AdjustFlightSpeed(true, 100f);
        Require(Mathf.Approximately(speedSwitcher.verticalSpeedMultiplier, 3f), "Speed adjustment upper bound");
        UnityEngine.Object.DestroyImmediate(speedObject);
        var gate = new VerticalFlightPermission();
        gate.SetInput(true, 0.59f, true, 0f);
        Require(!gate.Held, "Trigger pressed below threshold");
        gate.SetInput(true, 0.6f, true, 0f);
        Require(gate.LeftHeld && gate.Held, "Left Trigger did not open system permission");
        gate.SetInput(true, 0.5f, true, 0f);
        Require(gate.LeftHeld, "Hysteresis did not retain press");
        gate.SetInput(true, 0.4f, true, 1f);
        Require(!gate.LeftHeld && gate.RightHeld && gate.Held, "Right Trigger did not retain global permission");
        gate.SetInput(false, 1f, false, 1f);
        Require(!gate.Held, "Disconnected input left permission held");
        gate.SetInput(true, float.NaN, true, float.PositiveInfinity);
        Require(!gate.Held, "Invalid analog samples accepted");
        Require(!VerticalFlightPermission.ReadHeld(false, true, 0f, 0.6f, 0.4f), "Released boolean fallback pressed");
        Require(VerticalFlightPermission.ReadHeld(false, true, 1f, 0.6f, 0.4f), "Pressed boolean fallback ignored");

        var dynamics = Dynamics(gate);
        var state = dynamics.Initialize(Vector3.zero, Quaternion.identity);
        var command = Stroke(-0.1f);
        var baselineCommand = command;
        var baseline = Dynamics(null);
        var baselineResult = baseline.Integrate(state, ref baselineCommand, 0.02f);
        var result = dynamics.Integrate(state, ref command, 0.02f);
        Close(command.totalForce.y, 0f, "Unpressed biased stroke added vertical thrust");
        Close(result.velocity.y, 0f, "Unpressed biased stroke rose from rest");
        Close(result.velocity.x, baselineResult.velocity.x, "Horizontal X changed");
        Close(result.velocity.z, baselineResult.velocity.z, "Horizontal Z changed");
        Close(command.rawTotalTorque, baselineCommand.rawTotalTorque, "Trigger changed hand torque");

        foreach (bool left in new[] { true, false })
        {
            Prime(gate, left, !left);
            foreach (float direction in new[] { -1f, 1f })
            {
                command = Stroke(direction);
                result = dynamics.Integrate(state, ref command, 0.02f);
                Require(result.velocity.z > 0f, "Held Trigger removed horizontal propulsion");
                Require(result.velocity.y * direction < 0f, "Either-hand Trigger failed to unlock both-hand vertical stroke");
                var leftOnly = command;
                leftOnly.rightForce = Vector3.zero;
                var rightOnly = command;
                rightOnly.leftForce = Vector3.zero;
                Require(dynamics.Integrate(state, ref leftOnly, 0.02f).velocity.y * direction < 0f,
                    "Left hand propulsion not globally unlocked");
                Require(dynamics.Integrate(state, ref rightOnly, 0.02f).velocity.y * direction < 0f,
                    "Right hand propulsion not globally unlocked");
            }
            var idle = new FlightCommand();
            Close(dynamics.Integrate(state, ref idle, 0.02f).velocity, Vector3.zero, "Trigger alone generated motion");
        }

        // Release moves briefly, continuously increasing damping, with no height lock.
        Prime(gate, true, true);
        gate.SetInput(true, 0f, true, 0f);
        float lastDamping = 0f;
        float lastWeight = 1f;
        state.velocity = Vector3.up * 0.5f;
        float initialHeight = state.position.y;
        for (int i = 0; i < 80; i++)
        {
            var idle = new FlightCommand();
            var previous = state;
            state = dynamics.Integrate(state, ref idle, 0.02f);
            Require(gate.Weight <= lastWeight + 1e-6f && gate.StepDamping >= lastDamping - 1e-6f,
                "Release weights not continuous and monotonic");
            Require(state.velocity.y >= 0f && state.velocity.y <= previous.velocity.y + 1e-6f,
                "Release damping reversed or accelerated vertical velocity");
            Require(Mathf.Abs(state.velocity.y - previous.velocity.y) <= 0.04001f, "Velocity jumped on release");
            if (i == 0) Require(state.velocity.y > 0f, "Release instantly cleared velocity");
            lastWeight = gate.Weight; lastDamping = gate.StepDamping;
        }
        Require(state.position.y > initialHeight && state.velocity.y < 0.01f,
            "Release did not coast briefly and then stop gradually");
        Close(gate.Weight, 0f, "Release did not fully close permission");

        // Compare horizontal solver outputs at identical states, including existing 3D limits.
        foreach (float vy in new[] { -0.5f, 0f, 0.5f })
        {
            var first = Dynamics(null);
            var second = Dynamics(gate);
            var identical = first.Initialize(Vector3.zero, Quaternion.identity);
            identical.velocity = new Vector3(0.6f, vy, 0.7f);
            var a = Stroke(-0.3f); var b = a;
            var aState = first.Integrate(identical, ref a, 0.02f);
            var bState = second.Integrate(identical, ref b, 0.02f);
            Close(aState.velocity.x, bState.velocity.x, "Permission altered horizontal X solver");
            Close(aState.velocity.z, bState.velocity.z, "Permission altered horizontal Z solver");
            Close(first.asymmetricDamping.EffectiveDamping, second.asymmetricDamping.EffectiveDamping,
                "Permission changed existing horizontal damping classification");
        }

        var rapid = new VerticalFlightPermission();
        var rapidDynamics = Dynamics(rapid);
        state = rapidDynamics.Initialize(Vector3.zero, Quaternion.identity);
        state.velocity = Vector3.up * 0.5f;
        for (int i = 0; i < 120; i++)
        {
            rapid.SetInput(true, i % 6 < 3 ? 1f : 0f, true, 0f);
            var idle = new FlightCommand();
            float previous = state.velocity.y;
            state = rapidDynamics.Integrate(state, ref idle, 0.01f);
            Require(state.velocity.y >= 0f && state.velocity.y <= previous + 1e-6f,
                "Repeated presses injected momentum or reversed damping");
            Require(previous - state.velocity.y <= 0.02001f, "Repeated presses jumped velocity");
        }

        foreach (float sign in new[] { -1f, 1f })
        {
            gate.extraVerticalDamping = 1e6f;
            gate.BeginStep(2f);
            float decayed = gate.DampVertical(sign * 0.5f, 0.001f, 2f);
            Require(decayed * sign >= 0f && Mathf.Abs(decayed) <= 0.5f, "Extreme extra damping reversed velocity");
            var extremeDynamics = new FlightDynamicsV2 { verticalPermission = gate, mass = 0.01f,
                linearDamping = 100f, quadraticDamping = 100f };
            state = extremeDynamics.Initialize(Vector3.zero, Quaternion.identity);
            state.velocity = Vector3.up * sign * 0.01f;
            var idle = new FlightCommand();
            Require(extremeDynamics.Integrate(state, ref idle, 0.02f).velocity.y * sign >= 0f,
                "Base drag plus extra damping reversed velocity");
        }
        gate.extraVerticalDamping = 2f;

        // Exact permission integration / exponential damping are independent of step partition.
        float[] decay = new float[3];
        float[] rates = { 50f, 90f, 120f };
        for (int r = 0; r < rates.Length; r++)
        {
            var p = new VerticalFlightPermission(); Prime(p, true, false);
            p.SetInput(true, 0f, true, 0f);
            float velocity = 1f;
            for (int i = 0; i < (int)rates[r]; i++)
            {
                p.BeginStep(1f / rates[r]);
                velocity = p.DampVertical(velocity, 1f, 1f / rates[r]);
            }
            decay[r] = velocity;
        }
        Close(decay[0], decay[1], "Permission decay partition changed", 1e-4f);
        Close(decay[1], decay[2], "Permission decay partition changed", 1e-4f);

        // Net-force cancellation / recovery remain upstream, never invent propulsion.
        var turn = new FlightCommand { leftArm = Vector3.left * 0.5f, rightArm = Vector3.right * 0.5f,
            leftForce = Vector3.forward, rightForce = Vector3.back };
        var turnBase = turn;
        Prime(gate, false, false);
        state = dynamics.Initialize(Vector3.zero, Quaternion.identity);
        baseline = Dynamics(null);
        baselineResult = baseline.Integrate(state, ref turnBase, 0.02f);
        result = dynamics.Integrate(state, ref turn, 0.02f);
        Close(result.velocity, Vector3.zero, "Pure turning gained translation");
        Close(result.angularVelocity, baselineResult.angularVelocity, "Turning strength changed");
        var recovery = new BiomechanicalMapper().Map(new FlightInput {
            leftHandVelocity = Vector3.down, rightHandVelocity = Vector3.down });
        Prime(gate, true, false);
        Close(dynamics.Integrate(state, ref recovery, 0.02f).velocity, Vector3.zero, "Recovery generated thrust");

        // Direction assist remains independently switchable before permission.
        foreach (bool assistEnabled in new[] { false, true })
        {
            var assist = new PrimaryDirectionAssist { enabled = assistEnabled };
            command = Stroke(-1f);
            assist.Apply(ref command, 0.02f);
            Prime(gate, false, false);
            Close(dynamics.Integrate(state, ref command, 0.02f).velocity.y, 0f, "Assist bypassed closed permission");
            Prime(gate, true, false);
            result = dynamics.Integrate(state, ref command, 0.02f);
            Require(result.velocity.y > 0f && result.velocity.z > 0f, "Assist/permission combination lost diagonal");
        }

        Prime(gate, true, false);
        gate.enabled = false;
        var original = Dynamics(null);
        var disabled = Dynamics(gate);
        var oldState = original.Initialize(Vector3.zero, Quaternion.identity);
        var disabledState = oldState;
        for (int i = 0; i < 120; i++)
        {
            gate.SetInput(true, 1f, true, 1f);
            var a = Stroke(i < 60 ? -0.5f : 0.3f); var b = a;
            oldState = original.Integrate(oldState, ref a, 0.01f);
            disabledState = disabled.Integrate(disabledState, ref b, 0.01f);
            Close(oldState.velocity, disabledState.velocity, "Disabled feature changed original velocity");
            Close(oldState.position, disabledState.position, "Disabled feature changed original position");
        }
        Require(!gate.Held && gate.StepDamping == 0f, "Disabled feature retained input/damping");

        // The legacy target-velocity branch uses the same permission, once per substep.
        var shared = new SwimFlight.ComparisonMotionMotor { Settings = new SwimFlight.ComparisonMotionSettings() };
        var sharedBase = new SwimFlight.ComparisonMotionMotor { Settings = new SwimFlight.ComparisonMotionSettings() };
        gate.enabled = true; Prime(gate, false, false);
        shared.AdvanceMotion(new Vector3(0f, 1f, 1f), 10f, 0.02f, gate);
        sharedBase.AdvanceMotion(new Vector3(0f, 1f, 1f), 10f, 0.02f);
        Close(shared.Velocity.y, 0f, "Compatibility motor bypassed permission");
        Close(shared.Velocity.z, sharedBase.Velocity.z, "Compatibility motor horizontal response changed");
        Close(shared.YawRate, sharedBase.YawRate, "Compatibility motor yaw changed");

        CheckLifecycleAndWorldUp();
    }

    private static void CheckHorizontalPropulsionGain()
    {
        var command = new FlightCommand { leftArm = Vector3.left, rightArm = Vector3.right,
            leftForce = new Vector3(0.1f, 0.2f, 0.3f), rightForce = new Vector3(0.2f, 0.1f, 0.3f) };
        var original = command;
        SwimFlightControllerV2.ApplyHorizontalPropulsionGain(ref command, 2f);
        Close(command.translationForce, new Vector3(0.6f, 0.3f, 1.2f), "Horizontal gain not XZ-only");
        Close(command.leftForce, original.leftForce, "Horizontal gain changed left torque source");
        Close(command.rightForce, original.rightForce, "Horizontal gain changed right torque source");
        var turn = new FlightCommand { leftForce = Vector3.forward, rightForce = Vector3.back };
        SwimFlightControllerV2.ApplyHorizontalPropulsionGain(ref turn, 2f);
        Close(turn.translationForce, Vector3.zero, "Horizontal gain created force from turning cancellation");
        var weak = new FlightCommand { leftForce = Vector3.forward * 0.3f, rightForce = Vector3.forward * 0.3f };
        var boosted = weak;
        SwimFlightControllerV2.ApplyHorizontalPropulsionGain(ref boosted, 2f);
        var dynamics = new FlightDynamicsV2();
        var state = dynamics.Initialize(Vector3.zero, Quaternion.identity);
        var a = state; var b = state;
        for (int i = 0; i < 50; i++)
        {
            a = dynamics.Integrate(a, ref weak, 0.01f);
            b = dynamics.Integrate(b, ref boosted, 0.01f);
        }
        Require(b.position.z > a.position.z * 1.5f, "Same weak stroke did not travel noticeably farther");
        Close(a.velocity.y, b.velocity.y, "Pure horizontal stroke gained vertical movement");
        var unchanged = original;
        SwimFlightControllerV2.ApplyHorizontalPropulsionGain(ref unchanged, 1f);
        Close(unchanged.translationForce, original.leftForce + original.rightForce, "Gain=1 did not restore original thrust");
    }

    private static void CheckLifecycleAndWorldUp()
    {
        var rig = new GameObject("PermissionValidationRig");
        var head = new GameObject("PermissionValidationHead");
        var left = new GameObject("PermissionValidationLeft");
        var right = new GameObject("PermissionValidationRight");
        try
        {
            var controller = rig.AddComponent<SwimFlightControllerV2>();
            controller.leftInputController = left.AddComponent<Light>();
            controller.rightInputController = right.AddComponent<Light>();
            var sample = new XRDeviceSample { leftTriggerAvailable = true, rightTriggerAvailable = true,
                leftTrigger = 1f, rightTrigger = 1f };
            var update = typeof(SwimFlightControllerV2).GetMethod("UpdateVerticalPermissionInput",
                BindingFlags.Instance | BindingFlags.NonPublic);
            update.Invoke(controller, new object[] { sample });
            Require(controller.verticalPermission.LeftHeld && controller.verticalPermission.RightHeld, "Both bindings not read");
            controller.leftInputController.enabled = false;
            update.Invoke(controller, new object[] { sample });
            Require(!controller.verticalPermission.LeftHeld && controller.verticalPermission.RightHeld,
                "Disabled left controller kept permission or disabled other hand");
            right.SetActive(false);
            update.Invoke(controller, new object[] { sample });
            Require(!controller.verticalPermission.Held, "Inactive right controller kept permission");
            controller.leftInputController.enabled = true;
            update.Invoke(controller, new object[] { sample });
            controller.verticalPermission.BeginStep(0.2f);
            controller.enabled = false;
            // This runner is EditMode: Unity does not send ordinary MonoBehaviour
            // lifecycle callbacks before entering Play. Exercise the production callback explicitly.
            typeof(SwimFlightControllerV2).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, null);
            Require(!controller.verticalPermission.Held && controller.verticalPermission.Weight == 0f,
                "Disabled flight controller retained permission state");
            head.transform.SetParent(rig.transform);
            head.transform.localRotation = Quaternion.Euler(75f, 0f, 0f);
            var coordinates = new SwimFlight.Coordinate.FlightCoordinateSystem {
                xrOrigin = rig.transform, headTransform = head.transform };
            var world = coordinates.ToWorld(new XRRawInput { leftTrackingVelocity = Vector3.forward }, 0.02f);
            Close(world.leftHandVelocity, Vector3.forward, "Head pitch changed world horizontal");
            var p = new VerticalFlightPermission(); p.BeginStep(0.02f);
            Close(p.FilterForce(new Vector3(1f, 2f, 3f)), new Vector3(1f, 0f, 3f), "World-up force projection failed");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(head); UnityEngine.Object.DestroyImmediate(left);
            UnityEngine.Object.DestroyImmediate(right); UnityEngine.Object.DestroyImmediate(rig);
        }
    }
}
