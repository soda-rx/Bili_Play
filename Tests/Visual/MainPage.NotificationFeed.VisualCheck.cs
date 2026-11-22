using Microsoft.UI.Xaml.Controls;

namespace BiliPlay;

public sealed partial class MainPage
{
    private async Task VerifyNotificationVirtualizationAsync()
    {
        var original = ViewModel.AccountNotifications.ToArray(); var category = ViewModel.NotificationCategoryIndex;
        try
        {
            ViewModel.AccountNotifications.Clear();
            for (var index = 0; index < 500; index++) ViewModel.AccountNotifications.Add(new("私信", "合成会话 " + index, "离线通知虚拟化验证", "", TalkerId: index + 1));
            ViewModel.NotificationCategoryIndex = 1; ViewModel.NotificationCategoryIndex = 0;
            ViewModel.Navigate("notifications"); UpdateLayout(); await Task.Delay(120); UpdateLayout();
            var scroll = FindVisual<ScrollViewer>(NotificationList);
            var realized = Enumerable.Range(0, 500).Count(index => NotificationList.ContainerFromIndex(index) is not null);
            Check(NotificationList.Items.Count == 500 && realized > 0 && realized < 100 && scroll?.ScrollableHeight > 0,
                "通知列表没有恢复虚拟化或未使用有界滚动区域");
            var pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            App.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () => pulse.TrySetResult());
            await pulse.Task.WaitAsync(TimeSpan.FromSeconds(1));
            NotificationList.ScrollIntoView(NotificationList.Items.Last()); await Task.Delay(100); UpdateLayout();
            Check(NotificationList.ContainerFromIndex(499) is not null, "通知列表无法滚动到后续内容");
        }
        finally
        {
            ViewModel.AccountNotifications.Clear(); foreach (var item in original) ViewModel.AccountNotifications.Add(item);
            ViewModel.NotificationCategoryIndex = category == 0 ? 1 : 0; ViewModel.NotificationCategoryIndex = category;
            ViewModel.Navigate("home");
        }
    }
}
