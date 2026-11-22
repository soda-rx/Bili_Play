using BiliPlay.Core;
using System.Net;
using System.Text.Json;

namespace BiliPlay.Services;

public static class DebugSession
{
    private static TemporarySession? _session;
    public static bool IsActive => _session is not null;
    public static string? DirectoryPath => _session?.DirectoryPath;
    public static string ParentDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppProfile.DataDirectoryName,"debug-sessions");
    public static void Begin(string? parent=null)
    {if(IsActive)throw new InvalidOperationException("已处于调试模式。");_session=new(parent??ParentDirectory);}
    public static void CleanAbandoned()
    {try{TemporarySession.CleanAbandoned(ParentDirectory);}catch(IOException){}catch(UnauthorizedAccessException){}}
    public static void End()
    {try{_session?.Dispose();}finally{_session=null;}}
    public static IReadOnlyList<Track> CreateTracks()
    {
        if(DirectoryPath is not { } root)throw new InvalidOperationException();
        var file=Path.Combine(root,"demo.wav");
        using(var stream=new BinaryWriter(File.Create(file)))
        {
            const int rate=16000,seconds=24,size=rate*seconds*2;
            stream.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));stream.Write(36+size);stream.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            stream.Write(16);stream.Write((short)1);stream.Write((short)1);stream.Write(rate);stream.Write(rate*2);stream.Write((short)2);stream.Write((short)16);stream.Write(System.Text.Encoding.ASCII.GetBytes("data"));stream.Write(size);
            for(var n=0;n<rate*seconds;n++)stream.Write((short)(Math.Sin(n*2*Math.PI*220/rate)*500));
        }
        var cover=new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","Square150x150Logo.scale-200.png")).AbsoluteUri;
        var lines=Enumerable.Range(0,6).Select(i=>new SubtitleLine(i*4,(i+1)*4,new[]{"这是离线调试的示例歌词","所有数据只在本次调试中使用","试着打开播放列表与收藏夹","也可以查看歌词与页面动画","不会连接真实的哔哩哔哩账号","退出后这些示例数据将被清理"}[i])).ToArray();
        File.WriteAllText(Path.ChangeExtension(file,".subtitles.json"),JsonSerializer.Serialize(new[]{new CachedSubtitle(new("zh","示例字幕",new Uri("https://i0.hdslb.com/demo.json"),false),lines)},SubtitleJsonContext.Default.CachedSubtitleArray));
        return Enumerable.Range(1,8).Select(i=>new Track(i,"BV"+i.ToString("D10"),$"离线示例曲目 {i}","Bili Play 调试示例",cover,24,i,LocalFile:file)).ToArray();
    }
    public sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var json=request.Method==HttpMethod.Post
                ? "{\"code\":-400,\"message\":\"离线调试不执行真实账号操作。\"}"
                : "{\"code\":0,\"data\":{\"isLogin\":true,\"mid\":1,\"uname\":\"离线调试\",\"list\":[],\"replies\":[],\"result\":[],\"page\":{\"count\":0}}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json")});
        }
    }
}
