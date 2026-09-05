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

    // ── Sprite-scan backoff ───────────────────────────────────────────────────────────────────────────────────────

    // ORIGIN: same report. Resources.FindObjectsOfTypeAll<Sprite>() ran every second for the whole session whenever
    // any tracked profession's icon was not loaded, materialising thousands of Il2CppInterop wrappers per second.
    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(1, 2.0)]
    [InlineData(2, 5.0)]
    [InlineData(3, 10.0)]
    [InlineData(4, 10.0)]
    [InlineData(50, 10.0)]
    public void Scan_delay_backs_off_1_2_5_10_and_caps(int consecutiveMisses, double expected)
        => Assert.Equal(expected, ClassIconRules.NextScanDelaySeconds(consecutiveMisses));

    [Fact]
    public void Scan_delay_never_drops_below_one_second()
        => Assert.Equal(1.0, ClassIconRules.NextScanDelaySeconds(-1));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_profession_is_still_sought_below_the_miss_cap(int misses)
        => Assert.True(ClassIconRules.ShouldSeekSprite(professionId: 5, missCount: misses));

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(99)]
    public void A_profession_is_dropped_at_or_above_the_miss_cap(int misses)
        => Assert.False(ClassIconRules.ShouldSeekSprite(professionId: 5, missCount: misses));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_profession_is_never_sought(int professionId)
        => Assert.False(ClassIconRules.ShouldSeekSprite(professionId, missCount: 0));

    [Fact]
    public void Miss_cap_is_three()
        => Assert.Equal(3, ClassIconRules.MaxSpriteMisses);
}
