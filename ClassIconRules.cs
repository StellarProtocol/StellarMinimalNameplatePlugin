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

    // ── Sprite-scan backoff ───────────────────────────────────────────────────────────────────────────────────────
    //
    // Class icons are picked up piggyback: whatever sprites the game has ALREADY loaded are scanned for the ones we
    // need. A profession whose atlas is simply not loaded therefore never resolves, and the pre-fix gate ("scan once
    // a second while any tracked profession is uncached") re-ran a full loaded-object scan every single second for
    // the rest of the session. These two rules bound it.

    /// <summary>Scans of a single profession's sprite before the overlay gives up on it.</summary>
    public const int MaxSpriteMisses = 3;

    /// <summary>
    /// Whether to keep looking for <paramref name="professionId"/>'s icon sprite. False for a non-profession, and
    /// false once the sprite has failed to turn up <see cref="MaxSpriteMisses"/> times — until a profession id the
    /// overlay has never tracked enters the AOI, which re-arms every memo (a new atlas may have loaded since).
    /// </summary>
    public static bool ShouldSeekSprite(int professionId, int missCount)
        => professionId > 0 && missCount < MaxSpriteMisses;

    /// <summary>
    /// Seconds to wait before the next sprite scan after <paramref name="consecutiveMisses"/> fruitless ones:
    /// 1, 2, 5, then 10 (capped). Reset to 0 misses by a match or by a newly seen profession.
    /// </summary>
    public static double NextScanDelaySeconds(int consecutiveMisses) => consecutiveMisses switch
    {
        <= 0 => 1.0,
        1 => 2.0,
        2 => 5.0,
        _ => 10.0,
    };
}
