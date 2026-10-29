using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

namespace UniCAVE
{
    /// <summary>
    /// Frame lockstep between the head and its display clients over a dedicated TCP channel. Every frame
    /// the head sends the rig pose once all pose writers have run. While the present barrier is in sync, a
    /// client waits for the next pose and shows every pose once and in order. Otherwise it waits up to
    /// "frameLockWaitMs" (default 8) for a new pose, shows the newest one, or reuses the previous pose.
    /// With <see cref="SwapBarrier"/> active, the head picks the state at which all instances join the
    /// barrier. Once every instance is in sync, the head holds once, until every client has shown the
    /// previous pose and every present queue is empty, so the next pose reaches all screens at the same
    /// refresh; this is the only time the head waits for the clients. It then measures for every pose how
    /// much later a client's GPU showed it than the head's, and keeps the difference it measured right
    /// after the hold. With "frameLockTargetMs N" the head skips the hold and keeps the difference within
    /// half a frame of N instead. Corrections only ever delay: when a client is behind, the head shows its
    /// own poses a frame later; when a client is ahead, the head removes that delay or the client shows its
    /// previous pose once more ("frameLockAutoAlign 0" turns the hold and the corrections off). Advancing a
    /// client instead would leave it waiting a whole frame for a pose. On the head, ] and [ apply the same
    /// corrections by hand and move the target with them.
    /// Active only with the command-line argument "frameLock 1"; turns VSync on.
    /// </summary>
    [DefaultExecutionOrder(-30000)]
    public class FrameLock : MonoBehaviour
    {
        const uint StateMagic = 0x4B434C46;
        const uint AckMagic = 0x4B414C46;
        const int StateSize = 88;
        const int AckSize = 24;

        const uint AckFlagShownValid = 1;
        const uint AckFlagBarrierReady = 2;
        const uint AckFlagInOrder = 4;

        const int DefaultWaitBudgetMs = 8;     // newest-pose mode: wait this long, then reuse the previous pose
        const int InOrderTimeoutMs = 2000;     // in-order mode: a skipped frame would shift the pairing for good
        const int MaxBacklog = 8;              // in-order mode drops to the newest pose beyond this many
        const int ClientSilenceMs = 2000;      // the head drops a client that stops acknowledging
        const int JoinLeadStates = 10;         // the head announces the join state this far ahead
        const int JoinSettleFrames = 10;       // every instance reports ready this long before the join
        const int HoldSettleFrames = 10;       // every instance is in sync this long before the head holds
        const int HoldTimeoutMs = 1000;        // the head stops waiting for an acknowledgement after this
        const int HoldDrainFrames = 6;         // frames that let every present queue empty during the hold
        const int AlignWindow = 30;            // pairing measurements needed before a correction
        const float AlignAgreement = 0.8f;     // share of the window that must agree on the offset
        const int AlignQuietFrames = 60;       // after a correction, new measurements still describe the old pairing
        const int MaxHeadDelay = 4;            // a larger delay means something else is wrong
        const float ConnectRetrySeconds = 1f;
        const float ReportInterval = 5f;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceCounter(out long count);
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceFrequency(out long frequency);
#endif

        public static FrameLock Instance { get; private set; }

        /// <summary>True on a client while poses arrive through the frame lock; UCNetwork then ignores its pose RPC.</summary>
        public static bool ClientLocked { get; private set; }

        /// <summary>Id of the last state this instance sent (head) or applied (client).</summary>
        public static uint CurrentState { get; private set; }

        struct Shown
        {
            public long Qpc;                   // vsync at which the frame reached the screen
            public uint Tag;                   // state id that frame showed
        }

        sealed class Peer
        {
            public Socket Socket;
            public string Name;
            public readonly byte[] Rx = new byte[4096];
            public int RxLen;
            public bool Synced;                // has acknowledged at least one state
            public uint LastAck;               // newest state the client applied
            public uint LastAckFlags;
            public long LastAckTime;
            public int HoldPending;            // poses the client still has to show twice, sent with the next state
            public readonly List<Shown> Reported = new List<Shown>(32);   // client screen samples not yet matched
            public long LastReportedQpc;
            public uint LastMatchedTag;
            public readonly int[] Offsets = new int[AlignWindow];       // frames the client is behind the target
            public int OffsetCount;
            public int OffsetNext;
            public int Mode;
            public bool HasReference;
            public double ReferenceMs;         // client display minus head display, measured after the hold
            public readonly List<double> ReferenceSamples = new List<double>(AlignWindow);
            public int ReportSamples;
            public int ReportAligned;
            public readonly List<double> ReportDeltaMs = new List<double>(512);   // client display minus head display, same pose
        }

        struct State
        {
            public uint Id;
            public uint PoseSeq;
            public uint JoinAt;
            public uint Hold;                  // poses the receiving client shows twice before this one
            public int ReplayStep;
            public float HeadTime;
            public Vector3 RootPos;
            public Quaternion RootRot;
            public Vector3 HeadPos;
            public Quaternion HeadRot;
        }

