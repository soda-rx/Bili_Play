using System.Collections.Specialized;
using System.Numerics;
using BiliPlay.Core;
using BiliPlay.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace BiliPlay;

public sealed partial class MainPage
{
    private void VerifyFooterGeometry()
    {
        foreach(var group in new[]{FooterActions,TransportGroup})
        {
            double? right=null;double? top=null;
            foreach(var child in group.Children.OfType<FrameworkElement>())
            {
                var point=child.TransformToVisual(group).TransformPoint(new(0,0));
                Check(Math.Abs(child.ActualWidth-48)<.1&&Math.Abs(child.ActualHeight-48)<.1,"底栏按钮与点击区域不是统一的 48×48");
                if(right is { } edge) Check(Math.Abs(point.X-edge-8)<.2,"底栏按钮间距不相等");
                if(top is { } y) Check(Math.Abs(point.Y-y)<.2,"底栏按钮高度位置不一致");
                right=point.X+child.ActualWidth;top=point.Y;
            }
        }
    }
    private async Task VerifyPageEntrancesAsync()
    {
        var song=new Track(71,"BV7xx411c7mD","页面动效验证","UP","",240,71);
        var oldHome=ViewModel.HomeGridMode;var oldFolders=ViewModel.FolderGridMode;var oldFolderTracks=ViewModel.FolderTracksGridMode;
        ViewModel.Queue.Add(song);ViewModel.Folders.Add(new(71,"页面验证收藏夹",0,true));
        try
        {
            foreach(var view in new[]{"home","library","history","playlist","cache","search","folder","settings"})
            foreach(var grid in new[]{true,false})
            {
                ViewModel.HomeGridMode=grid;ViewModel.FolderGridMode=grid;ViewModel.FolderTracksGridMode=grid;
                ViewModel.Navigate(view);
                Check(ElementCompositionPreview.GetElementVisual(BrowseSurface).Opacity==0,$"{view} 切换后在动画排队前暴露了完成态");
                if(view is not ("settings" or "library" or "playlist")) {ViewModel.Tracks.Clear();ViewModel.Tracks.Add(song);}
                ViewModel.IsEmpty=false;await Task.Delay(80);UpdateLayout();await Task.Delay(40);
                Check(_browseMotionElements.Contains(BrowseSurface),$"{view} 没有页面入场动画");
                if(view=="settings") Check(SettingsOptions.Children.All(child=>_browseMotionElements.Contains(child)),"设置项没有逐项入场");
                else Check(_browseRowsEntered&&_browseEntranceRows>0,$"{view} 的列表／图标没有逐项入场");
                var version=_browseTransition;var count=_browseMotionElements.Count;
                ViewModel.CurrentTrack=song;
                if(view=="home") ViewModel.Tracks.Add(song with {Bvid="BV8xx411c7mD"});
                await Task.Delay(25);
                Check(version==_browseTransition,"切歌或增量加载重播了整页动画");
                Check(_enteredBrowseItems.Contains(BrowseItemKey(view=="library"?ViewModel.Folders[0]:song))||view=="settings","已有条目因追加内容丢失动画状态");
            }
            ViewModel.Navigate("settings");ViewModel.Navigate("library");ViewModel.Navigate("playlist");
            Check(ElementCompositionPreview.GetElementVisual(BrowseSurface).Opacity==0,"连续切页暴露了未初始化的新页面");
            await Task.Delay(650);
            Check(ViewModel.IsPlaylistView&&ElementCompositionPreview.GetElementVisual(BrowseSurface).Opacity==1,"旧入场回调覆盖新页面或页面未恢复显示");
            _forceReducedMotion=true;ViewModel.Navigate("settings");
            Check(ElementCompositionPreview.GetElementVisual(BrowseSurface).Opacity==1,"减少动画模式短暂隐藏了页面");await Task.Delay(60);
            Check(ElementCompositionPreview.GetElementVisual(BrowseSurface).Opacity==1,"减少动画模式仍隐藏页面");
        }
        finally
        {
            _forceReducedMotion=false;ViewModel.HomeGridMode=oldHome;ViewModel.FolderGridMode=oldFolders;ViewModel.FolderTracksGridMode=oldFolderTracks;
            ViewModel.ClearQueueCommand.Execute(null);ViewModel.Folders.Clear();ViewModel.Tracks.Clear();ViewModel.Navigate("home");
        }
    }
    private async Task VerifyStableQueueActivationAsync()
    {
        foreach(var playlistView in new[]{true,false})
        foreach(var behavior in new[]{0,1,2})
        foreach(var twice in new[]{false,true})
        {
            using var handler=new DeferredVideoHandler();using var model=new PlayerViewModel(new BilibiliClient(handler));
            model.Client.RestoreCachedAccount(new(123,"播放列表稳定性验证",""));model.IsLoggedIn=true;
            model.SingleClickBehaviorIndex=model.DoubleClickBehaviorIndex=behavior;
            var first=new Track(1,"BV1xx411c7mD","第一首","UP","",120,111);
            var second=new Track(2,"BV2xx411c7mD","第二首","UP","",120);
            model.Queue.Add(first);model.Queue.Add(second);
            model.Navigate(playlistView?"playlist":"home");
            var changes=new List<NotifyCollectionChangedAction>();model.Queue.CollectionChanged+=(_,args)=>changes.Add(args.Action);
            model.ConfirmQueueChange=_=>throw new InvalidOperationException("选择队列内已有曲目不应询问");
            var task=model.ActivateTrackAsync(second,twice,!playlistView);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(changes.Count==0&&model.Queue.Count==2&&ReferenceEquals(model.Queue[0],first)&&ReferenceEquals(model.Queue[1],second),"播放列表切歌重建或替换了列表");
            Check(QueueSelection.Same(model.CurrentTrack,second),"播放列表切歌没有立即切换当前项");
            model.ClearQueueCommand.Execute(null);handler.Release.TrySetResult();await task;
        }
        ViewModel.Store.SetSetting("singleClickBehavior",ViewModel.SingleClickBehaviorIndex.ToString());
        ViewModel.Store.SetSetting("doubleClickBehavior",ViewModel.DoubleClickBehaviorIndex.ToString());
    }
    private async Task VerifyBrowseAndNotificationsAsync(string directory)
    {
        var oldHome=ViewModel.HomeGridMode;
        try
        {
            // Leave home first so any in-flight home load is cancelled, then enter a forced-empty
            // home page: an empty page must not consume the icon entrance animation.
            ViewModel.Navigate("settings");
            ViewModel.PrepareEmptyHomeForTests();
            ViewModel.HomeGridMode=true;
            ViewModel.Navigate("home");
            UpdateLayout();
            await Task.Delay(180);
            Check(!_browseRowsEntered && ViewModel.Tracks.Count == 0,"空首页提前消耗了图标甩入动画");
            for(var i=0;i<8;i++) ViewModel.Tracks.Add(new(800+i,$"BV{i}xx411c7mD",$"首页动画 {i}","合成 UP 主","",100));
            UpdateLayout();await Task.Delay(100);
            Check(_browseEntranceRows>0&&_browseRowsEntered,"异步加载的首页图标没有甩入");
            var template=RecommendationGrid.ContainerFromIndex(0) as GridViewItem;
            Check(template?.ContentTemplateRoot is UIElement content&&_browseMotionElements.Contains(content),"图标没有在模板内容上执行甩入");
            Check(template?.ContentTemplateRoot is FrameworkElement tile && tile.Margin == new Thickness(0)
                && Math.Abs(tile.ActualWidth-template.ActualWidth)<1 && Math.Abs(tile.ActualHeight-template.ActualHeight)<1
                && template.FocusVisualMargin.Left >= 0, "首页图标悬浮区域包含了卡片外间距");
            var lateTrack=new Track(999,"BV9xx411c7mD","第二批推荐","合成 UP 主","",100);
            var previousVersion=_browseTransition;
            ViewModel.Tracks.Add(lateTrack);
            RecommendationGrid.ScrollIntoView(lateTrack);UpdateLayout();await Task.Delay(120);
            Check(_enteredBrowseItems.Contains(BrowseItemKey(lateTrack)),"后续批次推荐直接出现，跳过了甩入");
            Check(_browseTransition==previousVersion,"后续批次重播了整个首页");
            for(var click=0;click<3;click++)
            {
                var version=_browseTransition;ViewModel.ReplayBrowseEntrance();
                Check(_browseTransition>version,"重复点击首页未重新启动甩入");
                await Task.Delay(70);
            }
            await Task.Delay(750);
            Check(ElementCompositionPreview.GetElementVisual(RecommendationGrid).Opacity==1,"快速重播后首页不可见");
            ViewModel.Navigate("notifications");
            ViewModel.AccountNotifications.Clear();
            ViewModel.AccountNotifications.Add(new("回复与@","合成用户甲、合成用户乙","", "2026-11-06 10:30", IsLike:true,TargetText:new string('评',100),TargetKind:"评论",ActorCount:38,ActorAvatars:["",""]));
            ViewModel.AccountNotifications.Add(new("回复与@","另一位合成用户","一条简短的回复", "2026-11-06 10:20",TargetText:"原评论缩略文本"));
            ViewModel.NotificationCategoryIndex=1;
            Check(NotificationCategoryPicker.Items.Count==4&&NotificationCategoryPicker.Items[1] as string=="收到的赞","通知中心缺少收到的赞或顺序错误");
            await Task.Delay(450);UpdateLayout();
            Check(NotificationList.Items.Count==1&&NotificationList.Items[0] is AccountNotification{IsLike:true},"点赞与回复分类没有分开");
            Check(NotificationList.ContainerFromIndex(0) is ListViewItem notice&&notice.ContentTemplateRoot is UIElement body&&_browseMotionElements.Contains(body),"通知条目没有甩入");
            ViewModel.IsNotificationsLoading=true;UpdateLayout();
            Check(NotificationLoadingRow.Visibility==Visibility.Visible&&!ViewModel.HasNotificationStatus,"通知读取提示没有与进度指示放在同一行");
            ViewModel.IsNotificationsLoading=false;ViewModel.NotificationCategoryIndex=2;await Task.Delay(450);UpdateLayout();
            Check(NotificationList.Items.Count==1&&NotificationList.Items[0] is AccountNotification{IsLike:false},"回复分类仍显示点赞条目");
            for(var i=0;i<NotificationList.Items.Count;i++)
            {
                if(NotificationList.ContainerFromIndex(i) is ListViewItem row)
                {
                    Check(row.ActualWidth<=860,"通知行超出最大宽度");Check(row.ActualHeight<150,"通知引用文字未截断，撑高了整行");
                    Check(row.ContentTemplateRoot is Grid surface && HoverSurface.GetEnabled(surface) && surface.Children.OfType<Border>().FirstOrDefault() is { } card && card.Margin == new Thickness(0)
                        && row.Padding == new Thickness(0) && Math.Abs(card.ActualWidth-row.ActualWidth)<1 && Math.Abs(card.ActualHeight-row.ActualHeight)<1,
                        "通知条目的悬浮区域超出卡片边框");
                    Check(FindVisual<Microsoft.UI.Xaml.Controls.Primitives.ListViewItemPresenter>(row)?.CornerRadius == new CornerRadius(8), "通知悬浮背景圆角与卡片不一致");
                    var gap = row.TransformToVisual((UIElement)XamlRoot.Content).TransformPoint(new(10,row.ActualHeight+4));
                    Check(!VisualTreeHelper.FindElementsInHostCoordinates(gap,this,false).Contains(row), "通知间距仍能命中上一条通知");
                    var right=row.TransformToVisual(NotificationsScroller).TransformPoint(new(row.ActualWidth,0)).X;
                    Check(right<=NotificationsScroller.ActualWidth-20,"通知缩略图超出了页面可见范围");
                }
            }
            await SaveVisualAsync(Path.Combine(directory,"notification-cards.png"));
        }
        finally
        { ViewModel.AccountNotifications.Clear();ViewModel.HomeGridMode=oldHome;ViewModel.Navigate("home"); }
    }
    private async Task VerifyInterruptibleFooterAsync()
    {
        ViewModel.IsLyricsOpen=false;await _lyricsMotionTask;
        _footerInputSerial++;_footerInputAt=MotionTime;var first=ToggleLyricsFromInputAsync();
        Check(ViewModel.IsLyricsOpen&&!first.IsCompleted,"点击没有立即打开详情");
        await ToggleLyricsFromInputAsync();Check(ViewModel.IsLyricsOpen,"同一次按下／点击／双击事件被重复处理");
        await Task.Delay(48);
        var before=_coverFlight!.Current();var opacity=_lyricsOpacityTween!.Sample().Position.X;
        _footerInputSerial++;_footerInputAt=MotionTime;var second=ToggleLyricsFromInputAsync();
        Check(!ViewModel.IsLyricsOpen&&!second.IsCompleted,"动画期间的新点击被忽略");
        var initial=_coverFlight!.Tween.Motion.Initial;
        Check(Math.Abs(initial.X-before.X)<24&&Math.Abs(initial.Y-before.Y)<24&&Math.Abs(initial.Z-before.Width)<24,"打断动画时封面跳回了端点");
        Check(Math.Abs(_lyricsOpacityTween!.Motion.Initial.X-opacity)<.12,"反向时透明度闪回端点");
        await first.WaitAsync(TimeSpan.FromMilliseconds(80));
        await Task.Delay(42);
        _footerInputSerial++;_footerInputAt=MotionTime;var third=ToggleLyricsFromInputAsync();
        Check(ViewModel.IsLyricsOpen,"第二次打断没有响应最新点击");
        await second.WaitAsync(TimeSpan.FromMilliseconds(80));await third;
        Check(_coverFlight is null&&LyricsView.Visibility==Visibility.Visible,"旧动画结束覆盖了最新展开状态");
        _footerInputSerial++;_footerInputAt=MotionTime;await ToggleLyricsFromInputAsync();
        Check(!ViewModel.IsLyricsOpen&&LyricsView.Visibility==Visibility.Collapsed,"最后一次点击未正确收起");
        _footerInputSerial=0;_footerConsumedSerial=-1;
    }
}
