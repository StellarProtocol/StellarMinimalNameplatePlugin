namespace Stellar.MinimalNameplate;

// Per-frame caches for reflection reads that are the SAME for every badge in a frame (raid perf, 2026-11-10).
//
// The draw loop in OnUpdate runs these once PER TRACKED PLAYER PER FRAME. Each is a reflection invoke into IL2CPP
// (boxing the result) whose answer cannot differ between two players in one frame:
//   • ZEntityMgr.Instance                  — the singleton (TryGetHeadWorld + RebuildPlayers)
//   • the game's head-info setting         — only depends on self vs everyone else (GameShowsPlateFor)
//   • ZHideSeekUtils.IsHideSeekScene()     — a scene fact (IsHiddenByHideSeek)
// At ~20 raid players × 60 fps that was ~3,500 redundant invokes a second; now it's at most 4 per frame. Reset at the
// top of every OnUpdate, so nothing here is ever more than one frame old (no behaviour change, no added latency).
//
// Profiling note: the 2026-11-10 raid CPU profile listed Stellar.MinimalNameplate.dll at 12.1% INCLUSIVE, but 11.0% of
// that was one frame, `dynamicClass::Invoker_VoidThis` — Il2CppInterop's SHARED invoker for every injected `void M()`
// method, owned by whichever injected type asked for it first (our LateUpdater). It carries the framework's
// StellarTicker.Tick (all plugins' ticks). This plugin's own cost in that profile was ~1.0% (OnUpdate).
internal sealed partial class ClassIconOverlay
{
    private bool    _frameEntMgrRead;
    private object? _frameEntMgr;
    private int     _frameShowSelf = -1, _frameShowOther = -1;   // -1 = not read this frame, else 0/1
    private int     _frameHideSeekScene = -1;

    private void BeginFrameCaches()
    {
        _frameEntMgrRead = false; _frameEntMgr = null;
        _frameShowSelf = _frameShowOther = -1;
        _frameHideSeekScene = -1;
    }

    // ZEntityMgr.Instance, read once per frame. Caller has already passed Resolve().
    private object? FrameEntMgr()
    {
        if (_frameEntMgrRead) return _frameEntMgr;
        _frameEntMgr = _piEntMgrInstance!.GetValue(null);
        _frameEntMgrRead = true;
        return _frameEntMgr;
    }
}