        UCNetwork uc;
        bool initialized;
        bool isHead;
        bool ended;
        long clockFrequency;
        readonly byte[] stateBuffer = new byte[StateSize];
        readonly byte[] ackBuffer = new byte[AckSize];

        TcpListener listener;
        readonly List<Peer> peers = new List<Peer>();
        uint stateId;
        uint joinAt;
        int allReadyFrames;
        readonly State[] sentRing = new State[64];     // recent states, for the head's delayed image
        int headDelay;                                 // frames the head shows its poses after sending them
        bool restorePending;
        Vector3 savedRootPos, savedHeadPos;
        Quaternion savedRootRot, savedHeadRot;
        readonly Shown[] headShown = new Shown[256];   // the head's own screen samples, oldest overwritten
        int headShownCount;
        int headShownNext;
        uint headShownSeq;
        uint headNewestShown;
        double periodMs;
        float targetMs;                        // wanted client display time minus head display time for one pose
        bool autoAlign = true;
        bool holdToAlign;                      // hold once and keep that pairing; false with an explicit target
        bool held;
        int syncedFrames;                      // consecutive frames with every instance in sync, before the hold
        int alignQuietUntil;                   // frame before which measurements are not used for corrections
        int autoCorrections;

        string serverAddress;
        int serverPort;
        TcpClient tcp;
        Task connectTask;
        float nextConnect;
        Peer server;
        readonly Queue<State> pending = new Queue<State>();
        bool clientJoinIssued;
        bool clientInOrder;
        int holdsOwed;                         // client: poses still to show twice
        uint holdsSeenUpTo;                    // client: highest state whose hold count is already counted

        int waitBudgetMs = DefaultWaitBudgetMs;
        double waitSumMs;
        double waitMaxMs;
        int frames;
        int readyFrames;                       // client frames whose pose had already arrived
        int repeatFrames;                      // client frames that reused the previous pose
        float nextReport;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if(Application.isEditor || Util.GetArg("frameLock") != "1") return;

            UCNetwork root = FindFirstObjectByType<UCNetwork>(FindObjectsInactive.Include);
            if(root == null)
            {
                Debug.LogWarning("FrameLock: no UCNetwork in the scene. Disabled.");
                return;
            }
            SwapBarrier.JoinControlledExternally = true;
            if(root.GetComponent<FrameLock>() == null) root.gameObject.AddComponent<FrameLock>();
        }

        void Awake()
        {
            Instance = this;
            uc = GetComponent<UCNetwork>();
            SwapBarrier.JoinControlledExternally = true;
            clockFrequency = ClockFrequency();
            autoAlign = Util.GetArg("frameLockAutoAlign") != "0";
            bool fixedTarget = float.TryParse(Util.GetArg("frameLockTargetMs"), NumberStyles.Float, CultureInfo.InvariantCulture, out targetMs);
            holdToAlign = autoAlign && !fixedTarget;
        }

        void Start()
        {
            QualitySettings.vSyncCount = 1;
        }

        void Update()
        {
            if(ended) return;
            if(!initialized)
            {
                TryInit();
                if(!initialized || ended) return;
            }

            if(isHead)
            {
                HeadUpdate();
            }
            else
            {
                ClientUpdate();
                if(server != null && !ended) TagFrame(CurrentState);
            }
        }

        void LateUpdate()
        {
            if(ended || !initialized || !isHead) return;
            HeadLateUpdate();
        }

        void OnDestroy()
        {
            ClientLocked = false;
            foreach(Peer p in peers) Close(p.Socket);
            peers.Clear();
            if(server != null) Close(server.Socket);
            tcp?.Close();
            listener?.Stop();
        }

        /// <summary>True while the present barrier flips this instance in step with the others.</summary>
        static bool InSync(SwapBarrier barrier)
        {
            return barrier != null && barrier.SyncMode >= 2;
        }

        /// <summary>Starts the listener on the head or the connection attempts on a client once Netcode runs.</summary>
        void TryInit()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if(nm == null || !nm.IsListening) return;

            UnityTransport transport = nm.NetworkConfig.NetworkTransport as UnityTransport;
            string address = transport != null ? transport.ConnectionData.Address : "127.0.0.1";
            int port = (transport != null ? transport.ConnectionData.Port : 7777) + 1;
            string portArg = Util.GetArg("frameLockPort");
            if(portArg != null && int.TryParse(portArg, out int parsed)) port = parsed;

            isHead = nm.IsServer;
            initialized = true;

