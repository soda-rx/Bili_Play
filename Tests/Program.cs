using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BiliPlay.Core;

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void True(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
async Task Throws<T>(Func<Task> run) where T : Exception { try { await run(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
const string image = "7cd084941338484aae1ad9425b84077c";
const string sub = "4932caff0ff746eab6f01bf08b70ac45";
const string account = """{"code":0,"data":{"isLogin":true,"mid":123,"uname":"测试用户","wbi_img":{"img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png","sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}""";
static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

Test("WBI published reference vector", () => Equal(
 "bar=514&foo=114&wts=1702204169&zab=1919810&w_rid=8f6f2b5b3d485fe1886cec6a0be8c5d4",
 WbiSigner.Sign(new Dictionary<string,string> { ["foo"]="114", ["bar"]="514", ["zab"]="1919810" }, image, sub, 1702204169)));
Test("WBI UTF-8, spaces and filtered characters", () =>
{
 var values = new Dictionary<string,string> { ["keyword"]="五一四 !'()*" };
 var result = WbiSigner.Sign(values, image, sub, 1);
 True(result.StartsWith("keyword=%E4%BA%94%E4%B8%80%E5%9B%9B%20&wts=1&", StringComparison.Ordinal));
 Equal("五一四 !'()*", values["keyword"]);
});
Test("Video input validates origin and part number", () =>
{
 Equal(("BV1xx411c7mD", 3), Text.ParseVideoInput("https://www.bilibili.com/video/BV1xx411c7mD/?p=3")!.Value);
 True(Text.ParseVideoInput("https://www.bilibili.com.evil.test/video/BV1xx411c7mD") is null);
 True(Text.ParseVideoInput("https://evil.test/?id=BV1xx411c7mD") is null);
 True(Text.ParseVideoInput("这是 BV1xx411c7mD") is null);
 Equal(1, Text.ParseVideoInput("BV1xx411c7mD")!.Value.Page);
});
Test("CDN allowlist rejects local and suffix lookalikes", () =>
{
 True(Text.IsMediaHost(new("https://upos-sz.bilivideo.com/audio.m4s")));
 foreach(var url in new[] { "http://upos.bilivideo.com/a", "https://bilivideo.com.evil.test/a", "https://localhost/a", "https://127.0.0.1/a", "https://upos.bilivideo.com:8080/a" }) True(!Text.IsMediaHost(new(url)));
});
Test("Subtitle ordering, validation and gap boundaries", () =>
{
 var cues = Subtitles.Parse("""{"body":[{"from":5,"to":7,"content":"第二句"},{"from":1,"to":3,"content":"第一句 &amp;"},{"from":-1,"to":1,"content":"bad"},{"from":8,"to":7,"content":"bad"},{"from":9,"to":10,"content":""}]}""");
 Equal(2,cues.Count); Equal("第一句 &",cues[0].Content);
 Equal(-1,Subtitles.ActiveIndex(cues,0)); Equal(0,Subtitles.ActiveIndex(cues,1));
 Equal(0,Subtitles.ActiveIndex(cues,0,previewFirst:true));
 Equal(-1,Subtitles.ActiveIndex(cues,.5,previewFirst:true));
 Equal(-1,Subtitles.ActiveIndex(cues,4,previewFirst:true));
 Equal(-1,Subtitles.ActiveIndex(Array.Empty<SubtitleLine>(),0,previewFirst:true));
 Equal(-1,Subtitles.ActiveIndex(cues,3)); Equal(-1,Subtitles.ActiveIndex(cues,4));
 Equal(1,Subtitles.ActiveIndex(cues,5)); Equal(-1,Subtitles.ActiveIndex(cues,7));
});
Test("Duration and title normalization", () =>
{
 Equal("1:02:03",Text.FormatTime(3723)); Equal(3723d,Text.ParseDuration("1:02:03"));
 Equal("标题 & 歌名",Text.Clean("<em class=\"keyword\">标题</em> &amp; 歌名"));
});
tests.Add(("Homepage follows only trusted localization redirects", async () =>
{
 var calls = 0;
 using var client = new BilibiliClient(new Handler(r =>
 {
  calls++;
  if(calls == 1) { var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new("/zh-Hans",UriKind.Relative); return redirect; }
  Equal("/zh-Hans",r.RequestUri!.AbsolutePath); return new(HttpStatusCode.OK) { Content = new StringContent("") };
 }));
 await client.WarmUpAsync(); Equal(2,calls);
 using var unsafeClient = new BilibiliClient(new Handler(_ =>
 { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new("https://evil.test/"); return response; }));
 await Throws<InvalidDataException>(() => unsafeClient.WarmUpAsync());
}));
tests.Add(("Authentication gates protected operations", async () =>
{
 using var client = new BilibiliClient(new Handler((Func<HttpRequestMessage,HttpResponseMessage>)(_ => throw new Exception("Must not access network"))));
 await Throws<BilibiliException>(() => client.SearchAsync("test",1));
 await Throws<BilibiliException>(() => client.GetMusicChannelAsync(1));
 await Throws<BilibiliException>(() => client.CreateFolderAsync("test",true));
}));
Test("Favorite privacy uses bit 0 independently of default-folder bit", () =>
{
 foreach (var attributes in new long[] {0,2,22}) True(!Folder.IsPrivateAttribute(attributes));
 foreach (var attributes in new long[] {1,3,23}) True(Folder.IsPrivateAttribute(attributes));
});
tests.Add(("Folder previews read three real covers and authoritative privacy", async () =>
{
 using var client = new BilibiliClient(new Handler(r =>
 {
  Equal(HttpMethod.Get,r.Method);
  if(r.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(account);
  Equal("3",Text.ParseQuery(r.RequestUri.Query)["ps"]);
  return Json("""{"code":0,"data":{"info":{"id":42,"attr":23,"media_count":3},"medias":[{"cover":"https://i0.hdslb.com/a.jpg"},{"cover":"https://i0.hdslb.com/b.jpg"},{"cover":"https://i0.hdslb.com/c.jpg"}]}}""");
 }));
 await client.GetAccountAsync(); var preview = await client.GetFolderPreviewAsync(new(42,"private folder",3,true,false));
 True(preview.IsPrivate); Equal("https://i0.hdslb.com/a.jpg",preview.Cover);
 Equal("https://i0.hdslb.com/b.jpg",preview.CoverMiddle); Equal("https://i0.hdslb.com/c.jpg",preview.CoverBack);
}));
tests.Add(("Missing preview metadata preserves known folder privacy", async () =>
{
 using var client = new BilibiliClient(new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/nav") ? Json(account) : Json("""{"code":0,"data":{"medias":[]}}""")));
 await client.GetAccountAsync(); var preview = await client.GetFolderPreviewAsync(new(42,"private",5,true,true));
 True(preview.IsPrivate); Equal(5,preview.Count);
}));
tests.Add(("Owned and subscribed folders, including pagination", async () =>
{
 using var client = new BilibiliClient(new Handler(r =>
 {
  var path = r.RequestUri!.AbsolutePath;
  if(path.EndsWith("/nav")) return Json(account);
  if(path.Contains("created")) return Json("""{"code":0,"data":{"list":[{"id":1,"title":"我的收藏","media_count":4,"attr":3}]}}""");
  var page = Text.ParseQuery(r.RequestUri.Query)["pn"];
  return page == "1" ? Json("""{"code":0,"data":{"list":[{"id":2,"title":"订阅一","media_count":2}],"has_more":true}}""")
   : Json("""{"code":0,"data":{"list":[{"id":3,"title":"订阅二","media_count":3}],"has_more":false}}""");
 }));
 await client.GetAccountAsync(); var folders = await client.GetFoldersAsync();
 Equal(3,folders.Count); True(folders[0].Owned); True(folders[0].IsPrivate); True(!folders[1].Owned);
 await Throws<InvalidOperationException>(() => client.SetFavoriteAsync(1,folders[1],true));
}));
tests.Add(("Favorite writes include CSRF and are never retried", async () =>
{
 var posts = 0;
 using var client = new BilibiliClient(new Handler(async r =>
 {
  if(r.Method == HttpMethod.Get) return Json(account);
  posts++; var form = Text.ParseQuery(await r.Content!.ReadAsStringAsync());
  Equal("test-csrf",form["csrf"]); Equal("2",form["type"]); Equal("456",form["add_media_ids"]);
  return Json("""{"code":-403,"message":"denied"}""");
 }));
 client.ImportSession([new("bili_jct","test-csrf",".bilibili.com","/",DateTime.UtcNow.AddDays(1))]);
 await client.GetAccountAsync();
 await Throws<BilibiliException>(() => client.SetFavoriteAsync(123,new(456,"test",0,true),true));
 Equal(1,posts);
}));
tests.Add(("Audio selection prefers native AAC and rejects video fallback", async () =>
{
 var noAudio = false;
 using var client = new BilibiliClient(new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/nav") ? Json(account) :
  Json(noAudio ? """{"code":0,"data":{"durl":[{"url":"https://upos.bilivideo.com/video.mp4"}]}}""" :
   """{"code":0,"data":{"dash":{"audio":[{"baseUrl":"https://upos.bilivideo.com/a.flac","codecs":"fLaC","bandwidth":1000},{"baseUrl":"https://upos.bilivideo.com/a.m4s","codecs":"mp4a.40.2","mimeType":"audio/mp4","bandwidth":100}]}}}""")));
 await client.GetAccountAsync(); var track = new Track(1,"BV1xx411c7mD","test","up","",20,3);
 Equal("/a.m4s",(await client.GetAudioAsync(track)).Url.AbsolutePath);
 noAudio = true; await Throws<InvalidOperationException>(() => client.GetAudioAsync(track));
}));
tests.Add(("Hi-Res selection requires VIP and keeps AAC fallback", async () =>
{
 var vip = false;
 using var client = new BilibiliClient(new Handler(r =>
 {
  if(r.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(vip ? account.Replace("\"isLogin\":true", "\"isLogin\":true,\"vipStatus\":1") : account);
  Equal(vip ? "4048" : "16", Text.ParseQuery(r.RequestUri.Query)["fnval"]);
  return Json("""{"code":0,"data":{"dash":{"audio":[{"id":30280,"baseUrl":"https://upos.bilivideo.com/a.m4s","codecs":"mp4a.40.2"}],"flac":{"audio":{"id":30251,"baseUrl":"https://upos.bilivideo.com/lossless.m4s","codecs":"fLaC"}}}}}""");
 }));
 var track = new Track(1,"BV1xx411c7mD","test","up","",20,3);
 await client.GetAccountAsync(); var normal = await client.GetAudioAsync(track);
 Equal("标准", normal.Quality); True(normal.Fallback is null); True(normal.HasHiRes);
 vip = true; await client.GetAccountAsync(); var lossless = await client.GetAudioAsync(track);
 Equal("Hi-Res 无损", lossless.Quality); Equal("/lossless.m4s",lossless.Url.AbsolutePath);
 Equal("/a.m4s", lossless.Fallback!.Url.AbsolutePath);
}));
Test("Music cover stays with its card instead of avatars, badges or lazy placeholders", () =>
{
 var html = """
 <div class="feed-card"><a href="https://www.bilibili.com/video/BV1xx411c7mD"></a>
 <img src="//i0.hdslb.com/bfs/face/avatar.jpg">
 <picture class="v-img bili-video-card__cover"><img src="//i0.hdslb.com/bfs/archive/first.jpg@480w.webp"></picture>
 <div class="bili-video-card__title" title="First"></div></div>
 <div class="feed-card"><a href="https://www.bilibili.com/video/BV2xx411c7mD"></a>
 <img src="//i0.hdslb.com/decoration.jpg">
 <picture class="bili-video-card__cover"><img data-src='//i0.hdslb.com/bfs/archive/second.jpg' src="data:image/png;base64,placeholder"></picture>
 <div class="bili-video-card__title" title="Second"></div></div>
 <div class="feed-card"><a href="https://www.bilibili.com/video/BV3xx411c7mD"></a>
 <img src="//i0.hdslb.com/avatar.jpg"><picture class="bili-video-card__cover"><img src="data:image/png;base64,placeholder"></picture>
 <div class="bili-video-card__title" title="Missing cover"></div></div>
 """;
 var tracks = MusicPage.Parse(html); Equal(2,tracks.Count);
 Equal("First",tracks[0].Title); Equal("https://i0.hdslb.com/bfs/archive/first.jpg",tracks[0].Cover);
 Equal("Second",tracks[1].Title); Equal("https://i0.hdslb.com/bfs/archive/second.jpg",tracks[1].Cover);
});
Test("Music page reads rendered channel cards without executing scripts or banners", () =>
{
 var html = """
 <script>throw new Error('must never execute')</script>
 <a href="https://www.bilibili.com/video/BV9xx411c7mD">广告横幅</a>
 <div class="feed-card head-card"><a href="https://www.bilibili.com/video/BV1xx411c7mD?spm_id_from=333.40138"></a>
 <img src="//i0.hdslb.com/cover.jpg@760w_428h_1c">
 <div class="bili-video-card__title" title="歌曲 &amp; 标题"></div>
 <a class="bili-video-card__author" href="https://space.bilibili.com/123?from=music"><span title="UP 主 · 11-01"></span></a>
 <span>03:25</span></div>
 <div class="feed-card head-card"><a href="https://www.bilibili.com/video/BV2xx411c7mD"></a>
 <img src="https://evil.test/cover.jpg"><div class="bili-video-card__title" title="拒绝外部封面"></div></div>
 """;
 var tracks = MusicPage.Parse(html);
 Equal(1, tracks.Count); Equal("歌曲 & 标题",tracks[0].Title); Equal("UP 主",tracks[0].Uploader);
 Equal(123L,tracks[0].UploaderId); Equal(205d,tracks[0].Duration); Equal("https://i0.hdslb.com/cover.jpg",tracks[0].Cover);
 True(MusicPage.Parse("<html>验证页面</html>").Count == 0);
});
tests.Add(("Music channel follows page origin and channel pagination schema", async () =>
{
 var exhausted = false;
 using var client = new BilibiliClient(new Handler(r =>
 {
  if(r.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(account);
  Equal("https://www.bilibili.com/c/music/",r.Headers.Referrer!.AbsoluteUri);
  if(r.RequestUri.AbsolutePath == "/c/music/") return new(HttpStatusCode.OK) { Content = new StringContent("""
   <div class="feed-card head-card"><a href="https://www.bilibili.com/video/BV1xx411c7mD"></a>
   <img src="//i0.hdslb.com/a.jpg"><div class="bili-video-card__title" title="首屏推荐"></div></div>
   """, Encoding.UTF8, "text/html") };
  Equal("/x/web-interface/region/feed/rcmd", r.RequestUri.AbsolutePath);
  var query = Text.ParseQuery(r.RequestUri.Query); Equal("1003",query["from_region"]); Equal("2",query["display_id"]);
  Equal("web",query["device"]); Equal("30",query["plat"]); Equal("15",query["request_cnt"]);
  return Json(exhausted ? """{"code":62013,"message":"no more"}""" : """
   {"code":0,"data":{"archives":[{"aid":1,"bvid":"BV2xx411c7mD","title":"下一页","cover":"//i0.hdslb.com/a.jpg","author":{"name":"UP","mid":456},"duration":90}]}}
   """);
 }));
 await client.GetAccountAsync(); var first = await client.GetMusicChannelAsync(1);
 Equal("首屏推荐",first.Items[0].Title); True(first.HasMore);
 var page = await client.GetMusicChannelAsync(2); Equal("下一页", page.Items[0].Title); Equal(456L,page.Items[0].UploaderId);
 exhausted = true; var end = await client.GetMusicChannelAsync(2); True(!end.HasMore); Equal(0,end.Items.Count);
}));
tests.Add(("Music page retries one transient read failure and respects cancellation", async () =>
{
 var calls = 0;
 using var client = new BilibiliClient(new Handler(r =>
 {
  if(r.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(account);
  if(++calls == 1) throw new HttpRequestException("Transient connection failure");
  return new(HttpStatusCode.OK) { Content = new StringContent("""
   <div class="feed-card head-card"><a href="https://www.bilibili.com/video/BV1xx411c7mD"></a>
   <img src="//i0.hdslb.com/a.jpg"><div class="bili-video-card__title" title="重试成功"></div></div>
   """, Encoding.UTF8, "text/html") };
 }));
 await client.GetAccountAsync(); var page = await client.GetMusicChannelAsync(1); Equal(2,calls); Equal(1,page.Items.Count);
 using var canceled = new CancellationTokenSource(); canceled.Cancel();
 using var canceledClient = new BilibiliClient(new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/nav") ? Json(account) : throw new OperationCanceledException(canceled.Token)));
 await canceledClient.GetAccountAsync(); await Throws<OperationCanceledException>(() => canceledClient.GetMusicChannelAsync(1,canceled.Token));
}));
tests.Add(("Following reads relation and writes only explicit CSRF actions", async () =>
{
 var posts = 0;
 using var client = new BilibiliClient(new Handler(async r =>
 {
  if(r.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(account);
  if(r.Method == HttpMethod.Get) return Json("""{"code":0,"data":{"attribute":6}}""");
  posts++;
  var fields = Text.ParseQuery(await r.Content!.ReadAsStringAsync()); Equal("456",fields["fid"]); Equal("csrf-test",fields["csrf"]);
  Equal(posts == 1 ? "1" : "2",fields["act"]); Equal("11",fields["re_src"]);
  return Json("""{"code":0}""");
 }));
 client.ImportSession(new[]{new SavedCookie("bili_jct","csrf-test",".bilibili.com","/",DateTime.UtcNow.AddDays(1))});
 await client.GetAccountAsync(); True(await client.IsFollowingAsync(456)); Equal(0,posts);
 await client.FollowAsync(456,true); await client.FollowAsync(456,false); Equal(2,posts);
 await Throws<InvalidOperationException>(() => client.FollowAsync(123,true)); Equal(2,posts);
}));
Test("Automatic subtitles follow source AI language and allow title fallback", () =>
{
 var zh = new SubtitleTrack("zh-Hans","中文",new("https://i0.hdslb.com/z.json"),false);
 var en = new SubtitleTrack("en-US","英语",new("https://i0.hdslb.com/e.json"),false);
 var original = new SubtitleTrack("ai-en","英语（自动生成）",new("https://i0.hdslb.com/a.json"),true);
 Equal(en,SubtitleSelection.Choose(new[]{zh,en},"Never Gonna Give You Up"));
 Equal(zh,SubtitleSelection.Choose(new[]{zh,en},"月亮代表我的心"));
 Equal(en,SubtitleSelection.Choose(new[]{zh,en,original},"中文介绍"));
 True(SubtitleSelection.Choose(Array.Empty<SubtitleTrack>(),"test") is null);
});
tests.Add(("Subtitle tracks preserve source labels and manual preference", async () =>
{
 using var client = new BilibiliClient(new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/nav") ? Json(account) : Json(
  """{"code":0,"data":{"subtitle":{"subtitles":[{"lan":"ai-zh","lan_doc":"中文（自动生成）","subtitle_url":"//aisubtitle.hdslb.com/a.json","ai_status":1},{"lan":"zh-CN","lan_doc":"中文","subtitle_url":"//aisubtitle.hdslb.com/b.json","ai_status":0}]}}}""")));
 await client.GetAccountAsync(); var list = await client.GetSubtitleTracksAsync(new(1,"BV1xx411c7mD","test","up","",20,3));
 Equal(2,list.Count); True(!list[0].IsAuto); True(list[1].IsAuto);
}));
tests.Add(("Loopback audio relay forwards ranges, headers and invalidates tokens", async () =>
{
 using var proxy = new AudioProxy(new Handler(r =>
 {
  Equal("bytes=2-5",r.Headers.Range!.ToString()); Equal("https://www.bilibili.com/",r.Headers.Referrer!.AbsoluteUri);
  True(!r.Headers.Contains("Cookie"));
  var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([2,3,4,5]) };
  response.Content.Headers.ContentRange = new(2,5,10); response.Content.Headers.ContentType = new("audio/mp4");
  response.Headers.AcceptRanges.Add("bytes"); return response;
 }));
 var source = new AudioStream(new("https://upos.bilivideo.com/audio.m4s"),"audio/mp4",[]);
 var url = proxy.SetSource(source);
 using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
 using var request = new HttpRequestMessage(HttpMethod.Get,url); request.Headers.Range = new(2,5);
 using var response = await http.SendAsync(request);
 Equal(HttpStatusCode.PartialContent,response.StatusCode); Equal("bytes 2-5/10",response.Content.Headers.ContentRange!.ToString());
 True((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[] {2,3,4,5}));
 proxy.SetSource(source); Equal(HttpStatusCode.NotFound,(await http.GetAsync(url)).StatusCode);
}));
tests.Add(("Audio relay remains responsive when its creating UI context cannot pump", async () =>
{
 var context = new SuspendedContext(); var previous = SynchronizationContext.Current;
 AudioProxy proxy;
 SynchronizationContext.SetSynchronizationContext(context);
 try { proxy = new AudioProxy(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1,2,3,4]) })); }
 finally { SynchronizationContext.SetSynchronizationContext(previous); }
 using (proxy)
 using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
 {
  var url = proxy.SetSource(new(new("https://upos.bilivideo.com/audio.m4s"),"audio/mp4",[]));
  True((await http.GetByteArrayAsync(url)).SequenceEqual(new byte[] {1,2,3,4}));
  Equal(0, context.Posts);
 }
}));
tests.Add(("Changing songs cancels the old upstream before opening the replacement", async () =>
{
 var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
 var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
 using var proxy = new AudioProxy(new CancellableHandler(async (request, token) =>
 {
  if (request.RequestUri!.AbsolutePath == "/slow.m4s")
  {
   entered.TrySetResult();
   try { await Task.Delay(Timeout.Infinite, token); }
   finally { if (token.IsCancellationRequested) canceled.TrySetResult(); }
  }
  return new(HttpStatusCode.OK) { Content = new ByteArrayContent([4,3,2,1]) };
 }));
 using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
 var old = http.GetByteArrayAsync(proxy.SetSource(new(new("https://upos.bilivideo.com/slow.m4s"),"audio/mp4",[])));
 await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
 var replacement = proxy.SetSource(new(new("https://upos.bilivideo.com/next.m4s"),"audio/mp4",[]));
 await canceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
 try { await old; throw new Exception("The obsolete stream remained open"); }
 catch (HttpRequestException) { }
 True((await http.GetByteArrayAsync(replacement)).SequenceEqual(new byte[] {4,3,2,1}));
}));
if(args.Contains("--live")) tests.Add(("Live Bilibili home and QR schema (no account writes)", async () =>
{
 using var client = new BilibiliClient(); await client.WarmUpAsync(); var code = await client.CreateLoginCodeAsync();
 True(code.Key.Length > 10); True(new Uri(code.Url).Host.EndsWith(".bilibili.com",StringComparison.OrdinalIgnoreCase));
}));

if (Array.IndexOf(args,"--music-page") is var musicIndex && musicIndex >= 0 && musicIndex + 1 < args.Length)
 Test("Captured music page yields a complete first screen", () =>
 {
  var tracks = MusicPage.Parse(File.ReadAllText(args[musicIndex+1],Encoding.UTF8));
  True(tracks.Count >= 12); True(tracks.All(t => t.Title.Length > 0 && t.Uploader.Length > 0 && t.Duration > 0));
  True(tracks.All(t => !t.Title.Contains('\uFFFD')));
  Console.WriteLine($"Music page: {tracks.Count} rendered video cards");
 });

Test("Artwork requests preserve original center and do not stack crop transforms", () =>
{
 var track = new Track(1, "BV1xx411c7mD", "music", "UP", "https://i0.hdslb.com/bfs/archive/cover.jpg@320w_200h_1c.webp", 10);
 Equal("https://i0.hdslb.com/bfs/archive/cover.jpg@480w.jpg", track.CardCover);
 Equal("https://i0.hdslb.com/bfs/archive/cover.jpg@128w.jpg", track.Thumbnail);
 True(!track.Artwork.Contains("_1c"));
 Equal("https://i0.hdslb.com/cover.webp@960w.jpg?test=1", Text.CoverImage("https://i0.hdslb.com/cover.webp@320w.webp?test=1", 960));
 Equal("file:///C:/fixture/cover.png", Text.CoverImage("file:///C:/fixture/cover.png", 480));
 Equal("https://example.com/cover.png?test=1", Text.CoverImage("https://example.com/cover.png?test=1", 480));
});

Test("WebP CDN images use native PNG without changing avatar geometry or unrelated URLs", () =>
{
 var inputs = new[] {
  ("https://i0.hdslb.com/avatar.jpg@320w_320h_1c.webp?test=1", "https://i0.hdslb.com/avatar.jpg@320w_320h_1c.png?test=1"),
  ("https://i1.hdslb.com/original.webp", "https://i1.hdslb.com/original.webp@.png"),
  ("https://i1.hdslb.com/original.webp@100w", "https://i1.hdslb.com/original.webp@100w.png"),
  ("https://i0.hdslb.com/cover.jpg@480w.jpg", "https://i0.hdslb.com/cover.jpg@480w.jpg"),
  ("https://i0.hdslb.com/original.webp@100w.png", "https://i0.hdslb.com/original.webp@100w.png"),
  ("https://i0.hdslb.com/avatar.png?test=.webp", "https://i0.hdslb.com/avatar.png?test=.webp"),
  ("https://hdslb.com.evil.test/avatar.webp", "https://hdslb.com.evil.test/avatar.webp"),
  ("https://example.com/avatar.webp", "https://example.com/avatar.webp"),
  ("file:///C:/fixture/cover.webp", "file:///C:/fixture/cover.webp")
 };
 foreach (var (input, expected) in inputs)
 {
  var normalized = Text.CompatibleImageUri(new Uri(input));
  Equal(expected, normalized.AbsoluteUri);
  Equal(normalized, Text.CompatibleImageUri(normalized));
 }
});
Test("Artwork codec policy prefers WebP only when supported and compatibility mode is off", () =>
{
 var support = ArtworkFormats.WebpSupported; var compatibility = ArtworkFormats.CompatibilityMode;
 var webp = new Uri("https://i0.hdslb.com/avatar.jpg@80w.webp");
 try
 {
  foreach (var supported in new[] {false,true})
  foreach (var forced in new[] {false,true})
  {
   ArtworkFormats.Configure(supported,forced);
   Equal(supported && !forced, ArtworkFormats.UseWebp);
   var cover = Text.CoverImage("https://i0.hdslb.com/cover.jpg",480);
   True(cover.EndsWith(supported && !forced ? "@480w.webp" : "@480w.jpg", StringComparison.Ordinal));
   // The system media flyout decodes in a shell context, so the SMTC variant always requests jpg.
   True(Text.CoverImage("https://i0.hdslb.com/cover.jpg",480,false).EndsWith("@480w.jpg", StringComparison.Ordinal));
   Equal(supported && !forced ? webp.AbsoluteUri : "https://i0.hdslb.com/avatar.jpg@80w.png", Text.CompatibleImageUri(webp).AbsoluteUri);
  }
 }
 finally { ArtworkFormats.Configure(support,compatibility); }
});
tests.Add(("Favorite relation is fetched each time and supports bool and numeric values", async () =>
{
 var calls = 0;
 using var client = new BilibiliClient(new Handler(r =>
 {
  if (r.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(account);
  Equal("/x/web-interface/archive/relation", r.RequestUri.AbsolutePath);
  Equal("42", Text.ParseQuery(r.RequestUri.Query)["aid"]);
  calls++;
  return Json(calls == 1 ? "{\"code\":0,\"data\":{\"favorite\":true}}" : calls == 2
   ? "{\"code\":0,\"data\":{\"favorite\":0}}" : "{\"code\":0,\"data\":{\"favorite\":1}}");
 }));
 await client.GetAccountAsync();
 True(await client.IsFavoritedAsync(42)); True(!await client.IsFavoritedAsync(42)); True(await client.IsFavoritedAsync(42)); Equal(3, calls);
}));
tests.Add(("UP search has separate signed request, profile metadata and pagination", async () =>
{
 using var client = new BilibiliClient(new Handler(r =>
 {
  if (r.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(account);
  Equal("/x/web-interface/wbi/search/type", r.RequestUri.AbsolutePath);
  var query = Text.ParseQuery(r.RequestUri.Query);
  Equal("bili_user", query["search_type"]); Equal("1", query["user_type"]); Equal("音乐 UP", query["keyword"]);
  True(query.ContainsKey("w_rid"));
  return Json("""{"code":0,"data":{"numPages":2,"result":[{"mid":42,"uname":"<em>测试</em> UP","upic":"//i0.hdslb.com/bfs/face/avatar.jpg","usign":"音乐 &amp; 生活","fans":1234,"videos":15}]}}""");
 }));
 await client.GetAccountAsync();
 var first = await client.SearchUploadersAsync("音乐 UP",1); True(first.HasMore); Equal(1, first.Items.Count);
 var user = first.Items[0]; Equal(42L,user.Id); Equal("测试 UP",user.Name); Equal("音乐 & 生活",user.Signature);
 Equal("https://i0.hdslb.com/bfs/face/avatar.jpg", user.Avatar); Equal(1234L,user.Fans);
 True(!(await client.SearchUploadersAsync("音乐 UP",2)).HasMore);
}));

Test("Notification durations and request hints follow policy without duplicate advice", () =>
{
 Equal(5d,Feedback.Lifetime(NoticeLevel.Info).TotalSeconds);
 Equal(7d,Feedback.Lifetime(NoticeLevel.Warn).TotalSeconds); Equal(7d,Feedback.Lifetime(NoticeLevel.Error).TotalSeconds);
 var hinted = Feedback.WithRequestHint("音频来源不在受支持的 B 站 CDN 范围内。");
 True(hinted.EndsWith(Feedback.RequestHint)); Equal(hinted,Feedback.WithRequestHint(hinted));
 True(Feedback.WithRequestHint("服务器返回了无效的资源地址。").EndsWith(Feedback.RequestHint));
 Equal("收藏夹已保存。",Feedback.WithRequestHint("收藏夹已保存。"));
});
Test("Queue selection stays inside the list through restore, metadata replacement and removal", () =>
{
 var first = new Track(1,"BV1xx411c7mD","one","UP","",10,Page:1);
 var second = first with { Page=2, Title="two" };
 var third = first with { Bvid="BV2xx411c7mD",Title="three" };
 True(QueueSelection.Resolve([],first) is null);
 Equal(first, QueueSelection.Resolve([first,second,third],null));
 Equal(second, QueueSelection.Resolve([first,second,third],second with { Cid=42 }));
 Equal(third,QueueSelection.Resolve([first,third],second,1));
 Equal(first,QueueSelection.Resolve([first],third,2));
 Equal(second,QueueSelection.Resolve([second,third],first));
});

tests.Add(("Incremental source coalesces concurrent requests and resumes after reset", async () =>
{
 var source = new PagedCollection<int>(); var more = true; var calls = 0;
 source.CanLoadMore = () => more;
 source.FetchPage = async ct => { calls++; await Task.Delay(10,ct); source.Add(calls); };
 var first = source.LoadPageAsync(); var second = source.LoadPageAsync();
 Equal(1u, await first); Equal(1u, await second); Equal(1,calls);
 Equal(1u, await source.LoadPageAsync()); Equal(2,calls);
 more = false; Equal(0u, await source.LoadPageAsync()); Equal(2,calls);
 more = true; source.ResetLoading(); Equal(1u, await source.LoadPageAsync()); Equal(3,calls);
}));
tests.Add(("Incremental source stops empty-page storms and ignores stale completion", async () =>
{
 var source = new PagedCollection<int>(); source.CanLoadMore = () => true;
 source.FetchPage = _ => Task.CompletedTask;
 Equal(0u,await source.LoadPageAsync()); True(!source.HasMoreItems);
 source.ResetLoading(); True(source.HasMoreItems);
 var ready = new TaskCompletionSource(); source.FetchPage = _ => ready.Task;
 var pending = source.LoadPageAsync(); await Task.Delay(10); source.ResetLoading(); ready.SetResult();
 Equal(0u, await pending); True(source.HasMoreItems);
 source.FetchPage = _ => { source.Add(1); return Task.CompletedTask; };
 Equal(1u,await source.LoadPageAsync());
}));

tests.Add(("Incremental cancellation and immediate cached completion release the loader", async () =>
{
 var source = new PagedCollection<int>(); source.CanLoadMore = () => true;
 source.FetchPage = _ => { source.Add(1); return Task.CompletedTask; };
 for (var i=0; i<50; i++) Equal(1u,await source.LoadPageAsync());
 Equal(50,source.Count);
 using var cancel = new CancellationTokenSource(); cancel.Cancel();
 await Throws<OperationCanceledException>(() => source.LoadPageAsync(cancel.Token));
 True(source.HasMoreItems); Equal(1u,await source.LoadPageAsync()); Equal(51,source.Count);
}));

Test("Track behavior honors saved append/replace and only Ask prompts in every view", () =>
{
 var a = new Track(1,"BV1xx411c7mD","one","UP","",10);
 var b = a with { Bvid="BV2xx411c7mD", Title="two" }; var c = a with { Page=2, Title="P2" };
 Equal(QueueChange.Append,QueuePolicy.AutomaticChange(TrackPlayBehavior.Append)!.Value);
 Equal(QueueChange.Replace,QueuePolicy.AutomaticChange(TrackPlayBehavior.Replace)!.Value);
 True(QueuePolicy.AutomaticChange(TrackPlayBehavior.Ask) is null);

 True(QueuePolicy.Apply([a],[b],false,b).SequenceEqual(new[]{a,b}));
 True(QueuePolicy.Apply([a,b],[b,c],false,c).SequenceEqual(new[]{a,b,c}));
 True(QueuePolicy.Apply([a],[b,c],true,c).SequenceEqual(new[]{b,c}));
 var resolved = b with { Cid=42 };
 Equal(2, QueuePolicy.Apply([a,b],[resolved],false,resolved).Count);
 Equal(42L,QueuePolicy.Apply([a,b],[resolved],false,resolved)[1].Cid);
});
Test("Video page parser reads JSON boundaries without evaluating scripts", () =>
{
 var data = VideoPage.ReadJson("<script>window.__playinfo__ = {\"data\":{\"title\":\"brace } and {\"}};alert('never evaluated')</script>","__playinfo__");
 Equal("brace } and {", data.GetProperty("data").GetProperty("title").GetString());
});
tests.Add(("Blocked metadata falls back to embedded video page and preserves parts and collection", async () =>
{
 using var client = new BilibiliClient(new Handler(r =>
 {
  if(r.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(account);
  if(r.RequestUri.Host == "api.bilibili.com") return Json("{\"code\":-352}");
  return new(HttpStatusCode.OK) { Content = new StringContent("""
  <script>window.__INITIAL_STATE__={"videoData":{"aid":1,"bvid":"BV1xx411c7mD","title":"multi","owner":{"mid":42,"name":"UP"},"pages":[{"cid":10,"page":1,"duration":20},{"cid":11,"page":2,"duration":30}],"ugc_season":{"id":55,"title":"series","sections":[{"episodes":[{"aid":1,"bvid":"BV1xx411c7mD","title":"one","cid":10,"arc":{"duration":20}},{"aid":2,"bvid":"BV2xx411c7mD","title":"two","cid":12,"arc":{"duration":40}}]}]}}};</script>
  """, Encoding.UTF8,"text/html") };
 }));
 await client.GetAccountAsync(); var video = await client.GetVideoAsync("BV1xx411c7mD");
 Equal(2,video.Parts.Count); Equal(11L,video.Parts[1].Cid); Equal(2,video.Parts[1].PartCount);
 Equal(55L,video.CollectionId); Equal(2,video.Collection!.Count); Equal(42L,video.Collection[1].UploaderId);
}));
tests.Add(("Collection episodes never inherit another video's artwork", async () =>
{
 using var client = new BilibiliClient(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/nav") ? Json(account) : Json("""
 {"code":0,"data":{"aid":1,"bvid":"BV1xx411c7mD","title":"Root","pic":"https://i0.hdslb.com/root.jpg","owner":{"mid":42,"name":"UP"},"pages":[{"cid":10,"page":1,"duration":20}],"ugc_season":{"sections":[{"episodes":[
 {"aid":1,"bvid":"BV1xx411c7mD","title":"Root episode","arc":{}},
 {"aid":2,"bvid":"BV2xx411c7mD","title":"Own cover","arc":{"pic":"https://i0.hdslb.com/second.jpg"}},
 {"aid":3,"bvid":"BV3xx411c7mD","title":"Missing cover","arc":{}}
 ]}]}}}
 """)));
 await client.GetAccountAsync(); var video = await client.GetVideoAsync("BV1xx411c7mD");
 Equal("https://i0.hdslb.com/root.jpg",video.Collection![0].Cover);
 Equal("https://i0.hdslb.com/second.jpg",video.Collection[1].Cover);
 Equal("",video.Collection[2].Cover);
}));
tests.Add(("Blocked audio falls back to page DASH and still validates CDN hosts", async () =>
{
 var evil = false;
 using var client = new BilibiliClient(new Handler(r =>
 {
  if(r.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(account);
  if(r.RequestUri.Host == "api.bilibili.com") return Json("{\"code\":-352}");
  var host = evil ? "evil.example" : "upos.bilivideo.com";
  return new(HttpStatusCode.OK) { Content = new StringContent("<script>window.__playinfo__={\"code\":0,\"data\":{\"dash\":{\"audio\":[{\"baseUrl\":\"https://" + host + "/audio.m4s\",\"codecs\":\"mp4a.40.2\"}]}}};</script>", Encoding.UTF8,"text/html") };
 }));
 await client.GetAccountAsync(); var track = new Track(1,"BV1xx411c7mD","one","UP","",20,10);
 Equal("upos.bilivideo.com",(await client.GetAudioAsync(track)).Url.Host);
 evil = true; await Throws<InvalidDataException>(() => client.GetAudioAsync(track));
}));
tests.Add(("Incomplete video metadata also falls back to the selected public video page", async () =>
{
 using var client = new BilibiliClient(new Handler(r =>
 {
  if(r.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(account);
  if(r.RequestUri.Host == "api.bilibili.com") return Json("{\"code\":0,\"data\":{}}");
  return new(HttpStatusCode.OK) { Content = new StringContent("""
  <script>window.__INITIAL_STATE__={"videoData":{"aid":1,"bvid":"BV1xx411c7mD","title":"one","pages":[{"cid":10,"page":1,"duration":20}]}};</script>
  """, Encoding.UTF8,"text/html") };
 }));
 await client.GetAccountAsync(); Equal(10L,(await client.GetVideoAsync("BV1xx411c7mD")).Parts[0].Cid);
}));
if (args.Contains("--live-music")) tests.Add(("Live public music channel pages continue with actual 15-card batches", async () =>
{
 using var client = new BilibiliClient(); client.RestoreCachedAccount(new(123,"test",""));
 var first = await client.GetMusicChannelAsync(1); var second = await client.GetMusicChannelAsync(2); var third = await client.GetMusicChannelAsync(3);
 True(first.Items.Count > 0); True(second.Items.Count > 0); True(third.Items.Count > 0);
 True(first.Items.Concat(second.Items).Concat(third.Items).All(t => !string.IsNullOrWhiteSpace(t.Uploader)));
 True(second.Items.Select(t=>t.Bvid).Except(first.Items.Select(t=>t.Bvid)).Any());
 Console.WriteLine($"Live music page counts: {first.Items.Count}, {second.Items.Count}, {third.Items.Count}");
}));

Test("Music author parser accepts attribute order, relative profile links and plain text", () =>
{
 var html = """
 <div class="feed-card head-card"><a href="https://www.bilibili.com/video/BV1xx411c7mD"></a><img src="//i0.hdslb.com/a.jpg"><div class="bili-video-card__title" title="two lines"></div>
 <a href="//space.bilibili.com/42" class="extra bili-video-card__author"><span></span><span>UP &amp; 音乐 · 11-01</span></a></div>
 """;
 var track = MusicPage.Parse(html).Single(); Equal("UP & 音乐", track.Uploader); Equal(42L,track.UploaderId);
});
Test("Lyrics remove music note decorations and omit empty note-only cues", () =>
{
 var lines = Subtitles.Parse("""{"body":[{"from":0,"to":1,"content":"♪ 中文歌曲 ♫"},{"from":1,"to":2,"content":"🎵 English lyrics 🎶"},{"from":2,"to":3,"content":"♩♬"}]}""");
 Equal(2,lines.Count); Equal("中文歌曲",lines[0].Content); Equal("English lyrics",lines[1].Content);
});

Test("Native volume icons match mute and three Windows wave levels", () =>
{
 Equal("\uE74F",PlaybackPresentation.VolumeGlyph(0)); Equal("\uE74F",PlaybackPresentation.VolumeGlyph(double.NaN));
 Equal("\uE993",PlaybackPresentation.VolumeGlyph(1)); Equal("\uE993",PlaybackPresentation.VolumeGlyph(33));
 Equal("\uE994",PlaybackPresentation.VolumeGlyph(34)); Equal("\uE994",PlaybackPresentation.VolumeGlyph(66));
 Equal("\uE995",PlaybackPresentation.VolumeGlyph(67)); Equal("\uE995",PlaybackPresentation.VolumeGlyph(100));
});
Test("Detail layout preserves lyrics space and square artwork in supported window sizes", () =>
{
 foreach(var size in new[]{(840d,380d),(1240d,640d),(1920d,850d)})
 {
  var layout = PlaybackPresentation.Measure(size.Item1,size.Item2);
  True(layout.LeftWidth+layout.Gap+layout.Padding*2+300 <= size.Item1);
  True(layout.CoverSize <= layout.LeftWidth && layout.CoverSize <= size.Item2-56);
  True(layout.CoverSize >= 100);
 }
});

Test("Artwork atmosphere preserves foreground contrast and never changes alpha", () =>
{
 var palette=ArtworkAtmosphere.Sample(new byte[]{0,40,200,255,0,40,200,255,200,90,0,255});
 Equal((byte)200,palette.Primary.R); Equal(ArtworkAtmosphere.Neutral,ArtworkAtmosphere.Sample([]));
 foreach(var dark in new[]{false,true})
 {
  var pixels=ArtworkAtmosphere.Render(palette,dark,32,20); Equal(32*20*4,pixels.Length);
  for(var i=0;i<pixels.Length;i++) if(i%4==3) Equal((byte)255,pixels[i]); else True(dark?pixels[i]<=80:pixels[i]>=214);
 }
});

Test("Interrupted lyric springs preserve position and velocity and settle without discontinuity", () =>
{
 var first = new SpringMotion(new(0,90,0), System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, .72, .4);
 var sample = first.At(.09);
 var next = new SpringMotion(sample.Position, new(6,0,0), sample.Velocity, .72, .4);
 True(System.Numerics.Vector3.Distance(sample.Position,next.At(0).Position)<.0001f);
 True(System.Numerics.Vector3.Distance(sample.Velocity,next.At(0).Velocity)<.0001f);
 True(System.Numerics.Vector3.Distance(next.At(3).Position,new(6,0,0))<.001f);
 True(next.At(3).Velocity.Length()<.001f);
 var delayed = first with { DelaySeconds=.08 };
 Equal(first.Initial,delayed.At(.04).Position);
 True(System.Numerics.Vector3.Distance(first.At(.1).Position,delayed.At(.18).Position)<.001f);
});

Test("English lyric lines capitalize their first letter without changing the remaining spelling", () =>
{
 Equal("And i love NASA", Subtitles.CleanContent("and i love NASA"));
 Equal("'Cause we're here", Subtitles.CleanContent("'cause we're here"));
 Equal("Hello\nWorld", Subtitles.CleanContent("hello\nworld"));
 Equal("中文 and English", Subtitles.CleanContent("中文 and English"));
 Equal("English lyrics", Subtitles.Parse("""{"body":[{"from":0,"to":2,"content":"♪ english lyrics ♫"}]}""").Single().Content);
});

tests.Add(("Own following and follower lists use account identity, pagination and sanitized profiles", async () =>
{
 var calls = 0;
 using var client = new BilibiliClient(new Handler(request =>
 {
  calls++; var query = Text.ParseQuery(request.RequestUri!.Query);
  Equal("123", query["vmid"]); Equal("50", query["ps"]);
  Equal(calls == 1 ? "/x/relation/followings" : "/x/relation/followers", request.RequestUri.AbsolutePath);
  Equal(calls == 1 ? "1" : "2", query["pn"]);
  return Json("""{"code":0,"data":{"total":51,"list":[{"mid":42,"uname":"<b>UP</b>","face":"//i0.hdslb.com/avatar.jpg","sign":"<i>hello</i>"},{"mid":42,"uname":"duplicate"},{"mid":0}]}}""");
 }));
 await Throws<BilibiliException>(() => client.GetSocialUsersAsync(false)); Equal(0,calls);
 client.RestoreCachedAccount(new(123,"test",""));
 var following = await client.GetSocialUsersAsync(false,1); True(following.HasMore); Equal(1,following.Items.Count);
 Equal("UP",following.Items[0].Name); Equal("hello",following.Items[0].Signature); Equal("https://i0.hdslb.com/avatar.jpg",following.Items[0].Avatar);
 var fans = await client.GetSocialUsersAsync(true,2); True(!fans.HasMore); Equal("https://space.bilibili.com/42",fans.Items[0].Url);
}));

tests.Add(("Social counts use the current authenticated account", async () =>
{
 using var client = new BilibiliClient(new Handler(request =>
 {
  Equal("/x/relation/stat",request.RequestUri!.AbsolutePath); Equal("123",Text.ParseQuery(request.RequestUri.Query)["vmid"]);
  return Json("""{"code":0,"data":{"following":1234,"follower":56789}}""");
 }));
 client.RestoreCachedAccount(new(123,"test",""));
 var counts=await client.GetSocialCountsAsync(); Equal(1234L,counts.Following);Equal(56789L,counts.Followers);
}));
Test("Local folder order survives reversed cloud order, metadata changes, additions, deletions and duplicates", () =>
{
 var a=new Folder(1,"A",0,true); var b=new Folder(2,"B",0,true); var c=new Folder(3,"订阅",0,false);
 True(FolderOrder.Merge(new[]{a,b,c},Array.Empty<long>()).Select(f=>f.Id).SequenceEqual(new long[]{1,2,3}));
 var saved=new long[]{3,2,1};
 var changed=FolderOrder.Merge(new[]{b with { Title="新版 B" },a,c,new Folder(4,"新收藏夹",0,true)},saved);
 True(changed.Select(f=>f.Id).SequenceEqual(new long[]{3,2,1,4}));Equal("新版 B",changed[1].Title);
 True(FolderOrder.Merge(new[]{a,c,a},saved.Concat(saved)).Select(f=>f.Id).SequenceEqual(new long[]{3,1}));
});

tests.Add(("Tracks without a Hi-Res stream do not advertise a selectable lossless quality", async () =>
{
 using var client = new BilibiliClient(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/nav") ? Json(account) :
  Json("""{"code":0,"data":{"dash":{"audio":[{"id":30280,"baseUrl":"https://upos.bilivideo.com/a.m4s","codecs":"mp4a.40.2"}],"flac":{"display":false,"audio":null}}}}""")));
 await client.GetAccountAsync(); var stream=await client.GetAudioAsync(new Track(1,"BV1xx411c7mD","test","up","",20,3));
 True(!stream.HasHiRes); Equal("标准",stream.Quality);
}));

Test("Preferred search ranking uses verified UID and requires query relevance", () =>
{
 Equal(9, PreferredUploaders.All.Count); Equal(9, PreferredUploaders.All.Select(user => user.Id).Distinct().Count());
 var ordinary = new Track(1,"ordinary","Free Loop","Other","",120, UploaderId:1);
 var preferred = ordinary with { Aid=2, Bvid="preferred", Uploader="Changed display name", UploaderId=383152785 };
 var unrelated = preferred with { Aid=3, Bvid="unrelated", Title="Yesterday" };
 var spoof = ordinary with { Aid=4, Bvid="spoof", Uploader="云端音乐铺" };
 var ranked = PreferredUploaders.RankSearch(new[]{ordinary,unrelated,spoof,preferred,preferred},"Ｆｒｅｅ loop");
 True(ranked.Select(track=>track.Bvid).SequenceEqual(new[]{"preferred","ordinary","unrelated","spoof"}));
 True(!PreferredUploaders.Matches(unrelated,"Free Loop"));
});
Test("Home blend increases preferred creators without duplicate videos", () =>
{
 var a = new Track(1,"a","A","UP","",120, UploaderId:383152785);
 var b = a with { Bvid="b" }; var c = a with { Bvid="c", UploaderId=9666167 };
 var other = a with { Bvid="other", UploaderId=1 };
 True(PreferredUploaders.BlendHome(new[]{a,b,c},new[]{a,other}).Select(track=>track.Bvid).SequenceEqual(new[]{"a","b","c","other"}));
});
tests.Add(("Preferred searches supplement only matching creator archives and cache responses", async () =>
{
 var archiveCalls = 0;
 using var client = new BilibiliClient(new Handler(request =>
 {
  var path=request.RequestUri!.AbsolutePath;
  if(path.EndsWith("/nav")) return Json(account);
  if(path=="/x/space/wbi/arc/search")
  {
   archiveCalls++; var query=Text.ParseQuery(request.RequestUri.Query);
   var mid=long.Parse(query["mid"]); Equal("Free Loop",query["keyword"]); Equal("3",query["tid"]);
   True(query.ContainsKey("w_rid"));
   return Json(System.Text.Json.JsonSerializer.Serialize(new {code=0,data=new {list=new {vlist=new[] {
    new {aid=mid,bvid=$"preferred-{mid}",title="Free Loop",author="UP",mid,pic="//i0.hdslb.com/a.jpg",length="03:00"},
    new {aid=mid,bvid=$"wrong-query-{mid}",title="Yesterday",author="UP",mid,pic="//i0.hdslb.com/a.jpg",length="03:00"},
    new {aid=mid,bvid=$"wrong-owner-{mid}",title="Free Loop",author="UP",mid=404L,pic="//i0.hdslb.com/a.jpg",length="03:00"}
   }},page=new {count=3}}}));
  }
  return Json("""{"code":0,"data":{"numPages":2,"result":[{"aid":1,"bvid":"ordinary","title":"Free Loop","author":"Other","mid":1,"duration":"03:00"}]}}""");
 }));
 await client.GetAccountAsync(); var first=await client.SearchPreferredAsync("Free Loop",1);
 Equal(10,first.Items.Count); True(first.HasMore); True(first.Items.Take(9).All(track=>PreferredUploaders.Contains(track.UploaderId)));
 Equal("ordinary",first.Items[^1].Bvid); Equal(9,archiveCalls);
 await client.SearchPreferredAsync("Free Loop",1); Equal(9,archiveCalls);
 await client.SearchPreferredAsync("Free Loop",2); Equal(9,archiveCalls);
}));
tests.Add(("Creator rate limits preserve normal search and stop the remaining archive sweep", async () =>
{
 var archiveCalls=0;
 using var client=new BilibiliClient(new Handler(request =>
 {
  if(request.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(account);
  if(request.RequestUri.AbsolutePath=="/x/space/wbi/arc/search") { archiveCalls++; return Json("""{"code":-352,"message":"limited"}"""); }
  return Json("""{"code":0,"data":{"numPages":1,"result":[{"aid":1,"bvid":"ordinary","title":"Free Loop","author":"Other","mid":1,"duration":"03:00"}]}}""");
 }));
 await client.GetAccountAsync(); Equal(1,(await client.SearchPreferredAsync("Free Loop",1)).Items.Count);
 True(archiveCalls<=2); var before=archiveCalls;
 Equal(1,(await client.SearchPreferredAsync("Different song",1)).Items.Count); Equal(before,archiveCalls);
}));
tests.Add(("Homepage rotates all preferred creators while retaining channel discovery", async () =>
{
 using var client=new BilibiliClient(new Handler(request =>
 {
  var path=request.RequestUri!.AbsolutePath;
  if(path.EndsWith("/nav")) return Json(account);
  if(path=="/x/space/wbi/arc/search")
  {
   var mid=long.Parse(Text.ParseQuery(request.RequestUri.Query)["mid"]);
   return Json(System.Text.Json.JsonSerializer.Serialize(new {code=0,data=new {list=new {vlist=Enumerable.Range(0,30).Select(i=>new {
    aid=mid+i,bvid=$"preferred-{mid}-{i}",title=$"Song {i}",author="UP",mid,pic="//i0.hdslb.com/a.jpg",length="03:00"})},page=new {count=90}}}));
  }
  if(path=="/c/music/") return new(HttpStatusCode.OK) { Content=new StringContent("""<div class="feed-card head-card"><a href="https://www.bilibili.com/video/BV1xx411c7mD"></a><img src="//i0.hdslb.com/a.jpg"><div class="bili-video-card__title" title="频道推荐"></div></div>""",Encoding.UTF8,"text/html") };
  return Json("""{"code":0,"data":{"archives":[{"aid":1,"bvid":"channel","title":"频道发现","cover":"//i0.hdslb.com/a.jpg","author":{"name":"Other","mid":1},"duration":90}]}}""");
 }));
 await client.GetAccountAsync(); var creators=new HashSet<long>();
 foreach(var page in new[]{1,2,3})
 {
  var result=await client.GetPersonalizedMusicAsync(page);
  Equal(18,result.Items.Count(track=>PreferredUploaders.Contains(track.UploaderId)));
  True(result.Items.Any(track=>!PreferredUploaders.Contains(track.UploaderId))); True(result.HasMore);
  foreach(var track in result.Items.Where(track=>PreferredUploaders.Contains(track.UploaderId))) creators.Add(track.UploaderId);
 }
 Equal(9,creators.Count);
}));
Test("Original subtitle language wins over translation and Chinese marketing prose", () =>
{
 var zh=new SubtitleTrack("zh-Hans","中文",new("https://i0.hdslb.com/z.json"),false);
 var en=new SubtitleTrack("en-US","English",new("https://i0.hdslb.com/e.json"),false);
 var translated=new SubtitleTrack("ai-zh","中文（AI 翻译）",new("https://i0.hdslb.com/t.json"),true);
 var original=new SubtitleTrack("ai-en","英语（自动生成）",new("https://i0.hdslb.com/a.json"),true);
 Equal(en,SubtitleSelection.Choose(new[]{zh,en,translated,original},"开口就沦陷，这首宝藏歌曲太治愈了"));
 Equal(en,SubtitleSelection.Choose(new[]{zh,en},"前奏一响就沦陷，每次听都会想起往事—《Free Loop》"));
 var ja=new SubtitleTrack("ja-JP","日本語",new("https://i0.hdslb.com/j.json"),false);
 var aiJa=ja with { Language="ai-ja", IsAuto=true };
 Equal(ja,SubtitleSelection.Choose(new[]{zh,ja,translated,aiJa},"百万级录音棚试听"));
 Equal(ja,SubtitleSelection.Choose(new[]{zh,ja},"《真夜中のドア》"));
 var ko=new SubtitleTrack("ko-KR","한국어",new("https://i0.hdslb.com/k.json"),false);
 Equal(ko,SubtitleSelection.Choose(new[]{zh,ko},"《봄날》"));
});

tests.Add(("Creator subtitle status never adds an AI suffix without AI source metadata", async () =>
{
 using var client=new BilibiliClient(new Handler(request=>request.RequestUri!.AbsolutePath.EndsWith("/nav") ? Json(account) : Json(
  """{"code":0,"data":{"subtitle":{"subtitles":[{"lan":"zh-CN","lan_doc":"创作者字幕","subtitle_url":"//aisubtitle.hdslb.com/manual.json","ai_status":2,"ai_type":0},{"lan":"ai-en","lan_doc":"英语","subtitle_url":"//aisubtitle.hdslb.com/ai.json","ai_status":2,"ai_type":1}]}}}""")));
 await client.GetAccountAsync(); var tracks=await client.GetSubtitleTracksAsync(new(1,"BV1xx411c7mD","test","UP","",120,123));
 var creator=tracks.Single(track=>track.Language=="zh-CN"); var generated=tracks.Single(track=>track.Language=="ai-en");
 True(!creator.IsAuto); Equal("创作者字幕",creator.DisplayName); True(generated.IsAuto); Equal("英语 · AI",generated.DisplayName);
 var legacy=creator with { IsAuto=true,AiType=null };
 True(!legacy.NormalizeOrigin().IsAuto); Equal("创作者字幕",legacy.DisplayName);
 var oldGenerated=new SubtitleTrack("ai-zh","中文",new("https://i0.hdslb.com/ai.json"),true);
 True(oldGenerated.NormalizeOrigin().IsAuto); Equal("中文 · AI",oldGenerated.DisplayName);
}));

Test("Lyric animation keeps long adjacent lines separated through upward motion and reversals", () =>
{
 var heights=new[]{44d,132d,440d,88d,264d,44d};
 var tops=new double[heights.Length]; var cursor=0d;
 for(var i=0;i<heights.Length;i++) { var padding=LyricMotionLayout.Padding(heights[i]); tops[i]=cursor+padding; cursor+=heights[i]+2*padding; }
 var scroll=new SpringMotion(new(0,280,0),System.Numerics.Vector3.Zero,new(0,-250,0),.82,.5);
 var scales=heights.Select((_,i)=>new SpringMotion(new(i==0?1.13f:.95f),new(i==1?1.13f:.95f),new(i%2==0?2f:-2f),.62,.44,i*.018)).ToArray();
 var ripples=heights.Select((_,i)=>new SpringMotion(new(0,i%2==0?8:-8,0),System.Numerics.Vector3.Zero,new(0,i%2==0?120:-120,0),.7,.5,i*.018)).ToArray();
 for(var pass=0;pass<5;pass++)
 {
  for(var frame=0;frame<100;frame++)
  {
   var t=frame*.01; var flow=scroll.At(t).Position.Y; double? previousBottom=null;
   for(var row=0;row<heights.Length;row++)
   {
    var bounds=LyricMotionLayout.Bounds(tops[row],heights[row],scales[row].At(t).Position.Y,ripples[row].At(t).Position.Y,flow);
    if(previousBottom is { } bottom) True(bounds.Top-bottom>=19.9);
    previousBottom=bounds.Bottom;
   }
  }
  var sampled=scroll.At(.09);
  scroll=new(sampled.Position+new System.Numerics.Vector3(0,pass%2==0?-180:230,0),System.Numerics.Vector3.Zero,sampled.Velocity,.82,.5);
 }
});

tests.Add(("Comments read hot/latest, pinned deduplication and pagination without losing literal content", async () =>
{
 using var client=new BilibiliClient(new Handler(request=>
 {
  if(request.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(account);
  Equal("/x/v2/reply",request.RequestUri.AbsolutePath);
  var q=Text.ParseQuery(request.RequestUri.Query); Equal("1",q["type"]);Equal("123",q["oid"]);
  var page=int.Parse(q["pn"]);Equal(page==1?"2":"0",q["sort"]);
  var rows=Enumerable.Range(page==1?1:21,page==1?20:1).Select(i=>new {rpid=i,member=new {mid=i,uname=$"User {i}",avatar="//i0.hdslb.com/a.jpg"},content=new {message="Literal &lt;tag&gt; &amp; text"},like=3,ctime=1700000000});
  return Json(System.Text.Json.JsonSerializer.Serialize(new {code=0,data=new {page=new {count=21},top_replies=rows.Take(1),replies=rows}}));
 }));
 await client.GetAccountAsync();
 var hot=await client.GetCommentsAsync(123,CommentSort.Hot);Equal(20,hot.Items.Count);True(hot.Items[0].IsPinned);True(hot.HasMore);
 Equal("Literal <tag> & text",hot.Items[0].Content);Equal("https://i0.hdslb.com/a.jpg",hot.Items[0].Avatar);
 var latest=await client.GetCommentsAsync(123,CommentSort.Latest,2);Equal(1,latest.Items.Count);True(!latest.HasMore);
 using var loggedOut=new BilibiliClient(new Handler((Func<HttpRequestMessage,HttpResponseMessage>)(_=>throw new Exception("Should not send"))));
 await Throws<BilibiliException>(()=>loggedOut.GetCommentsAsync(123,CommentSort.Hot));
}));
tests.Add(("Comment posting validates input, includes CSRF and is never retried after an uncertain write", async () =>
{
 var posts=0;var fail=false;
 using var client=new BilibiliClient(new Handler(async request=>
 {
  if(request.Method==HttpMethod.Get) return Json(account);
  posts++;Equal("/x/v2/reply/add",request.RequestUri!.AbsolutePath);
  var form=Text.ParseQuery(await request.Content!.ReadAsStringAsync());
  Equal("test-csrf",form["csrf"]);Equal("1",form["type"]);Equal("123",form["oid"]);Equal("1",form["plat"]);Equal("测试评论",form["message"]);
  if(fail) throw new HttpRequestException("uncertain write");
  return Json("""{"code":0,"data":{"reply":{"rpid":42,"member":{"mid":123,"uname":"测试用户","avatar":"//i0.hdslb.com/a.jpg"},"content":{"message":"测试评论"},"ctime":1700000000}}}""");
 }));
 client.ImportSession([new("bili_jct","test-csrf",".bilibili.com","/",DateTime.UtcNow.AddDays(1))]);await client.GetAccountAsync();
 await Throws<ArgumentException>(()=>client.PostCommentAsync(123,"  "));await Throws<ArgumentException>(()=>client.PostCommentAsync(123,new string('x',1001)));Equal(0,posts);
 Equal(42L,(await client.PostCommentAsync(123," 测试评论 "))!.Id);Equal(1,posts);
 fail=true;await Throws<HttpRequestException>(()=>client.PostCommentAsync(123,"测试评论"));Equal(2,posts);
 var anonymousPosts=0;
 using var expired=new BilibiliClient(new Handler(request=>
 {
  if(request.Method==HttpMethod.Post) anonymousPosts++;
  return Json("""{"code":0,"data":{"isLogin":false}}""");
 }));
 expired.RestoreCachedAccount(new(123,"旧缓存账号",""));
 await Throws<BilibiliException>(()=>expired.GetCommentsAsync(123,CommentSort.Latest));
 await Throws<BilibiliException>(()=>expired.PostCommentAsync(123,"测试评论"));Equal(0,anonymousPosts);
}));
tests.Add(("Comment replies paginate with root and parse votes, reply counts and inline emotes", async () =>
{
 using var client=new BilibiliClient(new Handler(request=>
 {
  if(request.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(account);
  var q=Text.ParseQuery(request.RequestUri.Query);
  Equal("/x/v2/reply/reply",request.RequestUri.AbsolutePath);Equal("123",q["oid"]);Equal("42",q["root"]);Equal("1",q["type"]);Equal("20",q["ps"]);
  return Json("""{"code":0,"data":{"page":{"count":21},"replies":[{"rpid":99,"action":2,"rcount":0,"like":7,"member":{"mid":"123","uname":"回复用户"},"content":{"message":"好听[doge]","emote":{"[doge]":{"url":"//i0.hdslb.com/a.png"},"[bad]":{"url":"https://example.com/bad.png"}}}}]}}""");
 }));
 await client.GetAccountAsync();
 var first=await client.GetCommentRepliesAsync(123,42);True(first.HasMore);Equal(1,first.Items.Count);
 Equal(CommentReaction.Dislike,first.Items[0].Reaction);Equal(0L,first.Items[0].ReplyCount);Equal(1,first.Items[0].Emotes!.Count);Equal("[doge]",first.Items[0].Emotes![0].Text);
 True(!(await client.GetCommentRepliesAsync(123,42,2)).HasMore);
 await Throws<ArgumentOutOfRangeException>(()=>client.GetCommentRepliesAsync(123,0));
 await Throws<ArgumentOutOfRangeException>(()=>client.GetCommentRepliesAsync(123,42,0));
}));
tests.Add(("Comment vote endpoints include CSRF and distinguish add/cancel without retrying", async () =>
{
 var writes=new List<(string Path,string Action)>();var fail=false;
 using var client=new BilibiliClient(new Handler(async request=>
 {
  if(request.Method==HttpMethod.Get) return Json(account);
  var q=Text.ParseQuery(await request.Content!.ReadAsStringAsync());Equal("test-csrf",q["csrf"]);Equal("123",q["oid"]);Equal("42",q["rpid"]);Equal("1",q["type"]);
  writes.Add((request.RequestUri!.AbsolutePath,q["action"]));
  if(fail) throw new HttpRequestException("uncertain vote");
  return Json("""{"code":0,"data":{}}""");
 }));
 client.ImportSession([new("bili_jct","test-csrf",".bilibili.com","/",DateTime.UtcNow.AddDays(1))]);await client.GetAccountAsync();
 foreach(var reaction in new[]{CommentReaction.Like,CommentReaction.Dislike})
 foreach(var enabled in new[]{true,false}) await client.SetCommentReactionAsync(123,42,reaction,enabled);
 True(writes.SequenceEqual(new[]{("/x/v2/reply/action","1"),("/x/v2/reply/action","0"),("/x/v2/reply/hate","1"),("/x/v2/reply/hate","0")}));
 await Throws<ArgumentOutOfRangeException>(()=>client.SetCommentReactionAsync(123,42,CommentReaction.None,true));
 fail=true;await Throws<HttpRequestException>(()=>client.SetCommentReactionAsync(123,42,CommentReaction.Like,true));Equal(5,writes.Count);
}));
tests.Add(("Comment emote panel uses accessible packages and safe media URLs", async () =>
{
 using var client=new BilibiliClient(new Handler(request=>request.RequestUri!.AbsolutePath.EndsWith("/nav")?Json(account):Json("""{"code":0,"data":{"packages":[{"text":"小黄脸","emote":[{"text":"[doge]","url":"//i0.hdslb.com/a.png","flags":{"no_access":false,"unlocked":true}},{"text":"[locked]","url":"//i0.hdslb.com/b.png","flags":{"no_access":true}},{"text":"[bad]","url":"https://example.com/a.png"},{"text":"[locked2]","url":"//i0.hdslb.com/b.png","flags":{"unlocked":false}}]}]}}""")));
 await client.GetAccountAsync();var packages=await client.GetCommentEmotesAsync();Equal(1,packages.Count);Equal(1,packages[0].Items.Count);Equal("[doge]",packages[0].Items[0].Text);
}));
Test("Playback speed choices and labels reject unsupported or non-finite values",()=>
{
 Equal(7,PlaybackSpeed.Presets.Count);Equal("1.25×",PlaybackSpeed.Label(1.25));
 foreach(var value in PlaybackSpeed.Presets) Equal(value,PlaybackSpeed.Validate(value));
 foreach(var value in new[]{0d,-1,2.1,double.NaN,double.PositiveInfinity})
 { var rejected=false;try {PlaybackSpeed.Validate(value);}catch(ArgumentOutOfRangeException){rejected=true;}True(rejected); }
});

Test("Interrupted cover tweens preserve position and velocity and finish exactly at the latest target",()=>
{
 var source=new System.Numerics.Vector4(16,600,80,80);var target=new System.Numerics.Vector4(150,80,500,500);
 var tween=new MotionTween(source,target,(target-source)*(float)(3/.36),.36);
 var random=new Random(20261102);
 for(var turn=0;turn<80;turn++)
 {
  var elapsed=.01+random.NextDouble()*.16;var sample=tween.At(elapsed);
  var next=turn%2==0?source:target;
  var replacement=new MotionTween(sample.Position,next,sample.Velocity,turn%2==0?.24:.36);
  True(System.Numerics.Vector4.Distance(replacement.At(0).Position,sample.Position)<.001f);
  True(System.Numerics.Vector4.Distance(replacement.At(0).Velocity,sample.Velocity)<.001f);
  for(var frame=0;frame<=60;frame++)
  {
   var p=replacement.At(replacement.Seconds*frame/60).Position;True(float.IsFinite(p.X)&&float.IsFinite(p.Y)&&p.Z>0&&p.W>0);
  }
  Equal(next,replacement.At(replacement.Seconds).Position);Equal(System.Numerics.Vector4.Zero,replacement.At(replacement.Seconds).Velocity);
  tween=replacement;
 }
});

tests.Add(("Background discovery and account verification use one coherent signing-key snapshot",async()=>
{
 var nav=0;var searches=0;
 using var client=new BilibiliClient(new Handler(request=>
 {
  if(request.RequestUri!.AbsolutePath.EndsWith("/nav"))
  {
   var alternate=Interlocked.Increment(ref nav)%2==0;
   return Json(System.Text.Json.JsonSerializer.Serialize(new {code=0,data=new {isLogin=true,mid=123,uname="test",wbi_img=new {img_url=$"https://i0.hdslb.com/{(alternate?image:sub)}.png",sub_url=$"https://i0.hdslb.com/{(alternate?sub:image)}.png"}}}));
  }
  var query=Text.ParseQuery(request.RequestUri.Query);var timestamp=long.Parse(query["wts"]);var signature=query["w_rid"];query.Remove("wts");query.Remove("w_rid");
  var a=Text.ParseQuery(WbiSigner.Sign(query,image,sub,timestamp))["w_rid"];var b=Text.ParseQuery(WbiSigner.Sign(query,sub,image,timestamp))["w_rid"];
  True(signature==a||signature==b);Interlocked.Increment(ref searches);return Json("""{"code":0,"data":{"result":[],"numPages":0}}""");
 }));
 await client.GetAccountAsync();
 await Task.WhenAll(Enumerable.Range(0,60).Select(index=>Task.Run(async()=>
 {if(index%2==0) await client.GetAccountAsync();else await client.SearchAsync("test",1);})));Equal(30,searches);
}));


Test("Lyric following leads by 100ms while active highlight stays on the current cue", () =>
{
 SubtitleLine[] lines = [new(0, 5, "One"), new(5, 9, "Two")];
 Equal(0,Subtitles.FollowIndex(lines,4.89)); Equal(1,Subtitles.FollowIndex(lines,4.9));
 Equal(0,Subtitles.ActiveIndex(lines,4.9)); Equal(1,Subtitles.ActiveIndex(lines,5));
 Equal(-1,Subtitles.FollowIndex(lines,10));
});
tests.Add(("Audio parser promotes trusted backups and skips broken audio representations", async () =>
{
 using var client = new BilibiliClient(new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/nav") ? Json(account) : Json("""{"code":0,"data":{"dash":{"audio":[{"codecs":"mp4a","bandwidth":999,"baseUrl":"https://127.0.0.1/a","backupUrl":[null,"bad"]},{"codecs":"mp4a","base_url":"https://unsupported.invalid/a","backup_url":["broken","https://upos.bilivideo.com/good.m4s","https://backup.bilivideo.cn/good.m4s"]}]}}}""")));
 await client.GetAccountAsync(); var audio = await client.GetAudioAsync(new(1,"BV1xx411c7mD","test","UP","",10,1));
 Equal("upos.bilivideo.com",audio.Url.Host); Equal(1,audio.Backups.Count);
}));
tests.Add(("CDN relay follows trusted redirects and fails over without leaking headers", async () =>
{
 var calls = new List<string>();
 using var proxy = new AudioProxy(new Handler(r =>
 {
  calls.Add(r.RequestUri!.Host); True(!r.Headers.Contains("Cookie")); Equal("bytes=0-2",r.Headers.Range!.ToString());
  if(r.RequestUri.Host=="primary.bilivideo.com") return new(HttpStatusCode.Found){Headers={Location=new Uri("https://127.0.0.1/blocked")}};
  if(r.RequestUri.Host=="backup.bilivideo.com") return new(HttpStatusCode.TemporaryRedirect){Headers={Location=new Uri("https://final.bilivideo.cn/a")}};
  return new(HttpStatusCode.PartialContent){Content=new ByteArrayContent([1,2,3])};
 }));
 using var http=new HttpClient();using var request=new HttpRequestMessage(HttpMethod.Get,proxy.SetSource(new(new("https://primary.bilivideo.com/a"),"audio/mp4",[new("https://backup.bilivideo.com/a")])));
 request.Headers.Range=new(0,2);using var response=await http.SendAsync(request);
 Equal(HttpStatusCode.PartialContent,response.StatusCode);True((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[]{1,2,3}));
 True(calls.SequenceEqual(new[]{"primary.bilivideo.com","backup.bilivideo.com","final.bilivideo.cn"}));
}));


if(args.Contains("--live-audio")) tests.Add(("Anonymous public video resolves and streams real audio through the CDN relay",async()=>
{
 using var client=new BilibiliClient();client.RestoreCachedAccount(new(1,"Anonymous fixture",""));
 using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
 var video=await client.GetVideoAsync("BV1GJ411x7h7",ct:timeout.Token);
 var source=await client.GetAudioAsync(video.Parts[0],timeout.Token);
 using var proxy=new AudioProxy();using var http=new HttpClient();
 using var request=new HttpRequestMessage(HttpMethod.Get,proxy.SetSource(source));request.Headers.Range=new(0,1023);
 using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);response.EnsureSuccessStatusCode();
 await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);var bytes=new byte[128];await stream.ReadExactlyAsync(bytes,timeout.Token);
 True(bytes.Any(value=>value!=0));
}));


tests.Add(("Comment replies send the root and immediate parent exactly once",async()=>
{
 var writes=0;
 using var client=new BilibiliClient(new Handler(async request=>
 {
  if(request.Method==HttpMethod.Get)return Json(account);
  writes++;var form=Text.ParseQuery(await request.Content!.ReadAsStringAsync());
  Equal("100",form["root"]);Equal("101",form["parent"]);Equal("test-csrf",form["csrf"]);Equal("hello",form["message"]);
  return Json("""{"code":0,"data":{"reply":{"rpid":102,"member":{"mid":123,"uname":"test"},"content":{"message":"hello"}}}}""");
 }));
 client.ImportSession([new("bili_jct","test-csrf",".bilibili.com","/",DateTime.UtcNow.AddDays(1))]);await client.GetAccountAsync();
 var reply=await client.PostCommentAsync(71,"hello",root:100,parent:101);Equal(102L,reply!.Id);Equal(1,writes);
 await Throws<ArgumentException>(()=>client.PostCommentAsync(71,"hello",root:100));Equal(1,writes);
}));


tests.Add(("Artwork cache coalesces, survives restart, clears without touching unrelated files",async()=>
{
 var folder=Path.Combine(Path.GetTempPath(),"biliplay-cache-test-"+Guid.NewGuid().ToString("N"));var calls=0;
 try
 {
  using var cache=new ImageDiskCache(folder,new Handler(async r=>{Interlocked.Increment(ref calls);True(!r.Headers.Contains("Cookie"));await Task.Delay(30);return new(HttpStatusCode.OK){Content=new ByteArrayContent([137,80,78,71,13,10,26,10,0,0,0,0,1])};}));
  var uri=new Uri("https://i0.hdslb.com/test.png");var paths=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>cache.GetAsync(uri)));Equal(1,calls);True(paths.All(path=>path==paths[0]&&File.Exists(path)));
  using(var restarted=new ImageDiskCache(folder,new Handler(_=>Task.FromException<HttpResponseMessage>(new Exception("Unexpected network")))))Equal(paths[0],await restarted.GetAsync(uri));
  var unrelated=Path.Combine(folder,"keep.txt");await File.WriteAllTextAsync(unrelated,"fixture");await cache.ClearAsync();True(!Directory.EnumerateFiles(folder,"*.img").Any());True(File.Exists(unrelated));
  True(await cache.GetAsync(new Uri("https://127.0.0.1/private")) is null);Equal(1,calls);
  await cache.GetAsync(uri);Equal(2,calls);
 }
 finally {if(Directory.Exists(folder))Directory.Delete(folder,true);}
}));
tests.Add(("WebP cache migration bypasses old bytes and shares the native-format cache entry",async()=>
{
 var folder=Path.Combine(Path.GetTempPath(),"biliplay-cache-test-"+Guid.NewGuid().ToString("N"));
 var legacy=new Uri("https://i0.hdslb.com/avatar.jpg@80w.webp");
 var native=Text.CompatibleImageUri(legacy);var calls=0;
 try
 {
  Directory.CreateDirectory(folder);
  var oldKey=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(legacy.AbsoluteUri)));
  var oldPath=Path.Combine(folder,oldKey+".img");
  await File.WriteAllBytesAsync(oldPath,Encoding.ASCII.GetBytes("RIFF0000WEBP"));
  using var cache=new ImageDiskCache(folder,new Handler(r=>{
   calls++;Equal(native,r.RequestUri!);True(!r.Headers.Contains("Cookie"));
   return new(HttpStatusCode.OK){Content=new ByteArrayContent([137,80,78,71,13,10,26,10,0,0,0,0])};
  }));
  var path=await cache.GetAsync(legacy);True(path is not null && path!=oldPath);
  Equal(path,await cache.GetAsync(native));Equal(1,calls);
  Equal((byte)137,(await File.ReadAllBytesAsync(path!))[0]);
  Equal("RIFF0000WEBP",await File.ReadAllTextAsync(oldPath));
 }
 finally {if(Directory.Exists(folder))Directory.Delete(folder,true);}
}));

tests.Add(("A disposed profile image cache never recreates its directory",async()=>
{
 var folder=Path.Combine(Path.GetTempPath(),"biliplay-cache-test-"+Guid.NewGuid().ToString("N"));
 var calls=0;
 try
 {
  using var cache=new ImageDiskCache(folder,new Handler(_=>{calls++;return new(HttpStatusCode.OK);}));
  cache.Dispose();await cache.ClearAsync();
  True(await cache.GetAsync(new Uri("https://i0.hdslb.com/avatar.png")) is null);
  True(!Directory.Exists(folder));Equal(0,calls);
 }
 finally {if(Directory.Exists(folder))Directory.Delete(folder,true);}
}));

tests.Add(("Clearing artwork cache prevents in-flight downloads from repopulating it",async()=>
{
 var folder=Path.Combine(Path.GetTempPath(),"biliplay-cache-test-"+Guid.NewGuid().ToString("N"));var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
 try
 {
  using var cache=new ImageDiskCache(folder,new Handler(async _=>{entered.TrySetResult();await release.Task;return new(HttpStatusCode.OK){Content=new ByteArrayContent([137,80,78,71,13,10,26,10,0,0,0,0])};}));
  var pending=cache.GetAsync(new Uri("https://i0.hdslb.com/pending.png"));await entered.Task;await cache.ClearAsync();release.TrySetResult();True(await pending is null);True(!Directory.EnumerateFiles(folder).Any());
 }
 finally {release.TrySetResult();if(Directory.Exists(folder))Directory.Delete(folder,true);}
}));
tests.Add(("Search keyword suggestions preserve real text, remove markup and deduplicate",async()=>
{
 using var client=new BilibiliClient(new Handler(request=>{Equal("s.search.bilibili.com",request.RequestUri!.Host);Equal("晴天",Text.ParseQuery(request.RequestUri.Query)["term"]);return Json("""{"code":0,"result":{"tag":[{"value":"<em>晴天</em> 周杰伦"},{"value":"晴天 周杰伦"},{"value":"晴天 现场"}]}}""");}));
 var items=await client.GetSearchSuggestionsAsync("晴天");Equal(2,items.Count);Equal("晴天 周杰伦",items[0]);
}));


tests.Add(("Temporary debug sessions delete only owned data and skip active or unrelated folders",async()=>
{
 var parent=Path.Combine(Path.GetTempPath(),"biliplay-session-test-"+Guid.NewGuid().ToString("N"));
 try
 {
  var unrelated=Path.Combine(parent,"keep");Directory.CreateDirectory(unrelated);await File.WriteAllTextAsync(Path.Combine(unrelated,"data"),"keep");
  string owned;
  using(var session=new TemporarySession(parent))
  {owned=session.DirectoryPath;await File.WriteAllTextAsync(Path.Combine(owned,"state.json"),"fixture");TemporarySession.CleanAbandoned(parent);True(Directory.Exists(owned));}
  True(!Directory.Exists(owned));True(File.Exists(Path.Combine(unrelated,"data")));
  var abandoned=Path.Combine(parent,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(abandoned);await File.WriteAllTextAsync(Path.Combine(abandoned,".biliplay-demo"),"");
  TemporarySession.CleanAbandoned(parent);True(!Directory.Exists(abandoned));True(Directory.Exists(unrelated));
 }
 finally {if(Directory.Exists(parent))Directory.Delete(parent,true);}
}));

tests.Add(("Notification feeds parse typed rows, stacked users, actual counts and cursor pagination without phantom messages", async () =>
{
 var likePages=0;var mentionPages=0;
 using var client=new BilibiliClient(new Handler(request =>
 {
  Equal(HttpMethod.Get, request.Method);
  var url=request.RequestUri!;var query=Text.ParseQuery(url.Query);
  if(url.AbsolutePath.EndsWith("get_sessions"))
  {
   Equal("api.vc.bilibili.com",url.Host);
   return Json("""{"code":0,"data":{"has_more":0,"session_list":[{"talker_id":7,"talker_info":{"name":"私信用户","pic_url":"https://i0.hdslb.com/dm.jpg"},"session_ts":1730000000000000,"last_msg":{"content":"{\"content\":\"你好\"}"}}]}}""");
  }
  if(url.AbsolutePath.EndsWith("/like"))
  {
   likePages++;
   if(likePages==1) return Json("""{"code":0,"data":{"latest":{"items":[{"id":71,"counts":37,"like_time":1730000200,"users":[{"mid":8,"nickname":"甲","avatar":"https://i0.hdslb.com/a.jpg"},{"mid":9,"nickname":"乙","avatar":"https://i0.hdslb.com/b.jpg"}],"item":{"business":"评论","title":"被点赞的评论内容","image":"https://i0.hdslb.com/video.jpg","uri":"https://www.bilibili.com/video/BV1xx411c7mD"}}]},"total":{"cursor":{"id":71,"time":1730000200,"is_end":false},"items":[{"id":71,"counts":37,"like_time":1730000200,"users":[{"nickname":"甲","avatar":"https://i0.hdslb.com/a.jpg"}],"item":{"business":"评论","title":"被点赞的评论内容"}}]}}}""");
   Equal("71",query["id"]);Equal("1730000200",query["like_time"]);True(!query.ContainsKey("pn"));
   return Json("""{"code":0,"data":{"total":{"cursor":{"is_end":true},"items":[{"id":72,"counts":2,"like_time":1730000100,"users":[{"nickname":"丙","avatar":"https://i0.hdslb.com/c.jpg"}],"item":{"business":"视频","title":"视频标题","image":"https://i0.hdslb.com/video.jpg"}}]}}}""");
  }
  if(url.AbsolutePath.EndsWith("/at"))
  {
   mentionPages++;return Json("""{"code":0,"data":{"cursor":{"is_end":true},"items":[{"id":80,"at_time":1730000300,"user":{"nickname":"提及者","avatar":"https://i0.hdslb.com/at.jpg"},"item":{"business":"评论","source_content":"@我 的内容","title":"引用"}}]}}""");
  }
  if(url.AbsolutePath.EndsWith("/reply")) return Json("""{"code":0,"data":{"cursor":{"is_end":true},"items":[]}}""");
  Equal("message.bilibili.com",url.Host);
  return Json("""{"code":0,"data":{"system_notify_list":[{"id":90,"title":"系统公告","content":"<b>简短通知</b>","time_at":"2024-10-27 12:00:00"}]}}""");
 }));
 client.RestoreCachedAccount(new(123,"离线合成用户",""));
 var items=await client.GetNotificationsAsync();Equal(2,likePages);Equal(1,mentionPages);Equal(5,items.Count);
 var like=items.Single(item=>item.Id=="like:71");Equal(37,like.ActorCount);Equal(2,like.Avatars.Count);True(like.HasAvatarTwo);True(!like.HasTargetImage);Equal("评论",like.TargetKind);True(like.Summary.Contains("37 人赞了我的评论"));
 Equal("https://i0.hdslb.com/video.jpg",items.Single(item=>item.Id=="like:72").TargetImage);
 var dm=items.Single(item=>item.Category=="私信");Equal("你好",dm.Content);Equal("私信用户",dm.Title);True(dm.Timestamp>0);
 True(items.All(item=>item.Category is "私信" or "回复与@" or "系统通知"));Equal(0,client.NotificationReadWarnings.Count);
}));
tests.Add(("Notification errors preserve other feeds and cancellation does not look like an empty inbox",async()=>
{
 using var client=new BilibiliClient(new Handler(request=>request.RequestUri!.AbsolutePath.EndsWith("/like")
  ?Json("""{"code":-101,"message":"expired"}"""):Json("""{"code":0,"data":{"items":[],"session_list":[],"system_notify_list":[]}}""")));
 client.RestoreCachedAccount(new(123,"合成用户",""));var items=await client.GetNotificationsAsync();Equal(0,items.Count);True(client.NotificationReadWarnings.Contains("点赞"));
 using var cancel=new CancellationTokenSource();cancel.Cancel();await Throws<OperationCanceledException>(()=>client.GetNotificationsAsync(cancel.Token));
}));
Test("Folder previews update only changed cover properties without replacing the card",()=>
{
 var folder=new Folder(71,"收藏夹",3,true,true,Cover:"https://i0.hdslb.com/a.jpg");var changes=new List<string?>();folder.PropertyChanged+=(_,args)=>changes.Add(args.PropertyName);
 var preview=folder with{Cover="https://i0.hdslb.com/b.jpg",CoverMiddle="https://i0.hdslb.com/c.jpg"};folder.ApplyPreview(preview);True(changes.Contains(nameof(Folder.FrontImage)));True(folder.FrontImage.Contains("b.jpg"));
 changes.Clear();folder.ApplyPreview(preview);Equal(0,changes.Count);True(folder.Detail.Contains("私密"));
});
Test("Missing folder collage layers render blank slots instead of repeating the front cover",()=>
{
 // A folder with fewer than three items only has a front cover; the middle/back artwork is absent.
 var folder=new Folder(71,"收藏夹",1,true,false,Cover:"https://i0.hdslb.com/a.jpg");
 True(folder.HasContents);True(folder.FrontImage.Contains("a.jpg"));
 // Missing layers must resolve to no image (a blank card slot), never the front cover again.
 Equal("",folder.MiddleImage);Equal("",folder.BackImage);
 True(folder.MiddleImage!=folder.FrontImage);True(folder.BackImage!=folder.FrontImage);
 // A preview that also lacks the middle/back layers keeps those slots blank rather than stacking the front cover.
 folder.ApplyPreview(folder with{Count=1});
 Equal("",folder.MiddleImage);Equal("",folder.BackImage);True(folder.FrontImage.Contains("a.jpg"));
 // Present layers still render their own covers, so the fix must not blank real artwork.
 var full=new Folder(72,"收藏夹",3,true,false,Cover:"https://i0.hdslb.com/a.jpg",CoverMiddle:"https://i0.hdslb.com/b.jpg",CoverBack:"https://i0.hdslb.com/c.jpg");
 True(full.MiddleImage.Contains("b.jpg"));True(full.BackImage.Contains("c.jpg"));True(full.MiddleImage!=full.FrontImage);
});
Test("Notification excerpts are bounded and thumbnail visibility uses explicit booleans",()=>
{
 var item=new AccountNotification("回复与@",new string('甲',300),new string('文',500),"",TargetText:new string('评',100));
 True(item.Summary.Length<=121);True(item.Excerpt.Length<=161);True(item.TargetExcerpt.Length<=49);True(item.HasTarget);True(!item.HasTargetImage);True(!item.HasAvatarTwo);
});

tests.Add(("Personal spaces validate identity, VIP level and paginate every video category", async () =>
{
 var pages = new List<string>();
 using var client = new BilibiliClient(new Handler(request => {
  var path = request.RequestUri!.AbsolutePath;
  if(path.EndsWith("/nav")) return Json(account);
  var query = Text.ParseQuery(request.RequestUri.Query); Equal("42",query["mid"]);
  if(path.EndsWith("/info")) return Json("""{"code":0,"data":{"mid":42,"name":"UP &amp; 名字","face":"//i0.hdslb.com/avatar.jpg","level":6,"vip":{"type":2,"status":1},"sign":"签名"}}""");
  Equal("0",query["tid"]); pages.Add(query["pn"]);
  return Json("""{"code":0,"data":{"page":{"count":31},"list":{"vlist":[{"aid":71,"bvid":"BV1xx411c7mD","mid":42,"title":"公开作品","author":"UP","pic":"//i0.hdslb.com/cover.jpg","length":"03:20"},{"bvid":"foreign","mid":88}]}}}""");
 }));
 await client.GetAccountAsync(); var user = await client.GetUserSpaceAsync(42);
 Equal("UP & 名字",user.Name); Equal(6,user.Level); Equal("年度大会员",user.VipText); Equal("UID 42",user.UidText);
 var first = await client.GetUserWorksAsync(42,1); var last = await client.GetUserWorksAsync(42,2);
 Equal(1,first.Items.Count); True(first.HasMore); True(!last.HasMore); Equal(200d,first.Items[0].Duration); True(pages.SequenceEqual(new[]{"1","2"}));
}));
tests.Add(("Personal space fallback reads card schema and rejects a different UID", async () =>
{
 var mismatch = false;
 using var client = new BilibiliClient(new Handler(request => {
  if(request.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(account);
  if(request.RequestUri.AbsolutePath.EndsWith("/info")) return Json("""{"code":-352}""");
  return Json("""{"code":0,"data":{"card":{"mid":"42","name":"UP","level_info":{"current_level":5},"vip":{"vipType":1,"vipStatus":0}}}}""".Replace("\"42\"", mismatch ? "\"43\"" : "\"42\""));
 }));
 await client.GetAccountAsync(); var user = await client.GetUserSpaceAsync(42); Equal(5,user.Level); Equal("非大会员",user.VipText);
 mismatch = true; await Throws<InvalidDataException>(()=>client.GetUserSpaceAsync(42));
}));
tests.Add(("Private conversations are read-only, ordered, paginated and limited to their participants", async () =>
{
 var calls = 0;
 using var client = new BilibiliClient(new Handler(request => {
  Equal(HttpMethod.Get,request.Method);
  if(request.RequestUri!.AbsolutePath.EndsWith("/nav")) return Json(account);
  True(request.RequestUri.AbsolutePath.EndsWith("/fetch_session_msgs"));
  var query = Text.ParseQuery(request.RequestUri.Query); Equal("42",query["talker_id"]); Equal("1",query["session_type"]); calls++;
  if(query.ContainsKey("begin_seqno")) { Equal("10",query["begin_seqno"]); return Json("""{"code":0,"data":{"has_more":0,"min_seqno":9,"messages":[{"msg_key":"older","msg_seqno":9,"sender_uid":42,"receiver_id":123,"msg_type":2,"content":"{\"url\":\"https://i0.hdslb.com/image.png\"}"}]}}"""); }
  return Json("""{"code":0,"data":{"has_more":1,"min_seqno":10,"messages":[{"msg_key":"own","msg_seqno":11,"sender_uid":123,"receiver_id":42,"msg_type":1,"timestamp":1730000000,"content":"{\"content\":\"<hello>\"}"},{"msg_key":"peer","msg_seqno":10,"sender_uid":42,"receiver_id":123,"msg_type":1,"content":"{\"content\":\"你好\"}"},{"msg_seqno":12,"sender_uid":88,"receiver_id":123,"msg_type":1,"content":"private foreign message"}]}}""");
 }));
 await client.GetAccountAsync(); var first = await client.GetConversationAsync(42); Equal(2,first.Items.Count); True(first.HasMore); Equal("你好",first.Items[0].Text); True(first.Items[1].IsOwn); Equal("<hello>",first.Items[1].Text);
 var older = await client.GetConversationAsync(42,first.BeforeSequence); True(!older.HasMore); Equal("https://i0.hdslb.com/image.png",older.Items.Single().Image); Equal(2,calls);
}));

var failures = 0;
foreach(var test in tests)
{
 try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
 catch(Exception e) { failures++; Console.WriteLine($"FAIL {test.Name}: {e.Message}"); }
}
Console.WriteLine($"{tests.Count-failures}/{tests.Count} passed");
return failures == 0 ? 0 : 1;

sealed class Handler : HttpMessageHandler
{
 private readonly Func<HttpRequestMessage,Task<HttpResponseMessage>> _send;
 public Handler(Func<HttpRequestMessage,HttpResponseMessage> send) => _send = r => Task.FromResult(send(r));
 public Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send) => _send = send;
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken) => _send(request);
}

sealed class SuspendedContext : SynchronizationContext
{
 private int _posts;
 public int Posts => Volatile.Read(ref _posts);
 public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref _posts);
}

sealed class CancellableHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
{
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => send(request,token);
}
