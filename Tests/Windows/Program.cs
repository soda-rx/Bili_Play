using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using BiliPlay.Core;
using BiliPlay.Services;
using System.Text;
using Windows.Storage;

if (args is ["--media-session-probe"])
{
    var manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
    var session = manager.GetSessions().FirstOrDefault(item => item.SourceAppUserModelId.Contains("BiliPlay",StringComparison.OrdinalIgnoreCase) || item.SourceAppUserModelId.Contains("Bili_Play",StringComparison.OrdinalIgnoreCase));
    if (session is null) { Console.WriteLine("FAIL Bili Play system media session not registered"); return 1; }
    var state = session.GetPlaybackInfo();
    Console.WriteLine("PASS Bili Play system media session registered: " + state.PlaybackStatus + ", PlayEnabled=" + state.Controls.IsPlayEnabled);
    return 0;
}

if (args is ["--instance-probe", var probeScope])
{
    using var probe = new SingleInstanceService(() => { }, probeScope);
    return probe.IsPrimary ? 17 : 0;
}
void CheckInstanceGate()
{
    var scope = "Test" + Guid.NewGuid().ToString("N");
    using var signaled = new ManualResetEventSlim();
    using (var owner = new SingleInstanceService(signaled.Set, scope))
    {
        if (!owner.IsPrimary) throw new Exception("首次启动没有获得实例锁");
        for (var i = 0; i < 3; i++)
        {
            signaled.Reset();
            var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            info.ArgumentList.Add("--instance-probe"); info.ArgumentList.Add(scope);
            using var child = System.Diagnostics.Process.Start(info)!;
            if (!child.WaitForExit(10000) || child.ExitCode != 0) throw new Exception("重复启动没有被阻止");
            if (!signaled.Wait(5000)) throw new Exception("重复启动未发送主窗口激活信号");
        }
    }
    using var next = new SingleInstanceService(() => { }, scope);
    if (!next.IsPrimary) throw new Exception("正常退出后未释放实例锁");
}
CheckInstanceGate();
Console.WriteLine("PASS single instance across processes, activation signal and clean restart");
var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "windows-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Check(bool value, string message) { if (!value) throw new Exception(message); }
var first = new Track(1,"BV1xx411c7mD","第一首","UP","",10,123);
var second = first with { Bvid="BV2xx411c7mD", Title="第二首", LocalFile=Path.Combine(root,"sample.mp3") };
using(var store = new LocalStore(root))
{
    store.SaveProgress(123,first,2);
    Check(Math.Abs(store.GetProgress(123,first)-2)<1e-6,"播放进度没有写入");
    Check(store.GetProgress(456,first)==0,"播放进度串号");
    store.RecordRecentPlays = false;
    store.SaveProgress(123,second,3);
    Check(store.GetProgress(123,second)==0,"关闭记录后仍读取到播放进度");
    Check(store.History(123).SequenceEqual(new[]{first}),"关闭记录后仍记录了新歌曲");
    store.WriteTracks("queue:123",new[]{first,second});
    store.RememberSearch(123,"周杰伦"); store.RememberSearch(123,"陈奕迅"); store.RememberSearch(456,"周杰伦");
    store.DeleteSearch(123,"周杰伦");
    Check(store.SearchHistory(123).SequenceEqual(new[]{"陈奕迅"}),"单条历史删除失败");
    Check(store.SearchHistory(456).Count == 1,"影响了另一个账号");
    store.RecordSearchHistory=false;
    store.RememberSearch(123,"不应记录的合成搜索");
    Check(store.SearchHistory(123).Count==0,"关闭搜索历史后仍显示条目");
}
using(var reopened = new LocalStore(root))
{
    Check(!reopened.RecordRecentPlays,"关闭最近播放的设置未持久化");
    Check(!reopened.RecordSearchHistory,"搜索历史开关未持久化");
    reopened.RecordSearchHistory=true;
    Check(reopened.SearchHistory(123).SequenceEqual(new[]{"陈奕迅"}),"关闭搜索历史时仍记录搜索或破坏已有记录");
    reopened.SaveProgress(123,second,4);
    Check(reopened.History(123).Count == 1,"重启后关闭记录未生效");
    reopened.RecordRecentPlays = true;
    reopened.SaveProgress(123,second,4);
    Check(reopened.History(123).Count == 2,"重新开启最近播放后未记录");
    Check(Math.Abs(reopened.GetProgress(123,second)-4)<1e-6,"重启后播放进度丢失");
    Check(reopened.GetProgress(456,second)==0,"重启后播放进度串号");
    var queue = reopened.ReadTracks("queue:123");
    Check(queue.Count==2 && queue[0]==first && queue[1]==second,"重启后队列或顺序丢失");
    Check(reopened.ReadTracks("queue:456").Count==0,"队列串号");
}
Console.WriteLine("PASS recording preference across restart, queue persistence and individual search-history deletion");
var wav = Path.Combine(root,"source.wav");
using(var writer = new BinaryWriter(File.Create(wav)))
{
    const int sampleRate=44100, count=44100;
    writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36+count*2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate*2); writer.Write((short)2); writer.Write((short)16);
    writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count*2);
    for(var i=0;i<count;i++) writer.Write((short)(Math.Sin(2*Math.PI*440*i/sampleRate)*4000));
}
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await DownloadService.ConvertToMp3Async(wav,second.LocalFile,null,timeout.Token);
var bytes = File.ReadAllBytes(second.LocalFile);
Check(bytes.Length>1000,"MP3 转码输出为空");
Check(Encoding.ASCII.GetString(bytes,0,3)=="ID3" || bytes[0]==255 && (bytes[1]&224)==224,"并非真正的 MP3 文件");
var mp3 = await StorageFile.GetFileFromPathAsync(second.LocalFile);
var properties = await mp3.Properties.GetMusicPropertiesAsync();
Check(properties.Duration.TotalSeconds>=.8,"系统未能识别 MP3 时长");
Console.WriteLine("PASS Windows native MP3 conversion and decoder metadata validation");
Console.WriteLine(second.LocalFile);

