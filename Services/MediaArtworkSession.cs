using BiliPlay.Core;
using Windows.Storage.Streams;

namespace BiliPlay.Services;

// Artwork is optional: a slow or broken cover must never delay audio or replace a newer song.
internal sealed class MediaArtworkSession : IDisposable
{
    private long _version;
    private CancellationTokenSource? _pending;
    private bool _disposed;
    public void Reset()
    {
        _version++;
        var pending = _pending; _pending = null; pending?.Cancel();
    }
    public async Task UpdateAsync(Track? track, Func<Track,Task<RandomAccessStreamReference?>> resolve, Action<RandomAccessStreamReference?> publish)
    {
        Reset();
        if (_disposed || track is null) return;
        var version = _version;
        using var load = new CancellationTokenSource(TimeSpan.FromSeconds(14));
        _pending = load;
        try
        {
            var thumbnail = await resolve(track).WaitAsync(load.Token);
            if (!_disposed && version == _version) publish(thumbnail);
        }
        catch (Exception) { /* Thumbnail failures, cancellation and shell errors never stop playback. */ }
        finally { if (ReferenceEquals(_pending,load)) _pending = null; }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Reset(); }
}
