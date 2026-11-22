using System.Collections.ObjectModel;
using BiliPlay.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BiliPlay;

public sealed class MessageAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class AvatarColumnConverter : IValueConverter
{
    public object Convert(object value, Type type, object parameter, string language) => value is true ? 2 : 0;
    public object ConvertBack(object value, Type type, object parameter, string language) => throw new NotSupportedException();
}
public sealed class BubbleBrushConverter : IValueConverter
{
    // Own bubbles use the system accent colour and the matching on-accent text colour so they
    // follow the Windows accent / dark-mode choice instead of a fixed brand green.
    public object Convert(object value, Type type, object parameter, string language) => value is true ? Theme("AccentFillColorDefaultBrush") : Theme("CardBackgroundFillColorDefaultBrush");
    public object ConvertBack(object value, Type type, object parameter, string language) => throw new NotSupportedException();
    internal static Brush Theme(string key) => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}
public sealed class BubbleTextBrushConverter : IValueConverter
{
    public object Convert(object value, Type type, object parameter, string language) => value is true ? BubbleBrushConverter.Theme("TextOnAccentFillColorPrimaryBrush") : BubbleBrushConverter.Theme("TextFillColorPrimaryBrush");
    public object ConvertBack(object value, Type type, object parameter, string language) => throw new NotSupportedException();
}
public sealed partial class MainPage
{
    private readonly ObservableCollection<DirectMessage> _conversationMessages = [];
    private CancellationTokenSource? _conversationLifetime;
    private AccountNotification? _conversationItem;
    private long _conversationAccount, _conversationCursor;
    private bool _conversationBusy, _conversationMore;
    private readonly Dictionary<(long Account, long Talker), string> _conversationDrafts = [];
    private readonly HashSet<CancellationTokenSource> _conversationSends = [];
    private ScrollViewer? _conversationScroll;
    private double _conversationLastOffset;
    private bool _conversationScrollReady;
#if VISUAL_CHECKS
    private Func<long, CancellationToken, Task<ConversationPage>>? _conversationReader;
#endif
    private async void NotificationClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not AccountNotification item) return;
        if (item.Category == "私信" && item.TalkerId > 0)
        {
            if (item.SessionType != 1) { ViewModel.Notify("暂不支持群聊会话。", NoticeLevel.Info); return; }
            await ShowConversationAsync(item); return;
        }
    }
    private async Task ShowConversationAsync(AccountNotification item
#if VISUAL_CHECKS
        , Func<long, CancellationToken, Task<ConversationPage>>? read = null
#endif
        )
    {
        if (ViewModel.Client.Account is not { } account || !ViewModel.IsNotificationsView || ViewModel.NotificationCategoryIndex != 0) return;
        CloseConversation();
        _conversationLifetime = new(); _conversationItem = item; _conversationAccount = account.Id;
        NotificationList.SelectedItem = item;
#if VISUAL_CHECKS
        _conversationReader = read;
#endif
        ConversationDraft.Text = _conversationDrafts.GetValueOrDefault((account.Id, item.TalkerId), "");
        ConversationComposer.Visibility = item.CanReply && item.TalkerId != account.Id ? Visibility.Visible : Visibility.Collapsed;
        ConversationDraftChanged(ConversationDraft, null!);
        ConversationTitle.Text = item.Title; ConversationList.ItemsSource = _conversationMessages;
        ConversationList.Visibility = Visibility.Visible;
        UpdateNotificationLayout();
        await LoadConversationAsync(true);
    }
    private bool ConversationCurrent(CancellationTokenSource lifetime) => ReferenceEquals(_conversationLifetime, lifetime) && !lifetime.IsCancellationRequested
        && ViewModel.IsNotificationsView && ViewModel.IsLoggedIn && ViewModel.Client.Account?.Id == _conversationAccount;
    private async Task LoadConversationAsync(bool reset)
    {
        if (_conversationBusy || _conversationLifetime is not { } lifetime || _conversationItem is not { } item || !ConversationCurrent(lifetime)) return;
        _conversationBusy = true;
        ConversationLoading.IsActive = true; ConversationLoading.Visibility = Visibility.Visible; ConversationStatus.Text = "正在读取对话…"; ConversationStatusRow.Visibility = Visibility.Visible;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            // ViewChanged can run before the scroller has arranged its new offset.
            // Leave that callback before capturing the reading anchor or inserting rows.
            await Task.Delay(20, request.Token);
            if (!ConversationCurrent(lifetime)) return;
            ConversationList.UpdateLayout();
            var cursor = reset ? 0 : _conversationCursor;
#if VISUAL_CHECKS
            var page = await (_conversationReader is { } read ? read(cursor, request.Token) : ViewModel.Client.GetConversationAsync(item.TalkerId, cursor, request.Token));
#else
            var page = await ViewModel.Client.GetConversationAsync(item.TalkerId, cursor, request.Token);
#endif
            if (!ConversationCurrent(lifetime)) return;
            var account = ViewModel.Client.Account!;
            _conversationScrollReady = false;
            (DirectMessage Message, double Top)? anchor = reset ? null : CaptureConversationAnchor();
            if (!reset && anchor is null && _conversationMessages.FirstOrDefault() is { } first) anchor = (first, 0);
            if (reset) _conversationMessages.Clear();
            var known = _conversationMessages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var message in page.Items.DistinctBy(message => message.Id).OrderBy(message => message.Sequence))
            {
                if (!known.Add(message.Id)) continue;
                var position = 0;
                while (position < _conversationMessages.Count && _conversationMessages[position].Sequence <= message.Sequence) position++;
                _conversationMessages.Insert(position, message with { SenderAvatar = message.IsOwn ? account.Avatar : item.AvatarOne, SenderName = message.IsOwn ? account.Name : item.Title });
            }
            _conversationCursor = page.BeforeSequence;
            _conversationMore = page.HasMore && page.BeforeSequence > 0 && (reset || page.BeforeSequence < cursor);
            ConversationStatus.Text = _conversationMessages.Count == 0 ? "暂无可读取的对话内容。" : "";
            ConversationStatusRow.Visibility = ConversationStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            ConversationList.UpdateLayout();
            AttachConversationScroll();
            if (anchor is { } visible) ConversationList.ScrollIntoView(visible.Message, ScrollIntoViewAlignment.Leading);
            else if (reset && _conversationMessages.LastOrDefault() is { } last) ConversationList.ScrollIntoView(last);
            await Task.Delay(30, request.Token);
            if (!ConversationCurrent(lifetime)) return;
            ConversationList.UpdateLayout();
            if (anchor is { } saved && _conversationScroll is { } scroll && ConversationList.ContainerFromItem(saved.Message) is FrameworkElement container)
            {
                var top = container.TransformToVisual(scroll).TransformPoint(new(0, 0)).Y;
                scroll.ChangeView(null, scroll.VerticalOffset + top - saved.Top, null, true);
            }
            await ConversationLayoutAsync();
            if (!ConversationCurrent(lifetime)) return;
            _conversationLastOffset = _conversationScroll?.VerticalOffset ?? 0;
            _conversationScrollReady = true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { if (ConversationCurrent(lifetime)) { ConversationStatus.Text = "对话读取失败，请重新选择聊天，或上滑重试。"; _conversationScrollReady = true; } }
        finally
        {
            if (ReferenceEquals(_conversationLifetime, lifetime))
            {
                ConversationStatusRow.Visibility = ConversationStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                _conversationBusy = false; ConversationLoading.IsActive = false; ConversationLoading.Visibility = Visibility.Collapsed;
            }
        }
    }
    private void CloseConversation()
    {
        if (_conversationItem is { } previous && ViewModel.IsLoggedIn && ViewModel.Client.Account?.Id == _conversationAccount)
            _conversationDrafts[(_conversationAccount, previous.TalkerId)] = ConversationDraft.Text;
        else _conversationDrafts.Clear();
        ConversationComposer.Visibility = ConversationStatusRow.Visibility = Visibility.Collapsed;
        ConversationDraft.Text = "";
        _conversationLifetime?.Cancel(); _conversationLifetime?.Dispose(); _conversationLifetime = null;
        _conversationBusy = _conversationMore = _conversationScrollReady = false; _conversationCursor = 0; _conversationItem = null; _conversationAccount = 0;
#if VISUAL_CHECKS
        _conversationReader = null;
#endif
        _conversationMessages.Clear(); ConversationList.Visibility = Visibility.Collapsed;
        ConversationLoading.IsActive = false; ConversationLoading.Visibility = Visibility.Collapsed;
        ConversationTitle.Text = ConversationStatus.Text = ""; NotificationList.SelectedItem = null;
        UpdateNotificationLayout();
    }
    private void UpdateNotificationLayout()
    {
        if (NotificationWorkspace is null) return;
        var chat = ViewModel.NotificationCategoryIndex == 0;
        var narrow = NotificationsScroller.ActualWidth < 720;
        var detail = _conversationItem is not null;
        NotificationsPanel.MaxWidth = chat ? double.PositiveInfinity : 860;
        NotificationsPanel.HorizontalAlignment = chat ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        NotificationList.ItemTemplate = (DataTemplate)Resources[chat ? "NotificationSummaryTemplate" : "NotificationTemplate"];
        NotificationList.SelectionMode = chat ? ListViewSelectionMode.Single : ListViewSelectionMode.None;
        NotificationListPane.Visibility = chat && narrow && detail ? Visibility.Collapsed : Visibility.Visible;
        ConversationPane.Visibility = !chat || narrow && !detail ? Visibility.Collapsed : Visibility.Visible;
        NotificationWorkspace.ColumnSpacing = chat ? 12 : 0;
        NotificationListColumn.Width = !chat ? new GridLength(1, GridUnitType.Star) : narrow ? new GridLength(detail ? 0 : 1, detail ? GridUnitType.Pixel : GridUnitType.Star) : new GridLength(280);
        NotificationWorkspace.ColumnDefinitions[1].Width = !chat || narrow && !detail ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ConversationBack.Visibility = chat && narrow && detail ? Visibility.Visible : Visibility.Collapsed;
        ConversationHeader.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        ConversationPlaceholder.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ConversationBackClick(object sender, RoutedEventArgs args) => CloseConversation();
    private static ScrollViewer? ConversationScrollIn(DependencyObject node)
    {
        if (node is ScrollViewer scroll) return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            if (ConversationScrollIn(VisualTreeHelper.GetChild(node, index)) is { } found) return found;
        return null;
    }
    private void ConversationListLoaded(object sender, RoutedEventArgs args)
    {
        ConversationListUnloaded(sender, args);
        AttachConversationScroll();
        ConversationList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(ConversationWheelChanged), true);
    }
    private void AttachConversationScroll()
    {
        var current = ConversationScrollIn(ConversationList);
        if (ReferenceEquals(current, _conversationScroll)) return;
        if (_conversationScroll is { } previous) previous.ViewChanged -= ConversationViewChanged;
        _conversationScroll = current;
        if (current is not null) { _conversationLastOffset = current.VerticalOffset; current.ViewChanged += ConversationViewChanged; }
    }
    private void ConversationListUnloaded(object sender, RoutedEventArgs args)
    {
        if (_conversationScroll is { } scroll) scroll.ViewChanged -= ConversationViewChanged;
        _conversationScroll = null;
        ConversationList.RemoveHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(ConversationWheelChanged));
    }
    private async void ConversationViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (sender is not ScrollViewer scroll) return;
        var movedUp = scroll.VerticalOffset < _conversationLastOffset;
        _conversationLastOffset = scroll.VerticalOffset;
        if (movedUp) await LoadConversationAtTopAsync();
    }
    private async void ConversationWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (args.GetCurrentPoint(ConversationList).Properties.MouseWheelDelta > 0) await LoadConversationAtTopAsync();
    }
    private Task LoadConversationAtTopAsync() => _conversationScrollReady && _conversationMore && _conversationScroll is { VerticalOffset: <= 24 }
        ? LoadConversationAsync(false) : Task.CompletedTask;
    private (DirectMessage Message, double Top)? CaptureConversationAnchor()
    {
        if (_conversationScroll is not { } scroll || ConversationList.ItemsPanelRoot is not { } panel) return null;
        foreach (var container in panel.Children.OfType<ListViewItem>())
        {
            var top = container.TransformToVisual(scroll).TransformPoint(new(0, 0)).Y;
            if (top + container.ActualHeight > 0 && top < scroll.ViewportHeight && ConversationList.ItemFromContainer(container) is DirectMessage message) return (message, top);
        }
        return null;
    }
    private Task ConversationLayoutAsync()
    {
        var completion = new TaskCompletionSource();
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => completion.SetResult())) completion.SetResult();
        return completion.Task;
    }
    private void ConversationDraftChanged(object sender, TextChangedEventArgs args)
    {
        if (ConversationSend is not null) ConversationSend.IsEnabled = _conversationLifetime is { } lifetime && !_conversationSends.Contains(lifetime)
            && _conversationItem is { CanReply: true } && !string.IsNullOrWhiteSpace(ConversationDraft.Text);
    }
    private async void ConversationSendClick(object sender, RoutedEventArgs args) => await SendConversationAsync();
    private async Task SendConversationAsync()
    {
        if (_conversationLifetime is not { } lifetime || _conversationItem is not { CanReply: true } item || !ConversationCurrent(lifetime)
            || _conversationSends.Contains(lifetime) || string.IsNullOrWhiteSpace(ConversationDraft.Text)) return;
        var text = ConversationDraft.Text;
        _conversationSends.Add(lifetime); ConversationDraftChanged(ConversationDraft, null!);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
#if VISUAL_CHECKS
            if (ConversationWriter is { } writer) await writer(item.TalkerId, text, request.Token);
            else
#endif
                await ViewModel.Client.SendMessageAsync(item.TalkerId, text, request.Token);
            if (!ConversationCurrent(lifetime)) return;
            if (ConversationDraft.Text == text) ConversationDraft.Text = "";
            _conversationDrafts.Remove((_conversationAccount, item.TalkerId));
            if (!_conversationBusy) await LoadConversationAsync(true);
        }
        catch (Exception)
        {
            if (ConversationCurrent(lifetime)) { ConversationStatus.Text = "发送结果未确认，草稿已保留。请查看最新消息后再决定是否重试。"; ConversationStatusRow.Visibility = Visibility.Visible; }
        }
        finally { _conversationSends.Remove(lifetime); if (ConversationCurrent(lifetime)) ConversationDraftChanged(ConversationDraft, null!); }
    }
