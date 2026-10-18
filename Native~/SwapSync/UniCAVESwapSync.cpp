// Joins the Unity player's D3D12 swap chain to the NVIDIA present barrier (swap sync across GPUs on a
// framelocked Sync board) and reports barrier statistics, the GPU/monitor pairing, and when each tagged
// frame reached the screen.
#include <atomic>
#include <cstdint>
#include <mutex>
#include <d3d12.h>
#include <dxgi1_4.h>
#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"
#include "nvapi.h"

namespace
{
    enum EventId : int
    {
        kEventJoin = 0x55435301,
        kEventLeave = 0x55435302,
        kEventPoll = 0x55435303,
        kEventCheckAdapter = 0x55435304,
        kEventTag = 0x55435305,
    };

    enum Status : int
    {
        kStatusIdle = 0,
        kStatusJoined = 1,
        kStatusLeft = 2,
        kStatusNoD3D12 = -1,
        kStatusNoSwapChain = -2,
        kStatusNvapiInit = -3,
        kStatusUnsupported = -4,
        kStatusCreateClient = -5,
        kStatusCreateFence = -6,
        kStatusGetBuffer = -7,
        kStatusRegister = -8,
        kStatusJoin = -9,
    };

    const UINT kMaxBuffers = 16;

    IUnityInterfaces* s_Interfaces = nullptr;
    IUnityGraphics* s_Graphics = nullptr;
    IUnityGraphicsD3D12v7* s_D3D12 = nullptr;
    bool s_NvapiReady = false;
    NvPresentBarrierClientHandle s_Client = nullptr;
    IDXGISwapChain* s_ClientSwapChain = nullptr;   // swap chain the client was created for; owned by Unity
    ID3D12Fence* s_Fence = nullptr;                // signaled only by the driver
    bool s_Joined = false;

    std::atomic<int> s_Status{kStatusIdle};
    std::atomic<int> s_LastNvStatus{0};
    std::atomic<int> s_SyncMode{0};
    std::atomic<unsigned> s_PresentCount{0};
    std::atomic<unsigned> s_PresentInSyncCount{0};
    std::atomic<unsigned> s_FlipInSyncCount{0};
    std::atomic<unsigned> s_RefreshCount{0};
    std::atomic<unsigned> s_FrameCount{0};
    std::atomic<int> s_FrameCountValid{0};
    std::atomic<int> s_AdapterMatch{-1};           // 1 same GPU, 0 cross-adapter, -1 unknown
    std::atomic<int> s_DeviceAdapterIndex{-1};     // DXGI enumeration order
    std::atomic<int> s_DisplayAdapterIndex{-1};

    struct PresentTag
    {
        UINT presentId;
        uint32_t tag;
    };

    const UINT kTagRing = 128;
    PresentTag s_PresentTags[kTagRing] = {};       // present id -> tag of the frame it carried
    uint32_t s_PendingTag = 0;                     // tag of the frame whose present comes next
    bool s_HavePendingTag = false;
    UINT s_LastPresentSeen = 0;
    UINT s_PrevSyncRefresh = 0;
    LONGLONG s_PrevSyncQpc = 0;
    LONGLONG s_RefreshPeriodQpc = 0;

    std::mutex s_DisplayedLock;
    uint32_t s_DisplayedTag = 0;                   // tag of the last frame that reached the screen
    LONGLONG s_DisplayedQpc = 0;                   // QPC time of the vsync that showed it
    LONGLONG s_DisplayedPeriodQpc = 0;
    uint32_t s_DisplayedSeq = 0;
}

static void Fail(Status status, NvAPI_Status nv = NVAPI_OK)
{
    s_LastNvStatus = static_cast<int>(nv);
    s_Status = status;
}

/// Leaves the barrier and destroys the client and the fence.
static void ReleaseClient()
{
    if (s_Client)
    {
        if (s_Joined) NvAPI_LeavePresentBarrier(s_Client);
        NvAPI_DestroyPresentBarrierClient(s_Client);
    }
    s_Client = nullptr;
    s_ClientSwapChain = nullptr;
    s_Joined = false;
    if (s_Fence)
    {
        s_Fence->Release();
        s_Fence = nullptr;
    }
}