await CheckBufferedAudioAsync();
var chosenTrack = first with { Bvid = "BV9xx411c7mD" };
using (var source = new NativeAudioHandler(File.ReadAllBytes(wav), 0))
using (var client = new BilibiliClient(new NativeAudioHandler([], 0)))
{
    client.RestoreCachedAccount(new(123, "离线音质验证", "", IsVip: true));
    var chosenStream = new AudioStream(new Uri("https://audio.bilivideo.com/chosen-hires.wav"), "audio/wav", [], "Hi-Res 无损");
    var downloaded = await new DownloadService(() => new AudioProxy(source)).DownloadAsync(client, 123, chosenTrack, null, timeout.Token,
        root, options: new(chosenTrack, chosenStream, 320000));
    var downloadedFile = await StorageFile.GetFileFromPathAsync(downloaded.LocalFile);
    var downloadedProperties = await downloadedFile.Properties.GetMusicPropertiesAsync();
    Check(source.LastPath == "/chosen-hires.wav", "下载忽略了用户选择的音源");
    Check(downloadedProperties.Bitrate >= 300000, "高品质下载没有使用 320 kbps MP3 编码");
    client.RestoreCachedAccount(new(123, "普通账号", "", IsVip: false));
    try
    {
        await new DownloadService(() => new AudioProxy(new NativeAudioHandler([], 0))).DownloadAsync(client, 123, chosenTrack with { Page = 2 }, null,
            timeout.Token, root, options: new(chosenTrack, chosenStream, 320000));
        throw new Exception("非大会员绕过了 Hi-Res 下载权限");
    }
    catch (InvalidOperationException error) when (error.Message.Contains("大会员")) { }
}
Console.WriteLine("PASS selected download stream, native 320 kbps output and VIP enforcement");

