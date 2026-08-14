using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

namespace UniCAVE
{
    /// <summary>
    /// Drives the CAVE root along a fixed, scripted path so a benchmark run is byte-for-byte
    /// reproducible (same trajectory every time, independent of live head tracking or frame rate).
    /// Attach to the UniCAVE root (same GameObject as <see cref="UCNetwork"/>): this mutates the root
    /// <c>transform</c> on the server only, and UCNetwork replicates it to every display node via
    /// <c>SetTransformsClientRpc</c> — identical to how <see cref="CAVENavigator"/> works.
    ///
    /// The run is a sequential list of <see cref="NavSegment"/>s executed at fixed speeds and advanced
    /// by accumulated distance/angle (not frame count), so the path is wall-clock- and FPS-independent.
    /// <see cref="RunId"/> and <see cref="Phase"/> expose the server-truth run/segment so a metrics
    /// logger can tag every CSV sample with the run and phase it belongs to (the logger reads these via
    /// <see cref="GaussianSplatting"/> benchmark glue, which broadcasts them to all nodes).
    ///
    /// Manual navigation (CAVENavigator / GamepadLocomotion / UCNetwork keyboard nav) should be idle
    /// during an automated run, or the two will fight over the root transform.
    ///
    /// Record/replay seam: the public surface (StartRun/StopRun + RunId/Phase/IsRunning) is what the
    /// benchmark glue and logger depend on, NOT the scripted executor. A future record/replay mode can
    /// swap the segment-driving body in Update for playback of a captured real head trajectory while
    /// keeping that surface identical, so no caller changes. // TODO record/replay.
    /// </summary>
    public class AutoNavigator : NetworkBehaviour
    {
        /// <summary>
        /// One leg of a scripted path. A segment carries a translation AND a yaw, executed
        /// SIMULTANEOUSLY under one shared speed profile, so the two accelerate and decelerate
        /// together and a segment with both traces a curved arc rather than a straight line followed
        /// by a pivot. Setting one of them to zero gives a pure translation or a pure rotation, and
        /// setting both to zero gives a hold, so there is no separate segment kind.
        /// </summary>
        [Serializable]
        public class NavSegment
        {
            [Tooltip("Translation over this segment in the LOCAL frame, in meters. " +
                     "x = forward, y = right, z = up. The frame rotates with the yaw below, so a " +
                     "segment with both traces an arc.")]
            public Vector3 move = Vector3.zero;

            [Tooltip("Yaw over this segment in degrees, signed. Positive turns right.")]
            public float turnDegrees = 0f;

            [Tooltip("Nominal duration in seconds. Extended automatically if the motion would exceed " +
                     "Max Move Speed or Max Turn Speed, so the caps can never be broken by authoring.")]
            public float seconds = 1f;

            [Tooltip("Speed profile across the segment, applied to the translation AND the yaw.")]
            public NavEase ease = NavEase.Constant;
        }

        /// <summary>
        /// Speed profile over a segment. Each is a shape function f(s) mapping normalised time to the
        /// fraction of the motion completed, with f(0)=0 and f(1)=1, so the authored duration is
        /// respected exactly and the peak speed multiplier is known in closed form.
        /// </summary>
        public enum NavEase { Constant, EaseIn, EaseOut, EaseInOut }

        [Header("Path (executed top to bottom on Start)")]
        [Tooltip("Only used when Path is Custom. The named presets overwrite this list on StartRun.")]
        public List<NavSegment> segments = new();

        [Header("Speed limits")]
        [Tooltip("Peak translation speed in meters/second. A segment whose authored duration would " +
                 "exceed this is stretched until it does not.")]
        public float maxMoveSpeed = 5.0f;

        [Tooltip("Peak yaw rate in degrees/second. A segment whose authored duration would exceed " +
                 "this is stretched until it does not.")]
        public float maxTurnSpeed = 45.0f;

        [Tooltip("Multiplies every translation distance in the named presets, so one knob fits a path " +
                 "to a scene. The presets need roughly a 15 to 25 m footprint at 1.0. Use the SAME " +
                 "value for every scene in a session or the conditions are not comparable.")]
        public float pathScale = 1.0f;

