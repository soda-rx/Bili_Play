using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using BiliPlay.Core;
using BiliPlay.Services;

namespace BiliPlay;

public sealed partial class MainWindow
{
    internal async Task VerifyWindowPlacementAsync()
    {
        var original = new Windows.Graphics.RectInt32(AppWindow.Position.X,AppWindow.Position.Y,AppWindow.Size.Width,AppWindow.Size.Height);
        var saved=ViewModel.Store.GetSetting("windowPlacement","null");
        try
        {
            var area=Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            ViewModel.Store.SetSetting("windowPlacement",System.Text.Json.JsonSerializer.Serialize(new Placement(area.X+12,area.Y+12,760,580,false)));
            RestoreWindowPlacement();await Task.Delay(80);
            if(Math.Abs(AppWindow.Size.Width-Math.Min(760,area.Width))>2||Math.Abs(AppWindow.Size.Height-Math.Min(580,area.Height))>2) throw new InvalidOperationException("窗口尺寸恢复错误");
            ViewModel.Store.SetSetting("windowPlacement",System.Text.Json.JsonSerializer.Serialize(new Placement(-900000,-900000,760,580,false)));
            RestoreWindowPlacement();await Task.Delay(80);
            var visible=Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            if(AppWindow.Position.X<visible.X||AppWindow.Position.Y<visible.Y) throw new InvalidOperationException("离屏窗口没有恢复到可见区域");
            ViewModel.Store.SetSetting("windowPlacement","broken json"); RestoreWindowPlacement();
        }
        finally {ViewModel.Store.SetSetting("windowPlacement",saved);AppWindow.MoveAndResize(original);}
    }

    internal void VerifyGlobalBackdrop(bool lyricsOpen)
    {
        static bool Transparent(Brush? brush) => brush is null || brush is SolidColorBrush solid && solid.Color.A == 0;
        if (SystemBackdrop is not MicaBackdrop || !Transparent(WindowRoot.Background) || !Transparent(RootFrame.Background)
            || !Transparent(AppTitleBar.Background) || RootFrame.Content is not MainPage page || !Transparent(page.Background))
            throw new InvalidOperationException("普通页面的云母被不透明背景覆盖");
        if (AppWindow.TitleBar.ButtonBackgroundColor?.A != 0 || AppWindow.TitleBar.ButtonInactiveBackgroundColor?.A != 0)
            throw new InvalidOperationException("窗口控制键遮挡全局背景");
        if (!lyricsOpen)
        {
            if (AtmosphereHost.Visibility != Visibility.Collapsed || _artworkBackdropVisible || _backdropDrifting)
                throw new InvalidOperationException("关闭详情后背景特效没有恢复云母");
            return;
        }
        if (AtmosphereHost.Visibility != Visibility.Visible || AmbientBackdrop.Source is null || !_artworkBackdropVisible)
            throw new InvalidOperationException("全局封面背景未显示");
        var origin = AtmosphereHost.TransformToVisual(WindowRoot).TransformPoint(new(0, 0));
        if (Math.Abs(origin.X) > .5 || Math.Abs(origin.Y) > .5 || Math.Abs(AtmosphereHost.ActualWidth - WindowRoot.ActualWidth) > 1
            || Math.Abs(AtmosphereHost.ActualHeight - WindowRoot.ActualHeight) > 1
            || AtmosphereHost.Clip is not RectangleGeometry clip || Math.Abs(clip.Rect.Height - WindowRoot.ActualHeight) > 1)
            throw new InvalidOperationException("封面背景没有覆盖标题栏、正文和播放栏的完整窗口");
    }

