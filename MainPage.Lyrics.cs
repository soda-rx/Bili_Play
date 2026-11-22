using System.Numerics;
using System.Runtime.CompilerServices;
using BiliPlay.Core;
using BiliPlay.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace BiliPlay;

public sealed partial class MainPage
{
    private void LyricsViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        LyricsViewport.Clip = new RectangleGeometry { Rect = new(0, 0, args.NewSize.Width, args.NewSize.Height) };
        LyricLeadingSpace.Height = Math.Max(24, args.NewSize.Height * .32);
        LyricTrailingSpace.Height = Math.Max(80, args.NewSize.Height * .64);
        if (ViewModel.IsLyricsOpen) FocusLyric(true);
    }
    private void PauseLyricFollowing()
    {
        _followPausedUntil = DateTimeOffset.UtcNow.AddSeconds(6);
        ResumeLyricsButton.Visibility = ViewModel.HasLyrics ? Visibility.Visible : Visibility.Collapsed;
        _resumeLyricsTimer.Stop(); _resumeLyricsTimer.Start();
    }
    private void ResumeLyricFollowing()
    {
        _resumeLyricsTimer.Stop(); _followPausedUntil = default; ResumeLyricsButton.Visibility = Visibility.Collapsed;
        FocusLyric(true);
    }
    private void ResumeLyricsClick(object sender, RoutedEventArgs args) => ResumeLyricFollowing();
    private void SubtitleSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { SelectedItem: SubtitleChoice choice }) ViewModel.SelectedSubtitleChoice = choice;
    }
    private void LyricsPointerWheelChanged(object sender, PointerRoutedEventArgs args) => PauseLyricFollowing();
    private void LyricsPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) PauseLyricFollowing();
    }
    private ScrollViewer? _lyricsScroller;
    private bool _lyricLayoutPending;
    private bool _hadVisibleLyrics;
    private int _lyricContentVersion;
    private void LyricsListLoaded(object sender, RoutedEventArgs args)
    {
        if (_lyricsScroller is not null) return;
        _lyricsScroller = FindVisual<ScrollViewer>(LyricsList);
        if (_lyricsScroller is not null) _lyricsScroller.ViewChanged += (_, _) => StyleVisibleLyrics(false);
    }
    private void LyricsContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.ItemContainer is ListViewItem item) StyleLyric(item, args.ItemIndex, false, initialize: true);
        if (args.Phase == 0) args.RegisterUpdateCallback((_, phase) =>
        { if (!phase.InRecycleQueue && phase.ItemContainer is ListViewItem realized) StyleLyric(realized, phase.ItemIndex, false); });
    }
    private void LyricTextSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not TextBlock text || VisualTreeHelper.GetParent(text) is not Grid host) return;
        var padding = LyricMotionLayout.Padding(args.NewSize.Height);
        if (Math.Abs(host.Padding.Top - padding) < .5) return;
        host.Padding = new(12, padding, 12, padding);
        var visual = ElementCompositionPreview.GetElementVisual(text);
        visual.CenterPoint = new(0, (float)args.NewSize.Height / 2, 0);
        if (_lyricLayoutPending || !ViewModel.IsLyricsOpen) return;
        _lyricLayoutPending = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        { _lyricLayoutPending = false; if (ViewModel.IsLyricsOpen) FocusLyric(true); });
    }
    private void LyricContentChanged()
    {
        var visible = ViewModel.HasLyrics;
        if (visible == _hadVisibleLyrics) return;
        _hadVisibleLyrics = visible;
        var version = ++_lyricContentVersion;
        if (!visible) { ResetLyricScroll(); return; }
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (version != _lyricContentVersion || !ViewModel.IsLyricsOpen || !MotionEnabled) return;
            var visual = ElementCompositionPreview.GetElementVisual(LyricsList);
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, .35f); fade.InsertKeyFrame(1, 1);
            fade.Duration = TimeSpan.FromMilliseconds(240);
            var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            visual.StartAnimation("Opacity", fade);
            batch.Completed += (_, _) => { if (version == _lyricContentVersion) { visual.StopAnimation("Opacity"); visual.Opacity = 1; } batch.Dispose(); };
            batch.End();
        });
    }
    private sealed class LyricMotionState(CompositionPropertySet properties)
    {
        public CompositionPropertySet Properties = properties;
        public SpringMotion Translation;
        public SpringMotion Scale;
        public double Started;
        public double ScaleStarted;
        public bool Active;
    }
    private readonly ConditionalWeakTable<TextBlock, LyricMotionState> _lyricMotionStates = new();
    private static double MotionTime => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    private int _lastAnimatedLyric = -1;
    private int _lyricFocusVersion;
    private int _lyricScrollVersion;
    private CompositionPropertySet? _lyricScrollProperties;
    private SpringMotion _lyricScrollMotion = new(Vector3.Zero, Vector3.Zero, Vector3.Zero, .82, .5);
    private double _lyricScrollStarted;
    private CompositionPropertySet LyricScrollProperties
    {
        get
        {
            if (_lyricScrollProperties is not null) return _lyricScrollProperties;
            _lyricScrollProperties = ElementCompositionPreview.GetElementVisual(LyricsList).Compositor.CreatePropertySet();
            _lyricScrollProperties.InsertVector3("Offset", Vector3.Zero);
            return _lyricScrollProperties;
        }
    }
    private void ResetLyricScroll()
    {
        ++_lyricScrollVersion;
        _lyricScrollProperties?.StopAnimation("Offset"); _lyricScrollProperties?.InsertVector3("Offset", Vector3.Zero);
        _lyricScrollStarted = MotionTime;
        _lyricScrollMotion = new(Vector3.Zero, Vector3.Zero, Vector3.Zero, .82, .5);
    }
    private void AnimateLyricScroll(float shift)
    {
        if (!MotionEnabled) { ResetLyricScroll(); return; }
        if (Math.Abs(shift) < .5f) return;
        var now = MotionTime;
        var (position, velocity) = _lyricScrollMotion.At(now - _lyricScrollStarted);
        var initial = position + new Vector3(0, shift, 0);
        _lyricScrollStarted = now; _lyricScrollMotion = new(initial, Vector3.Zero, velocity, .82, .5);
        var properties = LyricScrollProperties; var version = ++_lyricScrollVersion;
        var move = properties.Compositor.CreateSpringVector3Animation();
        move.InitialValue = initial; move.InitialVelocity = velocity; move.FinalValue = Vector3.Zero;
        move.DampingRatio = .82f; move.Period = TimeSpan.FromMilliseconds(500);
        var batch = properties.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        properties.StartAnimation("Offset", move);
        batch.Completed += (_, _) => { if (version == _lyricScrollVersion) properties.InsertVector3("Offset", Vector3.Zero); batch.Dispose(); };
        batch.End();
    }
    private void StyleVisibleLyrics(bool animate)
    {
        if (!MotionEnabled) ResetLyricScroll();
        if (LyricsList.ItemsPanelRoot is not ItemsStackPanel panel) return;
        for (var i = Math.Max(0, panel.FirstCacheIndex); i <= panel.LastCacheIndex; i++)
            if (LyricsList.ContainerFromIndex(i) is ListViewItem item) StyleLyric(item, i, animate);
    }
    private void FocusLyric(bool force = false)
    {
        var index = ViewModel.FollowSubtitleIndex;
        if (!ViewModel.IsLyricsOpen) return;
        StyleVisibleLyrics(false);
        if (index < 0 || index >= ViewModel.Lines.Count || (!force && DateTimeOffset.UtcNow < _followPausedUntil)) return;
        var version = ++_lyricFocusVersion;
        if (LyricsList.ContainerFromIndex(index) is null) LyricsList.ScrollIntoView(ViewModel.Lines[index], ScrollIntoViewAlignment.Default);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (version != _lyricFocusVersion || !ViewModel.IsLyricsOpen || index != ViewModel.FollowSubtitleIndex || LyricsList.ContainerFromIndex(index) is not ListViewItem item) return;
            if (FindVisual<ScrollViewer>(LyricsList) is not { } scroll) return;
            var before = item.TransformToVisual(LyricsList).TransformPoint(new(0, 0)).Y;
            var target = Math.Clamp(scroll.VerticalOffset + before - LyricsList.ActualHeight * .36, 0, scroll.ScrollableHeight);
            scroll.ChangeView(null, target, null, true); LyricsList.UpdateLayout();
            var after = item.TransformToVisual(LyricsList).TransformPoint(new(0, 0)).Y;
            var shift = (float)(before - after);
            AnimateLyricScroll(shift);
            var changed = _lastAnimatedLyric != index; _lastAnimatedLyric = index;
            if (LyricsList.ItemsPanelRoot is not ItemsStackPanel panel) return;
            for (var row = Math.Max(0, panel.FirstCacheIndex); row <= panel.LastCacheIndex; row++)
                if (LyricsList.ContainerFromIndex(row) is ListViewItem realized)
                    StyleLyric(realized, row, changed && MotionEnabled, shift, initialize: !MotionEnabled);
        });
    }
    private void StyleLyric(ListViewItem item, int index, bool animate, float displacement = 0, bool initialize = false)
    {
        if (FindVisual<TextBlock>(item) is not { } text) return;
        var active = index == ViewModel.ActiveSubtitleIndex;
        text.FontSize = _lyricFontSize; text.LineHeight = _lyricFontSize * 1.38;
        if (LyricsList.ActualWidth > 0) text.MaxWidth = Math.Max(80, (LyricsList.ActualWidth - 48) / 1.18);
        text.HorizontalAlignment = HorizontalAlignment.Left;
        var opacity = active ? 1f : _accessibility.HighContrast ? .7f : .42f;
        if (VisualTreeHelper.GetParent(text) is UIElement host) host.Opacity = 1;
        text.Opacity = opacity;
        ElementCompositionPreview.SetIsTranslationEnabled(text, true);
        var visual = ElementCompositionPreview.GetElementVisual(text);
        visual.CenterPoint = new(0, (float)text.ActualHeight / 2, 0);
        visual.StopAnimation("Opacity"); visual.Opacity = opacity;
        var destination = new Vector3(active ? 6 : 0, 0, 0);
        var finalScale = new Vector3(!MotionEnabled ? 1 : active ? 1.13f : .95f);
        finalScale.Z = 1;
        var now = MotionTime;
        var firstStyle = !_lyricMotionStates.TryGetValue(text, out var state);
        if (firstStyle)
        {
            var properties = visual.Compositor.CreatePropertySet();
            properties.InsertVector3("Offset", destination); properties.InsertVector3("Size", finalScale);
            state = new(properties); _lyricMotionStates.Add(text, state);
            var position = visual.Compositor.CreateExpressionAnimation("Vector3(row.Offset.X, flow.Offset.Y + Clamp(row.Offset.Y, -8.0, 8.0), 0.0)");
            position.SetReferenceParameter("row", properties); position.SetReferenceParameter("flow", LyricScrollProperties);
            visual.StartAnimation("Translation", position);
            var size = visual.Compositor.CreateExpressionAnimation("Vector3(Clamp(row.Size.X, 0.92, 1.16), Clamp(row.Size.Y, 0.92, 1.16), 1.0)");
            size.SetReferenceParameter("row", properties); visual.StartAnimation("Scale", size);
        }
        var motion = state!;
        var changed = motion.Active != active; motion.Active = active;
        if (initialize || firstStyle || !MotionEnabled)
        {
            motion.Properties.StopAnimation("Size"); motion.Properties.StopAnimation("Offset");
            motion.Properties.InsertVector3("Size", finalScale); motion.Properties.InsertVector3("Offset", destination);
            motion.Started = motion.ScaleStarted = now;
            motion.Translation = new(destination, destination, Vector3.Zero, .7, .48);
            motion.Scale = new(finalScale, finalScale, Vector3.Zero, .62, .44);
            return;
        }
        if (!animate && !changed) return;
        var distance = Math.Min(Math.Abs(index - ViewModel.ActiveSubtitleIndex), 4);
        var (sizeNow, scaleVelocity) = motion.Scale.At(now - motion.ScaleStarted);
        var (offsetNow, velocity) = motion.Translation.At(now - motion.Started);
        var moving = velocity.LengthSquared() > .01f || scaleVelocity.LengthSquared() > .0001f;
        var delay = moving ? 0 : distance * .018;
        // Bounded row ripple + a common scroll spring cannot cause adjacent rows to overtake.
        if (animate) offsetNow.Y = Math.Clamp(offsetNow.Y + Math.Sign(displacement) * (6 - distance), -8, 8);
        motion.Started = now;
        motion.Translation = new(offsetNow, destination, velocity, .7, .48 + distance * .02, delay);
        var move = visual.Compositor.CreateSpringVector3Animation();
        move.InitialValue = offsetNow; move.InitialVelocity = velocity; move.FinalValue = destination;
        move.DampingRatio = .7f; move.Period = TimeSpan.FromSeconds(motion.Translation.PeriodSeconds);
        move.DelayTime = TimeSpan.FromSeconds(delay); move.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        motion.ScaleStarted = now; motion.Scale = new(sizeNow, finalScale, scaleVelocity, .62, .44, delay);
        var scale = visual.Compositor.CreateSpringVector3Animation();
        scale.InitialValue = sizeNow; scale.InitialVelocity = scaleVelocity; scale.FinalValue = finalScale;
        scale.DampingRatio = .62f; scale.Period = TimeSpan.FromMilliseconds(440);
        scale.DelayTime = TimeSpan.FromSeconds(delay); scale.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        motion.Properties.StartAnimation("Offset", move); motion.Properties.StartAnimation("Size", scale);
        var started = motion.Started; var scaleStarted = motion.ScaleStarted;
        batch.Completed += (_, _) =>
        {
            if (motion.Started == started) motion.Properties.InsertVector3("Offset", destination);
            if (motion.ScaleStarted == scaleStarted) motion.Properties.InsertVector3("Size", finalScale);
            batch.Dispose();
        };
        batch.End();
    }
}
