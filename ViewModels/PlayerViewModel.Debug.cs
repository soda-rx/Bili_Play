using BiliPlay.Services;

namespace BiliPlay.ViewModels;
public partial class PlayerViewModel
{
    public bool IsDebugMode => DebugSession.IsActive;
    public Microsoft.UI.Xaml.Visibility DebugModeVisibility => IsDebugMode ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility AccountLogoutVisibility => IsDebugMode ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public Microsoft.UI.Xaml.Visibility NewProfileCancelVisibility => ProfileManager.IsNewProfile ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    private async Task InitializeDebugAsync()
    {
        var tracks=DebugSession.CreateTracks();Client.RestoreCachedAccount(new(1,"离线调试",""));
        Store.SaveMusicCache(1,tracks);Store.WriteTracks("queue:1",tracks.Take(3));
        Store.SaveProgress(1,tracks[0],0);await EnterAccountAsync(false);
        _hasLoadedHome=true;_homeMore=false;HasMore=false;
        Folders.Add(new(1,"示例收藏夹",tracks.Count,true,false));
        AccountName="离线调试";LoginLoading=false;NeedsLogin=false;Navigate("home");
    }
}