/// Creates the client for Unity's current swap chain, registers its back buffers and joins.
static void Join()
{
    if (s_Joined) return;
    if (!s_D3D12) { Fail(kStatusNoD3D12); return; }

    ID3D12Device* device = s_D3D12->GetDevice();
    IDXGISwapChain* swapChain = s_D3D12->GetSwapChain();
    if (!device || !swapChain) { Fail(kStatusNoSwapChain); return; }
    if (!s_NvapiReady) { Fail(kStatusNvapiInit); return; }

    bool supported = false;
    NvAPI_Status nv = NvAPI_D3D12_QueryPresentBarrierSupport(device, &supported);
    if (nv != NVAPI_OK || !supported) { Fail(kStatusUnsupported, nv); return; }

    if (s_Client && s_ClientSwapChain != swapChain) ReleaseClient();
    if (!s_Client)
    {
        nv = NvAPI_D3D12_CreatePresentBarrierClient(device, swapChain, &s_Client);
        if (nv != NVAPI_OK) { s_Client = nullptr; Fail(kStatusCreateClient, nv); return; }
        s_ClientSwapChain = swapChain;
    }
    if (!s_Fence && FAILED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&s_Fence))))
    {
        s_Fence = nullptr;
        Fail(kStatusCreateFence);
        return;
    }

    DXGI_SWAP_CHAIN_DESC desc = {};
    swapChain->GetDesc(&desc);
    const UINT count = desc.BufferCount < kMaxBuffers ? desc.BufferCount : kMaxBuffers;
    ID3D12Resource* buffers[kMaxBuffers] = {};
    for (UINT i = 0; i < count; ++i)
    {
        if (FAILED(swapChain->GetBuffer(i, IID_PPV_ARGS(&buffers[i]))))
        {
            for (UINT j = 0; j < i; ++j) buffers[j]->Release();
            Fail(kStatusGetBuffer);
            return;
        }
    }
    nv = NvAPI_D3D12_RegisterPresentBarrierResources(s_Client, s_Fence, buffers, count);
    // Held references would make Unity's ResizeBuffers fail; the swap chain keeps the buffers alive.
    for (UINT i = 0; i < count; ++i) buffers[i]->Release();
    if (nv != NVAPI_OK) { Fail(kStatusRegister, nv); return; }

    NV_JOIN_PRESENT_BARRIER_PARAMS params = {};
    params.dwVersion = NV_JOIN_PRESENT_BARRIER_PARAMS_VER1;
    nv = NvAPI_JoinPresentBarrier(s_Client, &params);
    if (nv != NVAPI_OK) { Fail(kStatusJoin, nv); return; }

    s_Joined = true;
    s_LastNvStatus = NVAPI_OK;
    s_Status = kStatusJoined;
}

static void Leave()
{
    if (!s_Client || !s_Joined) return;
    NvAPI_LeavePresentBarrier(s_Client);
    s_Joined = false;
    s_Status = kStatusLeft;
}

/// Caches the barrier statistics of the last present and the Sync board frame counter.
static void Poll()
{
    if (s_Client)
    {
        NV_PRESENT_BARRIER_FRAME_STATISTICS stats = {};
        stats.dwVersion = NV_PRESENT_BARRIER_FRAME_STATICS_VER1;
        if (NvAPI_QueryPresentBarrierFrameStatistics(s_Client, &stats) == NVAPI_OK)
        {
            s_SyncMode = static_cast<int>(stats.SyncMode);
            s_PresentCount = stats.PresentCount;
            s_PresentInSyncCount = stats.PresentInSyncCount;
            s_FlipInSyncCount = stats.FlipInSyncCount;
            s_RefreshCount = stats.RefreshCount;
        }
    }

    ID3D12Device* device = s_D3D12 ? s_D3D12->GetDevice() : nullptr;
    NvU32 frameCount = 0;
    const bool valid = device && s_NvapiReady && NvAPI_D3D1x_QueryFrameCount(device, &frameCount) == NVAPI_OK;
    s_FrameCount = frameCount;
    s_FrameCountValid = valid ? 1 : 0;
}