        [Tooltip("Per-preset starting heading in degrees, indexed by NavPath (0 = Custom, unused). " +
                 "A path runs the same shape whichever way it faces, so pointing each one down a " +
                 "clear bearing is what keeps it out of the scene's geometry, and it also gives each " +
                 "path a different view of the scene. Derived from the model's own free space; see " +
                 "local/CAVE_Benchmarks.md.")]
        public float[] pathBearingDeg = new float[7];

        [Tooltip("Per-preset multiplier on pathScale, indexed by NavPath. 0 or 1 leaves the path at " +
                 "the scene's pathScale; below 1 shrinks just that path, for the few that cannot fit " +
                 "any bearing at full size.")]
        public float[] pathScaleMul = new float[7];

        [Header("Axis convention")]
        [Tooltip("If forward feels reversed for your rig, flip this. CAVENavigator drives forward as " +
                 "-transform.forward, so this defaults to true to match it.")]
        public bool invertForward = true;

        [Tooltip("If left/right feels reversed for your rig, flip this. Defaults true to match CAVENavigator.")]
        public bool invertRight = true;

        [Header("Trigger")]
        [Tooltip("Standalone Y-key trigger for using this navigator WITHOUT GSBenchmarkController. " +
                 "Leave OFF when GSBenchmarkController is present (it owns Y for both keyboard and " +
                 "gamepad); otherwise a single Y press starts the run twice and double-counts RunId.")]
        public bool allowKeyboardTrigger = false;

        [Tooltip("Auto-start one run when the scene begins (server only).")]
        public bool runOnStart = false;

        /// <summary>
        /// The scripted paths of the temporal evaluation. Pure head rotation and pure head translation
        /// are absent on purpose: the head is fixed at the CAVE centre for the evaluation build, so
        /// every path here is navigation-driven.
        /// </summary>
        public enum NavPath
        {
            Custom, ConstantTranslation, AccelDecel, Reversal180, ChunkOscillation, DepthComplexity,
            // The performance-session path. Appended, not inserted, so serialized values stay stable.
            ReferenceCircuit
        }

        [Header("Path preset")]
        [Tooltip("Custom keeps the segment list below. Anything else overwrites it on StartRun, so a " +
                 "condition can be re-run identically from a name instead of hand-edited waypoints.")]
        public NavPath path = NavPath.Custom;

        [Header("Replay (deterministic capture runs)")]
        [Tooltip("Advance the path by a FIXED step per rendered frame instead of by the wall clock. " +
                 "Capture stalls then cannot change the path, so a slow capture run reproduces exactly " +
                 "the frames a real run at 1/replayDeltaTime fps would show. The pipeline's decisions " +
                 "are frame-parameterised (sort gate on per-frame deltas, frame-counted eviction " +
                 "delay, per-frame upload budget), so they fire identically. Never report frame times " +
                 "from a replay run.")]
        public bool replayMode = false;

        [Tooltip("Replay step in seconds per frame. 0.016667 = a 60 fps run.")]
        public float replayDeltaTime = 1f / 60f;

        // --- Server-truth run state, read by the benchmark glue and broadcast to all nodes. ---
        /// <summary>Increments on every StartRun(); 0 before the first run.</summary>
        public int RunId { get; private set; }
        /// <summary>The preset the current (or last) run used. Broadcast and logged so a folder of runs can be split by path.</summary>
        public NavPath CurrentPath => path;
        /// <summary>Current segment index while running, or -1 when idle.</summary>
        public int Phase { get; private set; } = -1;
        public bool IsRunning { get; private set; }

        // Execution cursor for the active segment. Progress is tracked in TIME, and the shape function
        // converts elapsed time into the fraction of the motion completed, so translation and yaw stay
        // locked to one profile and the authored duration is exact.
        private int segmentIndex = -1;
        private float segmentElapsed;    // seconds into the current segment
        private float segmentDuration;   // its duration after the speed caps have been applied
        private float segmentFraction;   // fraction of the motion already applied, f(elapsed/duration)