            if(isHead)
            {
                IPAddress bind = IsLoopback(address) ? IPAddress.Loopback : IPAddress.Any;
                try
                {
                    listener = new TcpListener(bind, port);
                    listener.Start();
                }
                catch(SocketException e)
                {
                    Debug.LogError($"FrameLock: cannot listen on {bind}:{port} ({e.Message}). Disabled.");
                    listener = null;
                    ended = true;
                    return;
                }
                string pairing = !autoAlign ? "automatic pairing off"
                    : holdToAlign ? "holds once when every instance is in sync, then keeps that pairing"
                    : $"keeps the pairing at target {targetMs.ToString("F1", CultureInfo.InvariantCulture)} ms";
                Debug.Log($"FrameLock: head listening on {bind}:{port}; {pairing}. VSync on.");
            }
            else
            {
                serverAddress = address;
                serverPort = port;
                if(int.TryParse(Util.GetArg("frameLockWaitMs"), out int budget) && budget >= 0) waitBudgetMs = budget;
                Debug.Log($"FrameLock: client will connect to {address}:{port}; waits up to {waitBudgetMs} ms for a pose until the barrier is in sync. VSync on.");
            }
        }

        void HeadUpdate()
        {
            // Pose writers keep working from the newest pose, not the delayed one the head showed.
            if(restorePending)
            {
                uc.transform.SetPositionAndRotation(savedRootPos, savedRootRot);
                if(uc.head != null) uc.head.transform.SetPositionAndRotation(savedHeadPos, savedHeadRot);
                restorePending = false;
            }

            Keyboard kb = Keyboard.current;
            if(kb == null) return;
            if(kb.rightBracketKey.wasPressedThisFrame)
            {
                MoveTarget(-ShiftClients(1));
                BeginQuiet();
            }
            if(kb.leftBracketKey.wasPressedThisFrame)
            {
                MoveTarget(-ShiftClients(-1));
                BeginQuiet();
            }
        }

        /// <summary>Moves the pairing target with a manual shift, so the automatic correction keeps the choice.</summary>
        void MoveTarget(int frames)
        {
            if(frames == 0 || periodMs <= 0.0) return;
            if(holdToAlign)
            {
                foreach(Peer p in peers)
                {
                    if(p.HasReference) p.ReferenceMs += frames * periodMs;
                }
                Debug.Log($"FrameLock: pairing moved by {frames} frame(s) by hand; the head keeps the new pairing.");
                return;
            }
            targetMs += frames * (float)periodMs;
            string value = targetMs.ToString("F1", CultureInfo.InvariantCulture);
            Debug.Log($"FrameLock: pairing target now {value} ms; launch with \"frameLockTargetMs {value}\" to start from it.");
        }

        /// <summary>Runs after every pose writer and sends this frame's pose. Waits for the clients only in the one-time hold.</summary>
        void HeadLateUpdate()
        {
            long start = Now();
            AcceptPeers();
            PollAcks(start);
            SwapBarrier barrier = ActiveBarrier();
            MeasurePairing(barrier);
            AutoAlign(barrier);

            stateId++;
            ScheduleJoin(barrier);
            if(HoldDue(barrier)) HoldForClients();

            State s = new State
            {
                Id = stateId,
                PoseSeq = UCNetwork.LastPoseSeq,
                JoinAt = joinAt,
                ReplayStep = AutoNavigator.LastReplayStep,
                HeadTime = Time.time,
                RootPos = uc.transform.position,
                RootRot = uc.transform.rotation,
                HeadPos = uc.head != null ? uc.head.transform.position : Vector3.zero,
                HeadRot = uc.head != null ? uc.head.transform.rotation : Quaternion.identity,
            };
            WriteState(stateBuffer, s);
            sentRing[s.Id & 63] = s;

            for(int i = peers.Count - 1; i >= 0; i--)
            {
                Peer p = peers[i];
                PutU(stateBuffer, 84, (uint)p.HoldPending);
                p.HoldPending = 0;
                if(!Send(p.Socket, stateBuffer, StateSize)) Drop(i, "send failed");
            }

            CurrentState = s.Id;
            TagFrame(ShowDelayed(s));

            if(joinAt != 0 && s.Id == joinAt && barrier != null) barrier.Join();

            Report();
        }

        /// <summary>Shows the pose sent headDelay frames ago instead of the newest one; returns the state shown.</summary>
        uint ShowDelayed(State newest)
        {
            if(headDelay <= 0 || newest.Id <= (uint)headDelay) return newest.Id;
            uint id = newest.Id - (uint)headDelay;
            State d = sentRing[id & 63];
            if(d.Id != id) return newest.Id;

            savedRootPos = uc.transform.position;
            savedRootRot = uc.transform.rotation;
            if(uc.head != null)
            {
                savedHeadPos = uc.head.transform.position;
                savedHeadRot = uc.head.transform.rotation;
                uc.head.transform.SetPositionAndRotation(d.HeadPos, d.HeadRot);
            }
            uc.transform.SetPositionAndRotation(d.RootPos, d.RootRot);
            RecordPose(d);
            restorePending = true;
            return d.Id;
        }

        /// <summary>
        /// Moves every client's image by frames relative to the head's: positive when the clients are behind,
        /// negative when they are ahead. Only ever delays: the head's own delay grows, shrinks, or the clients
        /// show a pose twice. Returns the frames applied, fewer when the head delay reaches its limit.
        /// </summary>
        int ShiftClients(int frames)
        {
            int applied = frames;
            if(frames > 0)
            {
                int before = headDelay;
                headDelay = Math.Min(MaxHeadDelay, headDelay + frames);
                applied = headDelay - before;
            }
            else if(frames < 0)
            {
                int fromHead = Math.Min(headDelay, -frames);
                headDelay -= fromHead;
                foreach(Peer p in peers) p.HoldPending += -frames - fromHead;
            }
            Debug.Log($"FrameLock: head image delay now {headDelay} frame(s){(frames < 0 && headDelay == 0 ? ", clients show a pose twice" : "")}.");
            return applied;
        }

        void AcceptPeers()
        {
            while(listener != null && listener.Pending())
            {
                Socket socket = listener.AcceptSocket();
                socket.NoDelay = true;
                Peer p = new Peer { Socket = socket, Name = socket.RemoteEndPoint != null ? socket.RemoteEndPoint.ToString() : "client" };
                peers.Add(p);
                Debug.Log($"FrameLock: {p.Name} connected; {peers.Count} client(s).");
            }
        }

        /// <summary>Reads the acknowledgements that have arrived and drops clients that closed or went silent.</summary>
        void PollAcks(long now)
        {
            for(int i = peers.Count - 1; i >= 0; i--)
            {
                Peer p = peers[i];
                if(!Pump(p, 0) || !ReadAcks(p, now))
                {
                    Drop(i, "connection lost");
                    continue;
                }
                if(p.Synced && (now - p.LastAckTime) * 1000L / clockFrequency > ClientSilenceMs)
                {
                    Drop(i, $"no acknowledgement for {ClientSilenceMs} ms");
                }
            }
        }

        bool ReadAcks(Peer p, long now)
        {
            while(TakePacket(p, ackBuffer, AckSize))
            {
                if(GetU(ackBuffer, 0) != AckMagic) return false;

                uint id = GetU(ackBuffer, 4);
                uint shownTag = GetU(ackBuffer, 8);
                p.LastAckFlags = GetU(ackBuffer, 12);
                long shownQpc = GetL(ackBuffer, 16);
                p.LastAck = id;
                p.LastAckTime = now;
                if(!p.Synced)
                {
                    p.Synced = true;
                    Debug.Log($"FrameLock: {p.Name} in lockstep from state {id}.");
                }

                bool usable = (p.LastAckFlags & AckFlagShownValid) != 0 && (p.LastAckFlags & AckFlagInOrder) != 0;
                if(usable && shownQpc != p.LastReportedQpc)
                {
                    p.LastReportedQpc = shownQpc;
                    if(p.Reported.Count == p.Reported.Capacity) p.Reported.RemoveAt(0);
                    p.Reported.Add(new Shown { Qpc = shownQpc, Tag = shownTag });
                }
            }
            return true;
        }

        /// <summary>
        /// For every pose a client showed, takes how much later its screen showed it than the head's screen,
        /// and counts how many frames that is away from the target.
        /// </summary>
        void MeasurePairing(SwapBarrier barrier)
        {
            if(barrier == null || !barrier.DisplayedValid) return;
            if(barrier.DisplayedSeq != headShownSeq)
            {
                headShownSeq = barrier.DisplayedSeq;
                headShown[headShownNext] = new Shown { Qpc = barrier.DisplayedQpc, Tag = barrier.DisplayedTag };
                headShownNext = (headShownNext + 1) % headShown.Length;
                if(headShownCount < headShown.Length) headShownCount++;
                headNewestShown = Math.Max(headNewestShown, barrier.DisplayedTag);
            }

            long period = barrier.RefreshPeriodQpc;
            if(period <= 0 || !InSync(barrier)) return;
            periodMs = period * 1000.0 / clockFrequency;
            foreach(Peer p in peers)
            {
                while(p.Reported.Count > 0)
                {
                    Shown r = p.Reported[0];
                    if(r.Tag > headNewestShown) break;
                    p.Reported.RemoveAt(0);
                    if(r.Tag == p.LastMatchedTag) continue;
                    p.LastMatchedTag = r.Tag;
                    if(!TryHeadFirstShown(r.Tag, out long headQpc)) continue;
                    RecordSample(p, (r.Qpc - headQpc) * 1000.0 / clockFrequency);
                }
            }
        }

        /// <summary>When the head's screen first showed the given state, if that is still in the sample ring.</summary>
        bool TryHeadFirstShown(uint tag, out long qpc)
        {
            qpc = long.MaxValue;
            for(int i = 0; i < headShownCount; i++)
            {
                if(headShown[i].Tag == tag && headShown[i].Qpc < qpc) qpc = headShown[i].Qpc;
            }
            return qpc != long.MaxValue;
        }

        /// <summary>
        /// Records one pairing sample. After the hold, the first samples become the client's reference; every
        /// later sample counts the frames between the measured pairing and the reference or the fixed target.
        /// </summary>
        void RecordSample(Peer p, double deltaMs)
        {
            p.ReportSamples++;
            if(p.ReportDeltaMs.Count < p.ReportDeltaMs.Capacity) p.ReportDeltaMs.Add(deltaMs);
            bool quiet = Time.frameCount < alignQuietUntil;

            if(holdToAlign && !p.HasReference)
            {
                if(!held || quiet) return;
                p.ReferenceSamples.Add(deltaMs);
                if(p.ReferenceSamples.Count < AlignWindow) return;
                p.ReferenceSamples.Sort();
                p.ReferenceMs = p.ReferenceSamples[AlignWindow / 2];
                p.HasReference = true;
                Debug.Log($"FrameLock: {p.Name} keeps the pairing from the hold; it shows each pose {BeforeAfter(p.ReferenceMs)} the head.");
                return;
            }

            double target = holdToAlign ? p.ReferenceMs : targetMs;
            int offset = (int)Math.Round((deltaMs - target) / periodMs);
            if(offset == 0) p.ReportAligned++;
            if(quiet) return;
            p.Offsets[p.OffsetNext] = offset;
            p.OffsetNext = (p.OffsetNext + 1) % AlignWindow;
            if(p.OffsetCount < AlignWindow) p.OffsetCount++;
        }

        /// <summary>Once every client's offset is stable, delays whichever side is ahead until they match.</summary>
        void AutoAlign(SwapBarrier barrier)
        {
            if(!autoAlign || peers.Count == 0 || !InSync(barrier) || Time.frameCount < alignQuietUntil) return;

            int mostBehind = int.MinValue;
            foreach(Peer p in peers)
            {
                if(p.OffsetCount < AlignWindow || !StableMode(p, out p.Mode)) return;
                mostBehind = Math.Max(mostBehind, p.Mode);
            }

            bool misaligned = false;
            foreach(Peer p in peers) misaligned |= p.Mode != 0;
            if(!misaligned) return;

            // Line everyone up with the client that is furthest behind, then hold the others back to it.
            int shift = mostBehind;
            int delayBefore = headDelay;
            if(shift > 0)
            {
                headDelay = Math.Min(MaxHeadDelay, headDelay + shift);
            }
            else
            {
                headDelay -= Math.Min(headDelay, -shift);
            }
            int headMoved = headDelay - delayBefore;
            foreach(Peer p in peers) p.HoldPending += Math.Max(0, headMoved - p.Mode);

            autoCorrections++;
            Peer first = peers[0];
            Debug.Log($"FrameLock: pairing correction {autoCorrections}: {first.Name} shows poses {Math.Abs(first.Mode)} frame(s) " +
                      $"{(first.Mode > 0 ? "behind" : "ahead of")} the head; head image delay {delayBefore} -> {headDelay} frame(s)" +
                      $"{(first.HoldPending > 0 ? $", client shows {first.HoldPending} pose(s) twice" : "")}.");
            BeginQuiet();
        }

        static bool StableMode(Peer p, out int mode)
        {
            mode = 0;
            int best = 0;
            for(int i = 0; i < p.OffsetCount; i++)
            {
                int count = 0;
                for(int j = 0; j < p.OffsetCount; j++)
                {
                    if(p.Offsets[j] == p.Offsets[i]) count++;
                }
                if(count > best)
                {
                    best = count;
                    mode = p.Offsets[i];
                }
            }
            return best >= AlignAgreement * p.OffsetCount;
        }

        /// <summary>Discards the measurements that still describe the pairing before a correction.</summary>
        void BeginQuiet()
        {
            alignQuietUntil = Time.frameCount + AlignQuietFrames;
            foreach(Peer p in peers)
            {
                p.OffsetCount = 0;
                p.OffsetNext = 0;
                p.ReferenceSamples.Clear();
            }
        }

        /// <summary>True once every instance has been in sync for a while after the join, until the head has held.</summary>
        bool HoldDue(SwapBarrier barrier)
        {
            if(!holdToAlign || held || joinAt == 0 || stateId <= joinAt) return false;
            bool synced = InSync(barrier) && peers.Count > 0;
            foreach(Peer p in peers) synced &= (p.LastAckFlags & AckFlagInOrder) != 0;
            syncedFrames = synced ? syncedFrames + 1 : 0;
            return syncedFrames >= HoldSettleFrames;
        }

        /// <summary>
        /// Holds the head once, before it sends the next pose, until every client has applied the previous pose
        /// and every present queue has emptied. The barrier then flips the next pose on all screens at the same
        /// refresh, whatever the pairing was before.
        /// </summary>
        void HoldForClients()
        {
            held = true;
            uint previous = stateId - 1;
            long start = Now();
            long deadline = start + HoldTimeoutMs * clockFrequency / 1000;
            while(!AllAcked(previous))
            {
                if(peers.Count == 0 || Now() >= deadline)
                {
                    Debug.LogWarning($"FrameLock: no acknowledgement of state {previous} within {HoldTimeoutMs} ms, so the head did not hold " +
                                     "and keeps the current pairing. Press ] or [ if the seam breaks during motion.");
                    BeginQuiet();
                    return;
                }
                Thread.Sleep(1);
                PollAcks(Now());
            }

            double frameMs = Math.Max(periodMs > 0.0 ? periodMs : 1000.0 / 60.0, Time.smoothDeltaTime * 1000.0);
            Thread.Sleep((int)Math.Ceiling(HoldDrainFrames * frameMs));
            Debug.Log($"FrameLock: held the head for {(Now() - start) * 1000.0 / clockFrequency:F0} ms before state {stateId}, " +
                      "so every screen shows it at the same refresh.");
            BeginQuiet();
        }

        bool AllAcked(uint id)
        {
            foreach(Peer p in peers)
            {
                if(p.LastAck < id) return false;
            }
            return true;
        }

        /// <summary>Announces the join state once every instance has reported ready for a while.</summary>
        void ScheduleJoin(SwapBarrier barrier)
        {
            if(joinAt != 0 || barrier == null || barrier.JoinRequested) return;

            int expected = NetworkManager.Singleton != null ? NetworkManager.Singleton.ConnectedClientsIds.Count : 0;
            bool ready = barrier.Ready && peers.Count > 0 && peers.Count >= expected;
            foreach(Peer p in peers) ready &= p.Synced && (p.LastAckFlags & AckFlagBarrierReady) != 0;

            allReadyFrames = ready ? allReadyFrames + 1 : 0;
            if(allReadyFrames < JoinSettleFrames) return;

            joinAt = stateId + JoinLeadStates;
            Debug.Log($"FrameLock: all {peers.Count + 1} instances join the present barrier at state {joinAt}.");
        }

        Peer FirstPeer() => peers.Count > 0 ? peers[0] : null;

        void Drop(int index, string reason)
        {
            if(index < 0 || index >= peers.Count) return;
            Debug.LogWarning($"FrameLock: dropping {peers[index].Name}: {reason}.");
            Close(peers[index].Socket);
            peers.RemoveAt(index);
        }

        void ClientUpdate()
        {
            if(server == null)
            {
                Connect();
                return;
            }

            SwapBarrier barrier = ActiveBarrier();
            clientInOrder = clientJoinIssued && InSync(barrier);
            if(!clientInOrder) holdsOwed = 0;
            long start = Now();
            if(!Pump(server, 0))
            {
                EndClient("the head closed the connection");
                return;
            }
            if(!ReadStates()) return;
            int queuedAtStart = pending.Count;

            long deadline = start + (clientInOrder ? InOrderTimeoutMs : waitBudgetMs) * clockFrequency / 1000;
            if(!WaitForPose(deadline)) return;

            double waitMs = (Now() - start) * 1000.0 / clockFrequency;
            RecordFrame(waitMs, queuedAtStart > 0);

            if(pending.Count == 0)
            {
                if(clientInOrder)
                {
                    EndClient($"no pose from the head for {InOrderTimeoutMs} ms");
                    return;
                }
                repeatFrames++;
                Report();
                return;
            }

            if(!clientInOrder || pending.Count > MaxBacklog)
            {
                if(clientInOrder) Debug.LogWarning($"FrameLock: {pending.Count} poses queued; skipping to the newest, the barrier pairing may have shifted.");
                while(pending.Count > 1) CountHolds(pending.Dequeue());
            }

            State s = pending.Peek();
            CountHolds(s);
            if(clientInOrder && holdsOwed > 0)
            {
                holdsOwed--;
                Debug.Log($"FrameLock: showing state {CurrentState} once more (pairing correction).");
                Report();
                return;
            }

            pending.Dequeue();
            Apply(s);

            uint flags = (barrier != null && barrier.DisplayedValid ? AckFlagShownValid : 0) |
                         (barrier != null && barrier.Ready ? AckFlagBarrierReady : 0) |
                         (clientInOrder ? AckFlagInOrder : 0);
            uint shownTag = barrier != null ? barrier.DisplayedTag : 0;
            long shownQpc = barrier != null ? barrier.DisplayedQpc : 0;
            if(!SendAck(s.Id, shownTag, flags, shownQpc))
            {
                EndClient("sending the acknowledgement failed");
                return;
            }

            if(!clientJoinIssued && s.JoinAt != 0 && s.Id >= s.JoinAt && barrier != null)
            {
                barrier.Join();
                clientJoinIssued = true;
            }

            Report();
        }

        /// <summary>Waits until a pose is queued or the deadline passes. False once the client has ended.</summary>
        bool WaitForPose(long deadline)
        {
            while(pending.Count == 0)
            {
                long now = Now();
                if(now >= deadline) break;
                int waitUs = (int)Math.Min(int.MaxValue, (deadline - now) * 1000000L / clockFrequency);
                if(!Pump(server, waitUs))
                {
                    EndClient("the head closed the connection");
                    return false;
                }
                if(!ReadStates()) return false;
            }
            if(!Pump(server, 0))
            {
                EndClient("the head closed the connection");
                return false;
            }
            return ReadStates();
        }

        /// <summary>Adds the hold count a state carries, once per state.</summary>
        void CountHolds(State s)
        {
            if(s.Id <= holdsSeenUpTo) return;
            holdsSeenUpTo = s.Id;
            if(clientInOrder) holdsOwed += (int)s.Hold;
        }

        void Connect()
        {
            if(connectTask == null)
            {
                if(Time.unscaledTime < nextConnect) return;
                tcp = new TcpClient { NoDelay = true };
                connectTask = tcp.ConnectAsync(serverAddress, serverPort);
                return;
            }
            if(!connectTask.IsCompleted) return;

            bool failed = connectTask.IsFaulted || connectTask.IsCanceled || !tcp.Connected;
            _ = connectTask.Exception;
            connectTask = null;
            if(failed)
            {
                tcp.Close();
                tcp = null;
                nextConnect = Time.unscaledTime + ConnectRetrySeconds;
                return;
            }

            server = new Peer { Socket = tcp.Client, Name = $"{serverAddress}:{serverPort}" };
            Debug.Log($"FrameLock: connected to the head at {server.Name}.");
        }

        bool ReadStates()
        {
            while(TakePacket(server, stateBuffer, StateSize))
            {
                if(!ReadState(stateBuffer, out State s))
                {
                    EndClient("protocol error");
                    return false;
                }
                pending.Enqueue(s);
            }
            return true;
        }

        void Apply(State s)
        {
            uc.transform.SetPositionAndRotation(s.RootPos, s.RootRot);
            if(uc.head != null) uc.head.transform.SetPositionAndRotation(s.HeadPos, s.HeadRot);
            RecordPose(s);
            AutoNavigator.LastReplayStep = s.ReplayStep;
            CurrentState = s.Id;
            ClientLocked = true;
        }

        bool SendAck(uint id, uint shownTag, uint flags, long shownQpc)
        {
            PutU(ackBuffer, 0, AckMagic);
            PutU(ackBuffer, 4, id);
            PutU(ackBuffer, 8, shownTag);
            PutU(ackBuffer, 12, flags);
            PutL(ackBuffer, 16, shownQpc);
            return Send(server.Socket, ackBuffer, AckSize);
        }

        /// <summary>Stops the lockstep on this client; it falls back to the Netcode pose RPC.</summary>
        void EndClient(string reason)
        {
            Debug.LogWarning($"FrameLock: {reason}. This client falls back to the Netcode pose and leaves the present barrier.");
            ClientLocked = false;
            ended = true;
            if(server != null) Close(server.Socket);
            server = null;
            SwapBarrier barrier = ActiveBarrier();
            if(barrier != null) barrier.Leave();
        }

        static SwapBarrier ActiveBarrier()
        {
            SwapBarrier b = SwapBarrier.Instance;
            return b != null && b.isActiveAndEnabled ? b : null;
        }

        static void TagFrame(uint id)
        {
            SwapBarrier barrier = ActiveBarrier();
            if(barrier != null) barrier.Tag(id);
        }

        static void RecordPose(State s)
        {
            UCNetwork.LastPoseSeq = s.PoseSeq;
            UCNetwork.LastPoseTime = s.HeadTime;
            UCNetwork.LastHeadPos = s.HeadPos;
            UCNetwork.LastRootPos = s.RootPos;
        }

        /// <summary>System-wide clock, so the logs of processes on one PC can be compared directly.</summary>
        static long Now()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            QueryPerformanceCounter(out long count);
            return count;
