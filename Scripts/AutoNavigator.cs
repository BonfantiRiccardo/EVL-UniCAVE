using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

namespace UniCAVE
{
    /// <summary>
    /// Moves the CAVE root along a scripted path, so every run follows the same trajectory whatever
    /// the frame rate. Server-only; UCNetwork replicates the root to the clients, as with CAVENavigator.
    /// Leave the other navigation scripts idle during a run, or they will fight over the root.
    /// </summary>
    public class AutoNavigator : NetworkBehaviour
    {
        /// <summary>One leg of a path: a translation and a yaw that run together on one speed profile.</summary>
        [Serializable]
        public class NavSegment
        {
            [Tooltip("Translation in meters in the local frame: x forward, y right, z up. The frame " +
                     "turns with the yaw, so a segment with both traces an arc.")]
            public Vector3 move = Vector3.zero;

            [Tooltip("Yaw in degrees. Positive turns right.")]
            public float turnDegrees = 0f;

            [Tooltip("Duration in seconds. Stretched if the motion would exceed Max Move Speed or Max Turn Speed.")]
            public float seconds = 1f;

            [Tooltip("Speed profile, applied to both the translation and the yaw.")]
            public NavEase ease = NavEase.Constant;
        }

        public enum NavEase { Constant, EaseIn, EaseOut, EaseInOut }

        [Header("Path (executed top to bottom on Start)")]
        [Tooltip("Only used when Path is Custom. The presets replace this list on StartRun.")]
        public List<NavSegment> segments = new();

        [Header("Speed limits")]
        [Tooltip("Peak translation speed in meters/second. Longer segments are stretched to stay under it.")]
        public float maxMoveSpeed = 5.0f;

        [Tooltip("Peak yaw rate in degrees/second. Longer segments are stretched to stay under it.")]
        public float maxTurnSpeed = 45.0f;

        [Tooltip("Scales every translation in the presets to fit a scene. The presets need about 15 to " +
                 "25 m at 1.0. Keep it the same for scenes you want to compare.")]
        public float pathScale = 1.0f;

        [Tooltip("Starting heading of each preset in degrees, indexed by NavPath (0 = Custom, unused). " +
                 "Point each path along a clear direction so it stays out of the scene geometry.")]
        public float[] pathBearingDeg = new float[7];

        [Tooltip("Extra scale per preset, indexed by NavPath. 0 or 1 keeps pathScale; below 1 shrinks " +
                 "only that path.")]
        public float[] pathScaleMul = new float[7];

        [Header("Axis convention")]
        [Tooltip("Flip if forward is reversed on your rig. True matches CAVENavigator, which moves " +
                 "forward along -transform.forward.")]
        public bool invertForward = true;

        [Tooltip("Flip if left and right are reversed on your rig. True matches CAVENavigator.")]
        public bool invertRight = true;

        [Header("Trigger")]
        [Tooltip("Start a run with the Y key. Leave it off if another script already starts runs on " +
                 "Y, or one press starts two runs.")]
        public bool allowKeyboardTrigger = false;

        [Tooltip("Auto-start one run when the scene begins (server only).")]
        public bool runOnStart = false;

        /// <summary>Path presets. They all move the root; none of them moves the head.</summary>
        public enum NavPath
        {
            Custom, ConstantTranslation, AccelDecel, Reversal180, ChunkOscillation, DepthComplexity,
            ReferenceCircuit   // added last so serialized values stay stable
        }

        [Header("Path preset")]
        [Tooltip("Custom runs the segment list. Any other preset replaces the list on StartRun, so a " +
                 "run can be repeated by name.")]
        public NavPath path = NavPath.Custom;

        [Header("Replay")]
        [Tooltip("Advance the path by a fixed step per rendered frame instead of by the clock. A slow " +
                 "run then shows the same frames as a real-time run at 1/replayDeltaTime fps. Frame " +
                 "times measured in this mode are not real.")]
        public bool replayMode = false;

        [Tooltip("Replay step in seconds per frame. 0.016667 = a 60 fps run.")]
        public float replayDeltaTime = 1f / 60f;

        /// <summary>Increments on every StartRun(); 0 before the first run.</summary>
        public int RunId { get; private set; }
        /// <summary>Preset of the current or last run.</summary>
        public NavPath CurrentPath => path;
        /// <summary>Current segment index while running, or -1 when idle.</summary>
        public int Phase { get; private set; } = -1;
        public bool IsRunning { get; private set; }

        /// <summary>Replay steps taken in this run. Only meaningful in replay mode.</summary>
        public int ReplayStep { get; private set; }

        /// <summary>
        /// Replay step of the pose the root holds now. Set here on the server, and by UCNetwork on the
        /// clients from the same message as the pose, so step and pose always match.
        /// </summary>
        public static int LastReplayStep;

        /// <summary>True while a replay run is in progress on the server.</summary>
        public static bool ReplayActive;

        /// <summary>
        /// Optional gate for replay runs: step n is taken only when it returns true, so another script
        /// can hold the path, for example until every node has finished its frame. Null means no gate.
        /// </summary>
        public static Func<int, bool> ReplayGate;

        private int segmentIndex = -1;
        private float segmentElapsed;    // seconds into the current segment
        private float segmentDuration;   // its duration after the speed caps
        private float segmentFraction;   // fraction of the motion already applied

        // Pose the scene starts at. Every run restarts from here.
        private Vector3 homePosition;
        private Quaternion homeRotation;
        private bool homeCaptured;