        // Pose the scene was authored at. Every run restarts from here, so a run cannot inherit
        // wherever the previous one finished or whatever nudged the root in between.
        private Vector3 homePosition;
        private Quaternion homeRotation;
        private bool homeCaptured;

        void Start()
        {
            CaptureHome();
            if (runOnStart && IsServer) StartRun();
        }

        private void CaptureHome()
        {
            if (homeCaptured) return;
            homePosition = transform.position;
            homeRotation = transform.rotation;
            homeCaptured = true;
        }

        /// <summary>Return the root to the pose the scene started at. Server-only, like the run itself.</summary>
        public void ResetToHome()
        {
            if (!IsServer) return;
            CaptureHome();
            StopRun();
            transform.SetPositionAndRotation(homePosition, homeRotation);
        }

        void Update()
        {
            if (!IsServer) return;

            if (allowKeyboardTrigger)
            {
                var kb = Keyboard.current;
                if (kb != null && kb.yKey.wasPressedThisFrame) StartRun();
            }

            if (!IsRunning) return;

            // Use unscaled delta time so the run is independent of any Time.timeScale changes.
            float dt = replayMode ? Mathf.Max(1e-6f, replayDeltaTime) : Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            if (segmentIndex < 0 || segmentIndex >= segments.Count)
            {
                StopRun();
                return;
            }

            NavSegment seg = segments[segmentIndex];

            segmentElapsed += dt;
            float s = segmentDuration > 0f ? Mathf.Clamp01(segmentElapsed / segmentDuration) : 1f;
            float f = Shape(seg.ease, s);
            float df = f - segmentFraction;      // fraction of the segment to apply this frame
            segmentFraction = f;

            // Half the rotation, then the translation along the rotated local axes, then the other
            // half. Both are scaled by the SAME df, which is what makes the linear and angular motion
            // accelerate together and turns a segment carrying both into an arc.
            //
            // The half-and-half split is a midpoint step, and it is load-bearing rather than tidy. A
            // frame applies a straight chord in place of an arc, so rotating fully before translating
            // biases every curved frame the same way and the bias scales with the frame time: the path
            // then DEPENDS ON FRAME RATE, and two conditions running at different speeds would not
            // trace the same trajectory, which is exactly the comparison a performance session makes.
            // Measured on the reference circuit: rotate-then-translate drifts 157 mm between a 60 fps
            // and a 13 fps run and closes to 43 mm; the midpoint form drifts 0.5 mm and closes to
            // 0.3 mm.
            float halfTurn = seg.turnDegrees * df * 0.5f;
            if (halfTurn != 0f) transform.Rotate(Vector3.up, halfTurn, Space.World);

            if (df != 0f && seg.move != Vector3.zero)
            {
                float fSign = invertForward ? -1f : 1f;
                float rSign = invertRight ? -1f : 1f;
                Vector3 dir = transform.forward * (seg.move.x * fSign)
                            + transform.right * (seg.move.y * rSign)
                            + Vector3.up * seg.move.z;
                transform.position += dir * df;
            }

            if (halfTurn != 0f) transform.Rotate(Vector3.up, halfTurn, Space.World);

            if (s >= 1f) AdvanceSegment();
        }

        /// <summary>
        /// Fraction of a segment's motion completed at normalised time s, with f(0)=0 and f(1)=1.
        /// Because the shape reaches exactly 1 at s=1, the authored duration is respected exactly,
        /// unlike a speed multiplier which has to be clamped away from zero and then overruns.
        /// Peak speed multipliers, used by <see cref="SegmentDuration"/> to enforce the caps:
        /// Constant 1, EaseIn 2, EaseOut 2, EaseInOut 1.5 (smoothstep).
        /// </summary>
        private static float Shape(NavEase ease, float s)
        {
            s = Mathf.Clamp01(s);
            switch (ease)
            {
                case NavEase.EaseIn: return s * s;
                case NavEase.EaseOut: return 1f - (1f - s) * (1f - s);
                case NavEase.EaseInOut: return s * s * (3f - 2f * s);
                default: return s;
            }
        }

