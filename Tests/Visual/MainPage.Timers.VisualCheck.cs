using BiliPlay.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliPlay;

// Visual checks for resume position, lyrics visibility and the sleep timer.
public sealed partial class MainPage
{
    private async Task VerifyPlaybackTimersAsync(Track song, string directory)
    {
        ViewModel.Queue.Clear();
        ViewModel.Queue.Add(song);
        ViewModel.UpdateCurrentDisplayForTests(song);
        ViewModel.Duration = 240;

        Check(TimersHeaderPanel.Children.Contains(SleepTimerButton), "定时停止入口缺失");
        Check(FooterActions.Children.Last() == QueueButton, "底栏不再以播放列表按钮结尾");
        Check(LyricsList.ContextFlyout is null, "歌词仍有已移除的区间循环菜单");

        // Saved progress is painted on the slider before playback starts (session resume).
        ViewModel.Store.RecordRecentPlays = true;
        var resumeAccount = ViewModel.Client.Account?.Id ?? 1;
        ViewModel.Store.SaveProgress(resumeAccount, song, 95);
        ViewModel.UpdateCurrentDisplayForTests(song);
        var restored = ViewModel.RestoreResumePositionForTests(song);
        Check(Math.Abs(restored - 95) < 0.01 && Math.Abs(ViewModel.Position - 95) < 0.01, "没有恢复上次播放进度");
        Check(ViewModel.PositionText == "1:35" && ViewModel.RemainingText == "−2:25", "恢复进度后播放条时间不正确");
        ViewModel.Seek(0);

        // The lyrics language picker can turn the whole lyrics surface off again.
        var off = ViewModel.SubtitleChoices.FirstOrDefault(choice => choice.Track is null);
        Check(off is { DisplayName: "关闭歌词" }, "歌词语言下拉框没有关闭歌词选项");
        ViewModel.SelectedSubtitleChoice = off;
        Check(ViewModel.LyricsHidden && ViewModel.Lines.Count == 0, "选择关闭歌词后仍显示歌词");
        Check(ViewModel.SubtitleStatus.Contains("歌词已关闭", StringComparison.Ordinal), "关闭歌词后没有状态提示");
        Check(ViewModel.SelectedSubtitleChoice?.Track is null, "关闭歌词后下拉框没有选中关闭项");
        var language = ViewModel.SubtitleChoices.FirstOrDefault(choice => choice.Track is not null);
        if (language is not null)
        {
            ViewModel.SelectedSubtitleChoice = language;
            Check(!ViewModel.LyricsHidden, "重新选择字幕语言后歌词仍处于关闭状态");
        }
        else
        {
            ViewModel.SetLyricsHidden(false);
        }

        // Sleep timer: preset, custom and expiry.
        ViewModel.SetSleepTimerMinutes(15);
        Check(ViewModel.IsSleepTimerActive && ViewModel.SleepTimerMinutes == 15 && !ViewModel.IsSleepTimerCustom, "15 分钟定时关闭状态不正确");
        Check(ViewModel.SleepTimerButtonText is "15:00" or "14:59", "定时关闭按钮没有显示倒计时");
        Check(SleepTimerText.Visibility == Visibility.Visible && SleepTimerIcon.Visibility == Visibility.Collapsed, "定时关闭按钮没有切换为倒计时显示");
        SleepTimerOpening(SleepTimerMenu, new object());
        Check(SleepTimer15Item.IsChecked && !SleepTimerOffItem.IsChecked, "定时关闭菜单的选中状态不正确");
        Check(SleepTimerMenu.Items.Count == 5, "定时关闭菜单仍显示剩余时间项");
        Check(SleepTimerOffItem.Text == "关", "关闭定时播放的菜单项文案没有改为“关”");

        ViewModel.SetSleepTimerMinutes(45);
        Check(ViewModel.SleepTimerMinutes == 45 && ViewModel.IsSleepTimerCustom && ViewModel.SleepTimerCustomMinutes == 45, "自定义定时关闭状态不正确");
        SleepTimerOpening(SleepTimerMenu, new object());
        Check(SleepTimerCustomItem.IsChecked && !SleepTimer15Item.IsChecked, "自定义定时关闭没有选中自定义项");

        await Task.Delay(60);
        UpdateLayout();
        if (!Environment.GetCommandLineArgs().Contains("--resume-only")) await SaveVisualAsync(Path.Combine(directory, "playback-timers.png"));

        ViewModel.ExpireSleepTimerForTests();
        Check(!ViewModel.IsSleepTimerActive && ViewModel.SleepTimerButtonText.Length == 0, "定时关闭到期后没有清除计时");
        Check(SleepTimerText.Visibility == Visibility.Collapsed && SleepTimerIcon.Visibility == Visibility.Visible, "定时关闭到期后没有恢复计时图标");
        Check(ViewModel.Notifications.Any(notice => notice.Message.Contains("定时关闭", StringComparison.Ordinal)), "定时关闭到期没有提示");

        ViewModel.SetSleepTimerMinutes(5);
        ViewModel.ClearSleepTimer();
        Check(!ViewModel.IsSleepTimerActive && !ViewModel.IsSleepTimerCustom, "手动清除定时关闭失败");
        Check(SleepTimerButton.IsEnabled, "定时关闭按钮在无当前曲目时被错误禁用");
    }
}