/// Checks that the GPU Unity renders on also drives the monitor under the window. A mismatch means
/// cross-adapter presentation, which rules out independent flip and the present barrier.
static void CheckAdapter()
{
    s_AdapterMatch = -1;
    ID3D12Device* device = s_D3D12 ? s_D3D12->GetDevice() : nullptr;
    IDXGISwapChain* swapChain = s_D3D12 ? s_D3D12->GetSwapChain() : nullptr;
    if (!device || !swapChain) return;

    DXGI_SWAP_CHAIN_DESC desc = {};
    if (FAILED(swapChain->GetDesc(&desc)) || !desc.OutputWindow) return;
    const HMONITOR monitor = MonitorFromWindow(desc.OutputWindow, MONITOR_DEFAULTTONEAREST);
    const LUID deviceLuid = device->GetAdapterLuid();

    IDXGIFactory1* factory = nullptr;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return;

    int deviceIndex = -1, displayIndex = -1;
    bool deviceOwnsMonitor = false;
    IDXGIAdapter1* adapter = nullptr;
    for (UINT a = 0; factory->EnumAdapters1(a, &adapter) != DXGI_ERROR_NOT_FOUND; ++a)
    {
        DXGI_ADAPTER_DESC1 adapterDesc = {};
        adapter->GetDesc1(&adapterDesc);
        const bool isDevice = adapterDesc.AdapterLuid.LowPart == deviceLuid.LowPart &&
                              adapterDesc.AdapterLuid.HighPart == deviceLuid.HighPart;
        if (isDevice) deviceIndex = static_cast<int>(a);

        IDXGIOutput* output = nullptr;
        for (UINT o = 0; adapter->EnumOutputs(o, &output) != DXGI_ERROR_NOT_FOUND; ++o)
        {
            DXGI_OUTPUT_DESC outputDesc = {};
            output->GetDesc(&outputDesc);
            if (outputDesc.Monitor == monitor)
            {
                if (displayIndex < 0) displayIndex = static_cast<int>(a);
                if (isDevice) deviceOwnsMonitor = true;
            }
            output->Release();
        }
        adapter->Release();
    }
    factory->Release();

    s_DeviceAdapterIndex = deviceIndex;
    s_DisplayAdapterIndex = displayIndex;
    if (deviceIndex >= 0 && displayIndex >= 0) s_AdapterMatch = deviceOwnsMonitor ? 1 : 0;
}

/// Runs once per frame before its present: maps the previous frame's present id to that frame's tag, then
/// publishes the tag of the last present that reached the screen and the QPC time of that vsync.
static void Tag(uint32_t tag)
{
    IDXGISwapChain* swapChain = s_D3D12 ? s_D3D12->GetSwapChain() : nullptr;
    if (!swapChain) return;

    UINT last = 0;
    if (SUCCEEDED(swapChain->GetLastPresentCount(&last)))
    {
        // Exactly one present since the previous tag, so it carried the pending tag.
        if (s_HavePendingTag && last == s_LastPresentSeen + 1) s_PresentTags[last % kTagRing] = {last, s_PendingTag};
        s_LastPresentSeen = last;
    }
    s_PendingTag = tag;
    s_HavePendingTag = true;

    DXGI_FRAME_STATISTICS stats = {};
    if (FAILED(swapChain->GetFrameStatistics(&stats)) || stats.PresentCount == 0 || stats.SyncQPCTime.QuadPart == 0) return;

    if (s_PrevSyncRefresh != 0 && stats.SyncRefreshCount > s_PrevSyncRefresh)
    {
        const LONGLONG period = (stats.SyncQPCTime.QuadPart - s_PrevSyncQpc) / static_cast<LONGLONG>(stats.SyncRefreshCount - s_PrevSyncRefresh);
        s_RefreshPeriodQpc = s_RefreshPeriodQpc == 0 ? period : (s_RefreshPeriodQpc * 15 + period) / 16;
    }
    s_PrevSyncRefresh = stats.SyncRefreshCount;
    s_PrevSyncQpc = stats.SyncQPCTime.QuadPart;

    const PresentTag& entry = s_PresentTags[stats.PresentCount % kTagRing];
    if (entry.presentId != stats.PresentCount || s_RefreshPeriodQpc == 0) return;

    const LONGLONG shown = stats.SyncQPCTime.QuadPart +
        static_cast<LONGLONG>(static_cast<INT>(stats.PresentRefreshCount - stats.SyncRefreshCount)) * s_RefreshPeriodQpc;
    std::lock_guard<std::mutex> lock(s_DisplayedLock);
    s_DisplayedTag = entry.tag;
    s_DisplayedQpc = shown;
    s_DisplayedPeriodQpc = s_RefreshPeriodQpc;
    s_DisplayedSeq++;
}