#if VISUAL_CHECKS
    internal Func<long, string, CancellationToken, Task>? ConversationWriter { get; set; }
#endif
    private void NotificationRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (ViewModel.Client.Account is not { } account) return;
        var node = args.OriginalSource as DependencyObject;
        while (node is not null && node is not ListViewItem && node != NotificationList) node = VisualTreeHelper.GetParent(node);
        var container = node as ListViewItem;
        if (container is null || NotificationList.ItemFromContainer(container) is not AccountNotification item) return;
        var menu = BuildNotificationMenu(item, account.Id);
        menu.ShowAt(NotificationList, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = args.GetPosition(NotificationList) });
        args.Handled = true;
    }
    private MenuFlyout BuildNotificationMenu(AccountNotification item, long account)
    {
        var menu = new MenuFlyout();
        var delete = new MenuFlyoutItem { Text = "删除", Icon = new SymbolIcon(Symbol.Delete), IsEnabled = BilibiliClient.CanDeleteNotification(item) };
        ToolTipService.SetToolTip(delete, delete.IsEnabled ? "请求云端删除此通知或会话入口" : "此类通知尚无已核验的云端删除接口");
        delete.Click += async (_, _) =>
        {
            if (await ViewModel.DeleteNotificationAsync(item, account) && _conversationItem?.TalkerId == item.TalkerId && _conversationItem?.SessionType == item.SessionType) CloseConversation();
        };
        menu.Items.Add(delete);
        return menu;
    }
}