        private static float PeakFactor(NavEase ease) => ease switch
        {
            NavEase.EaseIn => 2f,
            NavEase.EaseOut => 2f,
            NavEase.EaseInOut => 1.5f,
            _ => 1f,
        };

        /// <summary>
        /// A segment's real duration: the authored value, stretched if the motion would otherwise
        /// exceed a speed cap. Stretching rather than clipping keeps the geometry of the path exactly
        /// as authored, so a path stays reproducible and closed whatever the caps are set to.
        /// </summary>
        private float SegmentDuration(NavSegment seg)
        {
            float peak = PeakFactor(seg.ease);
            float linear = seg.move.magnitude * peak / Mathf.Max(0.01f, maxMoveSpeed);
            float angular = Mathf.Abs(seg.turnDegrees) * peak / Mathf.Max(0.01f, maxTurnSpeed);
            return Mathf.Max(Mathf.Max(seg.seconds, linear), Mathf.Max(angular, 1e-4f));
        }

        /// <summary>
        /// Replace the segment list with a named path. Server-only, and only the server executes the
        /// path (clients follow the broadcast transform), so no RPC is needed to keep nodes in step.
        /// </summary>
        public void ApplyPath(NavPath p)
        {
            if (p == NavPath.Custom) return;
            var s = new List<NavSegment>();
            switch (p)
            {
                case NavPath.ConstantTranslation:
                    // The CONTROL. Deliberately the one trivial path: constant velocity, one axis, no
                    // rotation, no acceleration. Every other path varies several things at once, so
                    // something has to hold them all still.
                    s.Add(Seg(fwd: 24f, sec: 12f));
                    break;

                case NavPath.AccelDecel:
                    // Linear AND angular acceleration together. Every translation axis is used in both
                    // directions and both turn directions appear, so no axis is left untested.
                    s.Add(Seg(fwd: 8f, right: 3f, up: 1.2f, turn: +45f, sec: 4f, ease: NavEase.EaseIn));
                    s.Add(Seg(fwd: 8f, right: -3f, up: -1.2f, turn: -45f, sec: 4f, ease: NavEase.EaseOut));
                    s.Add(Seg(fwd: -8f, right: -3f, up: 1.2f, turn: -60f, sec: 4f, ease: NavEase.EaseIn));
                    s.Add(Seg(fwd: -8f, right: 3f, up: -1.2f, turn: +60f, sec: 4f, ease: NavEase.EaseOut));
                    break;

                case NavPath.Reversal180:
                    // The order is maximally stale right after the turn. The reversal runs at the
                    // angular cap and carries a lateral and vertical shift, so it is a real change of
                    // viewpoint rather than a pivot in place.
                    s.Add(Seg(fwd: 14f, sec: 7f));
                    s.Add(Seg(right: 4f, up: 1f, turn: +180f, sec: 6f, ease: NavEase.EaseInOut));
                    s.Add(Seg(fwd: 14f, sec: 7f));
                    s.Add(Seg(right: -4f, up: -1f, turn: -180f, sec: 6f, ease: NavEase.EaseInOut));
                    break;

                case NavPath.ChunkOscillation:
                    // Cross residency boundaries repeatedly. The amplitude VARIES per cycle so a
                    // different boundary is crossed each time rather than the same one four times, and
                    // each return carries a yaw plus a lateral and vertical offset so the frustum union
                    // changes SHAPE and not only position. The offsets alternate sign, so over the four
                    // cycles the net lateral and vertical displacement is zero and the path cannot
                    // wander out of the scene.
                    for (int i = 0; i < 4; i++)
                    {
                        float amp = new[] { 10f, 14f, 8f, 12f }[i];
                        float yaw = new[] { +40f, -55f, +30f, -25f }[i];
                        float sign = (i % 2 == 0) ? 1f : -1f;
                        s.Add(Seg(fwd: amp, turn: yaw, sec: 4.5f, ease: NavEase.EaseInOut));
                        s.Add(Seg(fwd: -amp, right: 5f * sign, up: 1.5f * sign, turn: -yaw,
                                  sec: 4.5f, ease: NavEase.EaseInOut));
                    }
                    break;

                case NavPath.DepthComplexity:
                    // Drive into the densest region, then work it: a pure lateral strafe, a pure yaw
                    // sweep and a true hold, all where per-pixel overlap peaks. The pure yaw at maximum
                    // depth complexity is the worst case for the shared sort order.
                    s.Add(Seg(fwd: 10f, up: 1f, turn: +25f, sec: 4.5f, ease: NavEase.EaseIn));
                    s.Add(Seg(right: 6f, sec: 4f));
                    s.Add(Seg(turn: +120f, sec: 4f));
                    s.Add(Seg(sec: 2f));
                    s.Add(Seg(right: -6f, turn: -120f, sec: 5f, ease: NavEase.EaseInOut));
                    s.Add(Seg(fwd: 10f, up: -1f, turn: -25f, sec: 4.5f, ease: NavEase.EaseOut));
                    break;

                case NavPath.ReferenceCircuit:
                    // The performance-session path. Properties that earn it a slot of its own:
                    // (1) It CLOSES exactly, so every condition starts and ends at the same pose and
                    //     none is biased by where in the scene it happened to stop. The turns sum to
                    //     360 so the heading closes; the final leg below closes the position.
                    // (2) It keeps PURE translation and PURE rotation segments alongside the combined
                    //     arcs, and the segment index goes into every log row, so the two can be
                    //     separated offline. That is what makes the distance sort key's rotation
                    //     behaviour measurable without designing a separate experiment.
                    // (3) The holds at each end give a static steady-state baseline inside the same
                    //     logging block.
                    // Turn angles are all different and go both ways on purpose: a circuit of identical
                    // 90 degree turns measures one turn four times.
                    s.Add(Seg(sec: 5f));                                                     // settle
                    s.Add(Seg(fwd: 8f, sec: 4f));                                            // pure translation
                    s.Add(Seg(turn: +75f, sec: 3f, ease: NavEase.EaseInOut));                // pure rotation
                    s.Add(Seg(right: 5f, sec: 3f));                                          // pure strafe
                    s.Add(Seg(turn: +105f, sec: 4f, ease: NavEase.EaseInOut));               // pure rotation
                    s.Add(Seg(fwd: 9f, up: 1.5f, turn: +60f, sec: 5f, ease: NavEase.EaseInOut));  // arc, climbing
                    s.Add(Seg(turn: -40f, sec: 2.5f, ease: NavEase.EaseInOut));              // counter-rotation
                    s.Add(Seg(fwd: 6f, sec: 3f));                                            // pure translation
                    s.Add(Seg(fwd: 7f, right: -3f, up: -1.5f, turn: +100f, sec: 5f, ease: NavEase.EaseIn));
                    s.Add(Seg(turn: +60f, sec: 3f, ease: NavEase.EaseInOut));                // pure rotation
                    s.Add(ClosingLeg(s, sec: 4f, ease: NavEase.EaseInOut));
                    s.Add(Seg(sec: 5f));                                                     // settle
                    break;
            }
            if (s.Count > 0) segments = s;
        }

