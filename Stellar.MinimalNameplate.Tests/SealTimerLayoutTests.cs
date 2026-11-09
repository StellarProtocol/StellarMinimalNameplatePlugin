using Stellar.MinimalNameplate;
using Xunit;

// Regression pins for the revive-sealed countdown (owner report 2026-11-09: the countdown overlapped the name).
public sealed class SealTimerLayoutTests
{
    [Theory]
    [InlineData(10f, 30f)]
    [InlineData(54f, 54f)]
    [InlineData(54f, 120f)]
    public void Name_line_never_overlaps_the_countdown_line(float timerH, float nameH)
    {
        // Countdown centred at 0; the name is pushed DOWN by NameCentreDrop.
        float timerBottom = -timerH * 0.5f;
        float nameTop = -SealTimerLayout.NameCentreDrop(timerH, nameH) + nameH * 0.5f;
        Assert.True(nameTop <= timerBottom + 0.0001f);
        Assert.True(SealTimerLayout.NameCentreDrop(timerH, nameH) > 0f);   // the name moves down, never up
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(96.5f)]
    [InlineData(173f)]
    public void Countdown_is_centred_on_its_line(float advPx)
    {
        float start = SealTimerLayout.PenStartPx(advPx);
        Assert.Equal(0f, start + advPx * 0.5f, 3);
        Assert.Equal(-(start + advPx), start, 3);                          // equal reach left and right of the centre
    }

    // Countdown setting OFF (user-approved 2026-11-09): no timer line, and the name keeps its unsealed position.
    [Theory]
    [InlineData(0f)]
    [InlineData(96.5f)]
    public void Countdown_off_never_makes_a_timer_line(float advPx)
        => Assert.False(SealTimerLayout.HasTimerLine(false, advPx));

    [Theory]
    [InlineData(true, 0f, false)]      // on, but nothing to draw (no Duration / clock unsynced / glyphs not baked)
    [InlineData(true, 96.5f, true)]
    public void Countdown_on_makes_a_line_only_with_something_to_draw(bool on, float advPx, bool expected)
        => Assert.Equal(expected, SealTimerLayout.HasTimerLine(on, advPx));

    [Theory]
    [InlineData(10f, 30f)]
    [InlineData(54f, 54f)]
    [InlineData(54f, 120f)]
    public void Without_a_timer_line_the_name_sits_exactly_like_an_unsealed_name(float timerH, float nameH)
    {
        // Unsealed: the name is the line under the badge, at drop 0. Timer off must match that exactly — no empty line.
        Assert.Equal(nameH, SealTimerLayout.TopLineHeight(false, timerH, nameH));
        Assert.Equal(0f, SealTimerLayout.NameDropBelowTop(false, timerH, nameH));
    }

    [Theory]
    [InlineData(10f, 30f)]
    [InlineData(54f, 120f)]
    public void With_a_timer_line_the_countdown_takes_the_top_line_and_the_name_drops(float timerH, float nameH)
    {
        Assert.Equal(timerH, SealTimerLayout.TopLineHeight(true, timerH, nameH));
        Assert.Equal(SealTimerLayout.NameCentreDrop(timerH, nameH), SealTimerLayout.NameDropBelowTop(true, timerH, nameH));
    }
}