        void Start()
        {
            if (replayMode)
                Debug.LogWarning("[AutoNavigator] Replay mode is on: the path advances a fixed step per " +
                                 "rendered frame, so frame times from this run are not real.");
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

        /// <summary>Moves the root back to the start pose. Server-only.</summary>
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

            if (replayMode)
            {
                if (ReplayGate != null && !ReplayGate(ReplayStep)) return;
                ReplayStep++;
                LastReplayStep = ReplayStep;
            }

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

            // Midpoint step: half the yaw, the translation, then the other half. Turning fully before
            // moving bends every curved frame the same way, so the path would depend on frame rate.
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

        /// <summary>Fraction of the motion done at normalised time s. f(0) = 0 and f(1) = 1, so the duration is exact.</summary>
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

        // Peak speed of each profile relative to its average speed (the maximum slope of Shape).
        private static float PeakFactor(NavEase ease) => ease switch
        {
            NavEase.EaseIn => 2f,
            NavEase.EaseOut => 2f,
            NavEase.EaseInOut => 1.5f,
            _ => 1f,
        };

        /// <summary>Authored duration, stretched if the motion would exceed a speed cap. Stretching keeps the path's shape.</summary>
        private float SegmentDuration(NavSegment seg)
        {
            float peak = PeakFactor(seg.ease);
            float linear = seg.move.magnitude * peak / Mathf.Max(0.01f, maxMoveSpeed);
            float angular = Mathf.Abs(seg.turnDegrees) * peak / Mathf.Max(0.01f, maxTurnSpeed);
            return Mathf.Max(Mathf.Max(seg.seconds, linear), Mathf.Max(angular, 1e-4f));
        }

        /// <summary>Replaces the segment list with a preset. Only the server runs the path; clients follow the root.</summary>
        public void ApplyPath(NavPath p)
        {
            if (p == NavPath.Custom) return;
            var s = new List<NavSegment>();
            switch (p)
            {
                case NavPath.ConstantTranslation:
                    // The baseline: constant speed along one axis, no rotation.
                    s.Add(Seg(fwd: 24f, sec: 12f));
                    break;

                case NavPath.AccelDecel:
                    // Linear and angular acceleration together, on every axis in both directions.
                    s.Add(Seg(fwd: 8f, right: 3f, up: 1.2f, turn: +45f, sec: 4f, ease: NavEase.EaseIn));
                    s.Add(Seg(fwd: 8f, right: -3f, up: -1.2f, turn: -45f, sec: 4f, ease: NavEase.EaseOut));
                    s.Add(Seg(fwd: -8f, right: -3f, up: 1.2f, turn: -60f, sec: 4f, ease: NavEase.EaseIn));
                    s.Add(Seg(fwd: -8f, right: 3f, up: -1.2f, turn: +60f, sec: 4f, ease: NavEase.EaseOut));
                    break;

                case NavPath.Reversal180:
                    // Two half turns at the yaw cap, each with a sideways and vertical shift.
                    s.Add(Seg(fwd: 14f, sec: 7f));
                    s.Add(Seg(right: 4f, up: 1f, turn: +180f, sec: 6f, ease: NavEase.EaseInOut));
                    s.Add(Seg(fwd: 14f, sec: 7f));
                    s.Add(Seg(right: -4f, up: -1f, turn: -180f, sec: 6f, ease: NavEase.EaseInOut));
                    break;

                case NavPath.ChunkOscillation:
                    // Back and forth with a different amplitude each cycle, so content keeps entering and
                    // leaving the view. The return offsets alternate sign and cancel over four cycles.
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
                    // Drive into the densest part of the scene, then strafe, turn and hold there.
                    s.Add(Seg(fwd: 10f, up: 1f, turn: +25f, sec: 4.5f, ease: NavEase.EaseIn));
                    s.Add(Seg(right: 6f, sec: 4f));
                    s.Add(Seg(turn: +120f, sec: 4f));
                    s.Add(Seg(sec: 2f));
                    s.Add(Seg(right: -6f, turn: -120f, sec: 5f, ease: NavEase.EaseInOut));
                    s.Add(Seg(fwd: 10f, up: -1f, turn: -25f, sec: 4.5f, ease: NavEase.EaseOut));
                    break;

                case NavPath.ReferenceCircuit:
                    // A closed loop: the turns sum to 360 degrees and the last leg closes the position.
                    // Pure translations and rotations sit between the arcs, with a hold at each end.
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

        /// <summary>Selects a preset and starts a run. Server-only.</summary>
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
            transform.SetPositionAndRotation(homePosition, homeRotation * Quaternion.Euler(0f, Bearing(path), 0f));
            ApplyPath(path);
            if (segments == null || segments.Count == 0)
            {
                Debug.LogWarning("[AutoNavigator] No segments defined; nothing to run.");
                return;
            }
            RunId++;
            ReplayStep = 0;
            LastReplayStep = 0;
            ReplayActive = replayMode;
            IsRunning = true;
            segmentIndex = -1;
            AdvanceSegment();
        }

        public void StopRun()
        {
            ReplayActive = false;
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

        /// <summary>
        /// Builds a preset segment. Distances scale with the path scale, and so does the duration of a
        /// pure translation, so speed does not change with scale. Turns and holds never scale.
        /// </summary>
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

        /// <summary>Leg that brings the path back to its start, solved with the current invert flags and scale.</summary>
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
