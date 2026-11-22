using System.ComponentModel;
using BiliPlay.Core;
using BiliPlay.ViewModels;
using BiliPlay.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Input;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace BiliPlay;

public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is true;
        if (parameter is "Invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class PlaybackTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => Text.FormatTime(System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class UrlImageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https" && !(DebugSession.IsActive && uri.IsFile)) return null!;
        return ArtworkCache.Create(uri, int.TryParse(parameter as string, out var size) ? size : 320);
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
public sealed class AvatarImageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string text || !Uri.TryCreate(text,UriKind.Absolute,out var uri) || !Text.IsMediaHost(uri)) return null!;
        return ArtworkCache.CreateAvatar(uri, int.TryParse(parameter as string,out var size)?size:96);
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class MainPage : Page
{
    public PlayerViewModel ViewModel { get; } = new();
    private bool _initialized;
    private readonly UISettings _uiSettings = new();
#if VISUAL_CHECKS
    private bool _forceReducedMotion;
    private Windows.Foundation.Rect _coverTargetAtStart;
    private bool MotionEnabled => _uiSettings.AnimationsEnabled && !_forceReducedMotion;
#else
    private bool MotionEnabled => _uiSettings.AnimationsEnabled;
#endif
    private readonly AccessibilitySettings _accessibility = new();
    private int _lyricsTransition;
    private Task _lyricsMotionTask = Task.CompletedTask;
    private double _lyricFontSize = 32;
    private readonly DispatcherTimer _resumeLyricsTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private DateTimeOffset _followPausedUntil;
    private bool _sidebarDragging;
    private bool _paneClosing;
    private int _browseTransition;
    private bool _queueRestoreFocus;
    public MainPage()
    {
        InitializeComponent();
        InitializeTrackActivation();
        InitializeBrowseMotion();
        InitializeFooterInput();
        Navigation.SelectedItem = HomeNav;
        ViewModel.ConfirmQueueChange = ConfirmQueueChangeAsync;
        ViewModel.RequestDownloadOptions = ChooseDownloadOptionsAsync;
        ViewModel.ConfirmClearHistory = ClearHistoryAsync;
        ViewModel.AnimateNoticeOut = AnimateNoticeOutAsync;
        Loaded += OnLoaded;
        SizeChanged += (_, _) => { UpdateSidebarGrip(); UpdateFooterActionSpacing(); };
        ViewModel.PropertyChanging += (_, args) => { if (args.PropertyName == nameof(ViewModel.IsLyricsOpen)) PrepareCoverTransfer(!ViewModel.IsLyricsOpen); };
        ViewModel.PropertyChanged += OnViewModelChanged;
        ActualThemeChanged += (_, _) => { _ = RefreshAtmosphereAsync(); };
        _resumeLyricsTimer.Tick += (_, _) => ResumeLyricFollowing();
        Unloaded += (_, _) => { ListSearchFlyout.Hide(); _hotkeyDialog?.Hide(); CloseConversation(); CancelPendingTrackClick(); _resumeLyricsTimer.Stop(); CancelCoverFlight(); _lyricsTransitionWait?.Cancel(); _lyricsTransitionWait=null; _ambientLoad?.Cancel(); };
        ViewModel.BrowseChanged += QueueBrowseEntrance;
        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => ViewModel.IsSeeking = true), true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => CommitSeek()), true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => { if (ViewModel.IsSeeking) CommitSeek(); }), true);
        SeekSlider.KeyUp += (_, e) =>
        {
            if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown)
                ViewModel.Seek(SeekSlider.Value);
        };
        TrackList.KeyDown += async (_, e) =>
        { if (e.Key == VirtualKey.Enter && !ViewModel.IsTrackSelectionMode) { e.Handled = true; CancelPendingTrackClick(); await ViewModel.PlayCommand.ExecuteAsync(ViewModel.SelectedTrack); } };
        KeyDown += async (_, e) =>
        {
            if (e.Key != VirtualKey.Escape) return;
            CancelPendingTrackClick();
            if (ViewModel.IsSidePaneOpen) { e.Handled = true; ViewModel.IsSidePaneOpen = false; }
            else if (ViewModel.IsLyricsOpen) { e.Handled = true; await CloseLyricsAsync(); }
        };
    }
    private void PlaybackIndicatorLoaded(object sender, RoutedEventArgs e) => ((NowPlayingIndicator)sender).Attach(ViewModel);
    private void PageHeadingSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePageHeadingWidth();
    private void UpdatePageHeadingWidth() => PageHeadingText.MaxWidth = Math.Max(0, PageHeadingHost.ActualWidth - (ViewModel.IsLocalView ? 40 : 0));
    private void UpdateFooterActionSpacing()
    {
        if (FooterActions is null) return;
        var compact = ActualWidth < 1240;
        Grid.SetRow(FooterActions, compact ? 2 : 1);
        FooterActions.Spacing = 8;
    }
    private void SpaceIdentitySizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (SpaceNameText is null || SpaceIdentity is null || SpaceLevelBadge is null) return;
        var badgeWidth = SpaceLevelBadge.Visibility == Visibility.Visible ? SpaceLevelBadge.ActualWidth + 8 : 0;
        SpaceNameText.MaxWidth = Math.Max(0, SpaceIdentity.ActualWidth - badgeWidth);
    }
    private void SettingsViewportSizeChanged(object sender,SizeChangedEventArgs args)
    {
        if(SettingsAboutColumn is null||AboutPanel is null) return;
        var sideBySide=args.NewSize.Width-56>=640;
        var width=sideBySide?280:0;
        if(SettingsAboutColumn.Width.Value!=width) SettingsAboutColumn.Width=new GridLength(width);
        SettingsLayout.ColumnSpacing=sideBySide?32:0;
        Grid.SetColumn(AboutPanel,sideBySide?1:0);Grid.SetRow(AboutPanel,sideBySide?0:1);
        AboutPanel.Width=sideBySide?280:double.NaN;
        AboutPanel.MaxWidth=sideBySide?280:640;
        AboutPanel.HorizontalAlignment=sideBySide?HorizontalAlignment.Right:HorizontalAlignment.Stretch;
    }
    private void NotificationViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (NotificationsPanel is not null) NotificationsPanel.Width = Math.Max(0, args.NewSize.Width - 56);
        UpdateNotificationLayout();
    }
    private void PlayerBarSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ViewModel.PlayerBarHeight = args.NewSize.Height;
    }
    private void CloseCommentsClick(object sender, RoutedEventArgs args) => ViewModel.ShowComments = false;
    private async void CommentAuthorClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: long id } && id > 0)
            await ViewModel.OpenSpaceAsync(id);
    }
    private void PlaybackRateOpening(object sender, object args)
    {
        if (sender is MenuFlyout menu)
            foreach (var item in menu.Items.OfType<RadioMenuFlyoutItem>())
                item.IsChecked = double.TryParse(item.Tag as string,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var rate)
                    && Math.Abs(rate-ViewModel.PlaybackRate)<.001;
    }
    private void PlaybackRateClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag:string value } && double.TryParse(value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var rate))
            ViewModel.PlaybackRate = PlaybackSpeed.Validate(rate);
    }
    private void CommitSeek() { ViewModel.Seek(SeekSlider.Value); ViewModel.IsSeeking = false; }
    private void TrackTitleSizeChanged(object sender, SizeChangedEventArgs e)
    { if (PlayerTrackTitle is not null) PlayerTrackTitle.MaxWidth = Math.Max(0, e.NewSize.Width); }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ApplyTheme();
        Navigation.ApplyTemplate();
        if (Navigation.SettingsItem is NavigationViewItem settings)
        {
            settings.Content = "设置"; settings.Tag = "settings";
            settings.Icon = new AnimatedIcon
            {
                Source = new Microsoft.UI.Xaml.Controls.AnimatedVisuals.AnimatedSettingsVisualSource(),
                FallbackIconSource = new SymbolIconSource { Symbol = Symbol.Setting }
            };
        }
        if (_initialized) return;
        _initialized = true;
        await ArtworkCache.InitializeAsync(ViewModel.ImageCompatibilityMode);
        if (!IsLoaded) return;
