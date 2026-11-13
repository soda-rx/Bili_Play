using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using BiliPlay.Core;
using BiliPlay.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;

namespace BiliPlay.ViewModels;

public sealed class ToastNotice(string message, NoticeLevel severity)
{
    public string Message { get; } = message;
    public NoticeLevel Severity { get; } = severity;
    public string Label => Severity.ToString();
    public Brush LabelBrush => (Brush)Application.Current.Resources[Severity switch
    {
        NoticeLevel.Warn => "SystemFillColorCautionBrush",
        NoticeLevel.Error => "SystemFillColorCriticalBrush",
        _ => "SystemFillColorSuccessBrush"
    }];
}

public partial class PlayerViewModel : ObservableObject, IDisposable
{
    public BilibiliClient Client { get; }
    public LocalStore Store { get; } = new(DebugSession.DirectoryPath ?? (App.IsVisualCheck ? Path.Combine(App.VisualCheckDirectory!, "data") : null), DebugSession.IsActive);
    public PlaybackService Player { get; }
    public ObservableCollection<Folder> Folders { get; } = [];
    public IncrementalCollection<Track> Tracks { get; } = [];
    public IncrementalCollection<Uploader> Uploaders { get; } = [];
    public ObservableCollection<string> SearchHistory { get; } = [];
    public bool HasTracks => IsPlaylistView ? Queue.Count > 0 : Tracks.Count > 0;
    public bool HasQueue => Queue.Count > 0;
    public bool CanClearHistory => IsHistoryView && HasTracks && !IsEmpty;
    public bool CanClearQueue => HasQueue;
    public double ClearHistoryOpacity => CanClearHistory ? 1 : .35;
    public double ClearQueueOpacity => CanClearQueue ? 1 : .35;
    public Func<Task>? ConfirmClearHistory { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackSelectionMode), nameof(CanClickTrack), nameof(CacheSelectionLabel), nameof(HasSelectedCacheTracks), nameof(DeleteSelectedCacheOpacity))]
    public partial bool IsCacheSelectionMode { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCacheTracks), nameof(DeleteSelectedCacheOpacity))]
    public partial int SelectedCacheCount { get; set; }
    partial void OnIsCacheSelectionModeChanged(bool value) => SelectedCacheCount=0;
    public bool HasSelectedCacheTracks => IsCacheSelectionMode && IsCachedView && SelectedCacheCount > 0;
    public double DeleteSelectedCacheOpacity => HasSelectedCacheTracks ? 1 : .35;
    public ListViewSelectionMode TrackSelectionMode => IsCacheSelectionMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
    public bool CanClickTrack => !IsCacheSelectionMode;
    public string CacheSelectionLabel => IsCacheSelectionMode ? "取消选择" : "选择";
    private void UpdateClearAvailability()
    {
        OnPropertyChanged(nameof(CanClearHistory)); OnPropertyChanged(nameof(CanClearQueue));
        OnPropertyChanged(nameof(ClearHistoryOpacity)); OnPropertyChanged(nameof(ClearQueueOpacity));
        ClearQueueCommand.NotifyCanExecuteChanged(); ClearHistoryCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanClearHistory))]
    private Task ClearHistoryAsync() => ConfirmClearHistory?.Invoke() ?? Task.CompletedTask;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackRateText))]
    public partial double PlaybackRate { get; set; } = 1;
    public string PlaybackRateText => PlaybackSpeed.Label(PlaybackRate);
    partial void OnPlaybackRateChanged(double value)
    {
        Player.PlaybackRate = PlaybackSpeed.Validate(value);
        Store.SetSetting("playbackRate", value.ToString(CultureInfo.InvariantCulture));
    }
    public bool IsLocalView => IsHistoryView || IsPlaylistView;
    [ObservableProperty] public partial string FollowingLabel { get; set; } = "关注 …";
    [ObservableProperty] public partial string FollowersLabel { get; set; } = "粉丝 …";
    public ObservableCollection<Track> Queue { get; } = [];
    public ObservableCollection<ToastNotice> Notifications { get; } = [];
    public Func<ToastNotice, Task>? AnimateNoticeOut { get; set; }
    private readonly HashSet<ToastNotice> _closingNotices = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _downloadSlots = new(1);
    public ObservableCollection<DownloadJob> Downloads { get; } = [];
    private CancellationTokenSource _accountDownloads = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCachedView), nameof(IsDownloadingView), nameof(IsTrackListVisible), nameof(HasTrackContent), nameof(HasSelectedCacheTracks), nameof(DeleteSelectedCacheOpacity), nameof(CanSelectCachedTracks), nameof(CacheSelectionOpacity))]
    public partial int CacheTabIndex { get; set; }
    public bool IsCachedView => IsCacheView && CacheTabIndex == 0;
    public bool IsDownloadingView => IsCacheView && CacheTabIndex == 1;
    partial void OnCacheTabIndexChanged(int value) { IsCacheSelectionMode = false; SelectedCacheCount = 0; if (IsCacheView) RefreshCacheContent(); }
    private void RefreshCacheContent()
    {
        if (_disposed || !IsCacheView || Client.Account is null) return;
        if (CacheTabIndex == 0) Replace(Tracks, Store.ReadTracks($"downloads:{Client.Account.Id}").Where(t => DownloadService.IsCached(Client.Account.Id, t)));
        IsEmpty = CacheTabIndex == 1 ? Downloads.Count == 0 : Tracks.Count == 0;
        EmptyText = CacheTabIndex == 1 ? "没有正在下载的音乐。" : "下载的音乐会显示在这里。";
    }
    private bool _restoringQueue;
    private int _queueFallbackIndex;
    private double _previewPosition;
    private long _queueAccount = -1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotificationMargin))]
    public partial double PlayerBarHeight { get; set; } = 132;
    public Thickness NotificationMargin => new(16, 0, 16, IsLoggedIn ? PlayerBarHeight + 16 : 16);
    public ObservableCollection<SubtitleTrack> SubtitleTracks { get; } = [];
    public ObservableCollection<SubtitleLine> Lines { get; } = [];
    public bool HasLyrics => Lines.Count > 0;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private CancellationTokenSource? _login, _browse, _subtitles, _subtitleDiscovery;
    private Track? _subtitleTrack;
    private int _page = 1, _browseVersion, _playVersion, _saveTicks;
    private string _query = "";
    private string _view = "home";
    private sealed record BrowseState(string View, string Title, Folder? Folder, Track[] Tracks, Uploader[] Uploaders,
        string Query, int Page, bool More, int Category, string EmptyText, UserSpace? Space = null, long SpaceUser = 0);
    private readonly Stack<BrowseState> _backStack = new();
    private bool _restoringNavigation;
    private int _favoriteVersion;
    private DateTimeOffset _favoriteChecked;
    private bool _favoriteLoading;
    public string CurrentView => _view;
    public event Action? BrowseChanged;
    public void ReplayBrowseEntrance()
    {
        if (IsLoggedIn && !IsLyricsOpen) BrowseChanged?.Invoke();
    }
    [ObservableProperty]
    public partial bool IsCurrentFavorited { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchUploaders), nameof(IsTrackListVisible), nameof(IsHomeGridView), nameof(CanChooseView), nameof(IsUploaderGridView), nameof(IsUploaderListView))]
    public partial bool IsSearchView { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchUploaders), nameof(IsTrackListVisible), nameof(IsHomeGridView), nameof(CanChooseView), nameof(IsUploaderGridView), nameof(IsUploaderListView))]
    public partial int SearchCategoryIndex { get; set; }
    public bool IsUploaderGridView => IsSearchUploaders && SearchGridMode;
    public bool IsUploaderListView => IsSearchUploaders && !SearchGridMode;
    public bool IsSearchUploaders => IsSearchView && SearchCategoryIndex == 1;
    partial void OnSearchCategoryIndexChanged(int value)
    {
        if (!_restoringNavigation && IsSearchView && _query.Length > 0)
            _ = SafeAsync(() => SearchCoreAsync(false));
    }
    private readonly List<Track> _homeCache = [];
    private int _regionPage;
    private bool _homeMore = true;
    private bool _hasLoadedHome;
    private bool _disposed;
    private int _busyOperations;
    private readonly Dictionary<int, int> _pageOperations = [];
    public bool ShowPageProgress => !IsCacheView && _pageOperations.GetValueOrDefault(_browseVersion) > 0;
    public Func<string, Task<QueueChange>>? ConfirmQueueChange { get; set; }
    public Func<Track, Task<DownloadOptions?>>? RequestDownloadOptions { get; set; }
    private int _selectionVersion;
    private int _foldersVersion;
    private CancellationTokenSource? _folderPreviews;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginVisibility), nameof(LibraryVisibility), nameof(StartupVisibility), nameof(CanGoBack), nameof(NotificationMargin))]
    public partial bool IsLoggedIn { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginVisibility), nameof(StartupVisibility))]
    public partial bool IsInitializing { get; set; } = true;
    public Visibility StartupVisibility => IsInitializing && !IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LoginVisibility => IsLoggedIn || IsInitializing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LibraryVisibility => IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
    [ObservableProperty] public partial string AccountName { get; set; } = "尚未登录";
    [ObservableProperty] public partial ImageSource? AccountAvatar { get; set; }
    [ObservableProperty] public partial ImageSource? AccountLargeAvatar { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VipVisibility))]
    public partial bool IsVip { get; set; }
    public Visibility VipVisibility => IsVip ? Visibility.Visible : Visibility.Collapsed;
    [ObservableProperty] public partial string AccountUid { get; set; } = "";
    [ObservableProperty] public partial string AccountLevel { get; set; } = "";
    [ObservableProperty] public partial string LoginStatus { get; set; } = "正在连接哔哩哔哩…";
    [ObservableProperty] public partial string LoginActionLabel { get; set; } = "刷新二维码";
    [ObservableProperty] public partial ImageSource? QrImage { get; set; }
    [ObservableProperty] public partial bool LoginLoading { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string PageTitle { get; set; } = "首页";
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial Folder? SelectedFolder { get; set; }
    [ObservableProperty] public partial Track? SelectedTrack { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentTrack))]
    public partial Track? CurrentTrack { get; set; }
    public bool HasCurrentTrack => CurrentTrack is not null;
    [ObservableProperty] public partial bool IsPlaybackLoading { get; set; }
    [ObservableProperty] public partial string CurrentTitle { get; set; } = "暂无播放歌曲";
    [ObservableProperty] public partial string CurrentArtist { get; set; } = "";
    [ObservableProperty] public partial bool CanFollowUploader { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FollowLabel), nameof(FollowGlyph))]
    public partial bool IsFollowingUploader { get; set; }
    public string FollowLabel => IsFollowingUploader ? "已关注" : "关注 UP 主";
    public string FollowGlyph => IsFollowingUploader ? "\uE73E" : "\uE710";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityDisplayText), nameof(QualityToolTip))]
    public partial string CurrentQuality { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityToolTip))]
    public partial bool IsQualitySwitching { get; set; }
    public string QualityDisplayText => CurrentQuality.StartsWith("Hi-Res", StringComparison.Ordinal) ? "Hi-Res"
        : CurrentQuality.StartsWith("MP3", StringComparison.Ordinal) ? "MP3" : CurrentQuality;
    public string QualityToolTip => IsQualitySwitching ? $"正在切换至 {CurrentQuality}" : "音质";
    [ObservableProperty] public partial ImageSource? CurrentCover { get; set; }
    [ObservableProperty] public partial ImageSource? LyricsCover { get; set; }
    [ObservableProperty] public partial string PlayGlyph { get; set; } = "\uE768";
    [ObservableProperty] public partial string PlayLabel { get; set; } = "播放";
    [ObservableProperty] public partial double Position { get; set; }
    partial void OnPositionChanged(double value)
    {
        PositionText = Text.FormatTime(value);
        RemainingText = "−" + Text.FormatTime(Math.Max(0, Duration - value));
        UpdateSubtitleHighlight();
    }
    [ObservableProperty] public partial double Duration { get; set; } = 1;
    [ObservableProperty] public partial string PositionText { get; set; } = "0:00";
    [ObservableProperty] public partial string RemainingText { get; set; } = "−0:00";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeGlyph), nameof(VolumeLabel))]
    public partial double Volume { get; set; } = 75;
    public string VolumeGlyph => PlaybackPresentation.VolumeGlyph(Volume);
    public string VolumeLabel => Volume <= 0 ? "音量 0% · 静音" : $"音量 {Volume:0}%";
    [ObservableProperty] public partial bool HasMore { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackModeLabel), nameof(PlaybackModeGlyph))]
    public partial bool IsShuffle { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackModeLabel), nameof(PlaybackModeGlyph))]
    public partial int RepeatMode { get; set; } = 1;
    public string PlaybackModeLabel => IsShuffle ? "随机播放" : RepeatMode == 2 ? "单曲循环" : RepeatMode == 1 ? "列表循环" : "顺序播放";
    public string PlaybackModeGlyph => IsShuffle ? "\uE8B1" : RepeatMode == 2 ? "\uE8ED" : "\uE8EE";
    public void SetPlaybackMode(int mode) { IsShuffle = mode == 3; RepeatMode = mode == 3 ? 1 : mode; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSubtitleLoading))]
    public partial string SubtitleStatus { get; set; } = "播放后显示可用字幕";
    public bool IsSubtitleLoading => !HasLyrics && SubtitleStatus == "正在加载字幕…";
    [ObservableProperty] public partial int ActiveSubtitleIndex { get; set; } = -1;
    [ObservableProperty] public partial int FollowSubtitleIndex { get; set; } = -1;
    [ObservableProperty] public partial SubtitleTrack? SelectedSubtitle { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidePaneOpen))]
    public partial bool ShowQueue { get; set; }
    partial void OnShowQueueChanged(bool value) { if (value) { IsCommentsPane = false; ShowComments = false; } }
    [ObservableProperty] public partial bool IsLibraryView { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshLabel), nameof(HasTrackContent), nameof(ToolbarOverflowVisibility))]
    public partial bool IsHomeView { get; set; } = true;
    [ObservableProperty] public partial bool IsHistoryView { get; set; }
    [ObservableProperty] public partial bool IsTracksView { get; set; }
    public bool HasTrackContent => (IsHomeView || IsTracksView) && !IsDownloadingView;
    public string RefreshLabel => IsHomeView ? "换一批" : "刷新";
    public CommandBarOverflowButtonVisibility ToolbarOverflowVisibility => CommandBarOverflowButtonVisibility.Collapsed;
    public bool CanChooseView => IsHomeView || IsLibraryView || IsFolderView || IsSearchView || IsSpaceView;
    public bool CanSelectCachedTracks => IsCachedView && !IsEmpty && Tracks.Count > 0;
    public double CacheSelectionOpacity => CanSelectCachedTracks ? 1 : .35;
    [ObservableProperty] public partial bool IsFolderView { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasTracks))] public partial bool IsPlaylistView { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCachedView), nameof(IsDownloadingView), nameof(IsTrackListVisible), nameof(HasTrackContent), nameof(CanSelectCachedTracks), nameof(CacheSelectionOpacity))]
    public partial bool IsCacheView { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReLoginVisibility))]
    public partial bool NeedsLogin { get; set; }
    public Visibility ReLoginVisibility => NeedsLogin ? Visibility.Visible : Visibility.Collapsed;
    [ObservableProperty] public partial bool IsSettingsView { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectCachedTracks), nameof(CacheSelectionOpacity))]
    public partial bool IsEmpty { get; set; }
    partial void OnIsEmptyChanged(bool value) { if (value && IsCacheView) IsCacheSelectionMode=false; }
    [ObservableProperty] public partial string EmptyText { get; set; } = "暂无内容";
    public string AppVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "未知";
    [ObservableProperty] public partial int ThemeIndex { get; set; }
    [ObservableProperty] public partial bool RecordRecentPlays { get; set; } = true;
    partial void OnRecordRecentPlaysChanged(bool value) { if (Store is not null) Store.RecordRecentPlays = value; }
    [ObservableProperty] public partial bool RecordSearchHistory { get; set; } = true;
    partial void OnRecordSearchHistoryChanged(bool value)
    {
        Store.RecordSearchHistory = value;
        Replace(SearchHistory, Client.Account is { } account ? Store.SearchHistory(account.Id) : []);
    }
    [ObservableProperty]
    public partial bool CloseToTray { get; set; } = true;
    private int _closeBehaviorIndex;
    public int CloseBehaviorIndex
    {
        get => _closeBehaviorIndex;
        set { var next = Math.Clamp(value, 0, 2); if (_closeBehaviorIndex == next) return; _closeBehaviorIndex = next;
            CloseToTray = next != 1; Store?.SetSetting("closeBehavior", next.ToString(CultureInfo.InvariantCulture)); OnPropertyChanged(); }
    }
    public void PauseForTray() => Player.Pause();
    [ObservableProperty] public partial string CacheDirectory { get; set; } = "";
    partial void OnCacheDirectoryChanged(string value) { if (!string.IsNullOrWhiteSpace(value)) Store.SetSetting("cacheDirectory", value); }
    public static bool IsOnline => !DebugSession.IsActive && Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel()
        == Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    public partial bool IsLyricsOpen { get; set; }
    public bool CanGoBack => IsLoggedIn && (_backStack.Count > 0 || IsLyricsOpen);
    public void NavigateBack()
    {
        if (!_backStack.TryPop(out var state)) return;
        _restoringNavigation = true;
        try
        {
            Navigate(state.View); SelectedFolder = state.Folder; PageTitle = state.Title; SpaceProfile = state.Space; _spaceUserId = state.SpaceUser;
            _query = state.Query; SearchText = state.Query; _page = state.Page; HasMore = state.View == "home" ? _homeMore : state.More;
            SearchCategoryIndex = state.Category; EmptyText = state.EmptyText;
            if (state.View is "folder" or "search" or "space") Replace(Tracks, state.Tracks);
            Replace(Uploaders, state.Uploaders);
            IsEmpty = state.View == "library" ? Folders.Count == 0 : state.View == "settings" ? false
                : IsPlaylistView ? Queue.Count == 0 : IsSearchUploaders ? Uploaders.Count == 0 : Tracks.Count == 0;
            _browse = new();
        }
        finally { _restoringNavigation = false; OnPropertyChanged(nameof(CanGoBack)); }
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarGripMargin))]
    public partial double SidebarWidth { get; set; } = 220;
    public Thickness SidebarGripMargin => new(SidebarWidth - 4, 0, 0, 0);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeGridView), nameof(IsTrackListVisible))]
    public partial bool HomeGridMode { get; set; } = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolderGridView), nameof(IsFolderListView))]
    public partial bool FolderGridMode { get; set; } = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeGridView), nameof(IsTrackListVisible), nameof(BrowseGridMode), nameof(BrowseListMode))]
    public partial bool FolderTracksGridMode { get; set; } = true;
    partial void OnFolderTracksGridModeChanged(bool value) => Store.SetSetting("folderTracksGrid", value ? "true" : "false");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeGridView), nameof(IsTrackListVisible), nameof(BrowseGridMode), nameof(BrowseListMode), nameof(IsUploaderGridView), nameof(IsUploaderListView))]
    public partial bool SearchGridMode { get; set; }
    partial void OnSearchGridModeChanged(bool value) => Store.SetSetting("searchGrid", value ? "true" : "false");
    public bool IsHomeGridView => IsHomeView && HomeGridMode || IsFolderView && FolderTracksGridMode || IsSearchView && !IsSearchUploaders && SearchGridMode || IsSpaceView && SpaceGridMode;
    public bool IsFolderGridView => IsLibraryView && FolderGridMode;
    public bool IsFolderListView => IsLibraryView && !FolderGridMode;
    public bool IsTrackListVisible => !IsPlaylistView && !IsDownloadingView && !IsSearchUploaders && (IsTracksView && (!IsFolderView || !FolderTracksGridMode) && (!IsSearchView || !SearchGridMode) && (!IsSpaceView || !SpaceGridMode) || IsHomeView && !HomeGridMode);
    public bool BrowseGridMode => IsLibraryView ? FolderGridMode : IsFolderView ? FolderTracksGridMode : IsSearchView ? SearchGridMode : IsSpaceView ? SpaceGridMode : HomeGridMode;
    public bool BrowseListMode => !BrowseGridMode;
    partial void OnHomeGridModeChanged(bool value) { Store.SetSetting("homeGrid", value ? "true" : "false"); NotifyBrowseMode(); }
    partial void OnFolderGridModeChanged(bool value) { Store.SetSetting("folderGrid", value ? "true" : "false"); NotifyBrowseMode(); }
    private void NotifyBrowseMode() { OnPropertyChanged(nameof(BrowseGridMode)); OnPropertyChanged(nameof(BrowseListMode)); }
    public bool IsSeeking { get; set; }

    partial void OnHasMoreChanged(bool value) { Tracks.NotifyHasMore(); Uploaders.NotifyHasMore(); }
    private async Task FetchIncrementalPageAsync(CancellationToken ct)
    {
        if (IsBusy)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            System.ComponentModel.PropertyChangedEventHandler changed = (_, e) =>
            { if (e.PropertyName == nameof(IsBusy) && !IsBusy) ready.TrySetResult(); };
            PropertyChanged += changed;
            using var cancel = ct.Register(() => ready.TrySetCanceled(ct));
            try { if (IsBusy) await ready.Task; }
            finally { PropertyChanged -= changed; }
        }
        ct.ThrowIfCancellationRequested();
        if (HasMore && IsLoggedIn) await LoadMoreCommand.ExecuteAsync(null);
    }
    public PlayerViewModel() : this(new BilibiliClient(DebugSession.IsActive ? new DebugSession.OfflineHandler() : null)) { }
    private readonly Func<IReadOnlyList<SavedCookie>> _readSession;
    internal PlayerViewModel(BilibiliClient client, Func<IReadOnlyList<SavedCookie>>? readSession = null)
    {
        Client = client;
        _readSession = readSession ?? SessionVault.Read;
        PropertyChanged += (_, args) => { if (args.PropertyName is nameof(IsHistoryView) or nameof(IsPlaylistView) or nameof(IsEmpty)) UpdateClearAvailability(); };
        Tracks.CanLoadMore = () => IsLoggedIn && HasMore && (_view is "home" or "folder" or "space" || _view == "search" && !IsSearchUploaders);
        Uploaders.CanLoadMore = () => IsLoggedIn && HasMore && IsSearchUploaders;
        Tracks.FetchPage = Uploaders.FetchPage = FetchIncrementalPageAsync;
        Player = new(Client, App.DispatcherQueue);
        Lines.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasLyrics)); OnPropertyChanged(nameof(IsSubtitleLoading)); UpdateSubtitleHighlight(); };
        Tracks.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasTracks)); OnPropertyChanged(nameof(CanSelectCachedTracks)); OnPropertyChanged(nameof(CacheSelectionOpacity)); UpdateClearAvailability(); };
        Folders.CollectionChanged += (_, _) => { if (IsLibraryView) IsEmpty = Folders.Count == 0; };
        Volume = double.TryParse(Store.GetSetting("volume", "75"), CultureInfo.InvariantCulture, out var volume) ? volume : 75;
        Player.Volume = Volume / 100;
        PlaybackRate = double.TryParse(Store.GetSetting("playbackRate", "1"), CultureInfo.InvariantCulture, out var rate)
            && double.IsFinite(rate) && rate is >= .5 and <= 2 ? rate : 1;
        InitializeComments();
        ThemeIndex = int.TryParse(Store.GetSetting("theme", "0"), out var theme) ? Math.Clamp(theme, 0, 2) : 0;
        SingleClickBehaviorIndex = ReadTrackBehavior("singleClickBehavior", 0);
        DoubleClickBehaviorIndex = ReadTrackBehavior("doubleClickBehavior", 1);
        RecordRecentPlays = Store.RecordRecentPlays;
        RecordSearchHistory = Store.RecordSearchHistory;
        ImageCompatibilityMode = Store.GetSetting("imageCompatibilityMode", "false") == "true";
        ArtworkCache.SetCompatibilityMode(ImageCompatibilityMode);
        CloseToTray = Store.GetSetting("closeToTray", "true") != "false";
        CloseBehaviorIndex = int.TryParse(Store.GetSetting("closeBehavior", CloseToTray ? "0" : "1"), out var behavior) ? behavior : 0;
        var savedDirectory = Store.GetSetting("cacheDirectory", DebugSession.DirectoryPath is { } debugRoot ? Path.Combine(debugRoot,"downloads") : DownloadService.CacheDirectory(0));
        var previousDefault = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic, Environment.SpecialFolderOption.DoNotVerify);
        CacheDirectory = string.Equals(savedDirectory.TrimEnd(Path.DirectorySeparatorChar), previousDefault.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            ? DownloadService.CacheDirectory(0) : savedDirectory;
        Downloads.CollectionChanged += (_, _) => { if (IsDownloadingView) RefreshCacheContent(); };
        SidebarWidth = double.TryParse(Store.GetSetting("sidebarWidth", "220"), CultureInfo.InvariantCulture, out var width) ? Math.Clamp(width, 180, 420) : 220;
        HomeGridMode = Store.GetSetting("homeGrid", "true") != "false";
        SearchGridMode = Store.GetSetting("searchGrid", "false") == "true";
        FolderGridMode = Store.GetSetting("folderGrid", "true") != "false";
        FolderTracksGridMode = Store.GetSetting("folderTracksGrid", "true") != "false";
        SpaceGridMode = Store.GetSetting("spaceGrid", "false") == "true";
        Queue.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasQueue)); OnPropertyChanged(nameof(HasTracks)); OnPropertyChanged(nameof(CanSelectCachedTracks)); UpdateClearAvailability();
            if (IsPlaylistView) IsEmpty = Queue.Count == 0;
            if (_restoringQueue) return;
            if (Client.Account is { Id: > 0 } account) Store.WriteTracks($"queue:{account.Id}", Queue);
            SynchronizeQueue();
        };
        Player.Changed += OnPlaybackChanged;
        Player.Failed += message => Notify(message);
        Player.Ended += () => _ = SafeAsync(() => MoveAsync(true, true), false);
        Player.PlaybackRequested += play =>
        {
            if (play && !Player.Playing) _ = TogglePlayCommand.ExecuteAsync(null);
            else if (!play && Player.Playing) Player.Toggle();
        };
        Player.SkipRequested += next => _ = SafeAsync(() => MoveAsync(next, false), false);
        _timer.Tick += (_, _) => Tick();
        if (!App.IsVisualCheck) _timer.Start();
        _loadingSettings = false;
    }
    public async Task InitializeAsync()
    {
        try { await InitializeSessionAsync(); }
        finally { IsInitializing = false; }
    }
    private async Task InitializeSessionAsync()
    {
        if (DebugSession.IsActive) { await InitializeDebugAsync(); return; }
        _login?.Cancel();
        LoginLoading = true; LoginStatus = "正在连接哔哩哔哩…";
        var cookies = _readSession();
        if (cookies.Count > 0)
        {
            Client.ImportSession(cookies);
            Account? cached = null;
            try { cached = JsonSerializer.Deserialize<Account>(Store.GetSetting("account", "null")); } catch (JsonException) { }
            long.TryParse(cookies.FirstOrDefault(c => c.Name == "DedeUserID")?.Value, out var id);
            if (cached is null || id > 0 && cached.Id != id) cached = new(id, "哔哩哔哩用户", "");
            Client.RestoreCachedAccount(cached);
            await EnterAccountAsync(false);
            _ = SafeAsync(async () =>
            {
                await VerifySavedAccountAsync();
                await EnterAccountAsync();
            },busy:false);
            return;
        }
        IsInitializing = false;
        await LoginAsync();
    }
    private async Task VerifySavedAccountAsync()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await Client.GetAccountAsync(timeout.Token); return; }
            catch (Exception e) when (attempt == 0 && e is HttpRequestException or OperationCanceledException)
            { await Task.Delay(400); }
        }
    }
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ReconnectAsync() => InitializeAsync();
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task LoginAsync()
    {
        _login?.Cancel(); _login?.Dispose();
        var cts = _login = new();
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        QrImage = null; LoginLoading = true;
        LoginActionLabel = "刷新二维码";
        LoginStatus = "正在获取登录二维码…";
        try
        {
            await Client.WarmUpAsync(cts.Token);
            var login = await Client.CreateLoginCodeAsync(cts.Token);
            using var qr = QRCodeGenerator.GenerateQrCode(login.Url, QRCodeGenerator.ECCLevel.M);
            using var png = new PngByteQRCode(qr);
            var image = new BitmapImage();
            using (var stream = new MemoryStream(png.GetGraphic(8))) await image.SetSourceAsync(stream.AsRandomAccessStream());
            cts.Token.ThrowIfCancellationRequested();
            QrImage = image; LoginLoading = false;
            LoginStatus = "使用哔哩哔哩手机客户端扫码登录";
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                var code = await Client.PollLoginAsync(login.Key, cts.Token);
                switch (code)
                {
                    case 0:
                        await Client.GetAccountAsync(cts.Token);
                        cts.Token.ThrowIfCancellationRequested();
                        SessionVault.Save(Client.ExportSession());
                        await EnterAccountAsync(); return;
                    case 86090: LoginStatus = "已扫码，请在手机上确认登录"; break;
                    case 86038: LoginStatus = "二维码已过期，请刷新后重试"; return;
                    case 86101: break;
                    default: LoginStatus = "登录未完成，请刷新二维码重试"; return;
                }
            }
        }
        catch (OperationCanceledException) { if (ReferenceEquals(_login, cts)) LoginStatus = "二维码已过期，请刷新后重试"; }
        catch (Exception e) { if (ReferenceEquals(_login, cts)) { LoginStatus = "暂时无法登录，请重试"; Notify(Friendly(e)); } }
        finally { if (ReferenceEquals(_login, cts)) LoginLoading = false; }
    }
    internal async Task EnterAccountAsync(bool refreshOnline = true)
    {
        if (refreshOnline) { NeedsLogin = false; Store.SetSetting("account", JsonSerializer.Serialize(Client.Account)); }
        IsLoggedIn = true; AccountName = Client.Account!.Name; QrImage = null; LoginLoading = false;
        ProfileManager.UpdateAccount(Client.Account);
        AccountUid = Client.Account.Id.ToString(); AccountLevel = $"Lv.{Client.Account.Level}";
        Replace(SearchHistory, Store.SearchHistory(Client.Account!.Id));
        RefreshAccountArtwork();
        if(!App.IsVisualCheck && !DebugSession.IsActive) _ = ArtworkCache.WarmAsync(ProfileManager.WithDefault().Select(profile=>profile.Avatar), _lifetime.Token);
        IsVip = Client.Account.IsVip;
        var accountChanged=_queueAccount!=Client.Account.Id;
        if (accountChanged && Client.Account.Id > 0)
        {
            ClearAccountPages();
            _homeCache.Clear(); _homeCache.AddRange(Store.MusicCache(Client.Account.Id));
            _regionPage=0;_hasLoadedHome=false;_homeMore=true;
            _backStack.Clear();
            CancelDownloads(); _queueAccount = Client.Account.Id; _restoringQueue = true;
            try
            {
                _playVersion++; _subtitleDiscovery?.Cancel(); _subtitleTrack = null; _subtitles?.Cancel(); Player.Stop();
                Replace(Queue, Store.ReadTracks($"queue:{Client.Account.Id}"));
            }
            finally { _restoringQueue = false; }
            var saved = Store.GetSetting($"queueCurrent:{Client.Account.Id}", "");
            UpdateCurrentDisplay(Queue.FirstOrDefault(t => $"{t.Bvid}:{t.Page}" == saved) ?? Queue.FirstOrDefault());
        }
        if (!refreshOnline) { if(accountChanged) Navigate("home"); return; }
        var folders=SafeAsync(RefreshFoldersAsync,busy:IsLibraryView);
        if (_view=="home"&&!_hasLoadedHome) await Task.WhenAll(folders,OpenHomeCommand.ExecuteAsync(null));
        else await folders;
    }
    public void Navigate(string view)
    {
        if (_view == "notifications" && view != "notifications") DisposeNotifications();
        if (!_restoringNavigation && IsLoggedIn && (view != _view || view == "search" && (Tracks.Count > 0 || Uploaders.Count > 0)))
            _backStack.Push(new(_view, PageTitle, SelectedFolder, Tracks.ToArray(), Uploaders.ToArray(), _query, _page, HasMore, SearchCategoryIndex, EmptyText, SpaceProfile, _spaceUserId));
        if (view != "cache") { IsCacheSelectionMode = false; SelectedCacheCount = 0; }
        if(view!="library") StopFolderPreviews();
        IsSearchView = view == "search";
        _view = view; _browse?.Cancel(); _browse?.Dispose(); _browse = null; _browseVersion++;
        IsLibraryView = view == "library"; IsTracksView = view is "history" or "search" or "folder" or "playlist" or "cache" or "space";
        IsSpaceView = view == "space"; IsSpaceLoading = false;
        if (!IsSpaceView) SpaceProfile = null;
        IsFolderView = view == "folder"; IsPlaylistView = view == "playlist"; IsCacheView = view == "cache";
        IsSettingsView = view == "settings"; IsNotificationsView = view == "notifications"; IsHomeView = view == "home";
        IsHistoryView = view == "history";
        OnPropertyChanged(nameof(IsLocalView)); OnPropertyChanged(nameof(CanSelectCachedTracks));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(HasTrackContent));
        foreach (var property in new[] { nameof(IsHomeGridView), nameof(IsFolderGridView), nameof(IsFolderListView), nameof(IsTrackListVisible) }) OnPropertyChanged(property);
        OnPropertyChanged(nameof(CanChooseView));
        NotifyBrowseMode();
        HasMore = false; IsEmpty = false;
        SelectedTrack = null;
        if (view != "folder") SelectedFolder = null;
        switch (view)
        {
            case "home":
                PageTitle = "首页"; 
                Replace(Tracks, _homeCache); HasMore = _homeMore;
                IsEmpty = Tracks.Count == 0; EmptyText = "正在寻找推荐里的音乐…"; break;
            case "library": PageTitle = "收藏夹"; IsEmpty = Folders.Count == 0; EmptyText = "还没有收藏夹，可以新建一个。"; StartFolderPreviews(); break;
            case "search": PageTitle = "搜索"; Tracks.Clear(); EmptyText = "输入关键词，寻找想听的内容。"; IsEmpty = true; break;
            case "history":
                PageTitle = "最近播放"; 
                Replace(Tracks, Store.History(Client.Account!.Id)); IsEmpty = Tracks.Count == 0; EmptyText = "播放过的内容会出现在这里。"; break;
            case "playlist":
                PageTitle = "播放列表"; IsEmpty = Queue.Count == 0; EmptyText = "播放列表为空。"; break;
            case "cache": PageTitle = "下载"; RefreshCacheContent(); break;
            case "notifications":
                PageTitle = "通知中心"; IsEmpty = false; NotificationsStatus = "正在读取通知…";
                _ = RefreshNotificationsAsync(); break;
            case "settings": PageTitle = "设置"; break;
            case "space": PageTitle = "个人空间"; break;
        }
        Tracks.ResetLoading(); Uploaders.ResetLoading();
        OnPropertyChanged(nameof(ShowPageProgress));
        BrowseChanged?.Invoke();
    }
    [RelayCommand]
    private Task RefreshAsync() => SafeAsync(async () =>
    {
        Tracks.ResetLoading(); Uploaders.ResetLoading();
        if (_view == "home") { await LoadHomeAsync(true); }
        else if (_view == "space" && _spaceUserId > 0) await OpenSpaceAsync(_spaceUserId);
        else if (_view == "notifications") await RefreshNotificationsAsync();
        else if (_view == "folder" && SelectedFolder is { } folder) await BrowseFolderAsync(folder);
        else if (_view == "search" && _query.Length > 0) await SearchCoreAsync(false);
        else if (_view is "history" or "playlist" or "cache") Navigate(_view);
        else { await RefreshFoldersAsync(); if (_view == "library") Navigate("library"); }
    });
    [RelayCommand]
    private Task OpenHomeAsync() => SafeAsync(async () =>
    {
        if(!IsHomeView) Navigate("home");
        if (!_hasLoadedHome) await LoadHomeAsync(true);
    });
    public void SaveSidebarWidth() => Store.SetSetting("sidebarWidth", SidebarWidth.ToString(CultureInfo.InvariantCulture));
    public void SaveFolderOrder()
    {
        if (Client.Account is { } account)
            Store.SetSetting($"folderOrder:{account.Id}", JsonSerializer.Serialize(Folders.Select(folder => folder.Id).Distinct().ToArray()));
    }
    private void StopFolderPreviews()
    { _folderPreviews?.Cancel(); _folderPreviews?.Dispose(); _folderPreviews=null; }
    public void PauseFolderPreviews() { StopFolderPreviews(); _foldersVersion++; }
    private void StartFolderPreviews()
    {
        if(_disposed||!IsLibraryView||_folderPreviews is not null||Folders.Count==0||App.IsVisualCheck) return;
        _folderPreviews=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _=LoadFolderPreviewsAsync(Folders.ToArray(),_foldersVersion,_folderPreviews.Token);
    }
    public async Task RefreshFoldersAsync()
    {
        if(DebugSession.IsActive)return;
        StopFolderPreviews();
        var version = ++_foldersVersion;
        var account = Client.Account!.Id;
        var folders = await Client.GetFoldersAsync(_lifetime.Token);
        if (version != _foldersVersion || Client.Account?.Id != account) return;
        long[] saved;
        try { saved = JsonSerializer.Deserialize<long[]>(Store.GetSetting($"folderOrder:{account}", "[]")) ?? []; }
        catch (JsonException) { saved = []; }
        folders = FolderOrder.Merge(folders, saved);
        Replace(Folders, folders);
        SaveFolderOrder();
        if (SelectedFolder is { } selected) SelectedFolder = folders.FirstOrDefault(f => f.Id == selected.Id);
        StartFolderPreviews();
    }
    private async Task LoadFolderPreviewsAsync(IReadOnlyList<Folder> folders, int version, CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(2);
        await Task.WhenAll(folders.Select(async folder =>
        {
            try
            {
                await slots.WaitAsync(ct);
                try
                {
                    var preview = await Client.GetFolderPreviewAsync(folder, ct);
                    if (_disposed || version != _foldersVersion || ct.IsCancellationRequested) return;
                    var index = Folders.ToList().FindIndex(f => f.Id == folder.Id);
                    if (index >= 0) Folders[index].ApplyPreview(preview);
                    if (SelectedFolder?.Id == folder.Id) SelectedFolder.ApplyPreview(preview);
                }
                finally { slots.Release(); }
            }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException or BilibiliException or System.Text.Json.JsonException) { /* Keep server list metadata when a preview is unavailable. */ }
        }));
    }
    [RelayCommand]
    private Task OpenFolderAsync(Folder? folder) => folder is null ? Task.CompletedTask : SafeAsync(() => BrowseFolderAsync(folder));
    private async Task BrowseFolderAsync(Folder folder)
    {
        Navigate("folder"); SelectedFolder = folder;
        PageTitle = folder.Title; 
        if(DebugSession.IsActive){Replace(Tracks,_homeCache);HasMore=false;IsEmpty=Tracks.Count==0;return;}
        Tracks.Clear(); _page = 1;
        _browse = new(); var version = _browseVersion;
        var result = await Client.GetFolderTracksAsync(folder.Id, 1, _browse.Token);
        if (version != _browseVersion) return;
        Append(Tracks, result.Items); HasMore = result.HasMore;
        IsEmpty = Tracks.Count == 0; EmptyText = "这个收藏夹还没有可播放的视频。";
    }
    [RelayCommand]
    private Task SearchAsync() => SafeAsync(() => SearchCoreAsync());
    private async Task SearchCoreAsync(bool remember = true)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) { Navigate("search"); return; }
        var text = SearchText.Trim();
        if(DebugSession.IsActive){Navigate("search");Replace(Tracks,_homeCache.Where(t=>t.Title.Contains(text,StringComparison.OrdinalIgnoreCase)||t.Uploader.Contains(text,StringComparison.OrdinalIgnoreCase)));IsEmpty=Tracks.Count==0;return;}
        if (remember) Store.RememberSearch(Client.Account!.Id, text);
        Replace(SearchHistory, Store.SearchHistory(Client.Account!.Id));
        if (remember) Navigate("search");
        else { _browse?.Cancel(); _browse?.Dispose(); _browseVersion++; Tracks.Clear(); }
        Tracks.ResetLoading(); Uploaders.ResetLoading();
        Uploaders.Clear(); _query = text; _page = 1; _browse = new(); HasMore = false; IsEmpty = false;
        var version = _browseVersion;
        PageTitle = "搜索结果"; 
        if (SearchCategoryIndex == 1)
        {
            var users = await Client.SearchUploadersAsync(text, 1, _browse.Token);
            if (version != _browseVersion) return;
            Replace(Uploaders, users.Items); HasMore = users.HasMore;
            IsEmpty = Uploaders.Count == 0; EmptyText = "没有找到相关 UP 主。"; return;
        }
        if (Text.ParseVideoInput(text) is { } parsed)
        {
            var video = await Client.GetVideoAsync(parsed.Bvid, ct: _browse.Token);
            if (version != _browseVersion) return;
            Append(Tracks, parsed.Page == 1 ? video.Parts : video.Parts.Where(t => t.Page == parsed.Page));
        }
        else
        {
            if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请粘贴完整的 bilibili.com/video/BV… 链接或 BV 号。");
            var result = await Client.SearchPreferredAsync(text, 1, _browse.Token);
            if (version != _browseVersion) return;
            Append(Tracks, result.Items); HasMore = result.HasMore;
        }
        IsEmpty = Tracks.Count == 0; EmptyText = "没有找到相关视频，试试其他关键词。";
    }
    [RelayCommand]
    private Task LoadMoreAsync() => SafeAsync(async () =>
    {
        if (!HasMore) return;
        if (_view == "home")
        {
            var count = Tracks.Count;
            for (var attempt = 0; attempt < 3 && HasMore && Tracks.Count == count; attempt++) await LoadHomeAsync();
            return;
        }
        var version = _browseVersion; var next = _page + 1;
        if (IsSearchUploaders)
        {
            var users = await Client.SearchUploadersAsync(_query, next, _browse?.Token ?? default);
            if (version != _browseVersion) return;
            foreach (var user in users.Items) if (!Uploaders.Any(u => u.Id == user.Id)) Uploaders.Add(user);
            _page = next; HasMore = users.HasMore; return;
        }
        var result = _view == "folder" && SelectedFolder is { } folder
            ? await Client.GetFolderTracksAsync(folder.Id, next, _browse?.Token ?? default)
            : _view == "space" && SpaceProfile is { } profile ? await Client.GetUserWorksAsync(profile.Id, next, _browse?.Token ?? default)
            : await Client.SearchAsync(_query, next, _browse?.Token ?? default);
        if (version != _browseVersion) return;
        _page = next;
        // A repeated page adds nothing new; the server's has_more cannot be trusted past
        // that point, so stop paging instead of re-requesting the same content forever.
        var added = Append(Tracks, result.Items);
        HasMore = result.HasMore && added > 0;
        if (_view == "search")
        {
            var ranked = PreferredUploaders.RankSearch(Tracks, _query);
            for (var i = 0; i < ranked.Count; i++)
            {
                var index = Tracks.IndexOf(ranked[i]);
                if (index != i) Tracks.Move(index, i);
            }
        }
    });
    [ObservableProperty] public partial int SingleClickBehaviorIndex { get; set; }
    [ObservableProperty] public partial int DoubleClickBehaviorIndex { get; set; } = 1;
    private int ReadTrackBehavior(string key, int fallback) => int.TryParse(Store.GetSetting(key, fallback.ToString()), out var value)
        && value is >= 0 and <= 2 ? value : fallback;
    partial void OnSingleClickBehaviorIndexChanged(int value) => Store.SetSetting("singleClickBehavior", Math.Clamp(value, 0, 2).ToString());
    partial void OnDoubleClickBehaviorIndexChanged(int value) => Store.SetSetting("doubleClickBehavior", Math.Clamp(value, 0, 2).ToString());

    [RelayCommand]
    private Task PlayAllAsync() => IsPlaylistView ? (Queue.FirstOrDefault() is { } first ? PlayQueueAsync(first) : Task.CompletedTask)
        : Tracks.Count == 0 ? Task.CompletedTask : SelectPlaybackAsync(Tracks[0], true, Queue.Count > 0 ? TrackPlayBehavior.Ask : TrackPlayBehavior.Append);
    [RelayCommand]
    private Task PlayAsync(Track? track) => ActivateTrackAsync(track ?? SelectedTrack ?? (IsPlaylistView ? Queue.FirstOrDefault() : Tracks.FirstOrDefault()), false);
    public Task ActivateTrackAsync(Track? track, bool doubleClick, bool fromQueue = false) => track is null ? Task.CompletedTask
        : fromQueue || IsPlaylistView ? PlayQueueAsync(track) : SelectPlaybackAsync(track, false,
        (TrackPlayBehavior)(doubleClick ? DoubleClickBehaviorIndex : SingleClickBehaviorIndex));

    private Task SelectPlaybackAsync(Track? selected, bool playAll, TrackPlayBehavior behavior) => SafeAsync(async () =>
    {
        if (selected is null || _disposed || Client.Account is not { } account) return;
        var requestedVersion = _selectionVersion;
        var view = _view; var folder = SelectedFolder;
        var fromFavorites = view == "folder" && folder is not null;
        var browseEntries = Tracks.ToArray(); var browseMore = HasMore; var browsePage = _page;
        var choice = QueuePolicy.AutomaticChange(behavior)
            ?? (ConfirmQueueChange is null ? QueueChange.Cancel : await ConfirmQueueChange(fromFavorites ? folder!.Title : selected.Title));
        // Only Ask prompts, before any queue or audio change. Saved Append/Replace applies in every view.
        if (requestedVersion != _selectionVersion || choice == QueueChange.Cancel || Client.Account?.Id != account.Id) return;
        var selection = ++_selectionVersion;
        selected = Store.ReadTracks($"downloads:{account.Id}").FirstOrDefault(t => QueueSelection.Same(t, selected) && DownloadService.IsCached(account.Id, t)) ?? selected;
        var provisional = !Queue.Any(item => QueueSelection.Same(item, selected));
        if (choice == QueueChange.Replace)
        {
            _restoringQueue = true;
            try { Replace(Queue, new[] { selected }); }
            finally { _restoringQueue = false; }
        }
        _playVersion++; ShowTrackLoading(selected);
        try
        {
            Video? video = null;
            var isCached = DownloadService.IsCached(account.Id, selected);
            if (!DebugSession.IsActive && (IsOnline || !isCached))
            {
                try { video = await Client.GetVideoAsync(selected.Bvid, selected.Aid, _lifetime.Token); }
                catch (Exception error) when (isCached && error is not OperationCanceledException) { /* Keep offline audio playable. */ }
            }
            if (selection != _selectionVersion) return;
            var track = video?.Parts.FirstOrDefault(t => t.Page == selected.Page) ?? selected;
            Track[] entries;
            if (fromFavorites || playAll) entries = browseEntries;
            else if (video?.Parts.Count > 1) entries = video.Parts.ToArray();
            else if (video?.Collection is { Count: > 1 } collection) entries = collection.ToArray();
            else if (selected.PartCount > 1)
                entries = Store.ReadTracks($"downloads:{account.Id}").Where(t => t.Bvid == selected.Bvid && DownloadService.IsCached(account.Id, t)).OrderBy(t => t.Page).ToArray();
            else entries = [track];
            // Fetch the entire chosen folder after approval, rather than only its first visible page.
            if (fromFavorites && browseMore)
            {
                var full = entries.ToList(); var page = browsePage + 1; var more = true;
                while (more)
                {
                    var next = await Client.GetFolderTracksAsync(folder!.Id, page++, _lifetime.Token);
                    foreach (var item in next.Items) if (!full.Any(t => QueueSelection.Same(t, item))) full.Add(item);
                    more = next.HasMore && next.Items.Count > 0;
                    if (selection != _selectionVersion) return;
                }
                entries = full.ToArray();
            }
            _restoringQueue = true;
            try
            {
                var basis = provisional ? Queue.Where(item => !QueueSelection.Same(item, selected)).ToArray() : Queue.ToArray();
                Replace(Queue, QueuePolicy.Apply(basis, entries, choice == QueueChange.Replace, track));
            }
            finally { _restoringQueue = false; }
            Store.WriteTracks($"queue:{account.Id}", Queue);
            UpdateCurrentDisplay(track);
            await PlayTrackAsync(track);
        }
        catch (OperationCanceledException) when (selection != _selectionVersion) { }
        finally { if (selection == _selectionVersion) IsPlaybackLoading = false; }
    }, false);
    private void ShowTrackLoading(Track track)
    {
        SaveProgress();
        _restoringQueue = true;
        try { Player.Stop(); if (!Queue.Any(item => QueueSelection.Same(item, track))) Queue.Add(track); }
        finally { _restoringQueue = false; }
        UpdateCurrentDisplay(track); IsPlaybackLoading = true;
        Store.WriteTracks($"queue:{Client.Account!.Id}", Queue);
    }
    public Task PlayQueueAsync(Track track) => SafeAsync(async () =>
    {
        _selectionVersion++;
        if (!Queue.Any(t => QueueSelection.Same(t, track))) Queue.Add(track);
        await PlayTrackAsync(track);
    }, false);
    private void SynchronizeQueue()
    {
        var selected = QueueSelection.Resolve(Queue, CurrentTrack, _queueFallbackIndex);
        _queueFallbackIndex = 0;
        var selectionChanged = !QueueSelection.Same(selected, CurrentTrack);
        var engineRemoved = Player.Current is { } playing && !Queue.Any(t => QueueSelection.Same(t, playing));
        var wasPlaying = Player.Playing;
        if (engineRemoved || selectionChanged || selected is null)
        {
            _playVersion++; _subtitles?.Cancel(); SaveProgress(); Player.Stop();
            IsSeeking = false; Position = 0;
        }
        UpdateCurrentDisplay(selected);
        if ((engineRemoved || selectionChanged) && wasPlaying && selected is not null) _ = SafeAsync(() => PlayTrackAsync(selected), false);
    }
    private async Task PlayTrackAsync(Track track, double startPosition = 0)
    {
        var version = ++_playVersion;
        ShowTrackLoading(track);
        try
        {
            if (Client.Account is { } cachedAccount)
            {
                var cached = Store.ReadTracks($"downloads:{cachedAccount.Id}").FirstOrDefault(t => t.Bvid == track.Bvid && t.Page == track.Page && DownloadService.IsCached(cachedAccount.Id, t));
                if (cached is not null) track = cached;
            }
            if (track.Cid == 0 && string.IsNullOrEmpty(track.LocalFile))
            {
                var video = await Client.GetVideoAsync(track.Bvid, track.Aid);
                if (version != _playVersion) return;
                var resolved = video.Parts.FirstOrDefault(p => p.Page == track.Page) ?? video.Parts[0];
                var index = Queue.ToList().FindIndex(t => QueueSelection.Same(t, track));
                track = resolved;
                if (index >= 0)
                {
                    _restoringQueue = true;
                    try { if (Queue[index] != track) Queue[index] = track; } finally { _restoringQueue = false; }
                    Store.WriteTracks($"queue:{Client.Account!.Id}", Queue);
                        }
            }
            track = Store.ApplyMetadata(Client.Account!.Id, track);
            if (version != _playVersion || !Queue.Any(t => QueueSelection.Same(t, track))) return;
            UpdateCurrentDisplay(track);
            await Player.PlayAsync(track, startPosition);
            if (version != _playVersion) return;
            Store.SaveProgress(Client.Account.Id, track, 0);
        }
        catch (OperationCanceledException) when (version != _playVersion) { }
        finally { if (version == _playVersion) IsPlaybackLoading = false; }
    }
    private bool _settingAutomaticSubtitle;
    private bool _subtitleManuallySelected;
    private void SetAutomaticSubtitle(SubtitleTrack? track)
    {
        _settingAutomaticSubtitle = true;
        try { SelectedSubtitle = track; }
        finally { _settingAutomaticSubtitle = false; }
    }
    private void PrepareSubtitles(Track? track, bool force = false)
    {
        var same = QueueSelection.Same(_subtitleTrack, track);
        if (same && !force) return;
        _subtitleDiscovery?.Cancel(); _subtitleDiscovery?.Dispose(); _subtitleDiscovery = null;
        _subtitleTrack = track;
        if (!same)
        {
            _subtitles?.Cancel(); _subtitleManuallySelected = false; SetAutomaticSubtitle(null);
            Lines.Clear(); SubtitleTracks.Clear(); ActiveSubtitleIndex = -1;
        }
        if (track is null) { SubtitleStatus = ""; return; }
        if (Lines.Count == 0) SubtitleStatus = "正在加载字幕…";
        var load = _subtitleDiscovery = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        load.CancelAfter(TimeSpan.FromSeconds(20));
        _ = LoadSubtitleTracksAsync(track, load);
    }
    private Track SubtitleCacheTrack(Track track) => !string.IsNullOrEmpty(track.LocalFile) ? track
        : Client.Account is { } account ? Store.ReadTracks($"downloads:{account.Id}")
            .FirstOrDefault(item => QueueSelection.Same(item, track) && DownloadService.IsCached(account.Id, item)) ?? track : track;
    private async Task LoadSubtitleTracksAsync(Track track, CancellationTokenSource load)
    {
        var token = load.Token;
        bool IsCurrent() => !_disposed && ReferenceEquals(_subtitleDiscovery, load) && QueueSelection.Same(CurrentTrack, track);
        try
        {
            var cacheTrack = SubtitleCacheTrack(track);
            var cached = SubtitleCache.Read(cacheTrack);
            if (cached.Count > 0)
            {
                var preferred = _subtitleManuallySelected ? SelectedSubtitle : null;
                Replace(SubtitleTracks, cached.Select(x => x.Track));
                SetAutomaticSubtitle(SubtitleTracks.FirstOrDefault(t => t.Language == preferred?.Language && t.IsAuto == preferred.IsAuto)
                    ?? SubtitleSelection.Choose(SubtitleTracks, track.Title));
            }
            // Isolated native rendering never uses the user's session or live subtitle endpoints.
            if (!IsOnline || App.IsVisualCheck) { if (cached.Count == 0) SubtitleStatus = "暂无离线字幕"; return; }
            if (track.Cid <= 0 || track.Aid <= 0)
            {
                var video = await Client.GetVideoAsync(track.Bvid, track.Aid, token);
                token.ThrowIfCancellationRequested();
                var part = video.Parts.FirstOrDefault(item => item.Page == track.Page);
                if (part is null) throw new InvalidDataException("未找到该分 P 的字幕信息。");
                track = part;
            }
            var subtitles = await Client.GetSubtitleTracksAsync(track, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent()) return;
            if (!string.IsNullOrEmpty(cacheTrack.LocalFile) && subtitles.Count > 0)
                await SubtitleCache.RefreshAsync(Client, cacheTrack, token, subtitles);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent()) return;
            // Keep usable cached lyrics visible when the online response has no available tracks.
            if (subtitles.Count == 0 && cached.Count > 0) { SubtitleStatus = "离线字幕"; return; }
            var selection = _subtitleManuallySelected ? SelectedSubtitle : null;
            Replace(SubtitleTracks, subtitles);
            SubtitleStatus = subtitles.Count > 0 ? "选择字幕轨道" : "暂无字幕";
            SetAutomaticSubtitle(null);
            SetAutomaticSubtitle(subtitles.FirstOrDefault(t => t.Language == selection?.Language && t.IsAuto == selection.IsAuto)
                ?? SubtitleSelection.Choose(subtitles, track.Title));
        }
        catch (OperationCanceledException)
        { if (IsCurrent()) SubtitleStatus = Lines.Count > 0 ? "离线字幕" : "字幕加载超时，可点击重试"; }
        catch (Exception)
        { if (IsCurrent()) SubtitleStatus = Lines.Count > 0 ? "离线字幕" : "字幕暂时无法加载，可点击重试"; }
    }
    partial void OnSelectedSubtitleChanged(SubtitleTrack? value)
    {
        if (!_settingAutomaticSubtitle && value is not null) _subtitleManuallySelected = true;
        _ = LoadSelectedSubtitleAsync(value);
    }
    private async Task LoadSelectedSubtitleAsync(SubtitleTrack? subtitle)
    {
        _subtitles?.Cancel(); _subtitles?.Dispose();
        if (subtitle is null) { _subtitles = null; return; }
        var cts = _subtitles = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var track = CurrentTrack;
        Lines.Clear(); ActiveSubtitleIndex = -1;
        SubtitleStatus = "正在加载字幕…";
        try
        {
            var cached = track is { } current ? SubtitleCache.Read(SubtitleCacheTrack(current)).FirstOrDefault(x => x.Track.Language == subtitle.Language && x.Track.IsAuto == subtitle.IsAuto) : null;
            if (cached is not null) { Replace(Lines, cached.Lines); SubtitleStatus = subtitle.DisplayName; }
            if (!IsOnline || App.IsVisualCheck) { if (cached is null) SubtitleStatus = "暂无离线字幕"; return; }
            var lines = await Client.GetSubtitlesAsync(subtitle, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_subtitles, cts) || !QueueSelection.Same(CurrentTrack, track)) return;
            Replace(Lines, lines);
            SubtitleStatus = lines.Count > 0 ? subtitle.DisplayName : "暂无字幕";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception) { if (ReferenceEquals(_subtitles, cts)) SubtitleStatus = Lines.Count > 0 ? subtitle.DisplayName + " · 离线字幕" : "字幕暂时无法加载，可点击重试"; }
    }
    [RelayCommand]
    private void RetrySubtitles()
    {
        if (CurrentTrack is { } track) PrepareSubtitles(track, force: true);
    }
    [RelayCommand] private Task TogglePlayAsync() => SafeAsync(async () =>
    {
        var track = QueueSelection.Resolve(Queue, CurrentTrack);
        if (track is null) return;
        if (QueueSelection.Same(track, Player.Current)) Player.Toggle();
        else await PlayTrackAsync(track, _previewPosition);
    }, false);
    public void Seek(double seconds)
    {
        if (CurrentTrack is null || !double.IsFinite(seconds)) return;
        if (QueueSelection.Same(CurrentTrack, Player.Current)) Player.Seek(seconds);
        else _previewPosition = Math.Clamp(seconds, 0, Math.Max(0, CurrentTrack.Duration));
        Position = Math.Clamp(seconds, 0, Math.Max(0, Duration));
    }
    [RelayCommand] private void Rewind() => Seek(Position - 10);
    [RelayCommand] private void FastForward() => Seek(Position + 10);
    [RelayCommand] private Task NextAsync() => SafeAsync(() => MoveAsync(true, false), false);
    [RelayCommand] private Task PreviousAsync() => SafeAsync(() => MoveAsync(false, false), false);
    private async Task MoveAsync(bool next, bool ended)
    {
        _selectionVersion++;
        if (Queue.Count == 0) return;
        if (!next && Player.Position > 3) { Player.Seek(0); return; }
        if (ended && RepeatMode == 2) { Player.Seek(0); Player.Resume(); return; }
        var current = CurrentTrack;
        var index = current is null ? -1 : Queue.ToList().FindIndex(t => t.Bvid == current.Bvid && (t.Cid == 0 || t.Cid == current.Cid));
        int target;
        if (IsShuffle && Queue.Count > 1)
        {
            target = Random.Shared.Next(Queue.Count - 1);
            if (target >= index && index >= 0) target++;
        }
        else target = index + (next ? 1 : -1);
        if (target < 0) target = Queue.Count - 1;
        if (target >= Queue.Count)
        {
            if (RepeatMode == 0 && ended) { SaveProgress(); return; }
            target = 0;
        }
        await PlayTrackAsync(Queue[target]);
    }
    [RelayCommand]
    private void Enqueue(Track? track)
    {
        if (track is null) return;
        Queue.Add(track); Notify("已加入播放列表。", NoticeLevel.Info);
    }
    [RelayCommand]
    private void PlayNext(Track? track)
    {
        if (track is null) return;
        var i = CurrentTrack is { } current ? Queue.ToList().FindIndex(t => t.Bvid == current.Bvid && (t.Cid == 0 || t.Cid == current.Cid)) : -1;
        Queue.Insert(Math.Max(0, i + 1), track); Notify("已设为下一首播放。", NoticeLevel.Info);
    }
    [RelayCommand]
    private void RemoveQueue(Track? track)
    {
        if (track is null) return;
        _selectionVersion++; _queueFallbackIndex = Math.Max(0, Queue.IndexOf(track)); Queue.Remove(track);
    }
    public Task RunAsync(Func<Task> action) => SafeAsync(action);
    private async Task SafeAsync(Func<Task> action, bool busy = true)
    {
        if (_disposed) return;
        if (busy) { _busyOperations++; IsBusy = true; }
        int? operationVersion = null;
        try
        {
            var operation = action();
            if (busy)
            {
                operationVersion = _browseVersion;
                _pageOperations[_browseVersion] = _pageOperations.GetValueOrDefault(_browseVersion) + 1;
                OnPropertyChanged(nameof(ShowPageProgress));
            }
            await operation;
        }
        catch (OperationCanceledException e) when (_disposed || e.CancellationToken.IsCancellationRequested && e.InnerException is not TimeoutException) { }
        catch (OperationCanceledException) { Notify("请求超时或已取消，请重试。若刚才修改了收藏夹，请先刷新确认结果。", NoticeLevel.Warn); }
        catch (BilibiliException e) when (e.RequiresLogin)
        {
            NeedsLogin = true;
            Notify("登录已过期，请点击右上角头像重新登录。下载和播放列表仍然可用。", NoticeLevel.Warn);
        }
        catch (Exception e) { Notify(Friendly(e)); }
        finally
        {
            if (operationVersion is { } version)
            {
                if (_pageOperations[version] <= 1) _pageOperations.Remove(version); else _pageOperations[version]--;
                OnPropertyChanged(nameof(ShowPageProgress));
            }
            if (busy) { _busyOperations--; IsBusy = _busyOperations > 0; }
        }
    }
    public void Notify(string text, NoticeLevel severity = NoticeLevel.Error)
    {
        if(_disposed) return;
        if (severity != NoticeLevel.Info) text = Feedback.WithRequestHint(text);
        var item = new ToastNotice(text, severity); Notifications.Add(item);
        foreach (var old in Notifications.Take(Math.Max(0, Notifications.Count - 4)).ToArray()) DismissNotice(old);
        _ = ExpireNoticeAsync(item);
    }
    private async Task ExpireNoticeAsync(ToastNotice item)
    {
        try { await Task.Delay(Feedback.Lifetime(item.Severity), _lifetime.Token); if (!_disposed) DismissNotice(item); }
        catch (OperationCanceledException) { }
    }
    public void DismissNotice(ToastNotice item) => _ = DismissNoticeAsync(item);
    private async Task DismissNoticeAsync(ToastNotice item)
    {
        if (!Notifications.Contains(item) || !_closingNotices.Add(item)) return;
        try { if (AnimateNoticeOut is { } animate) await animate(item); }
        finally
        {
            Notifications.Remove(item); _closingNotices.Remove(item);
        }
    }
    public void DeleteSearchHistory(string query)
    { if (Client.Account is { } account) { Store.DeleteSearch(account.Id, query); SearchHistory.Remove(query); } }
    public async Task ReauthenticateAsync()
    {
        if(DebugSession.IsActive)return;
        _selectionVersion++; CancelDownloads(); SaveProgress(); _playVersion++; Player.Stop(); ShowQueue = false; SessionVault.Clear(); Client.ClearSession(); NeedsLogin = false; IsLoggedIn = false;
        await LoginAsync();
    }
    [RelayCommand]
    private Task DownloadCurrentAsync() => CurrentTrack is { } track ? DownloadAsync(track) : Task.CompletedTask;
    public bool IsTrackDownloaded(Track track) => Client.Account is { } account &&
        (DownloadService.IsCached(account.Id,track) || Store.ReadTracks($"downloads:{account.Id}")
            .Any(item=>QueueSelection.Same(item,track)&&DownloadService.IsCached(account.Id,item)));
    public Task DownloadAsync(Track track) => SafeAsync(async () =>
    {
        if(DebugSession.IsActive){Notify("离线调试不下载真实音源。",NoticeLevel.Info);return;}
        var account = Client.Account!.Id;
        if (Store.ReadTracks($"downloads:{account}").Any(item => QueueSelection.Same(item, track) && DownloadService.IsCached(account, item)))
        { Notify("这首音乐已经下载，可在下载页面查看。", NoticeLevel.Info); return; }
        if (Downloads.Any(job => job.Account == account && QueueSelection.Same(job.Track, track)))
        { Notify("这个下载任务已经在列表中。", NoticeLevel.Info); return; }
        var options = RequestDownloadOptions is null ? null : await RequestDownloadOptions(track);
        if (options is null || Client.Account?.Id != account) return;
        if (Downloads.Any(job => job.Account == account && QueueSelection.Same(job.Track, options.Track))) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountDownloads.Token);
        var job = new DownloadJob(options.Track, account, CacheDirectory, cancellation, options);
        Downloads.Add(job);
        job.Completion = RunDownloadAsync(job);
        await job.Completion;
    }, false);
    private async Task RunDownloadAsync(DownloadJob job)
    {
        var token = job.Cancellation.Token;
        Track? created = null;
        try
        {
            await _downloadSlots.WaitAsync(token);
            try
            {
                job.Status = "正在下载";
                var progress = new Progress<double>(value =>
                {
                    if (job.IsCancelling || job.Status == "正在下载字幕") return;
                    job.Progress = value; job.Status = value < 70 ? "正在下载" : "正在转码";
                });
                var downloaded = await new DownloadService().DownloadAsync(Client, job.Account, job.Track, progress, token, job.Directory, committed: value => created = value, options: job.Options);
                token.ThrowIfCancellationRequested();
                if (IsOnline)
                {
                    job.Status = "正在下载字幕"; job.Progress = 100;
                    try { await SubtitleCache.RefreshAsync(Client, downloaded, token); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { Notify("音频已下载，字幕暂时未能同步，下次联网播放时会重试。", NoticeLevel.Warn); }
                }
                token.ThrowIfCancellationRequested();
                var items = Store.ReadTracks($"downloads:{job.Account}").Where(t => !QueueSelection.Same(t, downloaded)).Append(downloaded).ToArray();
                Store.WriteTracks($"downloads:{job.Account}", items);
                RefreshCacheContent(); Notify("音乐下载完成：" + downloaded.Title, NoticeLevel.Info);
            }
            finally { _downloadSlots.Release(); }
        }
        catch
        {
            if (created is not null) await DownloadService.DeleteCachedFilesAsync(created);
            throw;
        }
        finally
        {
            Downloads.Remove(job); job.Cancellation.Dispose();
        }
    }
    public async Task CancelDownloadAsync(DownloadJob job)
    {
        if (!Downloads.Contains(job) || job.IsCancelling) return;
        job.IsCancelling = true; job.Status = "正在取消并清理文件";
        job.Cancellation.Cancel();
        try { await job.Completion; } catch (OperationCanceledException) { }
        Notify("已取消下载并清理未完成文件。", NoticeLevel.Info);
    }
    public Task DeleteCachedAsync(Track track, bool notify = true) => SafeAsync(async () =>
    {
        if (Client.Account is not { } account) return;
        var cached = Store.ReadTracks($"downloads:{account.Id}").FirstOrDefault(item => QueueSelection.Same(item, track));
        if (cached is null) return;
        if (Player.Current?.LocalFile == cached.LocalFile)
        { _selectionVersion++; _playVersion++; SaveProgress(); Player.Stop(); }
        await DownloadService.DeleteCachedFilesAsync(cached);
        Store.WriteTracks($"downloads:{account.Id}", Store.ReadTracks($"downloads:{account.Id}").Where(item => !QueueSelection.Same(item, cached)));
        _restoringQueue = true;
        try
        {
            for (var i = 0; i < Queue.Count; i++)
                if (string.Equals(Queue[i].LocalFile, cached.LocalFile, StringComparison.OrdinalIgnoreCase)) Queue[i] = Queue[i] with { LocalFile = "" };
        }
        finally { _restoringQueue = false; }
        Store.WriteTracks($"queue:{account.Id}", Queue); SynchronizeQueue(); RefreshCacheContent();
        if (notify) Notify("已删除下载音乐及字幕。", NoticeLevel.Info);
    }, false);
    private void CancelDownloads()
    { _accountDownloads.Cancel(); _accountDownloads.Dispose(); _accountDownloads = new(); }
    [RelayCommand(CanExecute = nameof(CanClearQueue))]
    private void ClearQueue()
    {
        _selectionVersion++; Queue.Clear();
    }
    private static string Friendly(Exception e) => e switch
    {
        HttpRequestException => "网络请求失败，请检查网络连接后重试。",
        TaskCanceledException => "请求超时，请稍后重试。写入收藏夹时如已提交，请先刷新确认结果。",
        System.Text.Json.JsonException => "B 站返回了无法识别的数据，请稍后重试。",
        BilibiliException { Code: -352 or -403 or -412 or 412 or 429 } => e.Message + " " + Feedback.RequestHint,
        BilibiliException or InvalidOperationException or InvalidDataException => e.Message,
        _ => "操作未完成，请重试。"
    };
    private void OnPlaybackChanged()
    {
        if (_disposed || _restoringQueue) return;
        UpdateCurrentDisplay(QueueSelection.Resolve(Queue, Player.Current ?? CurrentTrack));
    }
    private void UpdateCurrentDisplay(Track? track)
    {
        var changed = !QueueSelection.Same(CurrentTrack, track) || CurrentTrack != track;
        CurrentTrack = track;
        if (track is null) { IsLyricsOpen = false; IsPlaybackLoading = false; }
        if (Player.Current is null) Player.PrepareSystemSession(track);
        CurrentTitle = track?.Title ?? "暂无播放歌曲"; CurrentArtist = track?.Uploader ?? "";
        CurrentQuality = track is null ? "" : QueueSelection.Same(track, Player.Current) ? Player.SelectedQuality : track.LocalFile.Length > 0 ? "MP3" : "标准";
        IsQualitySwitching = track is not null && QueueSelection.Same(track, Player.Current) && Player.IsQualitySwitching;
        if (changed)
        {
            _previewPosition = 0; IsSeeking = false; Position = 0;
            IsCurrentFavorited = false; _favoriteVersion++; _favoriteLoading = false;
            _ = RefreshFavoriteStateAsync();
            _currentUploader = 0; IsFollowingUploader = false; CanFollowUploader = false;
            _ = LoadUploaderRelationAsync(track);
            RefreshCurrentArtwork();
            if (track is not null && Client.Account is { Id: > 0 } account)
                Store.SetSetting($"queueCurrent:{account.Id}", $"{track.Bvid}:{track.Page}");
        }
        PrepareSubtitles(track);
        PlayGlyph = Player.Playing ? "\uE769" : "\uE768"; PlayLabel = Player.Playing ? "暂停" : "播放";
        var duration = QueueSelection.Same(track, Player.Current) && Player.Duration > 0 ? Player.Duration : track?.Duration ?? 0;
        Duration = Math.Max(1, duration);
        RemainingText = "−" + Text.FormatTime(Math.Max(0, duration - Position));
        UpdateSubtitleHighlight();
    }
    private void RefreshCurrentArtwork()
    {
        CurrentCover = Uri.TryCreate(Text.CoverImage(CurrentTrack?.Cover ?? "", 512), UriKind.Absolute, out var cover) ? ArtworkCache.Create(cover,512) : null;
        LyricsCover = Uri.TryCreate(CurrentTrack?.Artwork, UriKind.Absolute, out var artwork) ? ArtworkCache.Create(artwork,960) : null;
    }
    private void RefreshAccountArtwork()
    {
        AccountAvatar = Uri.TryCreate(Client.Account?.Avatar, UriKind.Absolute, out var avatar) ? ArtworkCache.CreateAvatar(avatar,80) : null;
        AccountLargeAvatar = avatar is not null ? ArtworkCache.CreateAvatar(new UriBuilder(avatar) { Path = avatar.AbsolutePath.Split('@')[0] + "@320w_320h_1c.webp" }.Uri,320) : null;
    }
    public async Task RefreshFavoriteStateAsync()
    {
        if (App.IsVisualCheck || CurrentTrack is not { Aid: > 0 } track || Client.Account is not { } account) return;
        var version = ++_favoriteVersion;
        _favoriteLoading = true; _favoriteChecked = DateTimeOffset.UtcNow;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var favorited = await Client.IsFavoritedAsync(track.Aid, timeout.Token);
            if (!_disposed && version == _favoriteVersion && CurrentTrack?.Aid == track.Aid && Client.Account?.Id == account.Id)
                IsCurrentFavorited = favorited;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or BilibiliException or JsonException) { }
        finally { if (version == _favoriteVersion) _favoriteLoading = false; }
    }
    private async Task LoadUploaderRelationAsync(Track? track)
    {
        if (App.IsVisualCheck || track is null || Client.Account is null) return;
        try
        {
            var uploader = track.UploaderId;
            if (uploader <= 0) uploader = (await Client.GetVideoAsync(track.Bvid, track.Aid)).Track.UploaderId;
            if (uploader <= 0 || uploader == Client.Account?.Id || CurrentTrack != track) return;
            var follows = await Client.IsFollowingAsync(uploader);
            if (CurrentTrack != track) return;
            _currentUploader = uploader; IsFollowingUploader = follows; CanFollowUploader = true;
        }
        catch (Exception e) when (e is HttpRequestException or BilibiliException or OperationCanceledException or InvalidDataException) { }
    }
    private long _currentUploader;
    public void ApplyUnfollowedUploader(long user) { if (_currentUploader == user) IsFollowingUploader = false; }
    [RelayCommand]
    private Task FollowUploaderAsync() => SafeAsync(async () =>
    {
        if (!CanFollowUploader) return;
        var uploader = _currentUploader; var follow = !IsFollowingUploader;
        CanFollowUploader = false;
        try
        {
            await Client.FollowAsync(uploader, follow);
            if (_currentUploader == uploader) IsFollowingUploader = follow;
        }
        finally { if (_currentUploader == uploader && CurrentTrack is not null) CanFollowUploader = true; }
    }, false);
    private void UpdateSubtitleHighlight()
    {
        var playingCurrent = QueueSelection.Same(CurrentTrack, Player.Current) && Player.Playing;
        ActiveSubtitleIndex = Subtitles.ActiveIndex(Lines, Position, CurrentTrack is not null && !playingCurrent);
        FollowSubtitleIndex = playingCurrent ? Subtitles.FollowIndex(Lines, Position) : ActiveSubtitleIndex;
    }
    private void Tick()
    {
        if (_disposed) return;
        if (!_favoriteLoading && CurrentTrack is not null && DateTimeOffset.UtcNow - _favoriteChecked > TimeSpan.FromSeconds(30))
            _ = RefreshFavoriteStateAsync();
        var active = QueueSelection.Same(CurrentTrack, Player.Current);
        var duration = active && Player.Duration > 0 ? Player.Duration : CurrentTrack?.Duration ?? 0;
        Duration = Math.Max(1, duration);
        if (!IsSeeking) Position = active ? Player.Position : _previewPosition;
        PositionText = Text.FormatTime(Position);
        RemainingText = "−" + Text.FormatTime(Math.Max(0, duration - Position));
        UpdateSubtitleHighlight();
        if (++_saveTicks >= 100)
        {
            _saveTicks = 0; Player.UpdateTimeline();
            if (Player.Playing) SaveProgress();
        }
    }
    public void SaveProgress()
    { if (Client.Account is { } account && Player.Current is { } track) Store.SaveProgress(account.Id, track, Player.Position); }
    partial void OnVolumeChanged(double value)
    {
        if (Player is not null) Player.Volume = value / 100;
        Store?.SetSetting("volume", value.ToString(CultureInfo.InvariantCulture));
    }
    partial void OnThemeIndexChanged(int value)
    {
        Store?.SetSetting("theme", value.ToString()); ApplyTheme();
    }
    public void ApplyTheme()
    {
        if (App.Window?.Content is FrameworkElement root) root.RequestedTheme = ThemeIndex switch
        { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
    }
    partial void OnCloseToTrayChanged(bool value) => Store?.SetSetting("closeToTray", value ? "true" : "false");
    [RelayCommand]
    private async Task LogoutAsync()
    {
        if(DebugSession.IsActive){App.Window.Close();return;}
        var dialog = new ContentDialog
        {
            XamlRoot = App.Window.Content.XamlRoot, Title = "退出账号？", Content = "将停止播放并清除这台电脑保存的登录凭据。",
            PrimaryButtonText = "退出登录", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _selectionVersion++; CancelDownloads(); ShowQueue = false; _queueAccount = -1;
        SaveProgress(); _playVersion++; _browseVersion++;
        _browse?.Cancel(); _subtitleDiscovery?.Cancel(); _subtitles?.Cancel(); _folderPreviews?.Cancel(); _foldersVersion++;
        Player.Stop(); SessionVault.Clear(); Client.ClearSession();
        ClearAccountPages();
        IsLoggedIn = false; AccountName = "尚未登录"; AccountAvatar = null; AccountLargeAvatar = null; IsVip = false; IsLyricsOpen = false;
        _homeCache.Clear(); _regionPage = 0; _hasLoadedHome = false; _homeMore = true;
        _view = "home"; _backStack.Clear();
        Folders.Clear(); Tracks.Clear(); Queue.Clear(); Lines.Clear(); SubtitleTracks.Clear(); SearchHistory.Clear();
        await LoginAsync();
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    { target.Clear(); foreach (var item in source) target.Add(item); }
    private int Append(ObservableCollection<Track> target, IEnumerable<Track> source)
    {
        // Paged endpoints can repeat content (fav lists clamp an out-of-range page to
        // the last one while still claiming has_more), so a repeat must never add twice.
        var known = new HashSet<(string Bvid, int Page)>(target.Select(TrackKey));
        var added = 0;
        foreach (var track in source)
        {
            if (!known.Add(TrackKey(track))) continue;
            target.Add(Client.Account is { } account ? Store.ApplyMetadata(account.Id, track) : track);
            added++;
        }
        return added;
    }
    private static (string Bvid, int Page) TrackKey(Track track) => (!string.IsNullOrEmpty(track.Bvid) ? track.Bvid : "aid:" + track.Aid, track.Page);
    public void Dispose()
    {
        if (_disposed) return;
        SaveProgress(); _disposed = true; DisposeComments(); _lifetime.Cancel(); _timer.Stop();
        _login?.Cancel(); _browse?.Cancel(); _subtitleDiscovery?.Cancel(); _subtitles?.Cancel(); _folderPreviews?.Cancel();
        DisposeNotifications();
        Player.Dispose(); Client.Dispose(); Store.Dispose();
    }
}
