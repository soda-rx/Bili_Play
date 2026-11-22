using BiliPlay.Core;

namespace BiliPlay.ViewModels;

// Included only by builds that explicitly set EnableVisualChecks=true.
public partial class PlayerViewModel
{
    internal Func<AccountNotification,CancellationToken,Task>? NotificationDeleteWriter { get; set; }
    internal Func<int,CancellationToken,Task<PageResult<Track>>>? ListPageReader { get; set; }
    internal Func<long,Folder,CancellationToken,Task>? FolderSelectionRemover { get; set; }
    internal Func<DownloadJob,Task>? BatchDownloadRunner { get; set; }
    internal Func<int,CancellationToken,Task<PageResult<Track>>>? HomeReader { get; set; }
    internal void ExpireSleepTimerForTests() => ExpireSleepTimer();
    internal double RestoreResumePositionForTests(Track track) => RestoreResumePosition(track);
    internal Task PreloadCurrentForTestsAsync(Track track, double position) => PreloadSessionAsync(track, position);
    internal void TickForTests() => Tick();
    internal double ResumeExpectedForTests => _resumeExpected;
    // Visual tests: force an empty home page without racing the asynchronous home loader.
    internal void PrepareEmptyHomeForTests()
    {
        _hasLoadedHome = true;
        _homeCache.Clear();
        _homeMore = false;
        _regionPage = 0;
        Tracks.Clear();
    }
    internal void UpdateCurrentDisplayForTests(Track? track) => UpdateCurrentDisplay(track);
    internal Func<Track,CommentSort,int,CancellationToken,Task<CommentPage>>? CommentsReader { get; set; }
    internal Func<Track,string,long,long,CancellationToken,Task<BiliComment?>>? CommentReplySender { get; set; }
    internal Func<Track,string,CancellationToken,Task<BiliComment?>>? CommentSender { get; set; }
    internal Func<CommentImagePayload,CancellationToken,Task<CommentPicture>>? CommentImageUploader { get; set; }
    internal Func<Track,string,long,long,IReadOnlyList<CommentPicture>,CancellationToken,Task<BiliComment?>>? CommentPictureSender { get; set; }
    internal Func<Track,long,int,CancellationToken,Task<CommentPage>>? CommentRepliesReader { get; set; }
    internal Func<Track,long,CommentReaction,bool,CancellationToken,Task>? CommentReactionWriter { get; set; }
    internal Func<CancellationToken,Task<IReadOnlyList<CommentEmotePackage>>>? CommentEmotesReader { get; set; }
}
