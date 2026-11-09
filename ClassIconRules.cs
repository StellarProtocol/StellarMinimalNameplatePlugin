using System;

namespace Stellar.MinimalNameplate;

/// <summary>
/// Pure decision rules for the class-icon overlay: which CLR type each game attribute is stored as, and how hard to
/// keep hunting for a class-icon sprite. No Unity, no reflection, no game types — so these are unit-testable
/// (<c>Stellar.MinimalNameplate.Tests</c>) without a running client.
/// </summary>
internal static class ClassIconRules
{
    // ── Attribute storage type ────────────────────────────────────────────────────────────────────────────────────
    //
    // Panda.ZGame.ZEntity.GetAttr<T>(EAttrType, bool) must be closed over the SAME CLR type the game stores the
    // attribute as. A mismatch does NOT throw: the game emits `arr type err, type=<T>, enum=<attr>` through
    // Debug.LogError — which captures a stack trace and appends it to Player.log on the main thread — and hands back
    // a default 0, so the caller silently reads nothing.
    //
    // MEASURED 2026-09-05, owner's Player.log (18.5 MB, one session): 42,362 `arr type err` lines, of which 42,353
    // were `type=Int64, enum=AttrProfessionId` (~43/s, constant from world entry). The overlay was reading profession
    // through the long closure at 2 Hz per AOI player. ZERO lines named AttrHp or AttrMaxHp, which is the positive
    // evidence that those two ARE Int64-stored and must keep using the long closure.
    //
    // The framework's own probe agrees and pre-seeds the same map:
    // framework/src/Stellar.Infrastructure/Game/PandaPlayerStateProbe.Bootstrap.cs (profession → int, hp/maxHp → long).
    public const int AttrProfessionId = 220;   // EAttrType.AttrProfessionId — Int32-stored
    public const int AttrHpId = 11310;         // EAttrType.AttrHp          — Int64-stored
    public const int AttrMaxHpId = 11320;      // EAttrType.AttrMaxHp       — Int64-stored

    /// <summary>
    /// The CLR type <c>GetAttr&lt;T&gt;</c> must be closed over for <paramref name="attrId"/>. Int64 is the default:
    /// it is what every attribute this overlay reads other than the profession id uses, and it is the behaviour the
    /// overlay shipped with for the HP pair.
    /// </summary>
    public static Type AttrClrType(int attrId) => attrId == AttrProfessionId ? typeof(int) : typeof(long);

    // ── Icon source: framework loader first, piggyback sprite scan as a backed-off fallback ───────────────────────
    //
    // 2026-11-09 (fix/class-icon-load): icons now come from IGameAssets.LoadProfessionIcon, which consumes its
    // UniTask ONCE inside the framework and caches the result in a shared slot, so any number of plugins can call it.
    // The old piggyback-only design gave up on a profession after 3 fruitless scans and re-armed only when a NEVER-SEEN
    // profession entered the AOI — once every class had been seen, a missing icon stayed missing for the session (the
    // crowd "coloured square, no glyph" report). The scan survives only as a fallback, with NO permanent give-up.

    /// <summary>Seconds a profession may stay pending on the framework loader before the sprite-scan fallback joins in.</summary>
    public const double FallbackAfterSeconds = 10.0;

    /// <summary>
    /// Whether the piggyback sprite scan should look for this profession's icon: immediately when the framework handed
    /// back a destroyed texture (its Loaded slot is never revalidated, so it would keep returning the dead one), or
    /// once the framework has been pending for <see cref="FallbackAfterSeconds"/>. Never for a non-profession.
    /// </summary>
    public static bool ShouldFallbackScan(int professionId, bool frameworkReturnedDead, double pendingSeconds)
        => professionId > 0 && (frameworkReturnedDead || pendingSeconds >= FallbackAfterSeconds);

    /// <summary>
    /// Seconds to wait before the next fallback scan after <paramref name="consecutiveMisses"/> fruitless ones:
    /// 2, 5, 10, then 30 (capped) — never infinite. The first scan for a newly eligible profession runs at once; the
    /// counter resets on a match or when no tracked profession needs the fallback any more.
    /// </summary>
    public static double NextScanDelaySeconds(int consecutiveMisses) => consecutiveMisses switch
    {
        <= 1 => 2.0,
        2 => 5.0,
        3 => 10.0,
        _ => 30.0,
    };
}