async Task CheckBufferedAudioAsync()
{
    byte[] wave;
    using (var memory = new MemoryStream())
    {
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
        {
            const int sampleRate = 16000, count = sampleRate * 20;
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
            for (var i = 0; i < count; i++) writer.Write((short)0);
        }
        wave = memory.ToArray();
    }
    var audio = new AudioStream(new Uri("https://audio.bilivideo.com/isolated.wav"), "audio/wav", []);
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var live = new AudioPlayback(new NativeAudioHandler(wave, 0));
    live.PlaybackRate = 1.5;
    await live.WarmMutedAsync(audio, limit.Token);
    Check(Math.Abs(live.Player.PlaybackSession.PlaybackRate-1.5)<.01,"媒体加载后未保留倍速");
    var before = live.Player.PlaybackSession.Position.TotalSeconds;
    using var replacement = new AudioPlayback(new NativeAudioHandler(wave, 900));
    var warming = replacement.WarmMutedAsync(audio, limit.Token);
    replacement.PlaybackRate = .75;
    await warming;
    Check(Math.Abs(replacement.Player.PlaybackSession.PlaybackRate-.75)<.01,"加载中选择的倍速未在准备完成后应用");
    Check(live.Player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing, "新音质加载时原播放器被暂停");
    Check(live.Player.PlaybackSession.Position.TotalSeconds > before + .5, "等待音质时原曲进度没有推进");
    await replacement.AlignToAsync(live, limit.Token);
    Check(Math.Abs((replacement.Player.PlaybackSession.Position - live.Player.PlaybackSession.Position).TotalSeconds) < .3, "新音质没有对齐最新播放进度");
    Check(replacement.ActivateInsteadOf(live), "播放状态在音质交接时丢失");
    Check(Math.Abs(replacement.Player.PlaybackSession.PlaybackRate-1.5)<.01,"音质切换丢失倍速");
    for (var wait = 0; wait < 50 && live.Player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing; wait++) await Task.Delay(20);
    Check(live.Player.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Playing && live.Player.Volume == 0, "音质交接后旧解码器仍在播放");
    using var pausedReplacement = new AudioPlayback(new NativeAudioHandler(wave, 400));
    var preparing = pausedReplacement.WarmMutedAsync(audio, limit.Token);
    replacement.PlaybackRate=1.75;
    replacement.Player.Pause(); replacement.Player.PlaybackSession.Position = TimeSpan.FromSeconds(6);
    await preparing; await pausedReplacement.AlignToAsync(replacement, limit.Token);
    Check(!pausedReplacement.ActivateInsteadOf(replacement), "准备期间暂停后却自动开始播放");
    Check(Math.Abs(pausedReplacement.Player.PlaybackSession.PlaybackRate-1.75)<.01,"准备期间更改的倍速没有同步到新音质");
    Check(Math.Abs(pausedReplacement.Player.PlaybackSession.Position.TotalSeconds - 6) < .3, "准备期间拖动进度后仍使用旧位置");
    replacement.Player.Play();
    // Play requests an asynchronous Windows media state transition. Establish the
    // live baseline before testing cancellation of a different decoder.
    for (var wait = 0; wait < 100 && replacement.Player.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Playing; wait++) await Task.Delay(20);
    Check(replacement.Player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing, "取消预加载测试前原音频尚未恢复播放");
    using var canceled = new AudioPlayback(new NativeAudioHandler(wave, 4000));
    using var cancellation = new CancellationTokenSource(150);
    try { await canceled.WarmMutedAsync(audio, cancellation.Token); throw new Exception("没有取消待切换音质"); }
    catch (OperationCanceledException) { }
    Check(replacement.Player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing, "取消音质预加载中断了原音频");
    using var failed = new AudioPlayback(new NativeAudioHandler([], 0));
    try { await failed.WarmMutedAsync(audio, limit.Token); throw new Exception("无效音源没有失败"); }
    catch (IOException) { }
    Check(replacement.Player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing, "新音质失败中断了原音频");
    Console.WriteLine("PASS native quality prebuffer keeps old audio playing, aligns latest seek, preserves pause and survives cancel/failure");
}

