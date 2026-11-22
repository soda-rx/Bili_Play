using BiliPlay.Core;

namespace BiliPlay.ViewModels;

// Sleep timer feature. The deadline is monotonic (Environment.TickCount64) so changing the
// system clock never extends or shortens it. Expiry only pauses playback.
public sealed partial class PlayerViewModel
{
    private long _sleepTimerEndsAt;
    private int _sleepTimerMinutes;
    private int _sleepTimerShownSecond = -1;

    public bool IsSleepTimerActive => _sleepTimerEndsAt > 0;
    public int SleepTimerMinutes => _sleepTimerMinutes;
    public bool IsSleepTimerCustom => _sleepTimerMinutes > 0 && !SleepTimer.Presets.Contains(_sleepTimerMinutes);
    public int SleepTimerCustomMinutes => IsSleepTimerCustom ? _sleepTimerMinutes : 0;
    public string SleepTimerButtonText => IsSleepTimerActive
        ? SleepTimer.Label(SleepTimer.Remaining(_sleepTimerEndsAt, Environment.TickCount64)) : "";
    public string SleepTimerToolTip => IsSleepTimerActive ? $"定时关闭 · 剩余 {SleepTimerButtonText}" : "定时关闭";

    public void SetSleepTimerMinutes(int? minutes)
    {
        var endsAt = SleepTimer.EndsAt(Environment.TickCount64, minutes);
        _sleepTimerEndsAt = endsAt;
        _sleepTimerMinutes = endsAt > 0 ? Math.Clamp(minutes!.Value, 1, SleepTimer.MaxMinutes) : 0;
        _sleepTimerShownSecond = -1;
        UpdateSleepTimer();
        RaiseSleepTimerChanged();
    }

    public void ClearSleepTimer()
    {
        if (_sleepTimerEndsAt == 0) return;
        _sleepTimerEndsAt = 0;
        _sleepTimerMinutes = 0;
        _sleepTimerShownSecond = -1;
        RaiseSleepTimerChanged();
    }

    private void UpdateSleepTimer()
    {
        if (_sleepTimerEndsAt == 0) return;
        var remaining = SleepTimer.Remaining(_sleepTimerEndsAt, Environment.TickCount64);
        if (remaining <= 0) { ExpireSleepTimer(); return; }
        var second = (int)(remaining / 1000);
        if (second == _sleepTimerShownSecond) return;
        _sleepTimerShownSecond = second;
        OnPropertyChanged(nameof(SleepTimerButtonText));
        OnPropertyChanged(nameof(SleepTimerToolTip));
    }

    private void ExpireSleepTimer()
    {
        if (_sleepTimerEndsAt == 0) return;
        _sleepTimerEndsAt = 0;
        _sleepTimerMinutes = 0;
        _sleepTimerShownSecond = -1;
        Player.Pause();
        RaiseSleepTimerChanged();
        Notify("定时关闭已暂停播放。", NoticeLevel.Info);
    }

    private void RaiseSleepTimerChanged()
    {
        OnPropertyChanged(nameof(IsSleepTimerActive));
        OnPropertyChanged(nameof(SleepTimerMinutes));
        OnPropertyChanged(nameof(IsSleepTimerCustom));
        OnPropertyChanged(nameof(SleepTimerCustomMinutes));
        OnPropertyChanged(nameof(SleepTimerButtonText));
        OnPropertyChanged(nameof(SleepTimerToolTip));
    }
}
