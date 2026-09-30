using System.Diagnostics;

namespace Stellar.MinimalNameplate;

// Diagnostic-only perf instrumentation for the load-in freeze investigation (branch fix/nameplate-loadin-perf).
// This partial is PURELY timing + logging — it changes no runtime behavior. Every line it emits is tagged with the
// literal prefix [MinimalNameplate][perf] so the owner can grep it out of LogOutput.log.
//
// Flip PerfDiag to false to silence all of it from one place (mirrors the existing `Diag` source flag). Timing uses
// Stopwatch timestamp diffs (allocation-free — no Stopwatch object is allocated), so the per-frame OnUpdate path
// allocates nothing extra except the spike-log string, which is built only on a >20 ms frame.
internal sealed partial class ClassIconOverlay
{
    // Source flag: FALSE for release (quiet). Flip to true to re-enable every [MinimalNameplate][perf] load-in timing
    // line in one edit, without touching any call site.
    internal static bool PerfDiag = false;

    // Per-frame handoff from TickThrottles to the OnUpdate spike-timing block: did THIS frame's 2 Hz throttle tick run
    // a player rebuild / a sprite scan? Reset at the top of each OnUpdate frame, read once when a spike is logged — so
    // a SPIKE line says whether the expensive throttle work coincided with the stall (the key signal we're after).
    private bool _perfRebuiltThisFrame;
    private bool _perfScannedThisFrame;

    // Elapsed milliseconds since a Stopwatch.GetTimestamp() reading — no allocation (no Stopwatch instance created).
    private static double PerfMsSince(long startTs)
        => (Stopwatch.GetTimestamp() - startTs) * 1000.0 / Stopwatch.Frequency;

    // #1 ScanSprites — ALWAYS logged (the scan is throttled + backed off, so it's low-volume). `findAll` isolates the
    // Resources.FindObjectsOfTypeAll<Sprite>() cost (the prime suspect) from the rest of the scan.
    private void PerfLogSpriteScan(long startTs, double findAllMs, int sprites, int needed, int matched)
        => _services.Log.Info($"[MinimalNameplate][perf] sprite-scan: findAll={findAllMs:F1}ms total={PerfMsSince(startTs):F1}ms sprites={sprites} needed={needed} matched={matched} cachedProfs={_iconCache.Count}");

    // #2 RebuildPlayers — only spikes over 4 ms (2 Hz path; stay quiet on normal ticks).
    private void PerfLogRebuild(long startTs, int walked)
    {
        double ms = PerfMsSince(startTs);
        if (ms > 4.0)
            _services.Log.Info($"[MinimalNameplate][perf] rebuild: {ms:F1}ms players={_players.Count} scoredWalk={walked}");
    }

    // #3 BakeNameCpu — only bakes over 3 ms (only runs when ShowName is on, so it may never fire; that itself tells us
    // the name path isn't the cause).
    private void PerfLogNameBake(long startTs, int textLen, int aw, int ah)
    {
        double ms = PerfMsSince(startTs);
        if (ms > 3.0)
            _services.Log.Info($"[MinimalNameplate][perf] name-bake: {ms:F1}ms text-len={textLen} atlas={aw}x{ah}");
    }

    // #4 OnUpdate frame body — only spike frames over 20 ms. The rebuilt/scanned flags say whether a 2 Hz rebuild or a
    // sprite scan coincided with the spike (i.e. which hot path most likely stalled the frame).
    private void PerfLogFrame(long startTs, int drawn)
    {
        double ms = PerfMsSince(startTs);
        if (ms > 20.0)
            _services.Log.Info($"[MinimalNameplate][perf] SPIKE frame={ms:F1}ms drawn={drawn} players={_players.Count} rebuiltThisFrame={_perfRebuiltThisFrame} scannedThisFrame={_perfScannedThisFrame}");
    }
}