Check(DownloadService.IsCached(123,second), "自选文件夹中的已有 MP3 被错误拒绝");
using (var store = new LocalStore(root)) store.SetSetting("cacheDirectory", root);
using (var store = new LocalStore(root)) Check(store.GetSetting("cacheDirectory", "") == root, "缓存目录没有持久化");
using(var store=new LocalStore(root))
{
 using(var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {DataSource=Path.Combine(root,"library.db")}.ToString()))
 {
  db.Open();using var command=db.CreateCommand();
  command.CommandText="INSERT OR REPLACE INTO metadata VALUES (123,'BV1xx411c7mD:0','fallback','artist'),(123,'BV1xx411c7mD:42','exact','owner'),(124,'BV1xx411c7mD:99','other account','other')";command.ExecuteNonQuery();
 }
 var samples=new[]{new Track(1,"BV1xx411c7mD","original","UP","",120,42),new Track(1,"BV1xx411c7mD","original","UP","",120,99),new Track(2,"BV2xx411c7mD","untouched","UP","",120)};
 var resolved=store.ApplyMetadataBatch(123,samples);
 Check(resolved[0].Title=="exact"&&resolved[1].Title=="fallback"&&resolved[2]==samples[2],"首页批量元数据查找没有保留精确 CID、本地回退或账号隔离");
 Check(resolved.SequenceEqual(samples.Select(track=>store.ApplyMetadata(123,track))),"批量元数据与原来的单条行为不一致");
 Console.WriteLine("PASS batch home metadata preserves edits, CID precedence and account isolation");
}
var language = new SubtitleTrack("zh-CN","中文",new Uri("https://i0.hdslb.com/test.json"),false);
var revision = "初版字幕"; var failSubtitle = false;
using(var client = new BilibiliClient(new SubtitleHandler(() =>
{
    if (failSubtitle) throw new HttpRequestException("offline");
    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
    { Content = new System.Net.Http.StringContent("{\"body\":[{\"from\":0,\"to\":1,\"content\":\"" + revision + "\"}]}") };
})))
{
    await SubtitleCache.RefreshAsync(client,second,CancellationToken.None,[language]);
    Check(SubtitleCache.Read(second).Single().Lines.Single().Content == "初版字幕", "离线字幕未保存");
    revision = "新版字幕";
    await SubtitleCache.RefreshAsync(client,second,CancellationToken.None,[language]);
    Check(SubtitleCache.Read(second).Single().Lines.Single().Content == "新版字幕", "联网新版字幕未覆盖缓存");
    failSubtitle = true;
    await SubtitleCache.RefreshAsync(client,second,CancellationToken.None,[language]);
    Check(SubtitleCache.Read(second).Single().Lines.Single().Content == "新版字幕", "联网失败破坏了可用离线字幕");
}
Console.WriteLine("PASS custom cache directory persistence and offline lyrics refresh without data loss");

Check(Path.GetFileName(DownloadService.CacheDirectory(123)) == AppProfile.DownloadDirectoryName, "默认下载目录与应用环境不符");
#if DEVELOPMENT_BUILD
Check(AppProfile.DataDirectoryName == "BiliPlay.Dev" && AppProfile.SessionResource == "BiliPlay.BilibiliSession.Development"
    && AppProfile.DownloadDirectoryName == "Bili_Play_Dev" && AppProfile.InstanceScope == "Development", "开发环境未与日常版完全分离");
using (var dailyGate = new SingleInstanceService(() => { }, "Main"))
using (var developmentGate = new SingleInstanceService(() => { }))
    Check(developmentGate.IsPrimary, "开发版实例锁与日常版发生冲突");
#else
Check(AppProfile.DataDirectoryName == "BiliPlay" && AppProfile.SessionResource == "BiliPlay.BilibiliSession"
    && AppProfile.DownloadDirectoryName == "Bili_Play" && AppProfile.InstanceScope == "Main", "日常版环境不兼容原有数据");
