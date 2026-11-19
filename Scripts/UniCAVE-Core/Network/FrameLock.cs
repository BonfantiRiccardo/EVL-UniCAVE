using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UniCAVE
{
    /// <summary>
    /// Frame lockstep between the head and its display clients over a dedicated TCP channel. Every frame the
    /// head sends the rig pose and renders only once every client has applied it, so all GPUs render the same
    /// pose together. With <see cref="SwapBarrier"/> active, the head also picks the state at which all
    /// instances join the barrier and checks that every screen shows each pose at the same refresh
    /// (Documentation~/DisplaySync.md). Active only with the command-line argument "frameLock 1"; turns VSync on.
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
        const uint AckFlagInSync = 4;
        const int AckModeShift = 8;            // the client's barrier sync mode, bits 8 and 9

        const int AckTimeoutMs = 100;          // the head renders without a late client after this
        const int ClientSilenceMs = 2000;      // the head drops a client that makes no progress this long
        const int PoseTimeoutMs = 2000;        // a client stops waiting for the head after this
        const int MaxBacklog = 8;              // a client jumps to the newest pose beyond this many
        const int JoinLeadStates = 10;         // the head announces the join state this far ahead
        const int JoinSettleFrames = 10;       // every instance reports ready this long before the join
        const int SyncSettleFrames = 30;       // every barrier is in sync this long before the pairing counts
        const int CheckWindow = 30;            // pairing samples per decision
        const float CheckAgreement = 0.9f;     // share of the window that must agree on the offset
        const int SyncModeCluster = 3;         // barrier mode in which the Sync board locks the refreshes
        const double GridToleranceMs = 1.0;    // a vsync difference further than this from a whole refresh is a measurement error
        const int StepSettleStates = 8;        // poses sent this soon after a step can still show the old pairing
        const int MaxSteps = 6;                // pairing steps allowed within StepWindowSeconds
        const float StepWindowSeconds = 60f;
        const float UnlockedWarningInterval = 30f;
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
            public bool Synced;                // has acknowledged a state; the head waits for it every frame
            public uint LastAck;
            public uint LastAckFlags;
            public long UnackedSince;          // first send the client has not caught up with, 0 when it has
            public bool RepeatPending;         // sent with the next state: show the current pose once more
            public readonly List<Shown> Reported = new List<Shown>(32);   // client screen samples not yet matched
            public long LastReportedQpc;
            public uint LastMatchedTag;
            public readonly List<int> Window = new List<int>(CheckWindow);   // client vsync minus head vsync for the same pose, in refreshes
            public bool Decided;               // Offset is a decision the head has not acted on yet
            public int Offset;                 // refreshes the client shows each pose after the head
            public int LoggedOffset = int.MinValue;
            public int ReportSamples;
            public int ReportSameRefresh;
            public int ReportOffGrid;
            public readonly List<double> ReportDeltaMs = new List<double>(512);
        }

        struct State
        {
            public uint Id;
            public uint PoseSeq;
            public uint JoinAt;
            public uint Repeat;                // the receiving client first shows its current pose once more
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
        readonly List<Socket> waitList = new List<Socket>(4);
        uint stateId;
        uint joinAt;
        int allReadyFrames;
        readonly Shown[] headShown = new Shown[256];   // the head's own screen samples, oldest overwritten
        int headShownCount;
        int headShownNext;
        uint headShownSeq;
        uint headNewestShown;
        double periodMs;
        bool autoAlign = true;
        int syncedFrames;                      // consecutive frames with every barrier in sync
        bool frameLocked;                      // every barrier in sync reports SYNC_CLUSTER
        float nextUnlockedWarning;
        uint measureFrom;                      // only poses sent after this state count for the pairing
        int steps;
        bool stepsStopped;
        readonly Queue<float> stepTimes = new Queue<float>();
        double ackWaitSumMs;
        double ackWaitMaxMs;
        int ackFrames;
        int ackTimeouts;

        string serverAddress;
        int serverPort;
        TcpClient tcp;
        Task connectTask;
        float nextConnect;
        Peer server;
        readonly Queue<State> pending = new Queue<State>();
        bool waiting;                          // client in lockstep: waits for the head's pose every frame
        bool clientJoinIssued;
        uint repeatedState;                    // state whose repeat request the client has carried out

        double waitSumMs;
        double waitMaxMs;
        int frames;
        int readyFrames;                       // client frames whose pose had already arrived
        int skippedPoses;
        int repeatedPoses;
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
            if(isHead) return;

            ClientUpdate();
            if(server != null && !ended) TagFrame(CurrentState);
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
                Debug.Log($"FrameLock: head listening on {bind}:{port}; renders every frame once each client has applied its pose" +
                          $"{(autoAlign ? "" : "; pairing steps off")}. VSync on.");
            }
            else
            {
                serverAddress = address;
                serverPort = port;
                Debug.Log($"FrameLock: client will connect to {address}:{port}; waits for the head's pose every frame. VSync on.");
            }
        }

        /// <summary>Runs after every pose writer: sends this frame's pose and waits until every client has applied it.</summary>
        void HeadLateUpdate()
        {
            AcceptPeers();
            PollAcks(Now());
            SwapBarrier barrier = ActiveBarrier();
            UpdateSync(barrier);
            MeasurePairing(barrier);

            if(PairingStep())
            {
                TagFrame(0);
                Report();
                return;
            }

            stateId++;
            ScheduleJoin(barrier);

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

            long sendTime = Now();
            for(int i = peers.Count - 1; i >= 0; i--)
            {
                Peer p = peers[i];
                PutU(stateBuffer, 84, p.RepeatPending ? 1u : 0u);
                p.RepeatPending = false;
                if(!Send(p.Socket, stateBuffer, StateSize))
                {
                    Drop(i, "send failed");
                    continue;
                }
                if(p.UnackedSince == 0) p.UnackedSince = sendTime;
            }

            CurrentState = s.Id;
            AwaitAcks(s.Id);
            TagFrame(s.Id);

            if(joinAt != 0 && s.Id == joinAt && barrier != null) barrier.Join();

            Report();
        }

        /// <summary>Waits until every client in lockstep has applied the given state, or the timeout passes.</summary>
        void AwaitAcks(uint id)
        {
            long start = Now();
            long deadline = start + AckTimeoutMs * clockFrequency / 1000;
            while(true)
            {
                waitList.Clear();
                foreach(Peer p in peers)
                {
                    if(p.Synced && p.LastAck < id) waitList.Add(p.Socket);
                }
                if(waitList.Count == 0) break;

                long now = Now();
                if(now >= deadline)
                {
                    if(ackTimeouts++ == 0) Debug.LogWarning($"FrameLock: no acknowledgement of state {id} within {AckTimeoutMs} ms; the head renders without waiting.");
                    break;
                }
                int waitUs = (int)Math.Min(int.MaxValue, (deadline - now) * 1000000L / clockFrequency);
                try
                {
                    Socket.Select(waitList, null, null, waitUs);
                }
                catch(SocketException)
                {
                }
                catch(ObjectDisposedException)
                {
                }
                PollAcks(Now());
            }

            double waitMs = (Now() - start) * 1000.0 / clockFrequency;
            ackWaitSumMs += waitMs;
            if(waitMs > ackWaitMaxMs) ackWaitMaxMs = waitMs;
            ackFrames++;
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

        /// <summary>Reads the acknowledgements that have arrived and drops clients that closed or stopped making progress.</summary>
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
                if(p.Synced && p.UnackedSince != 0 && (now - p.UnackedSince) * 1000L / clockFrequency > ClientSilenceMs)
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
                p.UnackedSince = id >= stateId ? 0 : now;
                if(!p.Synced)
                {
                    p.Synced = true;
                    Debug.Log($"FrameLock: {p.Name} in lockstep from state {id}.");
                }

                bool usable = (p.LastAckFlags & AckFlagShownValid) != 0 && (p.LastAckFlags & AckFlagInSync) != 0 && shownTag != 0;
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
        /// Counts the frames for which every barrier has been in sync; the pairing only counts after that. The refreshes
        /// count as locked while every barrier reports SYNC_CLUSTER.
        /// </summary>
        void UpdateSync(SwapBarrier barrier)
        {
            bool synced = InSync(barrier) && peers.Count > 0;
            bool locked = synced && barrier.SyncMode == SyncModeCluster;
            foreach(Peer p in peers)
            {
                synced &= p.Synced && (p.LastAckFlags & AckFlagInSync) != 0;
                locked &= ((p.LastAckFlags >> AckModeShift) & 3) == SyncModeCluster;
            }
            if(!synced)
            {
                if(syncedFrames >= SyncSettleFrames) ResetPairing(stateId);
                syncedFrames = 0;
                frameLocked = false;
                return;
            }
            syncedFrames++;
            if(syncedFrames == SyncSettleFrames || (syncedFrames > SyncSettleFrames && locked != frameLocked)) ResetPairing(stateId);
            frameLocked = locked;
            if(syncedFrames < SyncSettleFrames || locked || Time.unscaledTime < nextUnlockedWarning) return;

            nextUnlockedWarning = Time.unscaledTime + UnlockedWarningInterval;
            Debug.LogWarning($"FrameLock: the present barrier pairs the presents, but the refreshes of the GPUs are not locked (barrier {BarrierModes()}), " +
                             "so no pose can reach every screen at the same moment and the pairing is not corrected. Check NVIDIA Control Panel > " +
                             "Workstation > Synchronize displays.");
        }

        /// <summary>Discards the pairing samples and decisions; only poses sent after the given state count again.</summary>
        void ResetPairing(uint after)
        {
            measureFrom = after;
            foreach(Peer p in peers)
            {
                p.Window.Clear();
                p.Decided = false;
            }
        }

        /// <summary>For every pose that both screens have shown, takes the client's vsync time minus the head's.</summary>
        void MeasurePairing(SwapBarrier barrier)
        {
            if(barrier == null || !barrier.DisplayedValid) return;
            if(barrier.DisplayedSeq != headShownSeq)
            {
                headShownSeq = barrier.DisplayedSeq;
                if(barrier.DisplayedTag != 0)
                {
                    headShown[headShownNext] = new Shown { Qpc = barrier.DisplayedQpc, Tag = barrier.DisplayedTag };
                    headShownNext = (headShownNext + 1) % headShown.Length;
                    if(headShownCount < headShown.Length) headShownCount++;
                    headNewestShown = Math.Max(headNewestShown, barrier.DisplayedTag);
                }
            }

            long period = barrier.RefreshPeriodQpc;
            if(period <= 0) return;
            periodMs = period * 1000.0 / clockFrequency;
            bool counting = syncedFrames >= SyncSettleFrames;
            foreach(Peer p in peers)
            {
                while(p.Reported.Count > 0)
                {
                    Shown r = p.Reported[0];
                    if(r.Tag > headNewestShown) break;
                    p.Reported.RemoveAt(0);
                    if(r.Tag == p.LastMatchedTag) continue;
                    p.LastMatchedTag = r.Tag;
                    if(!counting || r.Tag <= measureFrom || !TryHeadFirstShown(r.Tag, out long headQpc)) continue;
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
        /// Adds one pairing sample unless it lies off the refresh grid. While the refreshes are locked, a full window
        /// becomes a decision in whole refreshes.
        /// </summary>
        void RecordSample(Peer p, double deltaMs)
        {
            int refreshes = (int)Math.Round(deltaMs / periodMs);
            if(Math.Abs(deltaMs - refreshes * periodMs) > GridToleranceMs)
            {
                p.ReportOffGrid++;
                return;
            }
            p.ReportSamples++;
            if(refreshes == 0) p.ReportSameRefresh++;
            if(p.ReportDeltaMs.Count < p.ReportDeltaMs.Capacity) p.ReportDeltaMs.Add(deltaMs);

            p.Window.Add(refreshes);
            if(p.Window.Count < CheckWindow) return;

            p.Window.Sort();
            int offset = p.Window[CheckWindow / 2];
            int agree = 0;
            foreach(int k in p.Window)
            {
                if(k == offset) agree++;
            }
            p.Window.Clear();
            if(!frameLocked || agree < CheckAgreement * CheckWindow) return;

            p.Offset = offset;
            p.Decided = true;
        }

        /// <summary>
        /// Acts once every client has a new decision. Returns true when the head renders this frame without a
        /// pose, which brings clients that show poses later one refresh closer; clients that show poses earlier
        /// show their current pose once more instead.
        /// </summary>
        bool PairingStep()
        {
            if(peers.Count == 0) return false;
            int mostBehind = int.MinValue;
            foreach(Peer p in peers)
            {
                if(!p.Decided) return false;
                mostBehind = Math.Max(mostBehind, p.Offset);
            }

            bool misaligned = false;
            foreach(Peer p in peers)
            {
                p.Decided = false;
                misaligned |= p.Offset != 0;
                if(p.Offset == p.LoggedOffset) continue;
                p.LoggedOffset = p.Offset;
                if(p.Offset == 0)
                {
                    Debug.Log($"FrameLock: {p.Name} shows every pose at the same refresh as the head.");
                }
                else
                {
                    Debug.LogWarning($"FrameLock: {p.Name} shows each pose {Math.Abs(p.Offset)} refresh(es) {(p.Offset > 0 ? "after" : "before")} the head" +
                                     $"{(autoAlign && !stepsStopped ? "." : "; pairing steps are off.")}");
                }
            }
            if(!misaligned || !autoAlign || stepsStopped) return false;

            while(stepTimes.Count > 0 && Time.unscaledTime - stepTimes.Peek() > StepWindowSeconds) stepTimes.Dequeue();
            if(stepTimes.Count >= MaxSteps)
            {
                stepsStopped = true;
                Debug.LogWarning($"FrameLock: {MaxSteps} pairing steps within {StepWindowSeconds:F0} s did not bring the screens together; " +
                                 "pairing steps stopped. Please send both player logs.");
                return false;
            }
            stepTimes.Enqueue(Time.unscaledTime);
            steps++;
            ResetPairing(stateId + StepSettleStates);

            if(mostBehind > 0)
            {
                Debug.Log($"FrameLock: pairing step {steps}: the head renders one frame without a pose, so clients that show poses later catch up by one refresh.");
                return true;
            }
            foreach(Peer p in peers) p.RepeatPending = p.Offset < 0;
            Debug.Log($"FrameLock: pairing step {steps}: clients that show poses earlier show their current pose once more.");
            return false;
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

        void Drop(int index, string reason)
        {
            if(index < 0 || index >= peers.Count) return;
            Debug.LogWarning($"FrameLock: dropping {peers[index].Name}: {reason}.");
            Close(peers[index].Socket);
            peers.RemoveAt(index);
        }

        /// <summary>
        /// Waits for the head's next pose, applies it and acknowledges it at once, so the head can render the same
        /// pose. Shows every pose once and in order.
        /// </summary>
        void ClientUpdate()
        {
            if(server == null)
            {
                Connect();
                return;
            }

            long start = Now();
            if(!Pump(server, 0))
            {
                EndClient("the head closed the connection");
                return;
            }
            if(!ReadStates()) return;
            bool poseWasReady = pending.Count > 0;

            if(waiting)
            {
                if(!WaitForPose(start + PoseTimeoutMs * clockFrequency / 1000)) return;
                if(pending.Count == 0)
                {
                    waiting = false;
                    Debug.LogWarning($"FrameLock: no pose from the head for {PoseTimeoutMs} ms; showing the last pose until poses arrive again.");
                    Report();
                    return;
                }
                RecordFrame((Now() - start) * 1000.0 / clockFrequency, poseWasReady);
            }
            else if(pending.Count == 0)
            {
                Report();
                return;
            }

            if(!waiting || pending.Count > MaxBacklog)
            {
                if(waiting)
                {
                    Debug.LogWarning($"FrameLock: {pending.Count} poses queued; skipping to the newest.");
                    skippedPoses += pending.Count - 1;
                }
                while(pending.Count > 1) pending.Dequeue();
                if(!waiting) Debug.Log($"FrameLock: in lockstep with the head from state {pending.Peek().Id}.");
                waiting = true;
            }

            State s = pending.Peek();
            if(s.Repeat != 0 && repeatedState != s.Id && CurrentState != 0)
            {
                repeatedState = s.Id;
                repeatedPoses++;
                Report();
                return;
            }

            pending.Dequeue();
            Apply(s);
            SwapBarrier barrier = ActiveBarrier();
            if(!SendAck(s.Id, barrier))
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

        bool SendAck(uint id, SwapBarrier barrier)
        {
            uint flags = 0;
            uint shownTag = 0;
            long shownQpc = 0;
            if(barrier != null)
            {
                if(barrier.DisplayedValid) flags |= AckFlagShownValid;
                if(barrier.Ready) flags |= AckFlagBarrierReady;
                if(InSync(barrier)) flags |= AckFlagInSync;
                flags |= (uint)(barrier.SyncMode & 3) << AckModeShift;
                shownTag = barrier.DisplayedTag;
                shownQpc = barrier.DisplayedQpc;
            }
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
            PutU(b, 84, s.Repeat);
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
            s.Repeat = GetU(b, 84);
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

        /// <summary>The barrier sync mode of the head and of every client, as the clients last reported it.</summary>
        string BarrierModes()
        {
            SwapBarrier barrier = ActiveBarrier();
            if(barrier == null) return "off";
            string modes = $"head {SwapBarrier.SyncModeName(barrier.SyncMode)}";
            foreach(Peer p in peers) modes += $", client {SwapBarrier.SyncModeName((int)(p.LastAckFlags >> AckModeShift) & 3)}";
            return modes;
        }

        void Report()
        {
            if(Time.unscaledTime < nextReport) return;
            nextReport = Time.unscaledTime + ReportInterval;

            if(isHead)
            {
                int expected = NetworkManager.Singleton != null ? NetworkManager.Singleton.ConnectedClientsIds.Count : 0;
                int locked = 0;
                foreach(Peer peer in peers)
                {
                    if(peer.Synced) locked++;
                }
                string join = joinAt == 0 ? "not scheduled" : (stateId >= joinAt ? $"joined at {joinAt}" : $"at {joinAt}");
                double mean = ackFrames > 0 ? ackWaitSumMs / ackFrames : 0.0;
                string late = ackTimeouts > 0 ? $", {ackTimeouts} frame(s) rendered without a late client" : "";
                Peer p = peers.Count > 0 ? peers[0] : null;
                string pairing = "no pairing measurements";
                if(p != null && p.ReportSamples > 0)
                {
                    p.ReportDeltaMs.Sort();
                    double delta = p.ReportDeltaMs.Count > 0 ? p.ReportDeltaMs[p.ReportDeltaMs.Count / 2] : 0.0;
                    pairing = $"first client shows each pose {BeforeAfter(delta)} the head, at the same refresh for " +
                              $"{100.0 * p.ReportSameRefresh / p.ReportSamples:F0}% of {p.ReportSamples} poses";
                }
                if(p != null && p.ReportOffGrid > 0) pairing += $", {p.ReportOffGrid} vsync time(s) off the refresh grid ignored";
                Debug.Log($"FrameLock head: state {stateId}, {locked} of {peers.Count} frame-lock client(s) in lockstep, {expected} Netcode client(s), " +
                          $"acknowledgement wait mean {mean:F2} ms max {ackWaitMaxMs:F2} ms{late}, barrier {BarrierModes()}, join {join}, " +
                          $"{pairing}, pairing steps {steps}.");
                foreach(Peer peer in peers)
                {
                    peer.ReportSamples = 0;
                    peer.ReportSameRefresh = 0;
                    peer.ReportOffGrid = 0;
                    peer.ReportDeltaMs.Clear();
                }
                ackWaitSumMs = 0.0;
                ackWaitMaxMs = 0.0;
                ackFrames = 0;
                ackTimeouts = 0;
            }
            else
            {
                double mean = frames > 0 ? waitSumMs / frames : 0.0;
                double ready = frames > 0 ? 100.0 * readyFrames / frames : 0.0;
                Debug.Log($"FrameLock client: state {CurrentState}, {(waiting ? "in lockstep" : "not waiting for the head")}, " +
                          $"pose wait mean {mean:F2} ms max {waitMaxMs:F2} ms, pose already there in {ready:F0}% of frames, " +
                          $"so far {skippedPoses} pose(s) skipped and {repeatedPoses} shown twice, barrier join {(clientJoinIssued ? "requested" : "pending")}.");
            }
            waitSumMs = 0.0;
            waitMaxMs = 0.0;
            frames = 0;
            readyFrames = 0;
        }

        /// <summary>A signed time difference as "X ms before" or "X ms after".</summary>
        static string BeforeAfter(double ms) => $"{Math.Abs(ms).ToString("F2", CultureInfo.InvariantCulture)} ms {(ms < 0.0 ? "before" : "after")}";
    }
}