        /// <summary>
        /// Select a path preset and begin a run from its first segment. Server-only. This is what the
        /// number keys on the benchmark controller call, so a condition is re-run from a NAME rather
        /// than from whatever the inspector happened to hold, and the chosen preset is logged.
        /// </summary>
        public void StartRun(NavPath p)
        {
            if (!IsServer) return;
            path = p;
            StartRun();
        }

        /// <summary>Begin (or restart) a run from the first segment. Server-only; safe to call while running.</summary>
        public void StartRun()
        {
            if (!IsServer) return;
            CaptureHome();
            // Every run starts from the same pose and the preset's own heading, so a path is
            // reproducible no matter what happened before it.
            transform.SetPositionAndRotation(homePosition, homeRotation * Quaternion.Euler(0f, Bearing(path), 0f));
            ApplyPath(path);
            if (segments == null || segments.Count == 0)
            {
                Debug.LogWarning("[AutoNavigator] No segments defined; nothing to run.");
                return;
            }
            RunId++;
            IsRunning = true;
            segmentIndex = -1;
            AdvanceSegment();
        }

        public void StopRun()
        {
            IsRunning = false;
            Phase = -1;
            segmentIndex = -1;
        }

        private void AdvanceSegment()
        {
            segmentIndex++;
            if (segmentIndex >= segments.Count)
            {
                StopRun();
                return;
            }

            Phase = segmentIndex;
            segmentElapsed = 0f;
            segmentFraction = 0f;
            segmentDuration = SegmentDuration(segments[segmentIndex]);
        }

