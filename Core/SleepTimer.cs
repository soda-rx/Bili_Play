using System.Globalization;

namespace BiliPlay.Core;

// Sleep timer ("定时关闭") shared logic: a monotonic deadline plus formatting helpers.
// The application timer must pause playback when the deadline passes; it never starts playback.
public static class SleepTimer
{
    public const int MaxMinutes = 720;
    public static readonly int[] Presets = [15, 30, 60];

    public static long EndsAt(long nowTicks, int? minutes) =>
        minutes is { } value && value >= 1 && value <= MaxMinutes ? nowTicks + value * 60_000L : 0;

    public static long Remaining(long endsAtTicks, long nowTicks) =>
        endsAtTicks <= 0 ? 0 : Math.Max(0, endsAtTicks - nowTicks);

    public static string Label(long remainingMs)
    {
        var seconds = remainingMs <= 0 ? 0 : remainingMs / 1000;
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    public static int? ParseMinutes(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            && minutes >= 1 && minutes <= MaxMinutes ? minutes : null;
}
