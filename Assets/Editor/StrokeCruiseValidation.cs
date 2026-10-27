using System;
using System.IO;
using System.Reflection;
using System.Text;
using SwimFlight;
using SwimFlight.Core;
using SwimFlight.Dynamics;
using SwimFlight.Input;
using SwimFlight.Locomotion;
using SwimFlight.Mapping;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Deterministic Unity-engine validation; no XR hardware or simulated user experience claims.</summary>
public static class StrokeCruiseValidation
{
    private static readonly StringBuilder Results = new StringBuilder();
    private const float Dt = 0.02f;

    [MenuItem("SwimFlight/Validate stroke cruise")]
    public static void Run()
    {
        Results.Clear();
        try
        {
            TestStrokeAndRecovery();
            TestMergeAndAlternation();
            TestReverseAndLimits();
            TestVerticalAndBrake();
            TestDynamicsAndTracking();
            TestCollisionAndModeOwnership();
            TestBuiltScene();
            Results.AppendLine("PASS: all deterministic checks. VR gesture comfort and tracking require headset testing.");
            Debug.Log(Results.ToString());
        }
        catch (Exception error)
        {
            Results.AppendLine("FAIL: " + error);
            throw;
        }
        finally
        {
            Directory.CreateDirectory("build");
            File.WriteAllText("build/stroke-cruise-validation.txt", Results.ToString());
        }
    }

    private static StrokeCruiseTranslation NewCruise()
    {
        var cruise = new StrokeCruiseTranslation();
        cruise.Reset();
        return cruise;
    }

    private static void Tick(StrokeCruiseTranslation c, Vector3 left, Vector3 right,
        bool leftActive = true, bool rightActive = false, bool vertical = false, bool brake = false,
        bool turning = false, Vector3? forward = null)
    {
        Vector3 before = c.Velocity;
        c.Sample(left, right, leftActive, rightActive, forward ?? Vector3.forward, vertical, brake, turning, Dt);
        c.Advance(Dt);
        if (!turning)
            Check((c.Velocity - before).magnitude <= Mathf.Max(c.Settings.acceleration, c.Settings.deceleration) * Dt + 0.0001f,
                "acceleration bound", false);
    }

    private static void Idle(StrokeCruiseTranslation c, int frames = 30, bool vertical = false, bool brake = false)
    {
        for (int i = 0; i < frames; i++) Tick(c, Vector3.zero, Vector3.zero, false, false, vertical, brake);
    }

    private static void Stroke(StrokeCruiseTranslation c, Vector3 handVelocity, bool both = false,
        bool vertical = false, bool rightOnly = false, int frames = 15)
    {
        Idle(c, 6, vertical);
        for (int i = 0; i < frames; i++)
            Tick(c, rightOnly ? Vector3.zero : handVelocity, both || rightOnly ? handVelocity : Vector3.zero,
                !rightOnly, both || rightOnly, vertical);
        for (int i = 0; i < 15; i++) Tick(c, Vector3.zero, Vector3.zero, true, both || rightOnly, vertical);
    }

    private static void TestStrokeAndRecovery()
    {
        var c = NewCruise();
        Stroke(c, Vector3.back * 0.7f);
        Check(c.EventCount == 1 && c.TargetVelocity.z > 0f, "one stroke yields one forward increment");
        Vector3 target = c.TargetVelocity;
        for (int i = 0; i < 150; i++) Tick(c, Vector3.forward, Vector3.zero);
        Check(c.EventCount == 1, "active-zone return cannot rearm or repeatedly fire");
        Idle(c, 500);
        Check(Near(c.TargetVelocity, target) && Near(c.Velocity, target), "10 seconds hands relaxed retains cruise");
        c = NewCruise();
        Idle(c);
        for (int i = 0; i < 100; i++) Tick(c, Vector3.back * 0.1f, Vector3.zero);
        for (int i = 0; i < 3; i++) Tick(c, Vector3.back * 0.4f, Vector3.zero);
        Idle(c);
        Check(c.EventCount == 0, "slow motion and short jitter produce no increment");
        c = NewCruise();
        for (int i = 0; i < 30; i++) Tick(c, Vector3.back, Vector3.zero);
        Check(c.EventCount == 0, "reset requires initial recovery before first stroke");
        Idle(c);
        for (int i = 0; i < 30; i++) Tick(c, Vector3.right, Vector3.zero);
        Idle(c);
        Check(c.EventCount == 0, "lateral strokes never create translation");
        Stroke(c, Vector3.back * 0.7f);
        Check(c.EventCount == 1, "fresh stroke can take off after stop/reset");
    }