#else
            return Stopwatch.GetTimestamp();
#endif
        }

        static long ClockFrequency()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            QueryPerformanceFrequency(out long frequency);
            return frequency;
#else
            return Stopwatch.Frequency;
#endif
        }

        static bool IsLoopback(string address)
        {
            if(string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            return IPAddress.TryParse(address, out IPAddress ip) && IPAddress.IsLoopback(ip);
        }

        /// <summary>Receives what has arrived, waiting at most waitUs for the first byte. False once the peer closed.</summary>
        static bool Pump(Peer p, int waitUs)
        {
            try
            {
                if(!p.Socket.Poll(waitUs, SelectMode.SelectRead)) return true;
                int space = p.Rx.Length - p.RxLen;
                if(space == 0) return true;
                int n = p.Socket.Receive(p.Rx, p.RxLen, space, SocketFlags.None);
                if(n <= 0) return false;
                p.RxLen += n;
                return true;
            }
            catch(SocketException)
            {
                return false;
            }
            catch(ObjectDisposedException)
            {
                return false;
            }
        }

        static bool TakePacket(Peer p, byte[] dst, int size)
        {
            if(p.RxLen < size) return false;
            Buffer.BlockCopy(p.Rx, 0, dst, 0, size);
            Buffer.BlockCopy(p.Rx, size, p.Rx, 0, p.RxLen - size);
            p.RxLen -= size;
            return true;
        }

        static bool Send(Socket socket, byte[] data, int size)
        {
            try
            {
                return socket.Send(data, 0, size, SocketFlags.None) == size;
            }
            catch(SocketException)
            {
                return false;
            }
            catch(ObjectDisposedException)
            {
                return false;
            }
        }

        static void Close(Socket socket)
        {
            try
            {
                socket?.Close();
            }
            catch(SocketException)
            {
            }
        }

        static void WriteState(byte[] b, State s)
        {
            PutU(b, 0, StateMagic);
            PutU(b, 4, s.Id);
            PutU(b, 8, s.PoseSeq);
            PutU(b, 12, s.JoinAt);
            PutU(b, 16, 0);
            PutU(b, 20, (uint)s.ReplayStep);
            PutF(b, 24, s.HeadTime);
            PutV(b, 28, s.RootPos);
            PutQ(b, 40, s.RootRot);
            PutV(b, 56, s.HeadPos);
            PutQ(b, 68, s.HeadRot);
            PutU(b, 84, s.Hold);
        }

        static bool ReadState(byte[] b, out State s)
        {
            s = default;
            if(GetU(b, 0) != StateMagic) return false;
            s.Id = GetU(b, 4);
            s.PoseSeq = GetU(b, 8);
            s.JoinAt = GetU(b, 12);
            s.ReplayStep = (int)GetU(b, 20);
            s.HeadTime = GetF(b, 24);
            s.RootPos = GetV(b, 28);
            s.RootRot = GetQ(b, 40);
            s.HeadPos = GetV(b, 56);
            s.HeadRot = GetQ(b, 68);
            s.Hold = GetU(b, 84);
            return true;
        }

        static void PutU(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        static uint GetU(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);

        static void PutL(byte[] b, int o, long v)
        {
            PutU(b, o, (uint)v);
            PutU(b, o + 4, (uint)(v >> 32));
        }

        static long GetL(byte[] b, int o) => (long)GetU(b, o) | (long)GetU(b, o + 4) << 32;

        static void PutF(byte[] b, int o, float v) => PutU(b, o, (uint)BitConverter.SingleToInt32Bits(v));

        static float GetF(byte[] b, int o) => BitConverter.Int32BitsToSingle((int)GetU(b, o));

        static void PutV(byte[] b, int o, Vector3 v)
        {
            PutF(b, o, v.x);
            PutF(b, o + 4, v.y);
            PutF(b, o + 8, v.z);
        }

        static Vector3 GetV(byte[] b, int o) => new Vector3(GetF(b, o), GetF(b, o + 4), GetF(b, o + 8));

        static void PutQ(byte[] b, int o, Quaternion q)
        {
            PutF(b, o, q.x);
            PutF(b, o + 4, q.y);
            PutF(b, o + 8, q.z);
            PutF(b, o + 12, q.w);
        }

        static Quaternion GetQ(byte[] b, int o) => new Quaternion(GetF(b, o), GetF(b, o + 4), GetF(b, o + 8), GetF(b, o + 12));

        void RecordFrame(double waitMs, bool poseWasReady)
        {
            waitSumMs += waitMs;
            if(waitMs > waitMaxMs) waitMaxMs = waitMs;
            frames++;
            if(poseWasReady) readyFrames++;
        }

        void Report()
        {
            if(Time.unscaledTime < nextReport) return;
            nextReport = Time.unscaledTime + ReportInterval;

            if(isHead)
            {
                int expected = NetworkManager.Singleton != null ? NetworkManager.Singleton.ConnectedClientsIds.Count : 0;
                string join = joinAt == 0 ? "not scheduled" : (stateId >= joinAt ? $"joined at {joinAt}" : $"at {joinAt}");
                Peer p = FirstPeer();
                string pairing = "";
                if(p != null && p.ReportSamples == 0)
                {
                    pairing = ", no pairing measurements yet";
                }
                else if(p != null)
                {
                    p.ReportDeltaMs.Sort();
                    double delta = p.ReportDeltaMs.Count > 0 ? p.ReportDeltaMs[p.ReportDeltaMs.Count / 2] : 0.0;
                    string target = !holdToAlign ? $"target {BeforeAfter(targetMs)}"
                        : p.HasReference ? $"kept from the hold: {BeforeAfter(p.ReferenceMs)}"
                        : held ? "measuring the pairing after the hold" : "before the hold";
                    string share = holdToAlign && !p.HasReference ? ""
                        : $", on target for {100.0 * p.ReportAligned / p.ReportSamples:F0}% of {p.ReportSamples} poses";
                    pairing = $", first client shows each pose {BeforeAfter(delta)} the head ({target}{share})";
                }
                Debug.Log($"FrameLock head: state {stateId}, {peers.Count} frame-lock client(s) of {expected} Netcode client(s), " +
                          $"barrier join {join}{pairing}, head image delay {headDelay} frame(s), automatic corrections {autoCorrections}.");
                foreach(Peer peer in peers)
                {
                    peer.ReportSamples = 0;
                    peer.ReportAligned = 0;
                    peer.ReportDeltaMs.Clear();
                }
            }
            else
            {
                double mean = frames > 0 ? waitSumMs / frames : 0.0;
                double ready = frames > 0 ? 100.0 * readyFrames / frames : 0.0;
                double repeated = frames > 0 ? 100.0 * repeatFrames / frames : 0.0;
                Debug.Log($"FrameLock client: state {CurrentState}, pose wait mean {mean:F2} ms max {waitMaxMs:F2} ms, " +
                          $"pose already there in {ready:F0}% of frames, previous pose reused in {repeated:F0}%, " +
                          $"{(clientInOrder ? "every pose in order" : "newest pose")}, barrier join {(clientJoinIssued ? "requested" : "pending")}.");
            }
            waitSumMs = 0.0;
            waitMaxMs = 0.0;
            frames = 0;
            readyFrames = 0;
            repeatFrames = 0;
        }

        /// <summary>A signed time difference as "X ms before" or "X ms after".</summary>
        static string BeforeAfter(double ms) => $"{Math.Abs(ms).ToString("F2", CultureInfo.InvariantCulture)} ms {(ms < 0.0 ? "before" : "after")}";
    }
}
