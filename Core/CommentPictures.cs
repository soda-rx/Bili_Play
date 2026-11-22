using System.Buffers.Binary;
using System.Globalization;

namespace BiliPlay.Core;

internal sealed record CommentPictureRequest(string img_src, int img_width, int img_height, double img_size);

public sealed record CommentPicture(string Url, int Width, int Height, double SizeKb = 0)
{
    public static bool TryUrl(string value, out Uri? uri)
    {
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value[7..];
        return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.IsDefaultPort
            && uri.UserInfo.Length == 0 && (uri.Host.Equals("hdslb.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".hdslb.com", StringComparison.OrdinalIgnoreCase))
            && uri.AbsolutePath.StartsWith("/bfs/", StringComparison.Ordinal);
    }
    public bool IsValid => TryUrl(Url, out _) && Width is > 0 and <= 100000 && Height is > 0 and <= 100000
        && double.IsFinite(SizeKb) && SizeKb is >= 0 and <= 20480;
    public string OriginalUrl => TryUrl(Url, out var uri)
        ? new UriBuilder(uri!) { Path = uri!.AbsolutePath.Split('@')[0] }.Uri.AbsoluteUri : "";
    internal CommentPictureRequest ToRequest() => new(Url, Width, Height, SizeKb);
    internal static double ReadSize(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)
        && double.IsFinite(size) && size >= 0 ? Math.Min(size, 20480) : 0;
}

public sealed record CommentImagePayload(byte[] Bytes, int Width, int Height, string MediaType = "image/png")
{
    public const int MaxPictures = 9;
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxDimension = 8192;
    public const long MaxPixels = 32_000_000;
    public void Validate()
    {
        var dimensionsMatch = MediaType == "image/png" ? Bytes.Length >= 33
            && Bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && Bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) && BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(8, 4)) == 13
            && BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(16, 4)) == Width && BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(20, 4)) == Height
            : MediaType == "image/jpeg" && JpegDimensionsMatch();
        if (Bytes.Length is < 8 or > MaxBytes || !dimensionsMatch || Width is < 1 or > MaxDimension
            || Height is < 1 or > MaxDimension || (long)Width * Height > MaxPixels)
            throw new InvalidDataException("图片无效或过大，请选择较小的图片。处理后的单张图片不能超过 5 MB。");
    }
    private bool JpegDimensionsMatch()
    {
        if (Bytes.Length < 4 || Bytes[0] != 255 || Bytes[1] != 216) return false;
        var offset = 2;
        while (offset + 3 < Bytes.Length)
        {
            if (Bytes[offset++] != 255) return false;
            while (offset < Bytes.Length && Bytes[offset] == 255) offset++;
            if (offset >= Bytes.Length) return false;
            var marker = Bytes[offset++];
            if (marker is 0xD9 or 0xDA || offset + 2 > Bytes.Length) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan(offset, 2));
            if (length < 2 || length > Bytes.Length - offset) return false;
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                return length >= 8 && BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan(offset + 3, 2)) == Height
                    && BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan(offset + 5, 2)) == Width;
            offset += length;
        }
        return false;
    }
}
