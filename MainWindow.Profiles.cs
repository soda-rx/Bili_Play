using BiliPlay.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliPlay;

public sealed partial class MainWindow
{
    private async void SwitchAccountClick(object sender, RoutedEventArgs args) => await ShowProfilesAsync();
    private async Task ShowProfilesAsync()
    {
        if (DebugSession.IsActive) return;
        var profiles = ProfileManager.WithDefault();
        string? selected = null;
        var list = new StackPanel { Spacing = 8, Width = 424 };
        var selectionButtons = new List<Button>();
        ContentDialog? dialog = null;
        foreach (var profile in profiles)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var profileContent = new Grid { ColumnSpacing = 12 };
            profileContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            profileContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            profileContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var avatar = new PersonPicture { Width = 36, Height = 36, ProfilePicture = ProfileAvatar(profile), IsGroup = false };
            profileContent.Children.Add(avatar);
            var label = new TextBlock { Text = profile.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(label, 1); profileContent.Children.Add(label);
            if (profile.Id == AppProfile.ProfileId)
            {
                var check = new FontIcon { Glyph = "\uE73E", FontSize = 16, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(check, 2); profileContent.Children.Add(check);
            }
            var button = new Button { Content = profileContent, Tag = profile.Id, Width = 360, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 8, 12, 8) };
            button.Click += (_, _) => { selected = profile.Id; };
            selectionButtons.Add(button);
            row.Children.Add(button);
            if (profile.Id != "default" && profile.Id != AppProfile.ProfileId)
            {
                var delete = new Button { Content = "删除", Tag = profile, Width = 56, Padding = new Thickness(8, 8, 8, 8) };
                Grid.SetColumn(delete, 1); row.Children.Add(delete);
                delete.Click += async (_, _) => { dialog?.Hide(); await DeleteProfileAsync(profile); };
            }
            list.Children.Add(row);
        }
        var create = new Button { Content = "＋ 新建配置文件", Width = 360, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 10, 12, 10) };
        create.Click += (_, _) => { selected = ProfileManager.Create(AppProfile.ProfileId).Id; };
        selectionButtons.Add(create);
        list.Children.Add(create);
        dialog = new ContentDialog { XamlRoot = WindowRoot.XamlRoot, RequestedTheme = WindowRoot.ActualTheme, Title = "切换账号", Content = list, CloseButtonText = "取消" };
        foreach (var button in selectionButtons) button.Click += (_, _) => dialog.Hide();
        await dialog.ShowAsync();
        if (selected is null || selected == AppProfile.ProfileId) return;
        SwitchToProfile(selected);
    }
    private static Microsoft.UI.Xaml.Media.ImageSource? ProfileAvatar(UserProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Avatar) || !Uri.TryCreate(profile.Avatar, UriKind.Absolute, out var uri)) return null;
        return ArtworkCache.CreateAvatar(uri, 96);
    }
    private async Task DeleteProfileAsync(UserProfile profile)
    {
        var confirm = new ContentDialog { XamlRoot = WindowRoot.XamlRoot, RequestedTheme = WindowRoot.ActualTheme, Title = "删除配置文件？", Content = $"将删除“{profile.Name}”的登录凭据、设置、历史、缓存和下载记录。此操作无法撤销。", PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) { await ShowProfilesAsync(); return; }
        ProfileManager.Delete(profile.Id); await ShowProfilesAsync();
    }
    internal async Task CancelNewProfileAsync()
    {
        var previous = ProfileManager.PendingPreviousProfileId;
        if (previous is null) { ProfileManager.ClearPending(); return; }
        var current = AppProfile.ProfileId;
        AccountFlyout.Hide();
        // Stop all network/audio work before removing the temporary profile. The old
        // implementation deleted the active profile while its SQLite connection and
        // artwork cache were still settling, which could deadlock or terminate the app.
        try { ViewModel.Dispose(); } catch { /* the replacement page remains usable */ }
        ProfileManager.ClearPending();
        AppProfile.SetProfile(previous);
        ArtworkCache.ResetScope();
        // Switch the active data root first, then remove the abandoned profile. This
        // keeps file handles and single-instance state pointed at the restored account.
        try { ProfileManager.Delete(current); } catch { }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                RootFrame.Navigate(typeof(MainPage));
                ViewModel = ((MainPage)RootFrame.Content).ViewModel; BindHotkeys();
                ViewModel.BrowseChanged += CloseSearchHistory;
                Bindings.Update();
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetCanceled();
        await completion.Task;
    }
    private void SwitchToProfile(string profileId)
    {
        AccountFlyout.Hide(); CloseSearchHistory(); _suggestionsCache.Clear();
        ViewModel.Dispose(); AppProfile.SetProfile(profileId); ArtworkCache.ResetScope();
        RootFrame.Navigate(typeof(MainPage));
        ViewModel = ((MainPage)RootFrame.Content).ViewModel; BindHotkeys();
        ViewModel.BrowseChanged += CloseSearchHistory;
        ViewModel.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ViewModel.IsLoggedIn) && !ViewModel.IsLoggedIn) CloseSearchHistory(); };
        Title = AppProfile.DisplayName; AppTitleBar.Title = Title; TitleSearch.Text = ""; Bindings.Update();
    }
}