#endif
Check(AppProfile.DisplayName == "Bili Play", "开发与日常版本都应显示正常应用名称");
Console.WriteLine("PASS application profile isolation and shared Bili Play display name");
var cancelRoot = Path.Combine(root,"cancel-test"); Directory.CreateDirectory(cancelRoot);
using(var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
using(var client = new BilibiliClient(new SubtitleHandler(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
{ Content = new StringContent("""{"code":0,"data":{"isLogin":true,"mid":123,"wbi_img":{"img_url":"https://i0.hdslb.com/7cd084941338484aae1ad9425b84077c.png","sub_url":"https://i0.hdslb.com/4932caff0ff746eab6f01bf08b70ac45.png"},"dash":{"audio":[{"baseUrl":"https://upos.bilivideo.com/test.m4a","codecs":"mp4a.40.2"}]}}}""") })))
{
    await client.GetAccountAsync();
    var receivedAudio = false;
    var progress = new ImmediateProgress(value => { if(value > 0) { receivedAudio = true; cancellation.Cancel(); } });
    var service = new DownloadService(() => new AudioProxy(new StreamingHandler()));
    try { await service.DownloadAsync(client,123,first,progress,cancellation.Token,cancelRoot); throw new Exception("下载没有被取消"); }
    catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
    Check(receivedAudio, "取消测试没有实际收到音频字节");
    Check(!Directory.EnumerateFiles(cancelRoot).Any(), "取消下载后残留了未完成文件：" + string.Join(",",Directory.EnumerateFiles(cancelRoot).Select(Path.GetFileName)));
}
await DownloadService.DeleteCachedFilesAsync(second);
Check(!File.Exists(second.LocalFile) && !File.Exists(Path.ChangeExtension(second.LocalFile,".subtitles.json")), "删除音乐未同时删除 MP3 与字幕");
Console.WriteLine("PASS default cache directory, cancellation during streaming cleans partial files, and cached deletion");

var pictureRoot = Path.Combine(root, "comment-pictures"); Directory.CreateDirectory(pictureRoot);
var pixels = Enumerable.Repeat(new byte[] { 20, 100, 200, 255 }, 6).SelectMany(pixel => pixel).ToArray();
foreach (var jpeg in new[] { false, true })
{
    var path = Path.Combine(pictureRoot, jpeg ? "private-name-fixture.jpg" : "private-name-fixture.png");
    File.WriteAllBytes(path, []);
    var file = await StorageFile.GetFileFromPathAsync(path);
    using (var output = await file.OpenAsync(FileAccessMode.ReadWrite))
    {
        var encoder = await BitmapEncoder.CreateAsync(jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 2, 3, 96, 96, pixels);
        if (jpeg) await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
        {
            ["/app1/ifd/{ushort=315}"] = new("synthetic-private-author", Windows.Foundation.PropertyType.String),
            ["/app1/ifd/{ushort=274}"] = new((ushort)6, Windows.Foundation.PropertyType.UInt16)
        });
        await encoder.FlushAsync();
    }
    var prepared = await CommentImagePreparation.PrepareAsync(file, CancellationToken.None); prepared.Validate();
    Check(prepared.MediaType == (jpeg ? "image/jpeg" : "image/png"), "图片处理使用了错误的文件格式");
    Check(prepared.Width == (jpeg ? 3 : 2) && prepared.Height == (jpeg ? 2 : 3), "图片处理没有保留方向或尺寸");
    Check(!Encoding.UTF8.GetString(prepared.Bytes).Contains("synthetic-private-author"), "图片保留了原始作者元数据");
    using var random = new MemoryStream(prepared.Bytes).AsRandomAccessStream();
    var decoder = await BitmapDecoder.CreateAsync(random);
    Check(decoder.PixelWidth == prepared.Width && decoder.PixelHeight == prepared.Height, "处理后的图片无法正确解码");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { await CommentImagePreparation.PrepareAsync(file, canceled.Token); throw new Exception("取消后仍处理图片"); }
    catch (OperationCanceledException) { }
}
Console.WriteLine("PASS comment images re-encode PNG/JPEG, strip metadata, preserve EXIF orientation and cancel safely");


return 0;

sealed class SubtitleHandler(Func<HttpResponseMessage> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send());
}

sealed class ImmediateProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
sealed class StreamingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var content = new StreamContent(new SlowAudioStream()); content.Headers.ContentLength = 1024 * 1024;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
    }
}
sealed class SlowAudioStream : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
    { await Task.Delay(25,ct); var count=Math.Min(4096,buffer.Length); buffer.Span[..count].Clear(); return count; }
    public override int Read(byte[] buffer,int offset,int count) => throw new NotSupportedException();
    public override void Flush() {} public override long Seek(long offset,SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer,int offset,int count) => throw new NotSupportedException();
}

sealed class NativeAudioHandler(byte[] bytes, int delay) : HttpMessageHandler
{
    private int _requests;
    public string? LastPath { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastPath = request.RequestUri?.AbsolutePath;
        if (Interlocked.Increment(ref _requests) == 1 && delay > 0) await Task.Delay(delay, cancellationToken);
        if (bytes.Length == 0) return new(System.Net.HttpStatusCode.BadGateway);
        var from = (int)(request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0);
        var to = (int)Math.Min(request.Headers.Range?.Ranges.FirstOrDefault()?.To ?? bytes.Length - 1, bytes.Length - 1);
        if (from >= bytes.Length) return new(System.Net.HttpStatusCode.RequestedRangeNotSatisfiable);
        var response = new HttpResponseMessage(request.Headers.Range is null ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent(bytes, from, to - from + 1) };
        response.Content.Headers.ContentType = new("audio/wav");
        response.Headers.AcceptRanges.Add("bytes");
        if (request.Headers.Range is not null) response.Content.Headers.ContentRange = new(from, to, bytes.Length);
        return response;
    }
}
