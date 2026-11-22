using System.Text;
using BiliPlay.Core;
using BiliPlay.ViewModels;

namespace BiliPlay;

// Startup/resume regression checks: the saved position must be painted immediately, the decoder
// must be prepared at that position, and pressing play must continue there instead of flashing
// back to 0:00 or restarting from the first lyric line.
public sealed partial class MainPage
{
    private bool _resumePreloadVerified;
    private bool _resumePlaybackVerified;
    private bool _lyricPreviewPlaybackVerified;

    private async Task VerifyResumeStartupAsync(string directory)
    {
        var wav = Path.Combine(directory, "resume-demo.wav");
        WriteResumeProbeWav(wav, 24);
        var track = new Track(91, "BV9xx411c7mD", "续听验证曲目", "验证 UP", "", 24, LocalFile: wav);
        using var model = new PlayerViewModel(new BilibiliClient(new PagedTrackFixtureHandler()));
        model.Client.RestoreCachedAccount(new(124, "续听验证", ""));
        model.IsLoggedIn = true;
        model.Store.RecordRecentPlays = true;
        model.Store.SaveProgress(124, track, 12);
        model.Store.WriteTracks("queue:124", new[] { track });
        model.Store.SetSetting("queueCurrent:124", $"{track.Bvid}:{track.Page}");
        await model.EnterAccountAsync(false);
        Check(QueueSelection.Same(model.CurrentTrack, track), "启动未恢复上次播放曲目");
        Check(Math.Abs(model.Position - 12) < 0.01, "启动未把上次进度画到进度条");
        Check(model.PositionText == "0:12", "启动进度文本没有显示上次位置");
        Check(model.ResumeExpectedForTests > 11.9, "启动续听没有进入 seek 保护状态");

        Check(model.Player.Current is null, "暂停启动不应提前打开音频或下载音源");
        for (var tick = 0; tick < 8; tick++) model.TickForTests();
        Check(Math.Abs(model.Position - 12) < .01, "未播放时进度被计时器清零");
        model.Seek(7); model.TickForTests();
        Check(Math.Abs(model.Position - 7) < .01, "暂停时拖动进度被重置");
        model.Seek(12); model.SaveProgress();
        Check(Math.Abs(model.Store.GetProgress(124, track) - 12) < .01, "暂停启动的逻辑位置无法保存");
        model.Volume = 0;
        var preparing = model.PreloadCurrentForTestsAsync(track, 12);
        model.Seek(5);
        await preparing;
        Check(Math.Abs(model.Player.Position - 5) < .75 && Math.Abs(model.Position - 5) < .75,
            "解码器加载期间的新定位被启动旧位置覆盖");
        model.Seek(0);
        Check(Math.Abs(model.Player.Position) < .75, "加载后无法定位到零秒");
        model.Seek(12);
        Check(QueueSelection.Same(model.Player.Current, track), "续听预加载没有准备音频");
        if (model.Player.Duration <= 0)
        {
            // The decoder may be unavailable in some headless environments; the UI-level resume
            // checks above still run, and normal playback falls back to its own streaming path.
            throw new InvalidOperationException("续听测试必须实际打开合成音频解码器");
        }
        Check(model.Player.Duration > 23, "续听预加载没有读到音频时长");
        Check(Math.Abs(model.Player.Position - 12) < 1.2, "续听预加载没有定位到上次位置");
        Check(Math.Abs(model.Position - 12) < 1.2, "续听预加载后进度条离开了上次位置");
        _resumePreloadVerified = true;
        await model.TogglePlayCommand.ExecuteAsync(null);
        await Task.Delay(700);
        Check(model.Player.Playing, "续听预加载后点击播放没有开始播放");
        Check(model.Player.Position > 10.5, "续听后点击播放又从头开始");
        _resumePlaybackVerified = true;
        model.Player.Pause();
        model.Store.SaveProgress(124, track, model.Player.Position);
    }

    private async Task VerifyLyricPreviewPlaybackAsync(string directory)
    {
        var wav = Path.Combine(directory, "preview-demo.wav");
        WriteResumeProbeWav(wav, 24);
        var track = new Track(92, "BV8xx411c7mD", "歌词预览验证曲目", "验证 UP", "", 24, LocalFile: wav);
        using var model = new PlayerViewModel(new BilibiliClient(new PagedTrackFixtureHandler()));
        model.Client.RestoreCachedAccount(new(125, "预览验证", ""));
        model.IsLoggedIn = true;
        model.Store.RecordRecentPlays = true;
        model.Store.SaveProgress(125, track, 3);
        model.Store.WriteTracks("queue:125", new[] { track });
        model.Store.SetSetting("queueCurrent:125", $"{track.Bvid}:{track.Page}");
        await model.EnterAccountAsync(false);
        Check(Math.Abs(model.Position - 3) < 0.01, "歌词预览验证启动进度没有恢复");

        // Clicking a later lyric line only previews it: audio is still not loaded, so the play
        // command must remember 0:15 and start the decoder there instead of at 0:00.
        model.Volume = 0;
        model.Seek(15);
        Check(Math.Abs(model.Position - 15) < 0.01, "点击靠后歌词后的预览位置不正确");
        await model.TogglePlayCommand.ExecuteAsync(null);
        await Task.Delay(1500);
        Check(model.Player.Playing, "点击靠后歌词后播放没有开始");
        Check(model.Player.Position > 13.5, "点击靠后歌词后音频起点不是所选位置");
        Check(model.Position > 13.5, "点击靠后歌词后进度条先跳回了开头");
        _lyricPreviewPlaybackVerified = true;
        model.Player.Pause();
        model.Store.SaveProgress(125, track, model.Player.Position);
    }

    private static void WriteResumeProbeWav(string path, int seconds)
    {
        const int rate = 8000;
        using var writer = new BinaryWriter(File.Create(path));
        var size = rate * seconds * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + size);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(size);
        for (var n = 0; n < rate * seconds; n++)
            writer.Write((short)(Math.Sin(n * 2 * Math.PI * 220 / rate) * 500));
    }
}
