using System.Text.Json;
using System.Text.Json.Serialization;
using BiliPlay.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace BiliPlay;

public sealed partial class MainWindow
{
    private DispatcherTimer? _placementTimer;
    private RectInt32 _restoredBounds;
    internal sealed record Placement(int X, int Y, int Width, int Height, bool Maximized);
    [JsonSerializable(typeof(Placement))]
    internal partial class PlacementJsonContext : JsonSerializerContext;
    private void InitializeWindowPlacement()
    {
        if (App.IsVisualCheck) return;
        RestoreWindowPlacement();
        if (_restoredBounds.Width == 0) _restoredBounds = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        _placementTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
        _placementTimer.Tick += (_, _) => { _placementTimer.Stop(); SaveWindowPlacement(); };
        AppWindow.Changed += (_, _) =>
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Restored)
                    _restoredBounds = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
                if (presenter.State == OverlappedPresenterState.Minimized)
                {
                    ViewModel.SetTrayResidentMode(true);
                    ScheduleMemoryTrim(200);
                }
                else
                {
                    ViewModel.SetTrayResidentMode(false);
                    Interlocked.Increment(ref _trimVersion);
                }
            }
            _placementTimer.Stop(); _placementTimer.Start();
        };
    }
    private void RestoreWindowPlacement()
    {
        try
        {
            var saved = JsonSerializer.Deserialize(ViewModel.Store.GetSetting("windowPlacement", "null"), PlacementJsonContext.Default.Placement);
            if (saved is { X: >= -1000000 and <= 1000000, Y: >= -1000000 and <= 1000000, Width: >= 320 and <= 32768, Height: >= 240 and <= 32768 })
            {
                var rect = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
                var work = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Nearest).WorkArea;
                rect.Width = Math.Min(rect.Width, work.Width); rect.Height = Math.Min(rect.Height, work.Height);
                rect.X = Math.Clamp(rect.X, work.X, work.X + work.Width - rect.Width);
                rect.Y = Math.Clamp(rect.Y, work.Y, work.Y + work.Height - rect.Height);
                AppWindow.MoveAndResize(rect);
                _restoredBounds = rect;
                if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
            }
        }
        catch (JsonException) { }
    }
    private void SaveWindowPlacement()
    {
        if (App.IsVisualCheck || DebugSession.IsActive || _restoredBounds.Width <= 0) return;
        var rect = _restoredBounds;
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        ViewModel.Store.SetSetting("windowPlacement", JsonSerializer.Serialize(new Placement(rect.X, rect.Y, rect.Width, rect.Height, maximized), PlacementJsonContext.Default.Placement));
    }
}
