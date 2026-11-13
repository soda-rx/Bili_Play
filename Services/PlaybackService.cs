using BiliPlay.Core;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace BiliPlay.Services;

public sealed class PlaybackService(BilibiliClient client, DispatcherQueue dispatcher) : IDisposable
{
    private PlaybackEngine? _engine;
    private double _volume = .75;
    private double _playbackRate = 1;
    public Track? Current => _engine?.Current;
    public bool Playing => _engine?.Playing == true;
    public double Position => _engine?.Position ?? 0;
    public double Duration => _engine?.Duration ?? 0;
    public string Quality => _engine?.Quality ?? "";
    public string SelectedQuality => _engine?.SelectedQuality ?? "";
    public bool IsQualitySwitching => _engine?.IsQualitySwitching == true;
    public IReadOnlyList<string> Qualities => _engine?.Qualities ?? [];
    public bool HasHiRes => _engine?.HasHiRes == true;
    public void SelectQuality(string quality) => _engine?.SelectQuality(quality);
    public double Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0, 1); if (_engine is not null) _engine.Volume = _volume; }
    }
    public double PlaybackRate
    {
        get => _playbackRate;
        set { _playbackRate = PlaybackSpeed.Validate(value); if (_engine is not null) _engine.PlaybackRate = value; }
    }
    public event Action? Changed;
    public event Action? Ended;
    public event Action<bool>? SkipRequested;
    public event Action<bool>? PlaybackRequested;
    public event Action<string>? Failed;
    private PlaybackEngine Engine
    {
        get
        {
            if (_engine is not null) return _engine;
            _engine = new(client, dispatcher) { Volume = _volume, PlaybackRate = _playbackRate };
            _engine.Changed += () => Changed?.Invoke();
            _engine.Ended += () => Ended?.Invoke();
            _engine.SkipRequested += next => SkipRequested?.Invoke(next);
            _engine.PlaybackRequested += play => PlaybackRequested?.Invoke(play);
            _engine.Failed += message => Failed?.Invoke(message);
            return _engine;
        }
    }
    public void PrepareSystemSession(Track? track) { if (!App.IsVisualCheck) Engine.PrepareSystemSession(track); }
    public Task PlayAsync(Track track, double startPosition = 0) => Engine.PlayAsync(track, startPosition);
    public void Toggle() => _engine?.Toggle();
    public void Resume() => _engine?.Resume();
    public void Pause() => _engine?.Pause();
    public void Seek(double seconds) => _engine?.Seek(seconds);
    public void UpdateTimeline() => _engine?.UpdateTimeline();
    public void Stop() => _engine?.Stop();
    public void Dispose() => _engine?.Dispose();
}

