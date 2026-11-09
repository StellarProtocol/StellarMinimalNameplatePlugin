namespace Stellar.MinimalNameplate;

/// <summary>
/// Pure-BCL layout of the revive-sealed "(42s)" countdown. The countdown sits on its OWN line in the name's usual spot
/// under the badge, and the name is pushed down one line; the badge never moves (owner 2026-11-09, after the old inline
/// " (42s)" overlapped the name — it re-measured the name with live font metrics, and a glyph evicted from the dynamic
/// font atlas measured as zero width). The countdown takes NO name metrics. Unit-tested in
/// <c>Stellar.MinimalNameplate.Tests</c>.
/// <para>
/// With the "Countdown" setting OFF (or no countdown to show: Duration 0, clock unsynced, glyphs not baked) there is NO
/// timer line at all — the name sits exactly where an unsealed name sits (no empty line, no shift).
/// </para>
/// </summary>
internal static class SealTimerLayout
{
    /// <summary>Pen start (baked px, relative to the line centre) that centres a countdown of this pen advance.</summary>
    public static float PenStartPx(float timerAdvPx) => -timerAdvPx * 0.5f;

    /// <summary>How far the name's centre drops below the countdown line's centre: the two lines touch, never overlap.</summary>
    public static float NameCentreDrop(float timerLineH, float nameH) => (timerLineH + nameH) * 0.5f;

    /// <summary>A countdown line exists only when the setting is on AND there is something to draw (advance &gt; 0).</summary>
    public static bool HasTimerLine(bool showCountdown, float timerAdvPx) => showCountdown && timerAdvPx > 0f;

    /// <summary>Height of whatever sits directly under the badge: the countdown line, else the name.</summary>
    public static float TopLineHeight(bool hasTimerLine, float timerLineH, float nameH) => hasTimerLine ? timerLineH : nameH;

    /// <summary>Name centre below the top line's centre: <see cref="NameCentreDrop"/> with a timer line, else 0 (normal spot).</summary>
    public static float NameDropBelowTop(bool hasTimerLine, float timerLineH, float nameH)
        => hasTimerLine ? NameCentreDrop(timerLineH, nameH) : 0f;
}