    private static void TestMergeAndAlternation()
    {
        var single = NewCruise(); var both = NewCruise();
        Stroke(single, Vector3.back * 0.7f);
        Stroke(both, Vector3.back * 0.7f, true);
        Check(both.EventCount == 1 && Near(single.TargetVelocity, both.TargetVelocity), "synchronous hands use max increment, not sum");
        var c = NewCruise();
        Idle(c);
        for (int i = 0; i < 30; i++)
            Tick(c, i < 10 ? Vector3.back * 0.7f : Vector3.zero,
                i < 24 ? Vector3.back * 0.7f : Vector3.zero, true, true);
        Idle(c);
        Check(c.EventCount == 1, "synchronous starts with unequal end times still merge once");
        c = NewCruise();
        Stroke(c, Vector3.back * 0.7f);
        Stroke(c, Vector3.back * 0.7f, rightOnly: true);
        Check(c.EventCount == 2, "alternating recovered hands form separate events");
        c = NewCruise();
        c.Settings.minimumStrokeInterval = 2f;
        Stroke(c, Vector3.back * 0.7f);
        Stroke(c, Vector3.back * 0.7f, rightOnly: true);
        Check(c.EventCount == 1, "global minimum event interval limits alternating acceleration");
    }

    private static void TestReverseAndLimits()
    {
        var c = NewCruise();
        Stroke(c, Vector3.back * 0.7f);
        Stroke(c, Vector3.forward * 1.2f);
        Check(Mathf.Abs(c.TargetVelocity.z) < 0.0001f, "opposite event reaches zero without crossing it");
        Stroke(c, Vector3.forward * 0.7f);
        Check(c.TargetVelocity.z < 0f, "later recovered stroke starts reverse cruise");
        c = NewCruise();
        for (int i = 0; i < 15; i++) Stroke(c, Vector3.back * 1.2f);
        Check(c.TargetVelocity.magnitude <= c.Settings.maximumHorizontalSpeed + 0.0001f,
            "repeated strokes respect horizontal speed cap");
        for (int i = 0; i < 15; i++) Stroke(c, Vector3.down * 1.2f, vertical: true);
        Check(c.TargetVelocity.magnitude <= c.Settings.maximumCombinedSpeed + 0.0001f &&
            Mathf.Abs(c.TargetVelocity.y) <= c.Settings.maximumVerticalSpeed + 0.0001f,
            "diagonal target respects vertical and combined caps");
        Check(c.LastIncrement.magnitude <= c.Settings.maximumStrokeIncrement + 0.0001f, "per-event increment capped");
        c = NewCruise();
        Stroke(c, Vector3.back * 0.7f);
        Idle(c, 100);
        Vector3 before = c.Velocity;
        for (int i = 0; i < 80; i++) Tick(c, Vector3.zero, Vector3.zero, false, false, forward: Vector3.right);
        Check(Near(before, c.Velocity) && Mathf.Abs(c.TargetVelocity.x) < 0.0001f,
            "changing sampled heading without strokes does not rotate cruise vector");
    }