        /// <summary>
        /// Builds a segment. Distances scale with <see cref="pathScale"/>, and so does the duration of
        /// a segment that translates without turning, which keeps linear speed identical at any scale.
        /// Yaw, pure rotations and holds never scale, so turn rates and hold times are also identical.
        /// Speed has to be preserved across scenes because the sort gate thresholds per-frame position
        /// and rotation deltas: a smaller scene run at a lower speed would fire fewer sorts and look
        /// cheaper for a reason that is the path rather than the content.
        /// </summary>
        private float Bearing(NavPath p)
        {
            int i = (int)p;
            return (pathBearingDeg != null && i < pathBearingDeg.Length) ? pathBearingDeg[i] : 0f;
        }

        /// <summary>Scene scale times this preset's own multiplier. A multiplier of 0 means "unset".</summary>
        private float EffectiveScale(NavPath p)
        {
            int i = (int)p;
            float mul = (pathScaleMul != null && i < pathScaleMul.Length) ? pathScaleMul[i] : 0f;
            if (mul <= 0f) mul = 1f;
            return Mathf.Max(0.01f, pathScale) * mul;
        }

        private NavSegment Seg(float fwd = 0f, float right = 0f, float up = 0f,
                               float turn = 0f, float sec = 1f, NavEase ease = NavEase.Constant)
        {
            float k = EffectiveScale(path);
            bool translates = fwd != 0f || right != 0f || up != 0f;
            return new NavSegment
            {
                move = new Vector3(fwd, right, up) * k,
                turnDegrees = turn,
                seconds = (translates && turn == 0f) ? sec * k : sec,
                ease = ease,
            };
        }

        /// <summary>
        /// The leg that returns the path to its start, solved from the segments already in the list
        /// under the CURRENT invert flags and scale. Computed rather than baked: a constant solved
        /// offline silently misses by tens of meters if the flags stop matching or a leg is edited.
        /// </summary>
        private NavSegment ClosingLeg(List<NavSegment> core, float sec, NavEase ease)
        {
            float fSign = invertForward ? -1f : 1f;
            float rSign = invertRight ? -1f : 1f;
            const int Steps = 512;

            float heading = 0f;
            Vector3 residual = Vector3.zero;
            foreach (NavSegment g in core)
            {
                for (int i = 0; i < Steps; i++)
                {
                    float rad = (heading + g.turnDegrees * ((i + 0.5f) / Steps)) * Mathf.Deg2Rad;
                    Vector3 f = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
                    Vector3 r = new Vector3(Mathf.Cos(rad), 0f, -Mathf.Sin(rad));
                    residual += (f * (g.move.x * fSign) + r * (g.move.y * rSign)
                                 + Vector3.up * g.move.z) / Steps;
                }
                heading += g.turnDegrees;
            }

            float endRad = heading * Mathf.Deg2Rad;
            Vector3 fEnd = new Vector3(Mathf.Sin(endRad), 0f, Mathf.Cos(endRad));
            Vector3 rEnd = new Vector3(Mathf.Cos(endRad), 0f, -Mathf.Sin(endRad));
            return new NavSegment
            {
                move = new Vector3(Vector3.Dot(-residual, fEnd) / fSign,
                                   Vector3.Dot(-residual, rEnd) / rSign,
                                   -residual.y),
                turnDegrees = 0f,
                seconds = sec * EffectiveScale(path),
                ease = ease,
            };
        }
    }
}