internal sealed class PlaybackEngine : IDisposable
{
    private readonly BilibiliClient _client;
    private readonly DispatcherQueue _dispatcher;
    private AudioPlayback _audio = new();
    private AudioProxy _proxy => _audio.Proxy;
    private MediaPlayer _player => _audio.Player;
    private MediaPlaybackItem? _item { get => _audio.Item; set => _audio.Item = value; }
    private CancellationTokenSource? _qualityLoad;
    private string? _pendingQuality;
    public string SelectedQuality => _pendingQuality ?? Quality;
    public bool IsQualitySwitching => _qualityLoad is not null;
    private CancellationTokenSource? _load;
    private AudioStream? _stream, _availableStream;
    private bool _resumeAfterOpen = true;
    private bool _pauseRequested;
    public bool HasHiRes => _availableStream?.HasHiRes == true || _availableStream?.Quality.StartsWith("Hi-Res", StringComparison.Ordinal) == true;
    public IReadOnlyList<string> Qualities => _availableStream is { Fallback: not null } audio
        ? [audio.Fallback.Quality, audio.Quality] : Current is null ? [] : [Quality];
    public void SelectQuality(string quality)
    {
        if (quality == _pendingQuality) return;
        CancelQualitySwitch(notify: false);
        if (_availableStream is null || quality == Quality)
        { Changed?.Invoke(); return; }
        if (quality.StartsWith("Hi-Res", StringComparison.Ordinal) && _client.Account?.IsVip != true)
        { Changed?.Invoke(); return; }
        var selected = quality == _availableStream.Quality ? _availableStream
            : quality == _availableStream.Fallback?.Quality ? _availableStream.Fallback : null;
        if (selected is null) { Changed?.Invoke(); return; }
        var load = _qualityLoad = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _pendingQuality = selected.Quality;
        // Acknowledge the user's choice before the first asynchronous decoder/network operation.
        Changed?.Invoke();
        _ = SwitchQualityAsync(selected, load);
    }
    private void CancelQualitySwitch(bool notify = true)
    {
        var pending = _qualityLoad; _qualityLoad = null; _pendingQuality = null;
        pending?.Cancel();
        if (notify && pending is not null) Changed?.Invoke();
    }
    private async Task SwitchQualityAsync(AudioStream selected, CancellationTokenSource load)
    {
        AudioPlayback? prepared = null; var live = _audio;
        var token = load.Token;
        try
        {
            prepared = new() { PlaybackRate = PlaybackRate };
            await prepared.WarmMutedAsync(selected, load.Token);
            await prepared.AlignToAsync(live, load.Token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_qualityLoad, load) || !ReferenceEquals(live, _audio)) return;
            // Both decoders are ready before the audible deck is changed.
            var playing = prepared.ActivateInsteadOf(live);
            _audio = prepared; prepared = null; _stream = selected; _fallbackPosition = 0; _resumeAfterOpen = playing;
            WirePlayer(_player);
            live.Dispose(); PublishPlaybackState();
        }
        catch (OperationCanceledException)
        { if (ReferenceEquals(_qualityLoad, load)) Failed?.Invoke("音质切换超时，继续播放原音质。"); }
        catch (Exception error)
        { if (ReferenceEquals(_qualityLoad, load)) Failed?.Invoke("音质切换失败，继续播放原音质：" + error.Message); }
        finally
        {
            prepared?.Dispose();
            if (ReferenceEquals(_qualityLoad, load))
            {
                _qualityLoad = null; _pendingQuality = null;
                // Commit the actual quality on success, or roll back the requested label on failure.
                Changed?.Invoke();
            }
            load.Dispose();
        }
    }
    private double _fallbackPosition;
    private bool _pageRetried;
    private bool _disposed;
    public string Quality => _stream?.Quality ?? (Current?.LocalFile.Length > 0 ? "MP3" : "");
    public Track? Current { get; private set; }
    public bool Playing => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public double Position => _player.PlaybackSession.Position.TotalSeconds;
    public double Duration => _player.PlaybackSession.NaturalDuration.TotalSeconds;
    public double Volume { get => _player.Volume; set => _player.Volume = Math.Clamp(value, 0, 1); }
    public double PlaybackRate
    {
        get => _audio.PlaybackRate;
        set { _audio.PlaybackRate = value; _controls.PlaybackRate = value; Changed?.Invoke(); }
    }
    public event Action? Changed;
    public event Action? Ended;
    public event Action<bool>? SkipRequested;
    public event Action<bool>? PlaybackRequested;
    public event Action<string>? Failed;
    private readonly SystemMediaTransportControls _controls;
    private readonly MediaArtworkSession _mediaArtwork = new();

    public PlaybackEngine(BilibiliClient client, DispatcherQueue dispatcher)
    {
        _client = client; _dispatcher = dispatcher;
        _player.CommandManager.IsEnabled = false;
        _player.SystemMediaTransportControls.IsEnabled = false;
        _controls = SystemMediaTransportControlsInterop.GetForWindow(App.WindowHandle);
        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true; _controls.IsPauseEnabled = true;
        _controls.IsNextEnabled = true; _controls.IsPreviousEnabled = true;
        _controls.ButtonPressed += (_, e) => _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;
            switch (e.Button)
            {
                case SystemMediaTransportControlsButton.Play: PlaybackRequested?.Invoke(true); break;
                case SystemMediaTransportControlsButton.Pause: PlaybackRequested?.Invoke(false); break;
                case SystemMediaTransportControlsButton.Next: SkipRequested?.Invoke(true); break;
                case SystemMediaTransportControlsButton.Previous: SkipRequested?.Invoke(false); break;
            }
        });
        _controls.PlaybackPositionChangeRequested += (_, e) => _dispatcher.TryEnqueue(() => { if (!_disposed) Seek(e.RequestedPlaybackPosition.TotalSeconds); });
        WirePlayer(_player);
    }
    private void PublishPlaybackState()
    {
        if (_disposed) return;
        _controls.PlaybackStatus = _player.PlaybackSession.PlaybackState switch
        {
            MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
            MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
            MediaPlaybackState.Opening or MediaPlaybackState.Buffering => MediaPlaybackStatus.Changing,
            _ => Current is null ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Stopped
        };
        Changed?.Invoke();
    }
    private void WirePlayer(MediaPlayer player)
    {
        player.PlaybackSession.PlaybackStateChanged += (_, _) => _dispatcher.TryEnqueue(() =>
        { if (!_disposed && ReferenceEquals(player, _player)) PublishPlaybackState(); });
        player.MediaOpened += (_, _) => _dispatcher.TryEnqueue(() =>
        { if (_disposed || !ReferenceEquals(player, _player)) return; if (_fallbackPosition > 0) { Seek(_fallbackPosition); _fallbackPosition = 0; } if (!_resumeAfterOpen) _player.Pause(); Changed?.Invoke(); });
        player.MediaEnded += (_, _) => _dispatcher.TryEnqueue(() => { if (!_disposed && ReferenceEquals(player, _player)) Ended?.Invoke(); });
        player.MediaFailed += (_, _) => _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || !ReferenceEquals(player, _player)) return;
            if (_stream?.Fallback is { } fallback && Current is not null)
            {
                _fallbackPosition = Position; _resumeAfterOpen = Playing;
                SetSource(fallback);
                Changed?.Invoke();
                return;
            }
            _ = RetryPageAsync();
        });
    }
    public void PrepareSystemSession(Track? track)
    {
        if (Current is not null) return;
        _mediaArtwork.Reset();
        _controls.IsEnabled = true; _controls.IsPlayEnabled = track is not null;
        _controls.IsPauseEnabled = track is not null; _controls.IsNextEnabled = track is not null; _controls.IsPreviousEnabled = track is not null;
        var updater = _controls.DisplayUpdater; updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = track?.Title ?? "Bili Play";
        updater.MusicProperties.Artist = track?.Uploader ?? "";
        updater.Thumbnail = null;
        updater.Update(); _controls.PlaybackStatus = MediaPlaybackStatus.Paused;
        _ = ApplyThumbnailAsync(track);
    }
    public async Task PlayAsync(Track track, double startPosition = 0)
    {
        _mediaArtwork.Reset();
        CancelQualitySwitch();
        _load?.Cancel(); _load?.Dispose();
        var load = _load = new();
        var token = load.Token; _pauseRequested = false;
        var stream = string.IsNullOrEmpty(track.LocalFile) ? await _client.GetAudioAsync(track, token) : null;
        token.ThrowIfCancellationRequested();
        _pageRetried = false;
        _fallbackPosition = Math.Max(0, startPosition); _resumeAfterOpen = !_pauseRequested; _availableStream = stream;
        Current = track;
        _controls.IsPlayEnabled = _controls.IsPauseEnabled = _controls.IsNextEnabled = _controls.IsPreviousEnabled = true;
        if (stream is not null) SetSource(stream);
        else
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(track.LocalFile);
            token.ThrowIfCancellationRequested();
            _proxy.Clear(); _player.Pause(); _player.Source = null; _item?.Source.Dispose(); _stream = null;
            _item = new(MediaSource.CreateFromStorageFile(file)); _player.Source = _item;
        }
        var updater = _controls.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = track.Title;
        updater.MusicProperties.Artist = track.Uploader;
        updater.Thumbnail = null;
        updater.Update();
        if (_pauseRequested) _player.Pause(); else _player.Play();
        Changed?.Invoke();
        _ = ApplyThumbnailAsync(track);
    }
    private Task ApplyThumbnailAsync(Track? track) => _mediaArtwork.UpdateAsync(track, value => ResolveThumbnailAsync(value), thumbnail =>
    {
        if (_disposed) return;
        _controls.DisplayUpdater.Thumbnail = thumbnail; _controls.DisplayUpdater.Update();
    });
    internal static async Task<RandomAccessStreamReference?> ResolveThumbnailAsync(Track track, Func<Uri,Task<Uri>>? resolve = null)
    {
        // The media flyout fetches remote URIs in a shell context and fails silently, so hand it
        // the artwork from the local disk cache and keep the SMTC variant on the plain jpg codec.
        if (!Uri.TryCreate(Text.CoverImage(track.Cover, 256, webp: false), UriKind.Absolute, out var cover)) return null;
        // Remote metadata must never direct the shell to arbitrary local files or UNC shares.
        if (cover.IsFile && (cover.IsUnc || !App.IsVisualCheck && !DebugSession.IsActive)) return null;
        if (!cover.IsFile && !Text.IsMediaHost(cover)) return null;
        try
        {
            var resolved = await (resolve is null ? ArtworkCache.ResolveUriAsync(cover) : resolve(cover));
            if (!resolved.IsFile) return null;
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(resolved.LocalPath);
            return RandomAccessStreamReference.CreateFromFile(file);
        }
        catch (Exception) { return null; }
    }
    private async Task RetryPageAsync()
    {
        if (Current is not { LocalFile.Length: 0 } track || _pageRetried || _load is null)
        { Failed?.Invoke("音频播放失败，网页也未提供其他可用音源。"); return; }
        _pageRetried = true; var load = _load;
        try
        {
            var stream = await _client.GetPageAudioAsync(track, load.Token);
            if (load.IsCancellationRequested || !ReferenceEquals(load, _load) || Current != track) return;
            _fallbackPosition = Position; _resumeAfterOpen = !_pauseRequested; _availableStream = stream;
            SetSource(stream); Changed?.Invoke();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception error) { Failed?.Invoke("音频播放失败，网页音源回退也未成功：" + error.Message); }
    }
    private void SetSource(AudioStream stream)
    {
        _stream = stream; _audio.SetSource(stream); if (_pauseRequested) _player.Pause(); else _player.Play();
    }
    public void Pause() { _pauseRequested = true; _resumeAfterOpen = false; _player.Pause(); }
    public void Toggle() { if (Current is null) return; if (Playing) Pause(); else Resume(); }
    public void Resume() { _pauseRequested = false; _resumeAfterOpen = true; if (Current is not null) _player.Play(); }
    public void Seek(double seconds)
    {
        if (Current is null || !double.IsFinite(seconds)) return;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, Duration)));
    }
    public void UpdateTimeline()
    {
        if (Current is null || Duration <= 0) return;
        _controls.UpdateTimelineProperties(new()
        {
            StartTime = TimeSpan.Zero, MinSeekTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromSeconds(Duration), MaxSeekTime = TimeSpan.FromSeconds(Duration),
            Position = TimeSpan.FromSeconds(Math.Clamp(Position, 0, Duration))
        });
    }
    public void Stop()
    {
        _mediaArtwork.Reset();
        CancelQualitySwitch();
        _load?.Cancel(); _proxy.Clear(); _player.Pause(); _player.Source = null;
        _item?.Source.Dispose(); _item = null; _stream = null; _availableStream = null; Current = null;
        PrepareSystemSession(null); Changed?.Invoke();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop(); _mediaArtwork.Dispose(); _controls.IsEnabled = false; _load?.Dispose(); _audio.Dispose();
    }
}