    private static void TestVerticalAndBrake()
    {
        var c = NewCruise();
        Stroke(c, Vector3.down, vertical: false);
        Check(c.EventCount == 0 && c.TargetVelocity.y == 0f, "Trigger off rejects vertical events");
        Stroke(c, Vector3.back * 0.7f);
        Stroke(c, Vector3.down * 0.7f, vertical: true);
        Check(c.TargetVelocity.y > 0f, "Trigger on accepts upward cruise");
        float horizontal = c.TargetVelocity.z;
        Idle(c, 180);
        Check(c.TargetVelocity.y == 0f && c.Velocity.y == 0f && Mathf.Abs(c.TargetVelocity.z - horizontal) < 0.0001f,
            "Trigger release stops vertical smoothly while preserving horizontal cruise");
        Idle(c, 10, brake: true);
        float reduced = c.TargetVelocity.z;
        Idle(c, 100);
        Check(c.TargetVelocity.z == reduced && reduced < horizontal, "partial brake persists after release");
        Idle(c, 300, brake: true);
        Idle(c, 100);
        Check(c.TargetVelocity == Vector3.zero && c.Velocity == Vector3.zero, "held brake stops; release cannot restore speed");
        c = NewCruise();
        Idle(c);
        for (int i = 0; i < 15; i++) Tick(c, Vector3.back, Vector3.zero);
        Idle(c, 1, brake: true);
        Idle(c, 100);
        Check(c.EventCount == 0 && c.TargetVelocity == Vector3.zero, "brake discards an unsettled stroke");
        Stroke(c, Vector3.back * 0.7f);
        Tick(c, Vector3.back, Vector3.zero, turning: true);
        Idle(c, 100);
        Check(c.TargetVelocity == Vector3.zero && c.Velocity == Vector3.zero, "Grip pure turn clears cruise and never resumes on release");
        Stroke(c, Vector3.back * 0.7f);
        c.Reset();
        Idle(c, 100);
        Check(c.EventCount == 0 && c.TargetVelocity == Vector3.zero && c.Velocity == Vector3.zero, "reset clears target, actual and detection state");
    }

    private static void TestDynamicsAndTracking()
    {
        var dynamics = new FlightDynamicsV2 { linearDamping = 100f, quadraticDamping = 100f,
            yawTorqueGain = 6f, gripTurnControl = true, gripTurnHeld = true };
        var state = dynamics.Initialize(Vector3.zero, Quaternion.identity);
        var command = new FlightCommand { leftArm = Vector3.left, rightArm = Vector3.right,
            leftForce = Vector3.forward, rightForce = Vector3.back };
        var rotationOnly = dynamics.Integrate(state, ref command, Dt, rotationOnly: true);
        var normal = dynamics.Integrate(state, ref command, Dt);
        Check(Quaternion.Angle(rotationOnly.orientation, normal.orientation) < 0.0001f &&
            Near(rotationOnly.angularVelocity, normal.angularVelocity), "cruise and legacy preserve identical yaw integrator/parameters");
        state.velocity = Vector3.forward;
        dynamics.gripTurnHeld = false;
        rotationOnly = dynamics.Integrate(state, ref command, Dt, rotationOnly: true);
        normal = dynamics.Integrate(state, ref command, Dt);
        Check(Near(rotationOnly.velocity, state.velocity) && Near(rotationOnly.position, state.position)
            && !Near(normal.velocity, state.velocity), "rotation-only path bypasses old thrust, damping and translation");
        var estimator = new HandVelocityEstimator();
        estimator.Estimate(new XRDeviceSample { leftValid = true, leftPosition = Vector3.one }, Dt);
        var raw = estimator.Estimate(new XRDeviceSample { leftValid = true, leftPosition = Vector3.one * 1.01f }, Dt);
        Check(raw.leftTrackingVelocity.sqrMagnitude > 0f, "actual hand velocity estimator produces motion");
        estimator.Reset();
        raw = estimator.Estimate(new XRDeviceSample { leftValid = true, leftPosition = Vector3.one * 2f }, Dt);
        Check(raw.leftTrackingVelocity == Vector3.zero, "estimator reset prevents reacquisition velocity spike");
        var gate = new SpatialActionGate { enterDistance = 0.41f, exitDistance = 0.38f };
        Check(!gate.IsActive && gate.Update(0.42f) == SpatialActionState.Active
            && gate.Update(0.40f) == SpatialActionState.Active && gate.Update(0.37f) == SpatialActionState.Inactive,
            "real spatial gate retains scene hysteresis");
        var permission = new VerticalFlightPermission();
        permission.SetInput(true, 0.7f, true, 0f);
        bool pressed = permission.Held;
        permission.SetInput(true, 0.5f, true, 0f);
        bool heldInMiddle = permission.Held;
        permission.SetInput(true, 0.3f, true, 0f);
        Check(pressed && heldInMiddle && !permission.Held, "actual Trigger permission keeps press/release hysteresis");
    }

