using System;
using Xunit;

namespace Stellar.MinimalNameplate.Tests;

/// <summary>
/// Regression pins for the class-icon overlay's decision rules. Named for their origin reports so a future change
/// that "simplifies" one of them has to read why it exists.
/// </summary>
public sealed class ClassIconRulesTests
{
    // ── Attribute storage type ────────────────────────────────────────────────────────────────────────────────────

    // ORIGIN: owner report 2026-09-05 — game stutter plus 30–60 s freezes, with Unity's Player.log taking ~43
    // Debug.LogError stack captures PER SECOND, constant from world entry (232,052 in the previous session; 42,353 of
    // the 42,362 `arr type err` lines in the sampled 18.5 MB log were `type=Int64, enum=AttrProfessionId`).
    // Cause: ZEntity.GetAttr<T> was closed over `long` for the Int32-stored profession attribute. The mismatch does
    // not throw — the game logs and returns 0 — so the overlay's live profession read never once worked, silently.
    // If this assert ever flips back to long, the error storm and the dead live-read both come back.
    [Fact]
    public void AttrProfessionId_is_read_as_Int32_owner_error_storm_2026_09_05()
    {
        Assert.Equal(typeof(int), ClassIconRules.AttrClrType(ClassIconRules.AttrProfessionId));
        Assert.Equal(220, ClassIconRules.AttrProfessionId);
    }

    // The same Player.log carried ZERO `arr type err` lines naming AttrHp or AttrMaxHp, which is the positive
    // evidence that the HP pair IS Int64-stored. Reading them as int would start a fresh error storm.
    [Theory]
    [InlineData(11310)]   // AttrHp
    [InlineData(11320)]   // AttrMaxHp
    public void Hp_attributes_are_read_as_Int64(int attrId)
        => Assert.Equal(typeof(long), ClassIconRules.AttrClrType(attrId));

    [Fact]
    public void Hp_attribute_ids_match_the_game_enum()
    {
        Assert.Equal(11310, ClassIconRules.AttrHpId);
        Assert.Equal(11320, ClassIconRules.AttrMaxHpId);
    }

    [Fact]
    public void Unknown_attributes_default_to_Int64_the_pre_existing_behaviour()
        => Assert.Equal(typeof(long), ClassIconRules.AttrClrType(78));   // AttrDeadType

    // ── Icon source fallback + scan backoff ───────────────────────────────────────────────────────────────────────

    // ORIGIN: owner report 2026-11-09 — in a crowd many badges drew as a coloured square with NO class glyph for the
    // rest of the session. The old rule gave up on a profession after 3 fruitless sprite scans and re-armed only when a
    // never-seen profession entered the AOI, so once every class had been seen a missing icon was permanent. The
    // fallback scan now backs off but NEVER stops: the delay caps at 30 s instead of turning into "never".
    [Theory]
    [InlineData(0, 2.0)]
    [InlineData(1, 2.0)]
    [InlineData(2, 5.0)]
    [InlineData(3, 10.0)]
    [InlineData(4, 30.0)]
    [InlineData(500, 30.0)]
    public void Fallback_scan_backs_off_2_5_10_30_and_never_gives_up(int consecutiveMisses, double expected)
        => Assert.Equal(expected, ClassIconRules.NextScanDelaySeconds(consecutiveMisses));

    // ORIGIN: same report's earlier half (2026-09-05) — a full FindObjectsOfTypeAll<Sprite>() at 1 Hz for a whole
    // session caused stalls. The floor keeps the fallback from ever scanning faster than every 2 s.
    [Fact]
    public void Fallback_scan_delay_never_drops_below_two_seconds()
        => Assert.Equal(2.0, ClassIconRules.NextScanDelaySeconds(-1));

    [Theory]
    [InlineData(0.0)]
    [InlineData(5.0)]
    [InlineData(9.99)]
    public void A_pending_framework_load_is_not_scanned_for_before_the_fallback_window(double pending)
        => Assert.False(ClassIconRules.ShouldFallbackScan(5, frameworkReturnedDead: false, pendingSeconds: pending));

    [Theory]
    [InlineData(10.0)]
    [InlineData(3600.0)]
    public void A_long_pending_framework_load_falls_back_to_the_scan(double pending)
        => Assert.True(ClassIconRules.ShouldFallbackScan(5, frameworkReturnedDead: false, pendingSeconds: pending));

    // The framework never revalidates a Loaded slot, so a destroyed atlas texture would be handed back forever —
    // the scan must take over at once, not after the pending window.
    [Fact]
    public void A_dead_framework_texture_falls_back_immediately()
        => Assert.True(ClassIconRules.ShouldFallbackScan(5, frameworkReturnedDead: true, pendingSeconds: 0));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_profession_is_never_scanned_for(int professionId)
        => Assert.False(ClassIconRules.ShouldFallbackScan(professionId, frameworkReturnedDead: true, pendingSeconds: 999));

    [Fact]
    public void Fallback_window_is_ten_seconds()
        => Assert.Equal(10.0, ClassIconRules.FallbackAfterSeconds);
}
