using BiliPlay.Core;
using BiliPlay.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BiliPlay;

public sealed partial class MainPage
{
    private async Task VerifyMediaArtworkAsync(string directory)
    {
        var song = new Track(71,"BV1xx411c7mD","媒体封面验证","离线用户","https://i0.hdslb.com/cover.jpg@480w.webp",30);
        var local = new Uri(Path.Combine(directory,"native-jpeg-cover.img"));
        var support = ArtworkFormats.WebpSupported; var compatible = ArtworkFormats.CompatibilityMode;
        try
        {
            ArtworkFormats.Configure(true,false);
            var thumbnail = await PlaybackEngine.ResolveThumbnailAsync(song, uri =>
            {
                Check(uri.AbsoluteUri.EndsWith("@256w.jpg",StringComparison.Ordinal),"媒体封面仍请求 WebP");
                return Task.FromResult(local);
            });
            Check(thumbnail is not null,"没有为系统媒体控件创建本地文件缩略图");
            using var stream = await thumbnail!.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            Check(decoder.PixelWidth == 8 && decoder.PixelHeight == 8,"媒体浮窗缩略图无法从缓存文件解码");
            Check(await PlaybackEngine.ResolveThumbnailAsync(song,uri=>Task.FromResult(uri)) is null,"磁盘解析失败后仍交给系统远程地址");
            var untrustedResolved = false;
            foreach (var address in new[]{"https://127.0.0.1/private", "https://i0.hdslb.com.evil.test/private", "file://server/share/cover.jpg"})
            {
                var refused = await PlaybackEngine.ResolveThumbnailAsync(song with{Cover=address}, uri=>
                { untrustedResolved=true; return Task.FromResult(local); });
                Check(refused is null && !untrustedResolved,"媒体封面接受了未授权主机或 UNC 路径");
            }
            using var session = new MediaArtworkSession();
            var published = new List<string>();
            var old = new TaskCompletionSource<RandomAccessStreamReference?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = session.UpdateAsync(song,_=>old.Task,_=>published.Add("old"));
            await session.UpdateAsync(song with { Title="新曲目" },_=>Task.FromResult<RandomAccessStreamReference?>(thumbnail),_=>published.Add("new"));
            old.TrySetResult(thumbnail); await pending;
            Check(published.SequenceEqual(new[]{"new"}),"旧媒体封面延迟覆盖了新歌曲");
            var stopped = new TaskCompletionSource<RandomAccessStreamReference?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = session.UpdateAsync(song,_=>stopped.Task,_=>published.Add("stopped"));
            session.Reset(); stopped.TrySetResult(thumbnail); await pending;
            Check(published.Count == 1,"停止播放后旧封面重新出现在媒体控件中");
            await session.UpdateAsync(song,_=>Task.FromException<RandomAccessStreamReference?>(new IOException("fixture")),_=>published.Add("failed"));
            await session.UpdateAsync(song,_=>Task.FromResult<RandomAccessStreamReference?>(thumbnail),_=>throw new InvalidOperationException("shell fixture"));
            session.Dispose();
            await session.UpdateAsync(song,_=>Task.FromResult<RandomAccessStreamReference?>(thumbnail),_=>published.Add("disposed"));
            Check(published.Count == 1,"失败或销毁后仍然发布媒体封面");
        }
        finally { ArtworkFormats.Configure(support,compatible); }
    }
}