    internal void VerifyTitleBarHeaderSpacing()
    {
        Grid? layout = null;
        void Walk(DependencyObject node)
        {
            if (layout is not null) return;
            if (node is Grid { Name: "PART_LayoutRoot" } grid) { layout = grid; return; }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(AppTitleBar);
        // The template's last column reserves the system caption button width (AppWindow.TitleBar.RightInset).
        var reserved = layout is { ColumnDefinitions.Count: > 0 } ? layout.ColumnDefinitions[layout.ColumnDefinitions.Count - 1].ActualWidth : 0;
        if (reserved <= 0) throw new InvalidOperationException("标题栏没有为系统最小化/最大化/关闭按钮预留宽度");
        var accountRight = AccountButton.TransformToVisual(WindowRoot).TransformPoint(new(AccountButton.ActualWidth, 0)).X;
        var captionLeft = WindowRoot.ActualWidth - reserved;
        if (accountRight > captionLeft + .5) throw new InvalidOperationException($"账号按钮侵入系统标题栏按钮区域 {accountRight - captionLeft:F1}px");
    }

    internal async Task VerifySocialListsAsync()
    {
        await RefreshSocialCountsAsync(_ => Task.FromResult((1234L, 56789L)));
        if (!ViewModel.FollowingLabel.Contains("1,234") || !ViewModel.FollowersLabel.Contains("56,789")) throw new InvalidOperationException("头像面板未显示关注／粉丝数量");
        static T? Find<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T found) return found;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (Find<T>(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
            return null;
        }
        ContentDialog? ActiveDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(WindowRoot.XamlRoot)
            .Select(popup => Find<ContentDialog>(popup.Child)).FirstOrDefault(dialog => dialog is not null);
        foreach (var followers in new[] { false, true })
        {
            var pages = new List<int>(); var failUnfollow=true; var unfollowWrites=0;
            var shown = ShowSocialListAsync(followers, (fans, page, token) =>
            {
                if (fans != followers) throw new InvalidOperationException("关注和粉丝列表混淆");
                pages.Add(page);
                var people = page == 1 ? Enumerable.Range(1, 50).Select(id => new SocialUser(id, $"用户 {id}", "", "个人简介")).ToArray()
                    : new[] { new SocialUser(1, "重复用户", "", ""), new SocialUser(51, "下一页用户", "", "") };
                return Task.FromResult(new PageResult<SocialUser>(people, page == 1));
            }, (user,token)=>
            {unfollowWrites++; if(failUnfollow) throw new System.Net.Http.HttpRequestException("离线取消关注失败");return Task.CompletedTask;});
            await Task.Delay(180);
            var dialog = ActiveDialog() ?? throw new InvalidOperationException("关注／粉丝入口未打开原生列表");
            if (dialog.Title as string != (followers ? "粉丝" : "关注")) throw new InvalidOperationException("列表标题不正确");
            var list = Find<ListView>((Grid)dialog.Content) ?? throw new InvalidOperationException("缺少用户列表");
            var users = (IncrementalCollection<SocialUser>)list.ItemsSource;
            if (users.Count != 50) throw new InvalidOperationException("用户列表第一页未加载");
            await users.LoadPageAsync();
            if (users.Count != 51 || users.HasMoreItems || !pages.SequenceEqual(new[] { 1, 2 })) throw new InvalidOperationException("用户列表分页或去重失败");
            if (Find<PersonPicture>((DependencyObject)list.ContainerFromIndex(0)) is null) throw new InvalidOperationException("用户列表头像未正确渲染");
            if(!followers)
            {
                var first=users[0]; var button=Find<Button>((DependencyObject)list.ContainerFromIndex(0));
                if(button?.Content as string!="取消关注") throw new InvalidOperationException("关注列表缺少取消关注按钮");
                await _unfollowSocialUser!(first);if(!users.Contains(first)) throw new InvalidOperationException("取消关注失败时删除了条目");
                failUnfollow=false;await _unfollowSocialUser!(first);
                if(users.Contains(first)||unfollowWrites!=2||!ViewModel.FollowingLabel.Contains("1,233")) throw new InvalidOperationException("取消关注成功后未更新列表和数量");
            }
            else if(Find<Button>((DependencyObject)list.ContainerFromIndex(0)) is not null) throw new InvalidOperationException("粉丝列表错误显示取消关注");
            dialog.Hide(); await shown;
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = ShowSocialListAsync(false, async (_, _, token) =>
        {
            started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new([], false);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(80); ActiveDialog()!.Hide();
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        if (_socialDialogOpen) throw new InvalidOperationException("关闭用户列表后仍保留加载状态");
    }
    internal async Task VerifySearchHistoryAsync()
    {
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        ViewModel.SearchHistory.Clear(); ViewModel.SearchHistory.Add("离线搜索验证一"); ViewModel.SearchHistory.Add("离线搜索验证二");
        TitleSearch.Text = "离线搜索"; TitleSearch.Focus(FocusState.Programmatic);
        _historySuppressed = false; UpdateSearchSuggestions(); await Task.Delay(60);
        Require(SearchHistoryPopup.IsOpen, "搜索历史没有打开");
        Require(!SearchHistoryPopup.IsLightDismissEnabled, "搜索历史仍使用拦截页面点击的模态遮罩");
        CloseSearchHistory();
        SearchGotFocus(TitleSearch, new RoutedEventArgs()); UpdateSearchSuggestions();
        await Task.Delay(60);
        Require(!SearchHistoryPopup.IsOpen, "提交后恢复焦点导致搜索历史重开");
        _historySuppressed = false; UpdateSearchSuggestions();
        DismissHistoryOutside(RootFrame);
        Require(!SearchHistoryPopup.IsOpen, "点击搜索结果区域后搜索历史未关闭");
        TitleSearch.Focus(FocusState.Programmatic); _historySuppressed = false; UpdateSearchSuggestions();
        DeleteSearchHistoryClick(new Button { Tag = "离线搜索验证一" }, new RoutedEventArgs()); await Task.Delay(60);
        Require(!ViewModel.SearchHistory.Contains("离线搜索验证一") && ViewModel.SearchHistory.Contains("离线搜索验证二"), "单条搜索历史删除异常");
        CloseSearchHistory();
        // Submit through the real title-search path. An invalid video origin is handled locally, without network.
        var query = "https://invalid.example/search";
        ViewModel.SearchHistory.Add(query); ViewModel.SearchHistory.Add(query + "-more");
        TitleSearch.Text = query; ViewModel.SearchText = query;
        _submittedSearchText = null; RequestSearchSuggestions(); await Task.Delay(40);
        Require(SearchHistoryPopup.IsOpen,"提交前历史框未打开");
        await SubmitTitleSearchAsync();
        RequestSearchSuggestions(); SearchGotFocus(TitleSearch,new RoutedEventArgs()); await Task.Delay(80);
        Require(!SearchHistoryPopup.IsOpen && !TitleSearch.IsSuggestionListOpen,"搜索完成后延迟输入／焦点事件重开了历史框");
        TitleSearch.Text = query[..^1]; RequestSearchSuggestions(); await Task.Delay(40);
        Require(SearchHistoryPopup.IsOpen,"真正修改搜索词后不能查看历史");
        _historySuppressed=false;_keywordSuggestions=["晴天 周杰伦","晴天 现场"];
        _songSuggestions=[new Track(71,"BV7xx411c7mD","晴天","测试 UP","",240,71)];SetSearchRecommendationItems();await Task.Delay(60);
        Require(SearchHistoryPopup.IsOpen&&SearchKeywordItems.Items.Count==2&&SearchSongItems.Items.Count==1,"联想和相关歌曲没有显示在搜索弹层");
        CloseSearchHistory();Require(!SearchHistoryPopup.IsOpen,"关闭后联想弹层仍挡住页面");
        CloseSearchHistory(); ViewModel.Notifications.Clear();
        ViewModel.Navigate("playlist"); await Task.Delay(60);
        Require(!SearchHistoryPopup.IsOpen, "页面切换后残留搜索历史");
    }
}
