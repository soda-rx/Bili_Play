using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliPlay.Core;

/// <summary>Reads embedded JSON only; never executes JavaScript from a remote page.</summary>
public static class VideoPage
{
    public static JsonElement ReadJson(string html, string variable)
    {
        var match = Regex.Match(html, @"(?:window\s*\.\s*)?" + Regex.Escape(variable) + @"\s*=\s*(?=\{)",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) throw new InvalidDataException("视频网页没有提供可用的播放数据。");
        var slice = SliceJsonObject(html.AsSpan(match.Index + match.Length));
        var rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(slice.Length));
        try
        {
            var count = Encoding.UTF8.GetBytes(slice, rented);
            var reader = new Utf8JsonReader(rented.AsSpan(0, count));
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.Clone();
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    internal static ReadOnlySpan<char> SliceJsonObject(ReadOnlySpan<char> span)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return span[..(i + 1)];
        }
        return span;
    }
}
