using BiliPlay.Core;

namespace BiliPlay.ViewModels;

// Session restore: remember where the last track was left, paint that position immediately and
// preload the decoder/lyrics so resuming is instant instead of starting over at 0:00.
public sealed partial class PlayerViewModel
{
    private double _resumeExpected;
    private long _resumeExpectedAt;

    // Reads the saved position and applies it to the UI without touching the audio engine yet.
    private double RestoreResumePosition(Track track)
    {
        if (Client.Account is not { Id: > 0 } account) return 0;
        var saved = Store.GetProgress(account.Id, track);
        if (saved <= 0.5) return 0;
        var duration = Player.Duration > 0 ? Player.Duration : track.Duration;
        var limit = duration > 1 ? duration - 0.5 : saved;
        _previewPosition = Math.Clamp(saved, 0, Math.Max(0, limit));
        Position = _previewPosition;
        // Keep the restored target visible while the decoder opens: its media position reads 0
        // until the seek lands, and a plain tick would flash the bar and lyrics back to 0:00.
        _resumeExpected = _previewPosition;
        _resumeExpectedAt = Environment.TickCount64;
        PositionText = Text.FormatTime(Position);
        RemainingText = "−" + Text.FormatTime(Math.Max(0, (duration > 0 ? duration : limit) - Position));
        UpdateSubtitleHighlight();
        return _previewPosition;
    }

#if VISUAL_CHECKS
    private async Task PreloadSessionAsync(Track track, double position)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await Player.PrepareAsync(track, position, timeout.Token);
            // PrepareAsync returns once the source is attached; wait for the decoder to open so the
            // seek lands and the slider keeps showing the restored position.
            for (var attempt = 0; attempt < 120 && Player.Duration <= 0; attempt++)
            {
                if (_disposed || !QueueSelection.Same(CurrentTrack, track)) return;
                await Task.Delay(100, timeout.Token);
            }
            if (_disposed || !QueueSelection.Same(CurrentTrack, track) || Player.Duration <= 0) return;
            // The engine owns the pending seek; never write a captured startup position back
            // over a later user seek after an asynchronous prepare.

        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Preloading is an optimisation; normal play still works if it fails. */ }
    }
#endif
}