    private static void TestCollisionAndModeOwnership()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var rig = new GameObject("CruiseValidationRig");
        var head = new GameObject("Head"); head.transform.SetParent(rig.transform);
        head.transform.localPosition = Vector3.up * 1.5f;
        var guard = rig.AddComponent<HeadCollisionGuard>(); guard.head = head.transform;
        guard.useGroundFloor = false; guard.Initialize();
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.transform.position = new Vector3(0f, 1.5f, 1f);
        wall.transform.localScale = new Vector3(5f, 5f, 0.1f);
        Physics.SyncTransforms();
        var c = NewCruise(); Stroke(c, Vector3.back * 0.7f); Idle(c, 100);
        guard.Move(Vector3.forward * 2f);
        Check(rig.transform.position.z < 0.85f && guard.LastCorrection.z < 0f, "real collision guard blocks crossing wall");
        c.RemoveIntoSurface(guard.LastCorrection.normalized);
        Check(c.TargetVelocity.z == 0f && c.Velocity.z == 0f, "collision removes inward target and actual momentum");
        var arm = rig.AddComponent<SwimFlightControllerV2>(); arm.xrOrigin = rig.transform; arm.headTransform = head.transform;
        var joystick = rig.AddComponent<JoystickFlightController>(); joystick.xrOrigin = rig.transform; joystick.headTransform = head.transform;
        var lean = rig.AddComponent<IntentionalHeadMotionFlightController>(); lean.xrOrigin = rig.transform; lean.headTransform = head.transform;
        var switcher = rig.AddComponent<FlightControlModeSwitcher>();
        switcher.biomechanicalController = arm; switcher.joystickController = joystick; switcher.intentionalHeadMotionController = lean;
        Invoke(arm, "Start");
        arm.forceDrivenDynamics = false; // cruise still owns its motion even with the legacy shared-motor setting.
        switcher.SelectStrokeCruise();
        Check(arm.enabled && arm.useStrokeCruise && arm.UsesForceDrivenDynamics && !joystick.enabled && !lean.enabled,
            "fourth mode has one enabled controller and bypasses injected comparison motor");
        var motor = (ComparisonMotionMotor)typeof(FlightControlModeSwitcher).GetField("_motor", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(switcher);
        motor.AdvanceMotion(Vector3.forward, 0f, Dt);
        typeof(ComparisonMotionMotor).GetField("_pendingTranslation", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(motor, Vector3.forward);
        Vector3 position = rig.transform.position;
        Invoke(switcher, "LateUpdate");
        Check(Near(position, rig.transform.position), "switcher does not apply pending shared-motor movement in cruise");
        Stroke(arm.Cruise, Vector3.back * 0.7f);
        switcher.SelectBiomechanical();
        Check(arm.enabled && !arm.useStrokeCruise && arm.Cruise.TargetVelocity == Vector3.zero,
            "legacy arm mode remains available and clears cruise on mode change");
        switcher.SelectJoystick();
        Check(!arm.enabled && joystick.enabled && !lean.enabled, "joystick retains sole ownership when selected");
        switcher.SelectStrokeCruise();
        Check(arm.Cruise.TargetVelocity == Vector3.zero && arm.Cruise.Velocity == Vector3.zero,
            "reselecting cruise cannot restore old speed");
        Stroke(arm.Cruise, Vector3.back * 0.7f);
        switcher.SetMotionPaused(true); switcher.SetMotionPaused(false);
        Check(arm.Cruise.TargetVelocity == Vector3.zero && arm.Cruise.Velocity == Vector3.zero,
            "disable/pause/re-enable clears cruise");
        Stroke(arm.Cruise, Vector3.back * 0.7f);
        var trackingValidator = typeof(SwimFlightControllerV2).GetMethod("ValidateCruiseTracking", BindingFlags.NonPublic | BindingFlags.Instance);
        bool validTracking = (bool)trackingValidator.Invoke(arm, new object[] {
            new XRDeviceSample { headTracked = false, leftTracked = true, rightTracked = true }, Dt });
        Check(!validTracking && arm.Cruise.TargetVelocity == Vector3.zero && arm.Cruise.Velocity == Vector3.zero,
            "actual controller tracking-loss path clears cruise");
        validTracking = (bool)trackingValidator.Invoke(arm, new object[] {
            new XRDeviceSample { headTracked = true, leftTracked = true, rightTracked = true }, Dt });
        Check(validTracking && arm.Cruise.TargetVelocity == Vector3.zero,
            "tracking reacquisition does not restore previous cruise");
        Quaternion pose = head.transform.localRotation = Quaternion.Euler(15f, 70f, 5f);
        var applier = new XRLocomotionApplierV2(rig.transform);
        var state = new SwimFlight.Core.FlightState { orientation = Quaternion.identity, position = rig.transform.position };
        applier.Reset(state); state.position += Vector3.back * 0.1f;
        applier.Apply(state, true, true, head.transform.position);
        Check(Quaternion.Angle(head.transform.localRotation, pose) < 0.0001f,
            "real locomotion applier does not overwrite tracked local head rotation");
        UnityEngine.Object.DestroyImmediate(wall); UnityEngine.Object.DestroyImmediate(rig);
        EditorSceneManager.MarkSceneDirty(scene);
    }

    private static void TestBuiltScene()
    {
        const string path = "Assets/JapaneseCity/Scenes/JapaneseCity_Demo.unity";
        Check(Array.Exists(EditorBuildSettings.scenes, s => s.enabled && s.path == path), "audited scene is enabled in Build Settings");
        EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        var switcher = UnityEngine.Object.FindObjectOfType<FlightControlModeSwitcher>();
        Check(switcher != null && switcher.startMode == FlightControlMode.Biomechanical &&
            switcher.biomechanicalController != null && switcher.biomechanicalController.forceDrivenDynamics,
            "actual build scene retains original default and force-driven legacy branch");
        var arm = switcher.biomechanicalController;
        Check(arm.cruiseSettings.brakeButton == CruiseBrakeButton.LeftSecondary,
            "default brake is left Y, avoiding scene water-gun right A and mode-switch left X");
        Check(arm.trackingSpaceReference == SwimFlight.Coordinate.TrackingSpaceReference.XROrigin
            && HeadCollisionGuard.Find(arm.xrOrigin) != null, "actual scene uses rig coordinates and existing collision guard");
        Invoke(arm, "Start");
        switcher.SelectStrokeCruise();
        Check(arm.enabled && arm.useStrokeCruise && arm.UsesForceDrivenDynamics && !switcher.joystickController.enabled
            && !switcher.intentionalHeadMotionController.enabled, "actual scene selects cruise with exactly one flight controller");
        Check(arm.Cruise.TargetVelocity == Vector3.zero && arm.Cruise.Velocity == Vector3.zero,
            "actual scene cruise starts at rest");
        switcher.SelectBiomechanical();
        Check(!arm.useStrokeCruise && arm.enabled, "actual scene can return to original force mode");
        // Do not save: validation must not serialize temporary mode choices or runtime components.
    }

    private static void Invoke(object target, string name)
        => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, null);
    private static bool Near(Vector3 a, Vector3 b) => (a - b).magnitude < 0.0001f;
    private static void Check(bool condition, string name, bool log = true)
    {
        if (!condition) throw new InvalidOperationException(name);
        if (log) Results.AppendLine("PASS: " + name);
    }
}
