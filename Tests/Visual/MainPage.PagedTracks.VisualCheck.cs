using System.Net;
using System.Text;
using BiliPlay.Core;
using BiliPlay.ViewModels;

namespace BiliPlay;

public sealed partial class MainPage
{
    // Regression for paged track appends. Favourites, search and space uploads share the append
    // path, and a favourites list clamps an out-of-range page to the last one while still claiming
    // has_more, so the same tracks could be appended again on every further page. A repeated page
    // must never add a track twice, and a page that adds nothing new must stop paging. Drives the
    // real PlayerViewModel through its public folder browse / load-more commands with an offline
    // fixture, never the user's account or media session.
    private async Task VerifyPagedTrackDedupAsync()
    {
        var handler = new PagedTrackFixtureHandler();
        using var model = new PlayerViewModel(new BilibiliClient(handler));
        model.Client.RestoreCachedAccount(new(123, "离线分页测试", "")); model.IsLoggedIn = true;
        var folder = new Folder(71, "离线收藏夹", 2, true);
        await model.OpenFolderCommand.ExecuteAsync(folder);
        Check(model.Tracks.Count == 2 && model.HasMore, "收藏夹首页没有载入两条曲目或没有继续分页");
        var firstPage = model.Tracks.Select(track => track.Bvid).ToArray();
        var requestsAfterFirst = handler.FolderRequests;
        // The clamped favourites endpoint repeats the last page while still claiming has_more.
        await model.LoadMoreCommand.ExecuteAsync(null);
        Check(handler.FolderRequests == requestsAfterFirst + 1, "重复的分页没有被请求");
        Check(model.Tracks.Count == 2, "重复的分页把相同曲目又追加了一遍");
        Check(model.Tracks.Select(track => track.Bvid).SequenceEqual(firstPage), "重复分页改变或丢掉了已有曲目");
        Check(!model.HasMore, "零新增的分页没有停止继续翻页");
        // Once paging stops, further load-more must not keep re-requesting the clamped page.
        await model.LoadMoreCommand.ExecuteAsync(null);
        Check(handler.FolderRequests == requestsAfterFirst + 1, "停止翻页后仍然重复请求相同内容");
        Check(model.Tracks.Count == 2, "停止翻页后曲目数量仍然变化");
    }

    private sealed class PagedTrackFixtureHandler : HttpMessageHandler
    {
        public int FolderRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/nav"))
                return Task.FromResult(Json("""{"code":0,"data":{"isLogin":true,"mid":123,"uname":"离线分页测试"}}"""));
            // x/v3/fav/resource/list: every page clamps to the same two tracks but still claims has_more.
            FolderRequests++;
            return Task.FromResult(Json("""{"code":0,"data":{"has_more":true,"medias":[{"type":2,"id":71,"bvid":"BV1xx411c7mD","title":"曲目一","upper":{"name":"离线 UP","mid":42},"cover":"https://i0.hdslb.com/a.jpg","duration":100},{"type":2,"id":72,"bvid":"BV2xx411c7mD","title":"曲目二","upper":{"name":"离线 UP","mid":42},"cover":"https://i0.hdslb.com/b.jpg","duration":100}]}}"""));
        }
        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}