#if VISUAL_CHECKS
        if (App.IsVisualCheck && !DebugSession.IsActive) { await RunVisualCheckAsync(); return; }
#endif
        if (!DebugSession.IsActive && !await EnsureAgreementAsync()) return;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,()=> { if(IsLoaded) ViewModel.Player.PrepareSystemSession(ViewModel.CurrentTrack); });
        await ViewModel.InitializeAsync();
    }
    private async void CancelNewProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) button.IsEnabled = false;
        await ((MainWindow)App.Window).CancelNewProfileAsync();
    }
    private async Task<bool> EnsureAgreementAsync()
    {
        if (ViewModel.Store.GetSetting("agreementAccepted", "false") == "true") return true;
        var check = new CheckBox { Content = "我已阅读并同意《Bili Play 用户协议与隐私政策》", Margin = new Thickness(0, 12, 0, 0) };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(AgreementBody());
        content.Children.Add(check);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = UserAgreement.Title, Content = content,
            PrimaryButtonText = "同意并继续", CloseButtonText = "退出", DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };
        check.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        check.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) { App.Window.Close(); return false; }
        ViewModel.Store.SetSetting("agreementAccepted", "true");
        return true;
    }
    private ScrollViewer AgreementBody() => new() { MaxHeight = Math.Max(160, Math.Min(520, ActualHeight - 240)), Content = new TextBlock { Text = UserAgreement.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } };
    private bool _agreementOpen;
    private async void ViewAgreementClick(object sender, RoutedEventArgs args)
    {
        if (_agreementOpen) return;
        _agreementOpen = true;
        try { await new ContentDialog { XamlRoot=XamlRoot, RequestedTheme=ActualTheme, Title=UserAgreement.Title, Content=AgreementBody(), CloseButtonText="关闭" }.ShowAsync(); }
        finally { _agreementOpen=false; }
    }
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsTrackSelectionMode)) UpdateTrackSelectionPresentation();
        if (_conversationLifetime is not null && ViewModel.Client.Account?.Id != _conversationAccount) CloseConversation();
        if (e.PropertyName == nameof(ViewModel.NotificationCategoryIndex)
            || e.PropertyName == nameof(ViewModel.IsNotificationsView) && !ViewModel.IsNotificationsView
            || e.PropertyName == nameof(ViewModel.IsLoggedIn) && !ViewModel.IsLoggedIn) CloseConversation();
        if (e.PropertyName == nameof(ViewModel.CanSearchWithinList)) ListSearchFlyout.Hide();
        if (e.PropertyName == nameof(ViewModel.IsSettingsView) && !ViewModel.IsSettingsView) _hotkeyDialog?.Hide();
        if (e.PropertyName == nameof(ViewModel.IsSettingsView) && ViewModel.IsSettingsView) _ = ViewModel.RefreshImageCacheSizeAsync();
        if (e.PropertyName is nameof(ViewModel.SingleClickBehaviorIndex) or nameof(ViewModel.DoubleClickBehaviorIndex) or nameof(ViewModel.IsCacheSelectionMode) or nameof(ViewModel.IsLoggedIn)) CancelPendingTrackClick();
        if (e.PropertyName == nameof(ViewModel.ShowComments) && ViewModel.ShowComments)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { if (ViewModel.ShowComments) CommentsCloseButton.Focus(FocusState.Programmatic); });
        if (e.PropertyName == nameof(ViewModel.IsLocalView)) UpdatePageHeadingWidth();
        if (e.PropertyName == nameof(ViewModel.IsCacheSelectionMode) && ViewModel.IsCacheSelectionMode)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (ViewModel.IsCacheSelectionMode && TrackList.SelectionMode == ListViewSelectionMode.Multiple)
                { TrackList.SelectedItems.Clear(); ViewModel.SelectedCacheCount = 0; }
            });
        if (e.PropertyName is nameof(ViewModel.HomeGridMode) or nameof(ViewModel.FolderGridMode)
            or nameof(ViewModel.SearchGridMode) or nameof(ViewModel.SpaceGridMode) or nameof(ViewModel.FolderTracksGridMode) or nameof(ViewModel.SearchCategoryIndex) or nameof(ViewModel.CacheTabIndex) or nameof(ViewModel.IsNotificationsView)) QueueBrowseEntrance();
        if (e.PropertyName == nameof(ViewModel.VisibleNotifications) && ViewModel.IsNotificationsView) QueueBrowseEntrance();
        if (e.PropertyName == nameof(ViewModel.ActiveSubtitleIndex)) StyleVisibleLyrics(false);
        if (e.PropertyName == nameof(ViewModel.FollowSubtitleIndex)) FocusLyric();
        if (e.PropertyName == nameof(ViewModel.HasLyrics)) LyricContentChanged();
        if (e.PropertyName == nameof(ViewModel.IsLyricsOpen))
        {
            UpdateSidebarGrip();
            _lyricsMotionTask = TransitionLyricsAsync(ViewModel.IsLyricsOpen);
        }
        if (e.PropertyName == nameof(ViewModel.LyricsCover) && ViewModel.IsLyricsOpen)
        {
            if (_coverFlight is null) SettleArtwork();
            ResumeLyricFollowing();
            _ = RefreshAtmosphereAsync();
        }
        if (e.PropertyName == nameof(ViewModel.PlayGlyph)) { UpdateAmbientMotion(); if(ViewModel.IsLyricsOpen && _coverFlight is null) SettleArtwork(); }
    }
    private async void NavigationInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (!ViewModel.IsLoggedIn) return;
        var view = args.IsSettingsInvoked ? "settings" : (args.InvokedItemContainer as NavigationViewItem)?.Tag as string;
        if (view is null) return;
        await CloseLyricsAsync();
        if (ViewModel.IsLyricsOpen) return;
        if (view == "home")
        {
            var alreadyHome = ViewModel.IsHomeView;
            if (alreadyHome) ViewModel.ReplayBrowseEntrance();
            await ViewModel.OpenHomeCommand.ExecuteAsync(null);
        }
        else ViewModel.Navigate(view);
    }
    public async Task GoBackAsync()
    {
        if (ViewModel.IsLyricsOpen) { await CloseLyricsAsync(); return; }
        if (!ViewModel.CanGoBack) return;
        ViewModel.NavigateBack();
        Navigation.SelectedItem = ViewModel.IsSettingsView ? Navigation.SettingsItem
            : Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag as string == (ViewModel.IsFolderView ? "library" : ViewModel.CurrentView));
    }
    private void QueuePaneOpening(SplitView sender, object args) => _queueRestoreFocus = true;
    private void QueuePaneOpened(SplitView sender, object args)
    { if (ViewModel.IsCommentsPane) CommentsCloseButton.Focus(FocusState.Programmatic); else QueueCloseButton.Focus(FocusState.Programmatic); }
    private void QueuePaneClosed(SplitView sender, object args)
    {
        if (sender.IsPaneOpen) return;
        ViewModel.IsSidePaneOpen = false;
        if (!_queueRestoreFocus) return;
        _queueRestoreFocus = false;
        if (ViewModel.IsCommentsPane && CommentsButton.IsEnabled) CommentsButton.Focus(FocusState.Programmatic);
        else QueueButton.Focus(FocusState.Programmatic);
    }
    private bool _queueDialogOpen;
    private async Task<QueueChange> ConfirmQueueChangeAsync(string title)
    {
        if (_queueDialogOpen) return QueueChange.Cancel;
        _queueDialogOpen = true;
        try
        {
            var dialog = Dialog("如何添加到播放列表？", new TextBlock
            {
                Text = (string.IsNullOrWhiteSpace(title) ? "所选内容" : title) + "\n添加到现有播放列表，还是替换当前列表？",
                TextWrapping = TextWrapping.Wrap, MaxWidth = 380
            }, "追加并播放");
            dialog.SecondaryButtonText = "替换并播放";
            return await dialog.ShowAsync() switch
            {
                ContentDialogResult.Primary => QueueChange.Append,
                ContentDialogResult.Secondary => QueueChange.Replace,
                _ => QueueChange.Cancel
            };
        }
        finally { _queueDialogOpen = false; }
    }
    private async void ChooseCacheFolderClick(object sender, RoutedEventArgs e) => await ViewModel.RunAsync(async () =>
    {
        if(DebugSession.IsActive){ViewModel.Notify("离线调试使用临时下载目录。",NoticeLevel.Info);return;}
        var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary };
        picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        if (await picker.PickSingleFolderAsync() is { } folder) ViewModel.CacheDirectory = folder.Path;
    });
    private async void OpenCacheFolderClick(object sender, RoutedEventArgs e) => await ViewModel.RunAsync(async () =>
    {
        System.IO.Directory.CreateDirectory(ViewModel.CacheDirectory);
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(ViewModel.CacheDirectory);
        await Launcher.LaunchFolderAsync(folder);
    });
    private async void ClearImageCacheClick(object sender,RoutedEventArgs e)
    {
        ClearImageCacheButton.IsEnabled=false;
        try {await ArtworkCache.ClearAsync();ViewModel.Notify("图片缓存已清除。",NoticeLevel.Info);}
        catch(Exception error) when(error is System.IO.IOException or UnauthorizedAccessException) {ViewModel.Notify("图片缓存暂时无法清除，请稍后重试。",NoticeLevel.Warn);}
        finally {await ViewModel.RefreshImageCacheSizeAsync();ClearImageCacheButton.IsEnabled=true;}
    }
    private async void CurrentUploaderClick(object sender, RoutedEventArgs e)
    { if (ViewModel.CurrentTrack is { } track) await OpenUploaderAsync(track); }
    private async void TrackUploaderClick(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { Tag: Track track }) await OpenUploaderAsync(track); }
    private Task OpenUploaderAsync(Track track) => ViewModel.RunAsync(async () =>
    {
        var user = track.UploaderId;
        if (user <= 0) user = (await ViewModel.Client.GetVideoAsync(track.Bvid, track.Aid)).Track.UploaderId;
        if (user > 0) await ViewModel.OpenSpaceAsync(user);
    });
    private async void UploaderClicked(object sender, ItemClickEventArgs e)
    { if (e.ClickedItem is Uploader user) await ViewModel.OpenSpaceAsync(user.Id); }
    private void QualityMenuOpening(object sender, object e)
    {
        if (sender is not Flyout flyout) return;
        QualityOptions.Children.Clear();
        var available = ViewModel.Player.Qualities;
        var qualities = available.Count > 0 ? available.ToList() : new List<string> { string.IsNullOrEmpty(ViewModel.CurrentQuality) ? "标准" : ViewModel.CurrentQuality };
        if (ViewModel.Player.HasHiRes && !qualities.Any(q => q.StartsWith("Hi-Res", StringComparison.Ordinal))) qualities.Add("Hi-Res 无损");
        foreach (var quality in qualities)
        {
            var hires = quality.StartsWith("Hi-Res", StringComparison.Ordinal);
            var content = QualityLabel(quality, hires);
            var row = new RadioButton
            {
                GroupName = "PlaybackQuality", Content = content, MinHeight = 40,
                IsChecked = quality == ViewModel.CurrentQuality,
                IsEnabled = !hires || ViewModel.IsVip && available.Contains(quality)
            };
            if (!row.IsEnabled) ToolTipService.SetToolTip(row, !ViewModel.IsVip ? "Hi-Res 需要哔哩哔哩大会员" : "此曲目暂未提供 Hi-Res 音源");
            row.Click += (_, _) => { if (row.IsEnabled) ViewModel.SelectPlaybackQuality(quality); flyout.Hide(); };
            QualityOptions.Children.Add(row);
        }
    }
    private static StackPanel QualityLabel(string label, bool vip)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        if (vip) content.Children.Add(new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 251, 114, 153)),
            Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "大会员", FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
        });
        return content;
    }
    private bool _downloadDialogOpen;
    private async Task<DownloadOptions?> ChooseDownloadOptionsAsync(Track track)
    {
        if (_downloadDialogOpen) return null;
        _downloadDialogOpen = true;
        using var load = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var content = new StackPanel { Spacing = 16, MinWidth = 320 };
        content.Children.Add(new TextBlock { Text = track.Title, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        content.Children.Add(new TextBlock { Text = "音质", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var standard = new RadioButton { Content = QualityLabel("标准 · MP3 192 kbps", false), GroupName = "DownloadQuality", IsChecked = true, IsEnabled = false };
        var hires = new RadioButton { Content = QualityLabel("Hi-Res 音源 · MP3 320 kbps", true), GroupName = "DownloadQuality", IsEnabled = false, Visibility = Visibility.Collapsed };
        if (!ViewModel.IsVip) ToolTipService.SetToolTip(hires, "Hi-Res 需要哔哩哔哩大会员");
        content.Children.Add(standard); content.Children.Add(hires);
        var loading = new ProgressRing { Width = 24, Height = 24, IsActive = true, HorizontalAlignment = HorizontalAlignment.Left };
        var status = new TextBlock { Text = "正在读取可用音质…", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        content.Children.Add(loading); content.Children.Add(status);
        var dialog = Dialog("下载音乐", content, "下载"); dialog.DefaultButton = ContentDialogButton.Primary; dialog.IsPrimaryButtonEnabled = false;
        dialog.Closing += (_, _) => load.Cancel();
        AudioStream? audio = null; Track resolved = track;
        async Task ReadQualitiesAsync()
        {
            try
            {
                if (!App.IsVisualCheck && resolved.Cid <= 0)
                {
                    var video = await ViewModel.Client.GetVideoAsync(track.Bvid, track.Aid, load.Token);
                    resolved = video.Parts.FirstOrDefault(part => part.Page == track.Page)
                        ?? throw new InvalidDataException("未找到这个分 P。");
                }
                audio = App.IsVisualCheck
                    ? new AudioStream(new Uri("https://audio.bilivideo.com/offline-fixture"), "audio/mp4", [], ViewModel.IsVip ? "Hi-Res 无损" : "标准",
                        ViewModel.IsVip ? new AudioStream(new Uri("https://audio.bilivideo.com/offline-standard"), "audio/mp4", []) : null)
                    : await ViewModel.Client.GetAudioAsync(resolved, load.Token);
                load.Token.ThrowIfCancellationRequested();
                standard.IsEnabled = true;
                hires.Visibility = audio.HasHiRes || audio.Quality.StartsWith("Hi-Res", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
                hires.IsEnabled = ViewModel.IsVip && audio.Quality.StartsWith("Hi-Res", StringComparison.Ordinal);
                if (ViewModel.IsVip && !hires.IsEnabled) ToolTipService.SetToolTip(hires, "此曲目暂未提供 Hi-Res 音源");
                dialog.IsPrimaryButtonEnabled = true;
                status.Text = hires.Visibility == Visibility.Visible ? "保存为 MP3；Hi-Res 音源转码后不保留无损格式。" : "保存为 MP3。";
            }
            catch (OperationCanceledException) { if (!dialog.IsPrimaryButtonEnabled) status.Text = "音质读取已取消或超时，请稍后重试。"; }
            catch (Exception error)
            { status.Text = "无法读取音质，请稍后重试。"; ViewModel.Notify(Feedback.WithRequestHint(error.Message)); }
            finally { loading.IsActive = false; loading.Visibility = Visibility.Collapsed; }
        }
        try
        {
            var shown = dialog.ShowAsync();
            var read = ReadQualitiesAsync();
            var result = await shown;
            load.Cancel(); await read;
            if (result != ContentDialogResult.Primary || audio is null) return null;
            var high = hires.IsChecked == true && hires.IsEnabled;
            return new(resolved, high ? audio : audio.Fallback ?? audio, high ? 320000 : 192000);
        }
        finally { load.Cancel(); _downloadDialogOpen = false; }
    }
    private void PlaybackModeOpening(object sender, object e)
    {
        if (sender is not MenuFlyout menu) return;
        foreach (var item in menu.Items.OfType<RadioMenuFlyoutItem>())
            item.IsChecked = int.TryParse(item.Tag as string, out var mode) && mode == (ViewModel.IsShuffle ? 3 : ViewModel.RepeatMode);
    }
    private void PlaybackModeClick(object sender, RoutedEventArgs e)
    { if (sender is RadioMenuFlyoutItem item && int.TryParse(item.Tag as string, out var mode)) ViewModel.SetPlaybackMode(mode); }
    private async void FavoriteCurrentClick(object sender, RoutedEventArgs e)
    { if (ViewModel.CurrentTrack is { } track) await ViewModel.RunAsync(() => AddFavoriteAsync(track)); }
    private void FolderRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not FrameworkElement { DataContext: Folder folder } element) continue;
            var menu = new MenuFlyout();
            var download = new MenuFlyoutItem { Text = "下载全部", Icon = new SymbolIcon(Symbol.Download) };
            download.Click += async (_, _) => await DownloadEntireFolderAsync(folder);
            menu.Items.Add(download);
            if (folder.Owned)
            {
                var edit = new MenuFlyoutItem { Text = "编辑收藏夹", Icon = new SymbolIcon(Symbol.Edit) };
                edit.Click += async (_, _) => await ViewModel.RunAsync(() => EditFolderAsync(folder)); menu.Items.Add(edit);
                var delete = new MenuFlyoutItem { Text = "删除收藏夹", Icon = new SymbolIcon(Symbol.Delete) };
                delete.Click += async (_, _) => await DeleteFolderAsync(folder); menu.Items.Add(delete);
            }
            if (menu.Items.Count > 0) menu.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) }); e.Handled = true; return;
        }
    }
    public async Task SearchFromTitleBarAsync()
    {
        await CloseLyricsAsync(); Navigation.SelectedItem = null;
        var search = ViewModel.SearchCommand.ExecuteAsync(null);
        if (ViewModel.IsSearchUploaders) (ViewModel.SearchGridMode ? (Control)UploaderGrid : UploaderList).Focus(FocusState.Programmatic); else TrackList.Focus(FocusState.Programmatic);
        await search;
    }
    private async void FolderClicked(object sender, ItemClickEventArgs e) => await ViewModel.OpenFolderCommand.ExecuteAsync(e.ClickedItem as Folder);
    private void GridViewClick(object sender, RoutedEventArgs e) => SetBrowseMode(true);
    private void ListViewClick(object sender, RoutedEventArgs e) => SetBrowseMode(false);
    private void SetBrowseMode(bool grid)
    { if (ViewModel.IsLibraryView) ViewModel.FolderGridMode = grid; else if (ViewModel.IsFolderView) ViewModel.FolderTracksGridMode = grid; else if (ViewModel.IsSearchView) ViewModel.SearchGridMode = grid; else if (ViewModel.IsSpaceView) ViewModel.SpaceGridMode = grid; else ViewModel.HomeGridMode = grid; }
    private void DismissNoticeClick(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { Tag: ToastNotice item }) ViewModel.DismissNotice(item); }
    private void TrackRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement element && (element.Tag as Track ?? element.DataContext as Track) is { } track)
            { ShowTrackMenu(element, track, e.GetPosition(element)); e.Handled = true; return; }
            if (node == TrackList || node == RecommendationGrid) break;
        }
    }
    private void LyricClicked(object sender, ItemClickEventArgs e)
    { if (e.ClickedItem is SubtitleLine line) { ResumeLyricFollowing(); ViewModel.Seek(line.From); } }
    private void RemoveQueueClick(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.Tag is Track track) ViewModel.RemoveQueueCommand.Execute(track); }
    private void CloseQueueClick(object sender, RoutedEventArgs e) => ViewModel.ShowQueue = false;

    private void NavigationDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) => UpdateSidebarGrip();
    private void NavigationPaneChanged(NavigationView sender, object args) { _paneClosing = !Navigation.IsPaneOpen; UpdateSidebarGrip(); }
    private void NavigationPaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
    { _paneClosing = true; _sidebarDragging = false; if (SidebarGrip is not null) SidebarGrip.Visibility = Visibility.Collapsed; if (SidebarGripLine is not null) SidebarGripLine.Opacity = 0; ProtectedCursor = null; }
    private void UpdateSidebarGrip()
    {
        if (SidebarGrip is null || SidebarGripLine is null || Navigation is null) return;
        var visible = !_paneClosing && ViewModel.IsLoggedIn && !ViewModel.IsLyricsOpen && Navigation.IsPaneOpen &&
            Navigation.DisplayMode == NavigationViewDisplayMode.Expanded;
        SidebarGrip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) { _sidebarDragging = false; SidebarGripLine.Opacity = 0; ProtectedCursor = null; }
    }
    private void SidebarDragStarted(object sender, DragStartedEventArgs e) { _sidebarDragging = true; SidebarGripLine.Opacity = 1; }
    private void SidebarDragDelta(object sender, DragDeltaEventArgs e) => ResizeSidebar(ViewModel.SidebarWidth + e.HorizontalChange);
    private void SidebarDragCompleted(object sender, DragCompletedEventArgs e)
    { _sidebarDragging = false; SidebarGripLine.Opacity = 0; ProtectedCursor = null; ViewModel.SaveSidebarWidth(); UpdateSidebarGrip(); }
    private void ResizeSidebar(double width) => ViewModel.SidebarWidth = Math.Clamp(width, 180, Math.Clamp(ActualWidth - 560, 180, 420));
    private void SidebarKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
        ResizeSidebar(ViewModel.SidebarWidth + (e.Key == VirtualKey.Right ? 8 : -8)); ViewModel.SaveSidebarWidth(); e.Handled = true;
    }
    private void SidebarPointerEntered(object sender, PointerRoutedEventArgs e)
    { if (!Navigation.IsPaneOpen || _paneClosing || SidebarGrip.Visibility != Visibility.Visible) return; SidebarGripLine.Opacity = 1; ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast); }
    private void SidebarPointerExited(object sender, PointerRoutedEventArgs e)
    { if (!_sidebarDragging) { SidebarGripLine.Opacity = 0; ProtectedCursor = null; } }
    private void InitializeFooterInput()
    {
        PlayerBar.AddHandler(PointerPressedEvent,new PointerEventHandler((_,args)=>
        {
            _footerInputSerial++; _footerInputAt=MotionTime;
            if(args.Pointer.PointerDeviceType==Microsoft.UI.Input.PointerDeviceType.Mouse && args.GetCurrentPoint(PlayerBar).Properties.IsLeftButtonPressed && IsFooterSurface(args.OriginalSource as DependencyObject))
                _=ToggleLyricsFromInputAsync();
        }),true);
        PlayerBar.AddHandler(KeyDownEvent,new KeyEventHandler((_,args)=>
        { if(args.Key is VirtualKey.Enter or VirtualKey.Space) { _footerInputSerial++; _footerInputAt=MotionTime; } }),true);
    }
    private long _footerInputSerial, _footerConsumedSerial=-1;
    private double _footerInputAt;
    private bool IsFooterSurface(DependencyObject? source)
    {
        for(var node=source;node is not null&&node!=PlayerBar;node=VisualTreeHelper.GetParent(node))
        {
            if(node==NowPlayingButton) return true;
            if(node is ButtonBase or Slider or ComboBox) return false;
        }
        return source is not null;
    }
    private Task ToggleLyricsFromInputAsync()
    {
        if(_footerInputSerial!=0 && MotionTime-_footerInputAt<1)
        {
            if(_footerConsumedSerial==_footerInputSerial) return Task.CompletedTask;
            _footerConsumedSerial=_footerInputSerial;
        }
        return ToggleLyricsAsync();
    }
    private async void OpenLyricsClick(object sender,RoutedEventArgs args) => await ToggleLyricsFromInputAsync();
    private async void PlayerTitleTapped(object sender,TappedRoutedEventArgs args) { args.Handled=true;await ToggleLyricsFromInputAsync(); }
    private async void PlayerBarTapped(object sender,TappedRoutedEventArgs args)
    { if(IsFooterSurface(args.OriginalSource as DependencyObject)) { args.Handled=true;await ToggleLyricsFromInputAsync(); } }
    private async void PlayerBarDoubleTapped(object sender,DoubleTappedRoutedEventArgs args)
    { if(IsFooterSurface(args.OriginalSource as DependencyObject)) { args.Handled=true;await ToggleLyricsFromInputAsync(); } }
    private async Task ToggleLyricsAsync()
    {
        if (!ViewModel.HasCurrentTrack) return;
        ViewModel.IsLyricsOpen = !ViewModel.IsLyricsOpen;
        await _lyricsMotionTask;
    }
    private async Task CloseLyricsAsync()
    {
        if (ViewModel.IsLyricsOpen) ViewModel.IsLyricsOpen = false;
        await _lyricsMotionTask;
    }
    private static void ResetMotion(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity"); visual.StopAnimation("Translation"); visual.StopAnimation("Scale");
        visual.Opacity = 1; visual.Scale = Vector3.One; visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }
    private TimedTween? _footerMetadataTween, _lyricsColumnTween, _lyricsOpacityTween;
    private void AnimateFooterMetadata(Vector3 offset,Vector4 velocity)
    {
        var target=Vector4.Zero;
        _footerMetadataTween=AnimateTween(FooterMetadata,"Translation",new(offset,0),target,velocity,160);
    }
    private void LyricsEntrance(bool fresh)
    {
        var current=_lyricsColumnTween?.Sample() ?? (new Vector4(0,fresh?48:0,0,0),Vector4.Zero);
        _lyricsColumnTween=AnimateTween(LyricsColumn,"Translation",current.Item1,Vector4.Zero,current.Item2,260);
    }
    private void SettleArtwork()
    {
        // The landing cover stays at its actual layout size. No secondary scale/settle is applied.
        var visual = ElementCompositionPreview.GetElementVisual(CoverFrame);
        visual.StopAnimation("Scale"); visual.Scale = Vector3.One; visual.CenterPoint = Vector3.Zero;
    }
    private void LyricsSizeChanged(object sender, SizeChangedEventArgs e) => ResizeArtwork();
    private void ResizeArtwork()
    {
        if (LyricsView.ActualWidth <= 0 || LyricsView.ActualHeight <= 0) return;
        var layout = PlaybackPresentation.Measure(LyricsView.ActualWidth, LyricsView.ActualHeight);
        LyricsBody.Margin = new Thickness(layout.Padding, 28, layout.Padding, 28);
        LyricsBody.ColumnSpacing = layout.Gap; ArtworkWidth.Width = new GridLength(layout.LeftWidth);
        CoverFrame.Width = CoverFrame.Height = layout.CoverSize; _lyricFontSize = layout.LyricFontSize;
        StyleVisibleLyrics(false);
    }
    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }

    private async void CancelDownloadClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DownloadJob job }) await ViewModel.RunAsync(() => ViewModel.CancelDownloadAsync(job));
    }
    private void DownloadRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DownloadJob job } anchor) return;
        var menu = new MenuFlyout(); var delete = new MenuFlyoutItem { Text = "删除音乐", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await ViewModel.RunAsync(() => ViewModel.CancelDownloadAsync(job));
        menu.Items.Add(delete); menu.ShowAt(anchor, new FlyoutShowOptions { Position = e.GetPosition(anchor) }); e.Handled = true;
    }
    private void TrackMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Track track } button) return;
        ShowTrackMenu(button, track);
    }
    private void ShowTrackMenu(FrameworkElement anchor, Track track, Windows.Foundation.Point? position = null)
    {
        var menu=BuildTrackMenu(track);
        if(position is { } point) menu.ShowAt(anchor,new FlyoutShowOptions { Position=point });
        else menu.ShowAt(anchor);
    }
    private MenuFlyout BuildTrackMenu(Track track)
    {
        var menu=new MenuFlyout();
        Add("播放",Symbol.Play,async()=>await ViewModel.PlayCommand.ExecuteAsync(track));
        Add("添加至播放列表",Symbol.Add,()=>{ViewModel.EnqueueCommand.Execute(track);return Task.CompletedTask;});
        Add("下一首播放并添加至播放列表",Symbol.Next,()=>{ViewModel.PlayNextCommand.Execute(track);return Task.CompletedTask;});
        Add("添加到收藏夹",Symbol.Favorite,()=>AddFavoriteAsync(track));
        var downloaded=ViewModel.IsTrackDownloaded(track);
        if(downloaded) Add("已下载",Symbol.Download,()=>Task.CompletedTask,false);
        if(ViewModel.IsCachedView) Add("删除音乐",Symbol.Delete,()=>DeleteCachedWithConfirmationAsync(new[]{track}));
        else if(!downloaded) Add("下载音乐",Symbol.Download,()=>ViewModel.DownloadAsync(track));
        return menu;
        void Add(string text,Symbol icon,Func<Task> action,bool enabled=true)
        {
            var item=new MenuFlyoutItem {Text=text,Icon=new SymbolIcon(icon),IsEnabled=enabled};
            item.Click+=async(_,_)=>await ViewModel.RunAsync(action);menu.Items.Add(item);
        }
    }
    private void CachedSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        ViewModel.SelectedCacheCount = ViewModel.IsCacheSelectionMode ? TrackList.SelectedItems.Count : 0;
        ViewModel.SelectedFolderTrackCount = ViewModel.IsFolderSelectionMode ? SelectedFolderTracks().Length : 0;
    }
    private void SelectAllCachedClick(object sender, RoutedEventArgs args) => TrackList.SelectAll();
    private async void DeleteSelectedCachedClick(object sender, RoutedEventArgs args)
    { var selected = TrackList.SelectedItems.OfType<Track>().ToArray(); await ViewModel.RunAsync(() => DeleteCachedWithConfirmationAsync(selected)); }
    private bool _cacheDeleteDialogOpen;
    private async Task DeleteCachedWithConfirmationAsync(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0 || _cacheDeleteDialogOpen) return;
        _cacheDeleteDialogOpen = true;
        try
        {
        var message = tracks.Count == 1 ? tracks[0].Title : $"已选择 {tracks.Count} 首音乐";
        message += "\n\n音频文件和离线字幕将从本机删除，需要时可重新下载。";
        if (await Dialog(tracks.Count == 1 ? "删除已下载音乐？" : "删除所选下载音乐？", message, "删除").ShowAsync() != ContentDialogResult.Primary) return;
        var deleted = 0;
        foreach (var track in tracks)
        {
            await ViewModel.DeleteCachedAsync(track, notify: false);
            if (!File.Exists(track.LocalFile)) deleted++;
        }
        if (TrackList.SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended) TrackList.SelectedItems.Clear();
        else TrackList.SelectedItem = null;
        ViewModel.IsCacheSelectionMode = false; ViewModel.SelectedCacheCount = 0;
        if (deleted > 0) ViewModel.Notify($"已删除 {deleted} 首下载音乐及字幕。", NoticeLevel.Info);
        }
        finally { _cacheDeleteDialogOpen = false; }
    }
    private Visibility ShowWhileNotSelecting(bool show, bool selecting) => show && !selecting ? Visibility.Visible : Visibility.Collapsed;
    private ContentDialog Dialog(string title, object content, string action) => new()
    {
        XamlRoot = XamlRoot, Title = title, Content = content, PrimaryButtonText = action,
        CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
        RequestedTheme = ActualTheme
    };
    private async void NewFolderClick(object sender, RoutedEventArgs e) => await ViewModel.RunAsync(() => EditFolderAsync(null));
    private async Task EditFolderAsync(Folder? folder)
    {
        var name = new TextBox { Header = "名称", Text = folder?.Title ?? "", MaxLength = 80, MinWidth = 320 };
        var privacy = new ToggleSwitch { Header = "隐私", OnContent = "私密", OffContent = "公开", IsOn = folder?.IsPrivate ?? true };
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(name); content.Children.Add(privacy);
        content.Children.Add(new TextBlock { Text = "保存后会同步到哔哩哔哩。", TextWrapping = TextWrapping.Wrap });
        var dialog = Dialog(folder is null ? "新建收藏夹" : "编辑收藏夹", content, "保存");
        dialog.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(name.Text)) { args.Cancel = true; name.Focus(FocusState.Programmatic); } };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (folder is null) await ViewModel.Client.CreateFolderAsync(name.Text, privacy.IsOn);
        else await ViewModel.Client.EditFolderAsync(folder, name.Text, privacy.IsOn);
        await ViewModel.RefreshFoldersAsync();
        if (folder is null) ViewModel.Navigate("library");
        else if (ViewModel.Folders.FirstOrDefault(f => f.Id == folder.Id) is { } updated) await ViewModel.OpenFolderCommand.ExecuteAsync(updated);
        ViewModel.Notify("收藏夹已保存。", NoticeLevel.Info);
    }
    private async Task DeleteFolderAsync(Folder folder)
    {
        await ViewModel.RunAsync(async () =>
        {
            var dialog = Dialog("删除收藏夹？", $"“{folder.Title}”将从哔哩哔哩账号中删除，其中的收藏记录也会移除。此操作无法在软件中撤销。", "删除");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.Client.DeleteFolderAsync(folder);
            await ViewModel.RefreshFoldersAsync(); ViewModel.Navigate("library");
            ViewModel.Notify("收藏夹已删除。", NoticeLevel.Info);
        });
    }
    private async Task AddFavoriteAsync(Track track)
    {
        if (track.Aid <= 0) track = (await ViewModel.Client.GetVideoAsync(track.Bvid)).Track;
        await ViewModel.RefreshFoldersAsync();
        var folders = ViewModel.Folders.Where(f => f.Owned).ToArray();
        if (folders.Length == 0) { ViewModel.Notify("请先新建一个收藏夹。", NoticeLevel.Info); return; }
        var select = new ComboBox { ItemsSource = folders, DisplayMemberPath = "Title", SelectedIndex = 0, Header = "收藏夹", MinWidth = 320 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = track.Title, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(select);
        panel.Children.Add(new TextBlock { Text = "将把原视频收藏到所选 B 站收藏夹。", TextWrapping = TextWrapping.Wrap });
        if (await Dialog("添加到收藏夹", panel, "添加").ShowAsync() != ContentDialogResult.Primary) return;
        var folder = (Folder)select.SelectedItem;
        await ViewModel.Client.SetFavoriteAsync(track.Aid, folder, true);
        await ViewModel.RefreshFavoriteStateAsync();
        await ViewModel.RefreshFoldersAsync();
        if (ViewModel.SelectedFolder?.Id == folder.Id) await ViewModel.OpenFolderCommand.ExecuteAsync(ViewModel.SelectedFolder);
        ViewModel.Notify("已添加到收藏夹。", NoticeLevel.Info);
    }
    private Task ClearHistoryAsync() => ViewModel.RunAsync(async () =>
    {
        if (await Dialog("清除播放记录？", "仅清除本机记录，B 站收藏夹不会改变。", "清除").ShowAsync() != ContentDialogResult.Primary) return;
        ViewModel.Store.ClearHistory(ViewModel.Client.Account!.Id); ViewModel.Notify("播放记录已清除。", NoticeLevel.Info);
        if (ViewModel.IsHistoryView) ViewModel.Navigate("history");
    });
}