static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType type)
{
    if (type == kUnityGfxDeviceEventInitialize)
    {
        s_D3D12 = s_Graphics->GetRenderer() == kUnityGfxRendererD3D12 ? s_Interfaces->Get<IUnityGraphicsD3D12v7>() : nullptr;
        if (s_D3D12)
        {
            // Run on the submission thread, in order with Unity's presents.
            UnityD3D12PluginEventConfig config = {};
            config.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_Allow;
            config.flags = 0;
            config.ensureActiveRenderTextureIsBound = false;
            const int ids[] = {kEventJoin, kEventLeave, kEventPoll, kEventCheckAdapter, kEventTag};
            for (int id : ids) s_D3D12->ConfigureEvent(id, &config);
        }
        if (!s_NvapiReady) s_NvapiReady = NvAPI_Initialize() == NVAPI_OK;
    }
    else if (type == kUnityGfxDeviceEventShutdown)
    {
        ReleaseClient();
        s_D3D12 = nullptr;
    }
}

static void UNITY_INTERFACE_API OnRenderEvent(int eventId)
{
    switch (eventId)
    {
    case kEventJoin: Join(); break;
    case kEventLeave: Leave(); break;
    case kEventPoll: Poll(); break;
    case kEventCheckAdapter: CheckAdapter(); break;
    default: break;
    }
}

static void UNITY_INTERFACE_API OnRenderEventAndData(int eventId, void* data)
{
    if (eventId == kEventTag) Tag(static_cast<uint32_t>(reinterpret_cast<uintptr_t>(data)));
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces)
{
    s_Interfaces = interfaces;
    s_Graphics = interfaces->Get<IUnityGraphics>();
    s_Graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
    // The plugin loads on first use, after the device exists, so the initialize event is replayed here.
    OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    ReleaseClient();
    if (s_Graphics) s_Graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    if (s_NvapiReady)
    {
        NvAPI_Unload();
        s_NvapiReady = false;
    }
}

extern "C" UnityRenderingEvent UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UCSS_GetRenderEventFunc()
{
    return OnRenderEvent;
}

extern "C" UnityRenderingEventAndData UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UCSS_GetRenderEventAndDataFunc()
{
    return OnRenderEventAndData;
}

/// Tag and display time of the last tagged frame that reached the screen; the return value grows with every new sample.
extern "C" uint32_t UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UCSS_GetDisplayed(uint32_t* tag, long long* qpc, long long* periodQpc)
{
    std::lock_guard<std::mutex> lock(s_DisplayedLock);
    *tag = s_DisplayedTag;
    *qpc = s_DisplayedQpc;
    *periodQpc = s_DisplayedPeriodQpc;
    return s_DisplayedSeq;
}

extern "C" int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UCSS_GetStatus()
{
    return s_Status;
}

extern "C" int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UCSS_GetLastNvStatus()
{
    return s_LastNvStatus;
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UCSS_GetStats(int* syncMode, unsigned* presentCount,
    unsigned* presentInSyncCount, unsigned* flipInSyncCount, unsigned* refreshCount)
{
    *syncMode = s_SyncMode;
    *presentCount = s_PresentCount;
    *presentInSyncCount = s_PresentInSyncCount;
    *flipInSyncCount = s_FlipInSyncCount;
    *refreshCount = s_RefreshCount;
}

extern "C" int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UCSS_GetFrameCount(unsigned* frameCount)
{
    *frameCount = s_FrameCount;
    return s_FrameCountValid;
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UCSS_GetAdapterCheck(int* match, int* deviceAdapter, int* displayAdapter)
{
    *match = s_AdapterMatch;
    *deviceAdapter = s_DeviceAdapterIndex;
    *displayAdapter = s_DisplayAdapterIndex;
}
