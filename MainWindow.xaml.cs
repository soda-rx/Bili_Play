using BiliPlay.Core;
using Microsoft.UI.Xaml;
using BiliPlay.Services;
using System.Runtime.InteropServices;
using BiliPlay.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace BiliPlay;

public sealed partial class MainWindow : Window
{
    private TrayService? _tray;
    private bool _exitRequested;
    private bool _aligningSearch;
    private int _copyUidVersion;
    private bool _historySuppressed;
    private string? _submittedSearchText;
    private int _searchSubmissionVersion;
    public PlayerViewModel ViewModel { get; private set; } = null!;
    public MainWindow()
    {
        InitializeComponent();
        Title = AppProfile.DisplayName;
        AppTitleBar.Title = AppProfile.DisplayName;

        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
        var page = (MainPage)RootFrame.Content;
        ViewModel = page.ViewModel;
        Bindings.Update();
        BindHotkeys();
        // A light-dismiss Popup captures outside input and can restore focus back into GotFocus,
        // reopening itself after a search. Keep suggestions non-modal and dismiss without eating clicks.
        WindowRoot.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) => DismissHistoryOutside(args.OriginalSource as DependencyObject)), true);
        SearchHost.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => RequestSearchSuggestions()), true);
        WindowRoot.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(SearchKeyDown), true);
        SearchHistoryPopup.Child.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(SearchKeyDown), true);
        SearchHost.LostFocus += SearchLostFocus;
        SearchHistoryPopup.Child.LostFocus += SearchLostFocus;
        ViewModel.BrowseChanged += CloseSearchHistory;
        ViewModel.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ViewModel.IsLoggedIn) && !ViewModel.IsLoggedIn) { CloseSearchHistory(); _suggestionsCache.Clear(); } };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new((int)Math.Min(1240 * scale, area.Width - 32), (int)Math.Min(820 * scale, area.Height - 32)));
        InitializeWindowPlacement();
        if (!App.IsVisualCheck) _tray = new(hwnd, ShowMainWindow, () => { _exitRequested = true; Close(); });
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) CloseSearchHistory();
            if(RootFrame.Content is MainPage activePage) activePage.SetPresentationActive(args.WindowActivationState != WindowActivationState.Deactivated);
            if (!App.IsVisualCheck && args.WindowActivationState != WindowActivationState.Deactivated && ViewModel.IsLoggedIn) _ = ViewModel.RefreshFavoriteStateAsync();
        };
        AppWindow.Closing += (_, e) =>
        {
            if (!_exitRequested && !DebugSession.IsActive && ViewModel.IsLoggedIn && ViewModel.CloseToTray && _tray?.Available == true)
            {
                SaveWindowPlacement();
                if (ViewModel.CloseBehaviorIndex == 2) ViewModel.PauseForTray();
                e.Cancel = true;
                AppWindow.Hide();
                ResetArtworkBackdrop();
                ViewModel.SetTrayResidentMode(true);
                TrimProcessWorkingSet();
            }
        };
        Closed += (_, _) => { CancelSearchRecommendations(); _suggestionsCache.Clear(); SaveWindowPlacement(); _placementTimer?.Stop(); _tray?.Dispose(); _hotkeys?.Dispose(); try {ViewModel.Dispose();} finally {ArtworkCache.ResetScope();DebugSession.End();} };
    }
    public void ShowMainWindow()
    {
        ViewModel.SetTrayResidentMode(false);
        AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
            presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized) presenter.Restore();
        Activate(); SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (RootFrame.Content is MainPage activePage) activePage.SetPresentationActive(true);
    }
    private static int _trimVersion;
    internal static async void ScheduleMemoryTrim(int delayMs = 800)
    {
        var version = Interlocked.Increment(ref _trimVersion);
        try
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            if (Volatile.Read(ref _trimVersion) != version) return;
            TrimProcessWorkingSet();
        }
        catch (Exception) { }
    }
    internal static void TrimProcessWorkingSet()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            _ = K32EmptyWorkingSet(current.Handle);
        }
        catch (Exception) { /* Best-effort memory trim when hidden or minimized. */ }
    }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool K32EmptyWorkingSet(nint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    private void TitleBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        AlignSearch();
    }
    private void SearchLayoutUpdated(object? sender, object e) => AlignSearch();
    private void AlignSearch()
    {
        if (_aligningSearch || WindowRoot is null || SearchShift is null || TitleSearch is null || AccountButton is null || WindowRoot.ActualWidth <= 0) return;
        _aligningSearch = true;
        try
        {
            var center = WindowRoot.ActualWidth / 2;
            var accountLeft = AccountButton.TransformToVisual(WindowRoot).TransformPoint(new(0, 0)).X;
            if (accountLeft <= 0) return;
            var width = Math.Clamp(2 * Math.Min(center - 156, accountLeft - 16 - center), 160, 560);
            if (Math.Abs(SearchHost.Width - width) > .5) { SearchHost.Width = width; return; }
            var currentCenter = TitleSearch.TransformToVisual(WindowRoot).TransformPoint(new(TitleSearch.ActualWidth / 2, 0)).X;
            var delta = center - currentCenter;
            if (Math.Abs(delta) > .5) { SearchShift.X += delta; AppTitleBar.RecomputeDragRegions(); }
        }
        finally { _aligningSearch = false; }
    }
    private async void TitleSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.SearchText = args.QueryText;
        await SubmitTitleSearchAsync();
    }
    private async Task SubmitTitleSearchAsync()
    {
        var version = ++_searchSubmissionVersion;
        var submitted = _submittedSearchText = TitleSearch.Text.Trim();
        CloseSearchHistory();
        try
        {
            if (RootFrame.Content is MainPage page) await page.SearchFromTitleBarAsync();
        }
        finally
        {
            // Late IME / binding / focus events must not reopen a just-submitted query.
            // A new, different edit remains eligible for suggestions while a request is pending.
            if (version == _searchSubmissionVersion && TitleSearch.Text.Trim() == submitted) CloseSearchHistory();
        }
    }
    private async void TitleBackRequested(TitleBar sender, object args)
    { CloseSearchHistory(); if (RootFrame.Content is MainPage page) await page.GoBackAsync(); }
    private void SearchGotFocus(object sender, RoutedEventArgs e)
    { if (!_historySuppressed) UpdateSearchSuggestions(); }
    private void SearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        RequestSearchSuggestions();
    }
    private void RequestSearchSuggestions()
    {
        if (TitleSearch.Text.Trim() == _submittedSearchText) { CloseSearchHistory(); return; }
        _historySuppressed = false; UpdateSearchSuggestions();
    }
    private void UpdateSearchSuggestions()
    {
        if (ViewModel is null || !ViewModel.IsLoggedIn || _historySuppressed || TitleSearch.Text.Trim() == _submittedSearchText) return;
        var suggestions = ViewModel.RecordSearchHistory ? ViewModel.SearchHistory.Where(s => s.Contains(TitleSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray() : [];
        SearchHistoryItems.ItemsSource = suggestions;
        RequestSearchRecommendations(TitleSearch.Text.Trim());
    }
    private void CloseSearchHistory()
    {
        _historySuppressed = true;
        CancelSearchRecommendations();
        SearchHistoryPopup.IsOpen = false;
        TitleSearch.IsSuggestionListOpen = false;
    }
    private static bool IsInside(DependencyObject? source, DependencyObject root)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node == root) return true;
        return false;
    }
    private void DismissHistoryOutside(DependencyObject? source)
    {
        if (!IsInside(source, SearchHost) && !IsInside(source, SearchHistoryPopup.Child)) CloseSearchHistory();
    }
    private void SearchLostFocus(object sender, RoutedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var focused = FocusManager.GetFocusedElement(WindowRoot.XamlRoot) as DependencyObject;
            DismissHistoryOutside(focused);
        });
    }
    private void SearchKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape && SearchHistoryPopup.IsOpen)
        {
            CloseSearchHistory(); TitleSearch.Focus(FocusState.Programmatic); args.Handled = true;
        }
        else if (args.Key == VirtualKey.Down && IsInside(args.OriginalSource as DependencyObject, TitleSearch))
        { _submittedSearchText = null; _historySuppressed = false; UpdateSearchSuggestions(); }
    }
    private async void SearchHistoryQueryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string query }) return;
        CloseSearchHistory(); TitleSearch.Text = query; ViewModel.SearchText = query;
        await SubmitTitleSearchAsync();
    }
    private void DeleteSearchHistoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string query })
        {
            ViewModel.DeleteSearchHistory(query); UpdateSearchSuggestions();
            // The removed button may have held keyboard focus; return it to the editor, without reopening.
            TitleSearch.Focus(FocusState.Programmatic);
        }
    }
    private async void ReLoginClick(object sender, RoutedEventArgs e)
    { AccountFlyout.Hide(); await ViewModel.ReauthenticateAsync(); }
    private async void CopyUidClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsLoggedIn) return;
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(ViewModel.AccountUid);
        try { Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); }
        catch (Exception clipboardError) when (clipboardError is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        { ViewModel.Notify("UID 复制失败，请重试。"); return; }
        var version = ++_copyUidVersion;
        CopyUidIcon.Glyph = "\uE73E"; CopyUidButton.SetValue(ToolTipService.ToolTipProperty, "已复制");
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (version != _copyUidVersion) return;
        CopyUidIcon.Glyph = "\uE8C8"; CopyUidButton.SetValue(ToolTipService.ToolTipProperty, "复制 UID");
    }
    private async void LogoutClick(object sender, RoutedEventArgs e)
    {
        AccountFlyout.Hide();
        await ViewModel.LogoutCommand.ExecuteAsync(null);
    }
}
