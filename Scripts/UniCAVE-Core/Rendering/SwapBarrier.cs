using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace UniCAVE
{
    /// <summary>
    /// Joins this instance's swap chain to the NVIDIA present barrier, so instances whose GPUs share a
    /// framelocked Sync board flip on the same refresh. Needs a Windows player on D3D12, an RTX/Quadro
    /// class GPU and the command-line argument "swapBarrier 1". Turns VSync on.
    /// With <see cref="FrameLock"/> active, the head chooses the frame of the join for every instance.
    /// </summary>
    public class SwapBarrier : MonoBehaviour
    {
        const string Plugin = "UniCAVESwapSync";

        const int EventJoin = 0x55435301;
        const int EventLeave = 0x55435302;
        const int EventPoll = 0x55435303;
        const int EventCheckAdapter = 0x55435304;
        const int EventTag = 0x55435305;

        const int MinStartFrames = 30;       // window repositioning happens within the first frames
        const int SettleFrames = 30;         // the window size must stay constant this long before joining
        const int AdapterReadDelay = 10;     // frames for the render thread to run the adapter check
        const float ReportInterval = 5f;

        [DllImport(Plugin)] static extern IntPtr UCSS_GetRenderEventFunc();
        [DllImport(Plugin)] static extern IntPtr UCSS_GetRenderEventAndDataFunc();
        [DllImport(Plugin)] static extern uint UCSS_GetDisplayed(out uint tag, out long qpc, out long periodQpc);
        [DllImport(Plugin)] static extern int UCSS_GetStatus();
        [DllImport(Plugin)] static extern int UCSS_GetLastNvStatus();
        [DllImport(Plugin)] static extern void UCSS_GetStats(out int syncMode, out uint presentCount, out uint presentInSyncCount, out uint flipInSyncCount, out uint refreshCount);
        [DllImport(Plugin)] static extern int UCSS_GetFrameCount(out uint frameCount);
        [DllImport(Plugin)] static extern void UCSS_GetAdapterCheck(out int match, out int deviceAdapter, out int displayAdapter);
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceFrequency(out long frequency);

        public static SwapBarrier Instance { get; private set; }

        /// <summary>Set by <see cref="FrameLock"/>; the barrier then joins only when <see cref="Join"/> is called.</summary>
        public static bool JoinControlledExternally { get; set; }

        /// <summary>True once the window has settled and the adapter check has run; joining is safe from here on.</summary>
        public bool Ready { get; private set; }

        public bool JoinRequested { get; private set; }
        public int Status { get; private set; }
        public bool Joined => Status == 1;
        public int SyncMode { get; private set; }
        public uint PresentCount { get; private set; }
        public uint PresentInSyncCount { get; private set; }
        public uint FlipInSyncCount { get; private set; }
        public uint RefreshCount { get; private set; }

        /// <summary>Universal frame counter of the Sync board, when the driver exposes it.</summary>
        public bool FrameCountValid { get; private set; }
        public uint FrameCount { get; private set; }

        /// <summary>Tag of the last tagged frame that reached the screen, and the QPC time of that vsync.</summary>
        public bool DisplayedValid => DisplayedSeq != 0;
        public uint DisplayedSeq { get; private set; }
        public uint DisplayedTag { get; private set; }
        public long DisplayedQpc { get; private set; }
        public long RefreshPeriodQpc { get; private set; }

        IntPtr renderEvent;
        IntPtr renderEventAndData;
        CommandBuffer tagCommands;
        Vector2Int lastSize;
        int stableFrames;
        int adapterCheckFrame = -1;
        bool adapterReported;
        int lastReportedStatus;
        float nextReport;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if(Application.isEditor || Util.GetArg("swapBarrier") != "1") return;

            UCNetwork root = FindFirstObjectByType<UCNetwork>(FindObjectsInactive.Include);
            GameObject host = root != null ? root.gameObject : new GameObject(nameof(SwapBarrier));
            if(host.GetComponent<SwapBarrier>() == null) host.AddComponent<SwapBarrier>();
        }

        void Awake()
        {
            Instance = this;
        }

        void Start()
        {
            if(SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Debug.LogWarning($"SwapBarrier: needs Direct3D 12, the player runs {SystemInfo.graphicsDeviceType}. Disabled.");
                enabled = false;
                return;
            }

            try
            {
                renderEvent = UCSS_GetRenderEventFunc();
                renderEventAndData = UCSS_GetRenderEventAndDataFunc();
                tagCommands = new CommandBuffer { name = "UniCAVE swap sync tag" };
            }
            catch(Exception e) when(e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                Debug.LogWarning($"SwapBarrier: {Plugin}.dll is missing from the build ({e.Message}). Disabled.");
                enabled = false;
                return;
            }

            QualitySettings.vSyncCount = 1;
            Debug.Log("SwapBarrier: VSync on; the present barrier joins once the window has settled" +
                      (JoinControlledExternally ? ", at the frame chosen by the head." : "."));
        }

        void Update()
        {
            Vector2Int size = new Vector2Int(Screen.width, Screen.height);
            if(size != lastSize)
            {
                if(JoinRequested)
                {
                    Debug.LogWarning($"SwapBarrier: the window changed from {lastSize.x}x{lastSize.y} to {size.x}x{size.y} after the join; leaving the present barrier.");
                    Leave();
                }
                lastSize = size;
                stableFrames = 0;
                Ready = false;
                adapterCheckFrame = -1;
                adapterReported = false;
            }
            else
            {
                stableFrames++;
            }

            if(adapterCheckFrame < 0 && Time.frameCount >= MinStartFrames && stableFrames >= SettleFrames)
            {
                GL.IssuePluginEvent(renderEvent, EventCheckAdapter);
                adapterCheckFrame = Time.frameCount;
            }
            else if(!adapterReported && adapterCheckFrame >= 0 && Time.frameCount >= adapterCheckFrame + AdapterReadDelay)
            {
                ReportAdapterCheck();
                adapterReported = true;
                Ready = true;
            }

            if(Ready && !JoinRequested && !JoinControlledExternally) Join();

            GL.IssuePluginEvent(renderEvent, EventPoll);
            ReadState();
            Report();
        }

        /// <summary>Joins the present barrier on the render thread, before this frame's present.</summary>
        public void Join()
        {
            if(!enabled || JoinRequested) return;
            GL.IssuePluginEvent(renderEvent, EventJoin);
            JoinRequested = true;
            Debug.Log($"SwapBarrier: join requested at frame {Time.frameCount}.");
        }

        public void Leave()
        {
            if(!JoinRequested) return;
            GL.IssuePluginEvent(renderEvent, EventLeave);
            JoinRequested = false;
            Debug.Log($"SwapBarrier: leave requested at frame {Time.frameCount}.");
        }

        /// <summary>Tags this frame on the render thread, so the plugin can report when the frame reached the screen.</summary>
        public void Tag(uint stateId)
        {
            if(!enabled || tagCommands == null) return;
            tagCommands.IssuePluginEventAndData(renderEventAndData, EventTag, (IntPtr)(long)stateId);
            Graphics.ExecuteCommandBuffer(tagCommands);
            tagCommands.Clear();
        }

        void OnApplicationQuit()
        {
            if(enabled && JoinRequested) GL.IssuePluginEvent(renderEvent, EventLeave);
        }

        void OnDestroy()
        {
            tagCommands?.Release();
            tagCommands = null;
        }

        void ReadState()
        {
            Status = UCSS_GetStatus();
            UCSS_GetStats(out int syncMode, out uint presents, out uint presentsInSync, out uint flipsInSync, out uint refreshes);
            SyncMode = syncMode;
            PresentCount = presents;
            PresentInSyncCount = presentsInSync;
            FlipInSyncCount = flipsInSync;
            RefreshCount = refreshes;
            FrameCountValid = UCSS_GetFrameCount(out uint frameCount) != 0;
            FrameCount = frameCount;
            DisplayedSeq = UCSS_GetDisplayed(out uint tag, out long qpc, out long period);
            DisplayedTag = tag;
            DisplayedQpc = qpc;
            RefreshPeriodQpc = period;
        }

        void ReportAdapterCheck()
        {
            UCSS_GetAdapterCheck(out int match, out int deviceAdapter, out int displayAdapter);
            string where = $"renders on DXGI adapter {deviceAdapter}, its monitor is on DXGI adapter {displayAdapter}";
            if(match == 1)
            {
                Debug.Log($"SwapBarrier: adapter check OK, {where}.");
            }
            else if(match == 0)
            {
                Debug.LogError($"SwapBarrier: CROSS-ADAPTER, {where}. The present barrier cannot work; " +
                               "set -force-device-index to the GPU that drives this window's monitor.");
            }
            else
            {
                Debug.LogWarning("SwapBarrier: adapter check inconclusive (swap chain or monitor not found).");
            }
        }

        void Report()
        {
            if(Status < 0 && Status != lastReportedStatus)
            {
                Debug.LogError($"SwapBarrier: {StatusName(Status)} (NvAPI status {UCSS_GetLastNvStatus()}).");
            }
            lastReportedStatus = Status;

            if(!JoinRequested || Time.unscaledTime < nextReport) return;
            nextReport = Time.unscaledTime + ReportInterval;
            QueryPerformanceFrequency(out long frequency);
            Debug.Log($"SwapBarrier: {StatusName(Status)}, sync mode {SyncModeName(SyncMode)}, presents {PresentCount}, " +
                      $"in sync {PresentInSyncCount}, flips in sync {FlipInSyncCount}, refreshes {RefreshCount}" +
                      (DisplayedValid && RefreshPeriodQpc > 0 ? $", frame timing at {(double)frequency / RefreshPeriodQpc:F2} Hz." : ", no frame timing yet."));
        }

        public static string StatusName(int status)
        {
            switch(status)
            {
                case 0: return "idle";
                case 1: return "joined";
                case 2: return "left";
                case -1: return "not running on D3D12";
                case -2: return "no swap chain";
                case -3: return "NvAPI initialization failed (no NVIDIA driver?)";
                case -4: return "present barrier not supported (needs an RTX/Quadro class GPU and driver 472.12 or newer)";
                case -5: return "creating the present barrier client failed";
                case -6: return "creating the present barrier fence failed";
                case -7: return "reading the back buffers failed";
                case -8: return "registering the back buffers failed";
                case -9: return "joining the present barrier failed";
                default: return $"status {status}";
            }
        }

        /// <summary>Client means joined but not synced: the window is composited, or no other client has joined.</summary>
        public static string SyncModeName(int mode)
        {
            switch(mode)
            {
                case 0: return "NOT_JOINED";
                case 1: return "SYNC_CLIENT";
                case 2: return "SYNC_SYSTEM";
                case 3: return "SYNC_CLUSTER";
                default: return $"mode {mode}";
            }
        }
    }
}
