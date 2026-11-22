using BiliPlay.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;

namespace BiliPlay;

// Sleep timer ("定时关闭") UI: footer button, preset menu and the custom-minutes dialog.
public sealed partial class MainPage
{
    private async void SleepTimerOpening(object sender, object args)
    {
        if (sender is not MenuFlyout menu) return;
        var minutes = ViewModel.SleepTimerMinutes;
        foreach (var item in menu.Items.OfType<RadioMenuFlyoutItem>())
        {
            var tag = item.Tag as string;
            if (tag == "custom")
            {
                item.IsChecked = ViewModel.IsSleepTimerActive && ViewModel.IsSleepTimerCustom;
                continue;
            }
            var preset = int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
            item.IsChecked = preset == 0
                ? !ViewModel.IsSleepTimerActive
                : ViewModel.IsSleepTimerActive && !ViewModel.IsSleepTimerCustom && preset == minutes;
        }
    }

    private void SleepTimerOptionClick(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (tag == "custom") { _ = ShowSleepTimerCustomAsync(); return; }
        if (int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
            ViewModel.SetSleepTimerMinutes(minutes == 0 ? null : minutes);
    }

    private async Task ShowSleepTimerCustomAsync()
    {
        var box = new NumberBox
        {
            Minimum = 1, Maximum = SleepTimer.MaxMinutes, Value = ViewModel.IsSleepTimerActive && ViewModel.SleepTimerCustomMinutes > 0 ? ViewModel.SleepTimerCustomMinutes : 30,
            SmallChange = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Header = $"分钟（1-{SleepTimer.MaxMinutes}）"
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "自定义定时关闭", Content = box,
            PrimaryButtonText = "开始", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        ViewModel.SetSleepTimerMinutes((int)Math.Clamp(Math.Round(box.Value), 1, SleepTimer.MaxMinutes));
    }
}
