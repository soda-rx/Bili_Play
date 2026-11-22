using System.Collections.ObjectModel;
using BiliPlay.Core;

namespace BiliPlay.ViewModels;

// Lyrics language picker choices. The picker always offers a "关闭歌词" entry first so the
// whole lyrics surface can be turned off without leaving the playback detail page.
public sealed record SubtitleChoice(string DisplayName, SubtitleTrack? Track);

public sealed partial class PlayerViewModel
{
    private const string LyricsHiddenStatus = "歌词已关闭";
    private bool _lyricsHidden;
    private bool _syncingSubtitleChoice;

    public ObservableCollection<SubtitleChoice> SubtitleChoices { get; } = [];

    public bool LyricsHidden => _lyricsHidden;

    public SubtitleChoice? SelectedSubtitleChoice
    {
        get
        {
            if (_lyricsHidden) return SubtitleChoices.FirstOrDefault(choice => choice.Track is null);
            var track = SelectedSubtitle;
            if (track is null) return null;
            foreach (var choice in SubtitleChoices)
                if (choice.Track is { } candidate && candidate.Language == track.Language && candidate.IsAuto == track.IsAuto)
                    return choice;
            return null;
        }
        set
        {
            if (_syncingSubtitleChoice || value is null) return;
            if (value.Track is null) { SetLyricsHidden(true); return; }
            if (!_lyricsHidden && Matches(SelectedSubtitle, value.Track)) return;
            SetLyricsHidden(false);
            SelectedSubtitle = value.Track;
            OnPropertyChanged(nameof(SelectedSubtitleChoice));
        }
    }

    public void SetLyricsHidden(bool hidden)
    {
        if (_lyricsHidden == hidden) { OnPropertyChanged(nameof(SelectedSubtitleChoice)); return; }
        _lyricsHidden = hidden;
        OnPropertyChanged(nameof(LyricsHidden));
        if (hidden)
        {
            _subtitles?.Cancel();
            _subtitles?.Dispose();
            _subtitles = null;
            SelectedSubtitle = null;
            Lines.Clear();
            ActiveSubtitleIndex = -1;
            FollowSubtitleIndex = -1;
            SubtitleStatus = LyricsHiddenStatus;
        }
        OnPropertyChanged(nameof(SelectedSubtitleChoice));
    }

    private static bool Matches(SubtitleTrack? left, SubtitleTrack right)
        => left is not null && left.Language == right.Language && left.IsAuto == right.IsAuto;

    private void RebuildSubtitleChoices()
    {
        if (_syncingSubtitleChoice) return;
        _syncingSubtitleChoice = true;
        try
        {
            SubtitleChoices.Clear();
            SubtitleChoices.Add(new("关闭歌词", null));
            foreach (var track in SubtitleTracks)
                SubtitleChoices.Add(new(track.DisplayName, track));
        }
        finally { _syncingSubtitleChoice = false; }
        OnPropertyChanged(nameof(SelectedSubtitleChoice));
    }
}
