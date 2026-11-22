using BiliPlay.Services;
using BiliPlay.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace BiliPlay;
public sealed partial class MainPage
{
    private async Task VerifyDebugModeAsync(string directory)
    {
        Check(!ViewModel.IsLoggedIn,"调试入口仅应在未登录时检查");
        var confirmation=ConfirmDebugModeAsync();await Task.Delay(100);
        var dialog=VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Select(p=>FindVisual<ContentDialog>(p.Child)).FirstOrDefault(d=>d is not null);
        Check(dialog?.Title as string=="进入离线调试模式？","双击版本号没有确认对话框");dialog!.Hide();await confirmation;
        Check(!DebugSession.IsActive&&!ViewModel.IsLoggedIn,"取消后进入了调试模式");
        var parent=System.IO.Path.Combine(directory,"debug-sessions");string? root=null;
        try
        {
            DebugSession.Begin(parent);root=DebugSession.DirectoryPath;ArtworkCache.ResetScope();
            using(var model=new PlayerViewModel())
            {
                await model.InitializeAsync();
                Check(model.IsDebugMode&&model.IsLoggedIn&&model.Tracks.Count==8&&model.Queue.Count==3,"离线演示未载入示例数据");
                Check(model.Queue.All(t=>System.IO.File.Exists(t.LocalFile))&&SubtitleCache.Read(model.Queue[0]).Count==1,"示例音频和字幕缺失");
                model.Store.SetSetting("debug-fixture","temporary");model.Navigate("library");
                Check(model.Folders.Count==1&&!PlayerViewModel.IsOnline,"调试模式没有使用离线示例收藏夹");
                SessionVault.Save([new("demo","fixture",".bilibili.com","/",DateTime.UtcNow.AddDays(1))]);Check(SessionVault.Read().Count==0,"调试写入了登录凭据");SessionVault.Clear();
                Check(model.CacheDirectory.StartsWith(root!,StringComparison.OrdinalIgnoreCase),"调试下载目录不在临时数据中");
            }
            ArtworkCache.ResetScope();DebugSession.End();Check(!DebugSession.IsActive&&!System.IO.Directory.Exists(root),"退出调试后数据未清理");
            DebugSession.Begin(parent);
            using(var store=new LocalStore(DebugSession.DirectoryPath,true))Check(store.GetSetting("debug-fixture","")=="","新会话继承了上次调试数据");
            DebugSession.End();
        }
        finally {ArtworkCache.ResetScope();DebugSession.End();}
    }
    private async Task VerifyDebugEntryAsync()
    {
        ViewModel.IsLoggedIn=false;
        var confirmation=ConfirmDebugModeAsync();await Task.Delay(100);
        var dialog=VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Select(p=>FindVisual<ContentDialog>(p.Child)).FirstOrDefault(d=>d is not null);
        Check(dialog is not null,"进入调试前缺少确认框");
        var button=FindNamedButton(dialog!,"PrimaryButton")!;
        ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();
        await confirmation;
        var window=(MainWindow)App.Window;
        for(var i=0;i<50&&!window.ViewModel.IsLoggedIn;i++)await Task.Delay(20);
        var demo=window.ViewModel;
        Check(!ReferenceEquals(demo,ViewModel)&&demo.IsDebugMode&&demo.IsLoggedIn&&demo.Tracks.Count==8,"确认后没有切换到离线调试界面");
        Check(window.Title.Contains("离线调试")&&demo.AccountLogoutVisibility==Visibility.Collapsed,"调试模式标记或退出入口失效");
        // Exercise the same home activation path as a user, including metadata resolution.
        await demo.ActivateTrackAsync(demo.Tracks[4],false);
        Check(demo.CurrentTrack?.Aid==5&&demo.Player.Current?.Aid==5,"离线曲目点击后没有载入本地音频");
        Check(demo.SubtitleTracks.Count==1&&demo.Lines.Count==6,"离线歌词没有载入");
        demo.Player.Pause();
        demo.SearchText="示例曲目 7";await demo.SearchCommand.ExecuteAsync(null);
        Check(demo.Tracks.Count==1&&demo.Tracks[0].Aid==7,"离线搜索没有筛选示例曲目");
    }

}
