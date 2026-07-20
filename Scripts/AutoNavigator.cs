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
        public enum NavKind { MoveForward, MoveBack, MoveLeft, MoveRight, MoveDiagonal, TurnYaw, Hold }

        [Serializable]
        public class NavSegment
        {
            public NavKind kind = NavKind.MoveForward;

            [Tooltip("Distance in meters for Move* segments. For MoveDiagonal this is the FORWARD component.")]
            public float meters = 1f;

            [Tooltip("MoveDiagonal only: RIGHT component in meters (negative = left).")]
            public float rightMeters = 0f;

            [Tooltip("TurnYaw only: yaw in degrees (positive = turn right / clockwise).")]
            public float degrees = 90f;

            [Tooltip("Hold only: seconds to stay still.")]
            public float seconds = 1f;
        }

        [Header("Path (executed top to bottom on Start)")]
        [Tooltip("Default encodes the reference path: 2 m forward, 1 m left, 180 turn, diagonal " +
                 "forward-left, then hold.")]
        public List<NavSegment> segments = new()
        {
            new NavSegment { kind = NavKind.MoveForward, meters = 2f },
            new NavSegment { kind = NavKind.MoveLeft,    meters = 1f },
            new NavSegment { kind = NavKind.TurnYaw,     degrees = 180f },
            new NavSegment { kind = NavKind.MoveDiagonal, meters = 1f, rightMeters = -1f },
            new NavSegment { kind = NavKind.Hold,        seconds = 2f },
        };

        [Header("Speeds")]
        [Tooltip("Translation speed in meters/second.")]
        public float moveSpeed = 1.0f;

        [Tooltip("Yaw speed in degrees/second. ~10 deg/s matches a realistic head turn (a 180 takes " +
                 "~18 s). Raise it only if you deliberately want a fast spin to stress the sort.")]
        public float turnSpeed = 10.0f;

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

        // --- Server-truth run state, read by the benchmark glue and broadcast to all nodes. ---
        /// <summary>Increments on every StartRun(); 0 before the first run.</summary>
        public int RunId { get; private set; }
        /// <summary>Current segment index while running, or -1 when idle.</summary>
        public int Phase { get; private set; } = -1;
        public bool IsRunning { get; private set; }

        // Execution cursor for the active segment.
        private int segmentIndex = -1;
        private float segmentAccum;      // meters covered or degrees turned or seconds waited
        private float segmentTarget;     // meters / degrees / seconds to reach
        private Vector3 segmentWorldDir; // cached world-space move direction for the current segment
        private float segmentTurnSign;   // +1 / -1 for TurnYaw

        void Start()
        {
            if (runOnStart && IsServer) StartRun();
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
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            if (segmentIndex < 0 || segmentIndex >= segments.Count)
            {
                StopRun();
                return;
            }

            NavSegment seg = segments[segmentIndex];
            bool done;
            switch (seg.kind)
            {
                case NavKind.TurnYaw:
                    float stepDeg = turnSpeed * dt;
                    float remainDeg = segmentTarget - segmentAccum;
                    if (stepDeg > remainDeg) stepDeg = remainDeg;
                    transform.Rotate(Vector3.up, segmentTurnSign * stepDeg, Space.World);
                    segmentAccum += stepDeg;
                    done = segmentAccum >= segmentTarget - 1e-4f;
                    break;

                case NavKind.Hold:
                    segmentAccum += dt;
                    done = segmentAccum >= segmentTarget;
                    break;

                default: // any Move* segment
                    float stepDist = moveSpeed * dt;
                    float remainDist = segmentTarget - segmentAccum;
                    if (stepDist > remainDist) stepDist = remainDist;
                    transform.position += segmentWorldDir * stepDist;
                    segmentAccum += stepDist;
                    done = segmentAccum >= segmentTarget - 1e-4f;
                    break;
            }

            if (done) AdvanceSegment();
        }

        /// <summary>Begin (or restart) a run from the first segment. Server-only; safe to call while running.</summary>
        public void StartRun()
        {
            if (!IsServer) return;
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
            segmentAccum = 0f;
            NavSegment seg = segments[segmentIndex];

            float fSign = invertForward ? -1f : 1f;
            float rSign = invertRight ? -1f : 1f;

            switch (seg.kind)
            {
                case NavKind.MoveForward:
                    segmentWorldDir = transform.forward * fSign;
                    segmentTarget = Mathf.Abs(seg.meters);
                    break;
                case NavKind.MoveBack:
                    segmentWorldDir = transform.forward * -fSign;
                    segmentTarget = Mathf.Abs(seg.meters);
                    break;
                case NavKind.MoveRight:
                    segmentWorldDir = transform.right * rSign;
                    segmentTarget = Mathf.Abs(seg.meters);
                    break;
                case NavKind.MoveLeft:
                    segmentWorldDir = transform.right * -rSign;
                    segmentTarget = Mathf.Abs(seg.meters);
                    break;
                case NavKind.MoveDiagonal:
                    Vector3 local = (transform.forward * fSign) * seg.meters
                                  + (transform.right * rSign) * seg.rightMeters;
                    segmentTarget = local.magnitude;
                    segmentWorldDir = segmentTarget > 1e-5f ? local / segmentTarget : Vector3.zero;
                    break;
                case NavKind.TurnYaw:
                    segmentTurnSign = Mathf.Sign(seg.degrees == 0f ? 1f : seg.degrees);
                    segmentTarget = Mathf.Abs(seg.degrees);
                    break;
                case NavKind.Hold:
                    segmentTarget = Mathf.Max(0f, seg.seconds);
                    break;
            }

            // Zero-length segment (e.g. Hold 0 or Move 0): skip immediately so we never stall.
            if (seg.kind != NavKind.Hold && segmentTarget <= 1e-5f) AdvanceSegment();
        }
    }
}
