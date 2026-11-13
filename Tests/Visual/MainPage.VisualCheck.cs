using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using BiliPlay.Core;
using BiliPlay.Services;
using BiliPlay.ViewModels;
using System.Net;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace BiliPlay;

// Run the real WinUI tree with isolated offline fixtures, never the user's account or media session.
public sealed partial class MainPage
{
    private async Task RunVisualCheckAsync()
    {
        var directory = App.VisualCheckDirectory!; Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(AppContext.BaseDirectory,"TestAssets","cover.png"),Path.Combine(directory,"cover.png"),true);
        var checks = new List<object>();
        try
        {
            ViewModel.CloseToTray = false;
            Check(ViewModel.IsInitializing && LoginSurface.Visibility == Visibility.Collapsed && StartupSurface.Visibility == Visibility.Visible, "首帧在恢复账号前闪现了二维码登录页面");
            await VerifyLoginLayoutAsync(directory);
            await VerifyDebugModeAsync(directory);
            await VerifyArtworkReplacementAsync(directory);
            await VerifyMediaArtworkAsync(directory);
            await ((MainWindow)App.Window).VerifyWindowPlacementAsync();
            ViewModel.Client.RestoreCachedAccount(new(123, "离线布局验证", ""));
            ViewModel.IsLoggedIn = true;
            await VerifyStartupResponsivenessAsync();
            await VerifyRapidAudioSwitchingAsync();
            await VerifyDownloadLabelsAndAboutAsync(directory);
            await VerifyPageEntrancesAsync();
            await VerifySpacesAndConversationsAsync(directory);
            await VerifyPagedTrackDedupAsync();
            await VerifyBrowseAndNotificationsAsync(directory);
            await VerifyTrackActivationAsync();
            await VerifyStableQueueActivationAsync();
            await VerifyImmediateSelectionAsync();
            await VerifyStartupSubtitlesAsync(directory);
            await VerifyCommentsAndSpeedAsync(directory);
            await VerifyLocalFolderOrderAsync();
            await VerifyCacheSelectionAsync(directory);
            var song = new Track(1,"BV1xx411c7mD","在月光下，等一首未完的歌 / A Song After Midnight","林间电台","",240,123);
            ViewModel.Queue.Add(song); ViewModel.Navigate("playlist");
            await VerifyQualityDialogsAsync(song);
            await ((MainWindow)App.Window).VerifySearchHistoryAsync();
            await ((MainWindow)App.Window).VerifySocialListsAsync();
            await VerifyPlaybackMarkersAsync(song, directory);
            ViewModel.Navigate("playlist");
            var cover = new BitmapImage(new Uri(Path.Combine(directory,"cover.png")));
            ViewModel.CurrentCover = ViewModel.LyricsCover = cover;
            foreach (var line in new[] { "当城市慢慢安静下来", "我们沿着晚风走向海", "把还没说出口的温柔", "都写进今晚的月色里", "There's a light beyond the skyline", "And a song that brings you home", "有些故事不必急着说完，让这一刻停留得再久一些", "下一次天亮，我们还会再相见" })
                ViewModel.Lines.Add(new(ViewModel.Lines.Count * 30, ViewModel.Lines.Count * 30 + 30,line));
            ViewModel.SubtitleTracks.Add(new("zh-CN","中文",new Uri("https://i0.hdslb.com/offline-check.json"),false));
            ViewModel.Duration = 240; ViewModel.Position = 72; ViewModel.ActiveSubtitleIndex = 2;
            var scale = XamlRoot.RasterizationScale;
            foreach (var scene in new (int Width,int Height,ElementTheme Theme)[] { (1240,820,ElementTheme.Light), (840,560,ElementTheme.Dark), (1680,960,ElementTheme.Dark) })
            {
                ViewModel.ThemeIndex = scene.Theme == ElementTheme.Dark ? 2 : 1;
                ((FrameworkElement)App.Window.Content).RequestedTheme = scene.Theme;
                if (App.Window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter
                    && presenter.State != Microsoft.UI.Windowing.OverlappedPresenterState.Restored) presenter.Restore();
                var requestedSize = new Windows.Graphics.SizeInt32((int)(scene.Width*scale), (int)(scene.Height*scale));
                App.Window.AppWindow.Resize(requestedSize);
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    await Task.Delay(50); ((FrameworkElement)App.Window.Content).UpdateLayout();
                    if (Math.Abs(ActualWidth - scene.Width) < 24) break;
                    if (attempt == 10) App.Window.AppWindow.Resize(requestedSize);
                }
                Check(Math.Abs(ActualWidth - scene.Width) < 24, $"测试窗口未实际调整至 {scene.Width}，当前 {ActualWidth}");
                UpdateLayout();
                ViewModel.IsLyricsOpen = true; await _lyricsMotionTask; await Task.Delay(700); UpdateLayout();
                Check(_ambientReady && _ambientPalette!=ArtworkAtmosphere.Neutral,"封面配色背景未生成");
                ((MainWindow)App.Window).VerifyGlobalBackdrop(true);
                ((MainWindow)App.Window).VerifyTitleBarHeaderSpacing();
                Check(Navigation.Visibility == Visibility.Collapsed, "透明歌词背景泄露下层列表");
                Check(PlayerBar.Background is SolidColorBrush playerSurface && playerSurface.Color.A < 32, "播放栏遮挡全局背景");
                Check(PlayerBar.Visibility == Visibility.Visible && PlayerBar.ActualHeight > 0,"详情打开时底栏消失");
                Check(NowPlayingButton.Visibility == Visibility.Collapsed,"底栏仍显示重复封面");
                Check(Math.Abs(CoverFrame.ActualWidth-CoverFrame.ActualHeight)<1 && CoverFrame.ActualWidth>100,"封面尺寸失效");
                var landed = CoverFrame.TransformToVisual(this).TransformPoint(new(0, 0));
                Check(Math.Abs(landed.X - _coverTargetAtStart.X) < 1 && Math.Abs(landed.Y - _coverTargetAtStart.Y) < 1
                    && Math.Abs(CoverFrame.ActualWidth - _coverTargetAtStart.Width) < 1, "封面动画结束后发生二次位置调整");
                var transport = TransportGroup.TransformToVisual(this).TransformPoint(new(TransportGroup.ActualWidth/2,0));
                Check(Math.Abs(transport.X-ActualWidth/2)<1,"播放控制按钮未保持居中");
                var actions = FooterActions.TransformToVisual(this).TransformPoint(new(0,0));
                var transportRight = TransportGroup.TransformToVisual(this).TransformPoint(new(TransportGroup.ActualWidth,0));
                if (Grid.GetRow(FooterActions) == 1) Check(actions.X >= transportRight.X+6,"右侧按钮与播放按钮重叠");
                else Check(actions.Y >= TransportGroup.TransformToVisual(this).TransformPoint(new(0,TransportGroup.ActualHeight)).Y + 6,"窄窗口功能按钮未独立换行");
                var actionRight = FooterActions.TransformToVisual(this).TransformPoint(new(FooterActions.ActualWidth,0));
                Check(actionRight.X<=ActualWidth-8,"右侧按钮被窗口裁切");
                Check(LyricsViewport.ActualHeight>100,"歌词可用高度不足");
                Check(Enumerable.Range(0,ViewModel.Lines.Count).All(i => LyricsList.ContainerFromIndex(i) is not ListViewItem item || FindVisual<TextBlock>(item)?.FontSize==_lyricFontSize),"虚拟化歌词字号不一致");
                VerifyFooterGeometry();
                Check(DownloadCurrentButton.ActualWidth >= 32,"下载按钮未显示");
                // Exercise consecutive cue changes, reverse seeks and viewport updates during the spring.
                foreach (var cue in new[] { 3, 4, 5, 2 })
                {
                    ViewModel.ActiveSubtitleIndex = cue;
                    for (var frame=0;frame<6;frame++) { await Task.Delay(16); VerifyLyricSeparation(); }
                    StyleVisibleLyrics(false);
                }
                await Task.Delay(1000);
                Check(_lastAnimatedLyric == 2, "歌词快速切换后停留在旧行");
                Check(Enumerable.Range(0, ViewModel.Lines.Count).All(i => LyricsList.ContainerFromIndex(i) is not ListViewItem row || FindVisual<TextBlock>(row) is { } text && text.FontSize == _lyricFontSize), "切换歌词后字号失效");
                _followPausedUntil = DateTimeOffset.UtcNow.AddMinutes(1);
                foreach (var cue in new[] { 3, -1, 2 })
                {
                    ViewModel.ActiveSubtitleIndex = cue; StyleVisibleLyrics(false);
                    for (var row = 0; row < ViewModel.Lines.Count; row++)
                        if (LyricsList.ContainerFromIndex(row) is ListViewItem realized && FindVisual<TextBlock>(realized) is { } text)
                        {
                            var alpha = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(text).Opacity;
                            Check(Math.Abs(alpha - (row == cue ? 1 : .42)) < .01, "手动浏览或字幕空隙时仍高亮旧歌词");
                        }
                }
                // Let older spring completions run, then verify none can restore an obsolete highlight.
                await Task.Delay(700);
                for (var row = 0; row < ViewModel.Lines.Count; row++)
                    if (LyricsList.ContainerFromIndex(row) is ListViewItem realized && FindVisual<TextBlock>(realized) is { } text)
                        Check(Math.Abs(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(text).Opacity - (row == 2 ? 1 : .42)) < .01,
                             $"旧动画结束后高亮失效：行 {row}，当前 {ViewModel.ActiveSubtitleIndex}，XAML {text.Opacity}，合成 {Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(text).Opacity}");
                _followPausedUntil = default;
                ViewModel.CurrentQuality = "Hi-Res 无损"; ViewModel.IsQualitySwitching = true; UpdateLayout();
                Check(QualityText.Text == "Hi-Res" && QualitySwitchProgress.Visibility == Visibility.Visible && QualitySwitchProgress.IsActive,
                    "所选音质没有立即显示或缺少切换进度提示");
                var pendingActions = FooterActions.TransformToVisual(this).TransformPoint(new(0, 0));
                if (Grid.GetRow(FooterActions) == 1) Check(pendingActions.X >= transportRight.X + 6, "音质切换提示挤占居中的播放按钮");
                var pendingRight = FooterActions.TransformToVisual(this).TransformPoint(new(FooterActions.ActualWidth, 0));
                Check(pendingRight.X <= ActualWidth - 8, "音质切换时最右侧按钮被裁切");
                await SaveVisualAsync(Path.Combine(directory,$"quality-switch-{scene.Width}.png"));
                ViewModel.IsQualitySwitching = false; ViewModel.CurrentQuality = "标准"; UpdateLayout();
                Check(QualitySwitchProgress.Visibility == Visibility.Collapsed && QualityText.Text == "标准", "音质切换完成或回退后状态残留");
                await SaveVisualAsync(Path.Combine(directory,$"details-{scene.Width}-{scene.Theme}.png"));
                checks.Add(new { scene.Width,scene.Height,ActualWidth,ActualHeight,Theme=scene.Theme.ToString(),Cover=CoverFrame.ActualWidth,PlayerHeight=PlayerBar.ActualHeight,LyricsHeight=LyricsViewport.ActualHeight,TextSizes=Enumerable.Range(0,ViewModel.Lines.Count).Select(i => LyricsList.ContainerFromIndex(i) is ListViewItem item ? FindVisual<TextBlock>(item)?.FontSize : null).ToArray() });
                ViewModel.IsLyricsOpen = false; await _lyricsMotionTask; await Task.Delay(500);
                Check(LyricsView.Visibility==Visibility.Collapsed && NowPlayingButton.Visibility==Visibility.Visible,"关闭后封面没有回到底栏");
                ((MainWindow)App.Window).VerifyGlobalBackdrop(false);
                Check(Navigation.Visibility == Visibility.Visible, "关闭详情未恢复导航内容");
                if(scene.Width==1240) await SaveVisualAsync(Path.Combine(directory,"footer-light.png"));
            }
            await VerifyInterruptibleFooterAsync();
            for(var i=0;i<12;i++) { ViewModel.IsLyricsOpen=!ViewModel.IsLyricsOpen; await Task.Delay(45); }
            ViewModel.IsLyricsOpen=false; await _lyricsMotionTask; await Task.Delay(200);
            Check(LyricsView.Visibility==Visibility.Collapsed && NowPlayingButton.Visibility==Visibility.Visible,"快速开关后状态残留");
            ((MainWindow)App.Window).VerifyGlobalBackdrop(false);
            _forceReducedMotion=true;
            ViewModel.IsLyricsOpen=true; await _lyricsMotionTask;
            Check(LyricsView.Visibility==Visibility.Visible && NowPlayingButton.Visibility==Visibility.Collapsed,"减少动画时无法打开详情");
            ViewModel.IsLyricsOpen=false; await _lyricsMotionTask;
            Check(LyricsView.Visibility==Visibility.Collapsed && NowPlayingButton.Visibility==Visibility.Visible,"减少动画时无法恢复底栏封面");
            ((MainWindow)App.Window).VerifyGlobalBackdrop(false);
            _forceReducedMotion=false;
            for(var volume=0;volume<=100;volume+=10) ViewModel.Volume=volume;
            await VerifyDebugEntryAsync();
            File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new { Success=true,OfflineDebug=true,LoginLayout=true,Scenes=checks,RapidToggles=12,PlaybackMarkers=true,SearchHistory=true,LyricCueTransitions=true,StartupSubtitles=true,CoverLanding=true,ImmediateSelection=true,QualityDialogs=true,QualitySelectionFeedback=true,LyricHighlight=true,SocialLists=true,LocalFolderOrder=true,CacheSelection=true,NoticeMotion=true,DisabledClearButtons=true,GlobalBackdrop=true,TrackActivation=true,RowCovers=true,InitialLyricHighlight=true,LyricSeparation=true,Comments=true,PlaybackSpeed=true,UniformFooter=true,StableQueue=true,PageEntrances=true,InterruptibleFooter=true,StartupResponsive=true,DownloadedMenu=true,SettingsAbout=true },new JsonSerializerOptions { WriteIndented=true }));
        }
        catch(Exception error)
        { File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new { Success=false,Error=error.ToString(),Scenes=checks },new JsonSerializerOptions { WriteIndented=true })); }
        finally
        {
            var demoRoot=DebugSession.DirectoryPath;
            App.Window.Close();
            if(demoRoot is not null&&(DebugSession.IsActive||Directory.Exists(demoRoot)))
                File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new {Success=false,Error="关闭软件后离线调试数据未清理"}));
        }
    }
    private async Task VerifyLocalFolderOrderAsync()
    {
        ViewModel.Navigate("library"); ViewModel.Folders.Clear();
        foreach (var folder in new[] { new Folder(71,"默认收藏夹",0,true), new Folder(72,"普通收藏夹",0,true), new Folder(73,"订阅收藏夹",0,false), new Folder(74,"收藏夹四",0,true) }) ViewModel.Folders.Add(folder);
        foreach (var grid in new[] { true, false })
        {
            ViewModel.FolderGridMode = grid; await Task.Delay(100); UpdateLayout();
            ListViewBase list = grid ? FolderGrid : FolderList;
            Check(list.CanDragItems && list.CanReorderItems && list.AllowDrop, "收藏夹视图没有允许本地拖动排序");
            var boxes = new List<Windows.Foundation.Rect>();
            for (var i = 0; i < 4; i++)
            {
                if (list.ContainerFromIndex(i) is not FrameworkElement item) continue;
                if (grid && item is GridViewItem container && container.ContentTemplateRoot is Border card)
                    Check(Math.Abs(card.ActualWidth-item.ActualWidth)<1 && Math.Abs(card.ActualHeight-item.ActualHeight)<1
                        && card.Margin == new Thickness(0), "收藏夹悬浮区域大于可见卡片边框");
                var point = item.TransformToVisual(list).TransformPoint(new(0,0));
                var box = new Windows.Foundation.Rect(point.X,point.Y,item.ActualWidth,item.ActualHeight);
                foreach (var other in boxes)
                {
                    var overlap = box; overlap.Intersect(other);
                    Check(overlap.IsEmpty || overlap.Width < .5 || overlap.Height < .5, "两个收藏夹单元重叠");
                }
                boxes.Add(box);
            }
            Check(boxes.Count > 1, "收藏夹单元未正确渲染");
            ViewModel.Folders.Move(2,0); ViewModel.SaveFolderOrder();
            var stored = JsonSerializer.Deserialize<long[]>(ViewModel.Store.GetSetting("folderOrder:123", "[]"));
            Check(stored!.SequenceEqual(ViewModel.Folders.Select(folder=>folder.Id)), "本地收藏夹顺序未持久化");
        }
        ViewModel.Navigate("history"); ViewModel.Tracks.Clear(); UpdateLayout();
        await Task.Delay(80);
        Check(!ClearHistoryButton.IsEnabled && ClearHistoryButton.Opacity < .5 && !ViewModel.ClearHistoryCommand.CanExecute(null), "空最近播放的全部删除没有禁用并变灰");
        await SaveVisualAsync(Path.Combine(App.VisualCheckDirectory!, "empty-history.png"));
        ViewModel.Tracks.Add(new Track(1,"BV1xx411c7mD","历史测试","UP","",120)); ViewModel.IsEmpty = false; UpdateLayout();
        Check(ClearHistoryButton.IsEnabled, "历史非空时全部删除没有恢复");
        ViewModel.Folders.Clear(); ViewModel.Queue.Clear(); ViewModel.Navigate("playlist"); await Task.Delay(80);
        Check(!ClearQueueButton.IsEnabled && ClearQueueButton.Opacity < .5 && !ViewModel.ClearQueueCommand.CanExecute(null), "空播放列表的全部清除没有禁用并变灰");
        await SaveVisualAsync(Path.Combine(App.VisualCheckDirectory!, "empty-playlist.png"));
    }
    private async Task VerifyCacheSelectionAsync(string directory)
    {
        var cached = new[] { new Track(71,"BV7xx411c7mD","下载一","UP","",30,71,LocalFile:Path.Combine(directory,"cache-one.mp3")),
            new Track(72,"BV8xx411c7mD","下载二","UP","",30,72,LocalFile:Path.Combine(directory,"cache-two.mp3")) };
        foreach (var track in cached) { File.WriteAllBytes(track.LocalFile, new byte[16]); File.WriteAllText(Path.ChangeExtension(track.LocalFile,".subtitles.json"),"[]"); }
        ViewModel.Store.WriteTracks("downloads:123",cached); ViewModel.Navigate("cache");
        TrackList.SelectedItem = cached[0];
        ViewModel.IsCacheSelectionMode = true; await Task.Delay(100); UpdateLayout();
        Check(!DeleteSelectedCachedButton.IsEnabled && DeleteSelectedCachedButton.Opacity < .5 && TrackList.SelectedItems.Count == 0 && ViewModel.SelectedCacheCount == 0, "选择模式沿用了旧单选项");
        TrackList.SelectAll(); await Task.Delay(40);
        Check(DeleteSelectedCachedButton.IsEnabled && DeleteSelectedCachedButton.Opacity > .9,"选中下载后删除按钮仍灰色");
        TrackList.SelectedItems.Clear(); await Task.Delay(40);
        Check(!DeleteSelectedCachedButton.IsEnabled && DeleteSelectedCachedButton.Opacity < .5,"取消全部选中后删除按钮未变灰");
        TrackList.SelectAll();
        Check(TrackList.SelectionMode == ListViewSelectionMode.Multiple && !TrackList.IsItemClickEnabled && ViewModel.SelectedCacheCount == 2, "下载批量选择未启用或选择时仍播放");
        foreach (var confirm in new[] { false, true })
        {
            var deleting = DeleteCachedWithConfirmationAsync(cached); await Task.Delay(120);
            var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Select(p => FindVisual<ContentDialog>(p.Child)).FirstOrDefault(d => d is not null);
            Check(dialog is not null && dialog.Title as string == "删除所选下载音乐？", "删除下载前未弹出确认警告");
            var button = FindNamedButton(dialog!,confirm ? "PrimaryButton" : "CloseButton")!;
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke(); await deleting;
            Check(cached.All(t => File.Exists(t.LocalFile) == !confirm && File.Exists(Path.ChangeExtension(t.LocalFile,".subtitles.json")) == !confirm), "删除确认或取消没有正确保留／删除音频和字幕");
        }
        Check(!SelectAllCachedButton.IsEnabled && !ViewModel.CanSelectCachedTracks,"空下载列表仍可全选");
        Check(!CacheSelectButton.IsEnabled&&CacheSelectButton.Opacity<.5,"空下载列表的选择按钮未禁用变灰");
        Check(ViewModel.Tracks.Count == 0 && !ViewModel.IsCacheSelectionMode, "批量删除后界面未刷新");
        ViewModel.Notify("通知方向验证",NoticeLevel.Info); await Task.Delay(100);
        var notice = ViewModel.Notifications.Last(); Check(_noticeBorders.ContainsKey(notice), "通知没有原生显示");
        ViewModel.DismissNotice(notice); await Task.Delay(280);
        Check(!ViewModel.Notifications.Contains(notice), "通知退出动画后没有移除");
        ViewModel.Navigate("playlist");
    }
    private async Task VerifyTrackActivationAsync()
    {
        var savedSingle = ViewModel.SingleClickBehaviorIndex; var savedDouble = ViewModel.DoubleClickBehaviorIndex;
        var confirm = ViewModel.ConfirmQueueChange;
        var previous = new Track(2,"BV2xx411c7mD","原有歌曲","UP","",120,222);
        var selected = new Track(1,"BV1xx411c7mD","选择的歌曲","UP","",120);
        try
        {
            // Exercise native settings bindings and restoration through a fresh model / store connection.
            ViewModel.Navigate("settings"); await Task.Delay(60); UpdateLayout();
            SingleClickBehaviorBox.SelectedIndex = 1; DoubleClickBehaviorBox.SelectedIndex = 0;
            Check(ViewModel.SingleClickBehaviorIndex == 1 && ViewModel.DoubleClickBehaviorIndex == 0, "单双击设置没有双向绑定");
            using (var restored = new PlayerViewModel(new BilibiliClient(new DeferredVideoHandler())))
                Check(restored.SingleClickBehaviorIndex == 1 && restored.DoubleClickBehaviorIndex == 0, "单双击设置没有持久化");
            // All default / customized mappings reach the correct real queue mutation before metadata completes.
            foreach (var mapping in new[] { (Single:0,Double:1), (Single:1,Double:0), (Single:0,Double:0), (Single:1,Double:1) })
            foreach (var twice in new[] { false, true })
            {
                using var handler = new DeferredVideoHandler();
                using var model = new PlayerViewModel(new BilibiliClient(handler));
                model.Client.RestoreCachedAccount(new(123,"隔离单双击验证","")); model.IsLoggedIn = true;
                model.SingleClickBehaviorIndex = mapping.Single; model.DoubleClickBehaviorIndex = mapping.Double;
                model.Queue.Add(previous);
                model.ConfirmQueueChange = _ => throw new InvalidOperationException("普通曲目意外弹出确认");
                var pending = model.ActivateTrackAsync(selected, twice);
                await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var replace = (twice ? mapping.Double : mapping.Single) == 1;
                Check(model.Queue.Select(track=>track.Bvid).SequenceEqual(replace ? new[]{selected.Bvid} : new[]{previous.Bvid,selected.Bvid}), "单双击没有遵循设置追加／替换");
                model.ClearQueueCommand.Execute(null); handler.Release.TrySetResult(); await pending;
            }
            // Favorites now honor saved settings; only Ask displays confirmation.
            foreach(var twice in new[]{false,true})
            foreach(var behavior in new[]{0,1,2})
            {
                using var handler=new DeferredVideoHandler();
                using var model=new PlayerViewModel(new BilibiliClient(handler));
                model.Client.RestoreCachedAccount(new(123,"隔离收藏夹验证",""));model.IsLoggedIn=true;
                model.Navigate("folder");model.SelectedFolder=new(71,"设置测试收藏夹",1,true);
                model.Tracks.Add(selected);model.Queue.Add(previous);
                model.SingleClickBehaviorIndex=model.DoubleClickBehaviorIndex=behavior;
                var prompts=0;
                model.ConfirmQueueChange=_=>{prompts++;return Task.FromResult(QueueChange.Cancel);};
                var pending=model.ActivateTrackAsync(selected,twice);
                if(behavior==2)
                {
                    await pending;Check(prompts==1&&model.Queue.Count==1&&!handler.Started.Task.IsCompleted,"每次询问取消后仍改变收藏夹播放");
                }
                else
                {
                    await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    Check(prompts==0&&model.Queue.Count==(behavior==1?1:2),"收藏夹没有遵循已保存的播放行为或仍弹出确认");
                    model.ClearQueueCommand.Execute(null);handler.Release.TrySetResult();await pending;
                }
            }
            // The native dialog exposes append / replace / cancel and maps each exact result.
            foreach (var (buttonName, expected) in new[] { ("PrimaryButton",QueueChange.Append), ("SecondaryButton",QueueChange.Replace), ("CloseButton",QueueChange.Cancel) })
            {
                var choosing = ConfirmQueueChangeAsync("测试收藏夹"); await Task.Delay(90);
                var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Select(popup => FindVisual<ContentDialog>(popup.Child)).FirstOrDefault(item=>item is not null);
                Check(dialog is not null && dialog.PrimaryButtonText == "追加并播放" && dialog.SecondaryButtonText == "替换并播放", "收藏夹确认选项不完整");
                var button = FindNamedButton(dialog!,buttonName)!;
                ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();
                Check(await choosing == expected,"收藏夹确认结果映射错误");
            }
            // Exercise the scheduler used by native input. No media/network on cancel.
            ViewModel.Navigate("home"); ViewModel.SingleClickBehaviorIndex = ViewModel.DoubleClickBehaviorIndex = 2;
            var calls = 0;
            ViewModel.ConfirmQueueChange = _ => { calls++; return Task.FromResult(QueueChange.Cancel); };
            ScheduleTrackClick(RecommendationGrid, selected);
            ActivateDoubleTrackClick(RecommendationGrid, selected); await _trackActivationTask;
            // A possible ItemClick delivered after DoubleTapped must also be consumed.
            ScheduleTrackClick(RecommendationGrid, selected);
            await Task.Delay((int)GetDoubleClickTime()+70);
            Check(calls == 1,"双击还执行了单击操作或重复弹出确认框");
            _ignoredClickOwner = null; _ignoredClickTrack = null;
            ScheduleTrackClick(RecommendationGrid, selected); await _trackActivationTask;
            Check(calls == 2,$"正常单击未执行：calls={calls}, view={ViewModel.CurrentView}, playlist={ViewModel.IsPlaylistView}, behavior={ViewModel.SingleClickBehaviorIndex}, login={ViewModel.IsLoggedIn}, pending={_pendingTrackClick is not null}");
            ScheduleTrackClick(RecommendationGrid, selected); CancelPendingTrackClick(); await _trackActivationTask;
            Check(calls == 2,"取消的单击仍执行");
            ScheduleTrackClick(RecommendationGrid, selected); ViewModel.Navigate("settings"); await _trackActivationTask;
            Check(calls == 2,"离开页面后旧的单击仍执行");
        }
        finally
        {
            CancelPendingTrackClick(); _ignoredClickOwner = null; _ignoredClickTrack = null;
            ViewModel.SingleClickBehaviorIndex = savedSingle; ViewModel.DoubleClickBehaviorIndex = savedDouble;
            ViewModel.Store.SetSetting("singleClickBehavior",savedSingle.ToString());
            ViewModel.Store.SetSetting("doubleClickBehavior",savedDouble.ToString());
            ViewModel.ConfirmQueueChange = confirm;
            ViewModel.Navigate("home");
        }
    }
    private async Task VerifyImmediateSelectionAsync()
    {
        foreach (var queueClick in new[] { false, true })
        {
            using var handler = new DeferredVideoHandler();
            using var model = new PlayerViewModel(new BilibiliClient(handler));
            model.Client.RestoreCachedAccount(new(123, "隔离请求验证", "")); model.IsLoggedIn = true;
            var track = new Track(1, "BV1xx411c7mD", "应立即显示的新歌曲", "隔离 UP", "", 120);
            var pending = queueClick ? model.PlayQueueAsync(track) : model.PlayCommand.ExecuteAsync(track);
            Check(QueueSelection.Same(model.CurrentTrack, track) && model.CurrentTitle == track.Title && model.IsPlaybackLoading,
                "仍然等待元数据加载后才显示所选歌曲");
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!pending.IsCompleted && model.Queue.Any(item => QueueSelection.Same(item, track)), "加载阶段没有立即更新播放列表");
            model.ClearQueueCommand.Execute(null);
            handler.Release.TrySetResult(); await pending;
            Check(model.CurrentTrack is null && !model.IsPlaybackLoading && model.Player.Current is null, "过期加载覆盖了最新选择");
            var asked = false;
            model.RequestDownloadOptions = _ => { asked = true; return Task.FromResult<DownloadOptions?>(null); };
            await model.DownloadAsync(track);
            Check(asked && model.Downloads.Count == 0, "取消下载音质选择后仍创建下载任务");
        }
    }
    private sealed class DeferredVideoHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = """{"code":0,"data":{"isLogin":true,"mid":123,"uname":"隔离","wbi_img":{"img_url":"https://i0.hdslb.com/7cd084941338484aae1ad9425b84077c.png","sub_url":"https://i0.hdslb.com/4932caff0ff746eab6f01bf08b70ac45.png"}}}""";
            if (request.RequestUri!.AbsolutePath.Contains("/view"))
            {
                Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
                json = """{"code":0,"data":{"aid":1,"bvid":"BV1xx411c7mD","title":"解析后的歌曲","owner":{"mid":1,"name":"UP"},"pages":[{"cid":123,"page":1,"duration":120}]}}""";
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }
    private async Task VerifyQualityDialogsAsync(Track song)
    {
        ViewModel.IsVip = false;
        QualityMenuOpening(new Flyout(), EventArgs.Empty);
        Check(QualityOptions.Children.OfType<RadioButton>().Count() == 1, "没有 Hi-Res 音源时仍显示 Hi-Res");
        foreach (var vip in new[] { false, true })
        {
            ViewModel.IsVip = vip;
            var choosing = ChooseDownloadOptionsAsync(song);
            await Task.Delay(100);
            var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot);
            var dialog = popups.Select(p => FindVisual<ContentDialog>(p.Child)).FirstOrDefault(d => d is not null);
            Check(dialog is not null && dialog.PrimaryButtonText == "下载" && dialog.CloseButtonText == "取消", "下载未显示原生音质选择对话框");
            var options = (StackPanel)dialog!.Content;
            var high = options.Children.OfType<RadioButton>().Last();
            Check(high.IsEnabled == vip && high.Visibility == (vip ? Visibility.Visible : Visibility.Collapsed), "下载仍显示不存在的 Hi-Res 或未应用会员权限");
            await SaveVisualAsync(Path.Combine(App.VisualCheckDirectory!, vip ? "download-vip.png" : "download-standard.png"), dialog);
            if (vip) high.IsChecked = true;
            var button = FindNamedButton(dialog, vip ? "PrimaryButton" : "CloseButton");
            Check(button is not null, "下载对话框缺少操作按钮");
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button!);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            var selected = await choosing;
            Check(vip ? selected is { Bitrate: 320000 } && selected.Source.Quality.StartsWith("Hi-Res") : selected is null,
                "下载对话框没有传递所选音质或取消未生效");
        }
        ViewModel.IsVip = false;
    }
    private static Button? FindNamedButton(DependencyObject root, string name)
    {
        if (root is Button button && button.Name == name) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindNamedButton(VisualTreeHelper.GetChild(root, i), name) is { } found) return found;
        return null;
    }
    private async Task VerifyStartupSubtitlesAsync(string directory)
    {
        var first = new Track(31, "BV3xx411c7mD", "启动字幕验证", "离线验证", "", 120, 301,
            LocalFile: Path.Combine(directory, "startup-lyrics.mp3"));
        var second = first with { Aid = 32, Bvid = "BV4xx411c7mD", Cid = 302, LocalFile = Path.Combine(directory, "second-lyrics.mp3") };
        var subtitle = new SubtitleTrack("zh-CN", "中文", new Uri("https://i0.hdslb.com/isolated-startup-subtitles.json"), false);
        foreach (var (track, text) in new[] { (first, "启动即载入的歌词"), (second, "切换后应显示的歌词") })
        {
            // A sidecar is enough: startup loading must never attempt to decode or play this file.
            File.WriteAllText(Path.ChangeExtension(track.LocalFile, ".subtitles.json"), JsonSerializer.Serialize(new[] { new CachedSubtitle(track == first ? subtitle with { IsAuto = true } : subtitle, new[] { new SubtitleLine(5, 120, text) }) }));
        }
        ViewModel.Store.WriteTracks("queue:123", new[] { first, second });
        ViewModel.Store.SetSetting("queueCurrent:123", $"{first.Bvid}:{first.Page}");
        await ViewModel.EnterAccountAsync(false);
        Check(QueueSelection.Same(ViewModel.CurrentTrack, first), "启动未恢复保存的当前曲目");
        Check(ViewModel.Lines.Count == 1 && ViewModel.Lines[0].Content == "启动即载入的歌词" && ViewModel.SelectedSubtitle is not null,
            "尚未播放时未自动载入字幕");
        Check(ViewModel.SelectedSubtitle is { IsAuto:false, DisplayName:"中文" }, "旧下载中的创作者字幕仍然显示 AI 尾缀");
        Check(ViewModel.ActiveSubtitleIndex == 0, "尚未播放且首句尚未开始时未默认高亮第一句");
        ViewModel.Seek(1); Check(ViewModel.ActiveSubtitleIndex == -1, "预览高亮错误覆盖真实前奏时间");
        ViewModel.Seek(6); Check(ViewModel.ActiveSubtitleIndex == 0, "预览跳转未按字幕时间定位");
        ViewModel.Seek(0); Check(ViewModel.ActiveSubtitleIndex == 0, "回到尚未播放的起点后未恢复首句预览");
        Check(ViewModel.Player.Current is null && !ViewModel.Player.Playing, "加载启动字幕意外启动了音频");
        ViewModel.RetrySubtitlesCommand.Execute(null);
        Check(ViewModel.Lines.Count == 1 && ViewModel.SelectedSubtitle is not null, "未播放时刷新字幕失败");
        ViewModel.RemoveQueueCommand.Execute(first);
        Check(QueueSelection.Same(ViewModel.CurrentTrack, second) && ViewModel.Lines.Count == 1 && ViewModel.Lines[0].Content == "切换后应显示的歌词",
            "停止状态切换当前曲目后残留旧字幕");
        ViewModel.ClearQueueCommand.Execute(null);
        Check(ViewModel.CurrentTrack is null && ViewModel.Lines.Count == 0 && ViewModel.SubtitleTracks.Count == 0 && ViewModel.ActiveSubtitleIndex == -1,
            "清空播放列表后仍残留歌词或高亮");
        Check(ViewModel.Player.Current is null && !ViewModel.Player.Playing, "恢复或刷新字幕改变了播放状态");
    }
    private async Task VerifyPlaybackMarkersAsync(Track song, string directory)
    {
        var artwork = new BitmapImage(new Uri(Path.Combine(directory,"cover.png")));
        var other = song with { Bvid = "BV2xx411c7mD", Title = "另一首歌" };
        var nextPart = song with { Page = 2, Cid = 456, Title = "同一视频的另一分 P" };
        var listing = new[] { song with { Cid = 0 }, other, nextPart };
        ViewModel.Queue.Add(other); ViewModel.Queue.Add(nextPart);
        async Task AssertRowsAsync(ListViewBase list, string context)
        {
            await Task.Delay(100); UpdateLayout();
            for (var i = 0; i < 3; i++)
            {
                var container = list.ContainerFromIndex(i) as DependencyObject;
                var marker = container is null ? null : FindVisual<NowPlayingIndicator>(container);
                var cover = container is null ? null : FindVisual<CenteredCover>(container);
                Check(cover is not null && cover.ActualWidth >= 48 && Math.Abs(cover.ActualWidth-cover.ActualHeight)<1, $"{context} 缺少正方形封面");
                // Offline fixture exercises the real cover surface without live CDN requests.
                cover!.Source = artwork;
                Check(marker is not null, $"{context} 缺少播放标记");
                Check(marker!.IsCurrent == (i == 0), $"{context} 播放标记匹配错误，尤其需要区分分 P");
            }
            ViewModel.CurrentTrack = other;
            Check(FindVisual<NowPlayingIndicator>((DependencyObject)list.ContainerFromIndex(0))?.IsCurrent == false && FindVisual<NowPlayingIndicator>((DependencyObject)list.ContainerFromIndex(1))?.IsCurrent == true, $"{context} 切歌后标记未实时同步");
            ViewModel.CurrentTrack = song;
        }
        await AssertRowsAsync(PlaylistList, "播放列表页面");
        ViewModel.ShowQueue = true; await AssertRowsAsync(QueueList, "右侧播放列表");
        await Task.Delay(280); await SaveVisualAsync(Path.Combine(directory,"queue-covers.png")); ViewModel.ShowQueue = false;
        foreach (var view in new[] { "history", "search", "folder", "cache", "home" })
        {
            ViewModel.Navigate(view); ViewModel.HomeGridMode = false; ViewModel.FolderTracksGridMode = false;
            ViewModel.Tracks.Clear(); foreach (var track in listing) ViewModel.Tracks.Add(track);
            ViewModel.IsEmpty = false;
            await AssertRowsAsync(TrackList, view);
        }
        ViewModel.HomeGridMode = true; await AssertRowsAsync(RecommendationGrid, "首页图标");
        ViewModel.Navigate("folder"); ViewModel.FolderTracksGridMode = true;
        ViewModel.Tracks.Clear(); foreach (var track in listing) ViewModel.Tracks.Add(track);
        ViewModel.IsEmpty = false; await AssertRowsAsync(RecommendationGrid, "收藏夹图标");
        ViewModel.Navigate("cache"); ViewModel.CacheTabIndex = 1;
        foreach (var track in listing) ViewModel.Downloads.Add(new(track, 123, directory, new CancellationTokenSource()));
        await AssertRowsAsync(DownloadsList, "正在下载");
        await SaveVisualAsync(Path.Combine(directory,"downloading-covers.png"));
        ViewModel.Downloads.Clear(); ViewModel.CacheTabIndex = 0;
        ViewModel.Navigate("playlist");
        await Task.Delay(100); await SaveVisualAsync(Path.Combine(directory, "playlist-markers.png"));
        // No queue selection must leave any row marked.
        ViewModel.CurrentTrack = null;
        Check(FindVisual<NowPlayingIndicator>((DependencyObject)PlaylistList.ContainerFromIndex(0))?.IsCurrent == false, "空播放状态仍有声波标记");
        ViewModel.CurrentTrack = song;
        ViewModel.Queue.Clear();
        if (!QueueButton.IsEnabled) throw new InvalidOperationException("空播放列表的底栏入口被禁用");
        ViewModel.ShowQueue = true; await Task.Delay(180);
        Check(QueueHost.IsPaneOpen && !ViewModel.HasQueue, "空播放列表不能展开");
        ViewModel.ShowQueue = false;
        ViewModel.Queue.Add(song); ViewModel.Queue.Add(other); ViewModel.Queue.Add(nextPart);
    }
    private void VerifyLyricSeparation()
    {
        var now=MotionTime; var scroll=_lyricScrollMotion.At(now-_lyricScrollStarted).Position.Y;
        double? previousBottom=null; var previousIndex=-2;
        for(var row=0;row<ViewModel.Lines.Count;row++)
        {
            if(LyricsList.ContainerFromIndex(row) is not ListViewItem item || FindVisual<TextBlock>(item) is not { } text
                || VisualTreeHelper.GetParent(text) is not Grid host || !_lyricMotionStates.TryGetValue(text,out var state)) continue;
            if(text.ActualHeight < 1) continue;
            Check(host.Padding.Top+.6>=LyricMotionLayout.Padding(text.ActualHeight),"长歌词没有为缩放保留空间");
            var top=host.TransformToVisual(LyricsList).TransformPoint(new(0,0)).Y+host.Padding.Top;
            var size=state.Scale.At(now-state.ScaleStarted).Position.Y;
            var ripple=state.Translation.At(now-state.Started).Position.Y;
            var bounds=LyricMotionLayout.Bounds(top,text.ActualHeight,size,ripple,scroll);
            if(row==previousIndex+1 && previousBottom is { } bottom)
                Check(bounds.Top-bottom>=8,$"歌词上收重叠：第 {row-1}/{row} 行间距 {bounds.Top-bottom:F2}");
            previousBottom=bounds.Bottom; previousIndex=row;
        }
    }
    private static void Check(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private async Task SaveVisualAsync(string path, UIElement? element = null)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(element ?? App.Window.Content);
        var pixels = await bitmap.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path),CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels.ToArray());
        await encoder.FlushAsync();
    }
}
