using System.Text.Json;
using System.Text.Json.Serialization;
using BiliPlay.Core;

namespace BiliPlay.Services;

public sealed record CachedSubtitle(SubtitleTrack Track, IReadOnlyList<SubtitleLine> Lines);

[JsonSerializable(typeof(CachedSubtitle[]))]
internal partial class SubtitleJsonContext : JsonSerializerContext;

public static class SubtitleCache
{
    private static string FileName(Track track) => Path.ChangeExtension(track.LocalFile, ".subtitles.json");
    public static IReadOnlyList<CachedSubtitle> Read(Track track)
    {
        if (string.IsNullOrEmpty(track.LocalFile)) return [];
        try { return (JsonSerializer.Deserialize(File.ReadAllText(FileName(track)), SubtitleJsonContext.Default.CachedSubtitleArray) ?? []).Select(item => item with { Track = item.Track.NormalizeOrigin(), Lines = item.Lines.Select(line => line with { Content = Subtitles.CleanContent(line.Content) }).Where(line => line.Content.Length > 0).ToArray() }).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    public static async Task<IReadOnlyList<CachedSubtitle>> RefreshAsync(BilibiliClient client, Track track, CancellationToken ct,
        IReadOnlyList<SubtitleTrack>? tracks = null)
    {
        if (string.IsNullOrEmpty(track.LocalFile)) return [];
        tracks ??= await client.GetSubtitleTracksAsync(track, ct);
        var previous = Read(track);
        var result = new List<CachedSubtitle>();
        var failures = new List<Exception>();
        foreach (var subtitle in tracks)
        {
            ct.ThrowIfCancellationRequested();
            try { result.Add(new(subtitle, await client.GetSubtitlesAsync(subtitle, ct))); }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
            {
                failures.Add(error);
                if (previous.FirstOrDefault(x => x.Track.Language == subtitle.Language && x.Track.IsAuto == subtitle.IsAuto) is { } old)
                    result.Add(new(subtitle, old.Lines));
            }
        }
        // A failed refresh must not destroy the last usable offline lyrics.
        if (tracks.Count > 0 && result.Count == 0 && failures.Count > 0) throw failures[0];
        var file = FileName(track); var staging = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(result.ToArray(), SubtitleJsonContext.Default.CachedSubtitleArray), ct);
            ct.ThrowIfCancellationRequested(); File.Move(staging, file, true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return result;
    }
}
