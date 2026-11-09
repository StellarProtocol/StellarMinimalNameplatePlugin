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
}
