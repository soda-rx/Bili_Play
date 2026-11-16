using System.Net;
using System.Text.Json;

namespace BiliPlay.Core;

public enum CommentSort { Hot, Latest }
public enum CommentReaction { None, Like, Dislike }
public sealed record CommentEmote(string Text, string Url) { public bool HasImage => Url.Length > 0; }
public sealed record CommentEmotePackage(string Name, IReadOnlyList<CommentEmote> Items);
public sealed record BiliComment(long Id, long AuthorId, string Author, string Avatar, string Content, long Likes, long CreatedAt, bool IsPinned = false, CommentReaction Reaction = CommentReaction.None, long ReplyCount = 0, IReadOnlyList<CommentEmote>? Emotes = null, IReadOnlyList<CommentPicture>? Pictures = null)
{
    public string Meta => (CreatedAt > 0 && CreatedAt <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(CreatedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "")
        + (IsPinned ? " · 置顶" : "");
}
public sealed record CommentPage(IReadOnlyList<BiliComment> Items, long Total, bool HasMore);

public sealed partial class BilibiliClient
{
    private long _commentVerifiedAccount;
    private DateTimeOffset _commentAccountVerifiedAt;
    private async Task EnsureCommentSessionAsync(CancellationToken token)
    {
        var expected = Account?.Id ?? 0;
        if (_commentVerifiedAccount == expected && expected > 0 && DateTimeOffset.UtcNow - _commentAccountVerifiedAt < TimeSpan.FromMinutes(15)) return;
        var verified = await GetAccountAsync(token);
        if (verified.Id != expected) throw new BilibiliException(-101,"账号已变更，请重新登录后打开评论。");
        _commentVerifiedAccount = verified.Id; _commentAccountVerifiedAt = DateTimeOffset.UtcNow;
    }
    private static BiliComment? ParseComment(JsonElement item, bool pinned = false)
    {
        if (item.N("rpid") <= 0) return null;
        var member = item.O("member");
        return new(item.N("rpid"), member.N("mid"), Text.Clean(member.S("uname")), ImageUrl(member.S("avatar")),
            WebUtility.HtmlDecode(item.O("content").S("message")), Math.Max(0,item.N("like")), item.N("ctime"), pinned,
            item.N("action") switch { 1 => CommentReaction.Like, 2 => CommentReaction.Dislike, _ => CommentReaction.None },
            Math.Max(0,(item.O("rcount").ValueKind == JsonValueKind.Undefined ? item.N("count") : item.N("rcount"))),
            item.O("content").O("emote").ValueKind == JsonValueKind.Object
                ? item.O("content").O("emote").EnumerateObject().Select(pair=>new CommentEmote(pair.Name,ImageUrl(pair.Value.S("url")))).Where(emote=>emote.Url.Length>0).ToArray() : null,
            item.O("content").O("pictures").A().Select(picture => new CommentPicture(
                CommentPicture.TryUrl(picture.S("img_src"), out var uri) ? uri!.AbsoluteUri : "",
                (int)Math.Clamp(picture.N("img_width"), 1, 100000), (int)Math.Clamp(picture.N("img_height"), 1, 100000),
                CommentPicture.ReadSize(picture.S("img_size")))).Where(picture => picture.IsValid).DistinctBy(picture => picture.Url).Take(CommentImagePayload.MaxPictures).ToArray());
    }
    public async Task<CommentPage> GetCommentsAsync(long aid, CommentSort sort, int page = 1, CancellationToken ct = default)
    {
        RequireAccount();
        if (aid <= 0) throw new ArgumentOutOfRangeException(nameof(aid));
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        await EnsureCommentSessionAsync(ct);
        const int size = 20;
        var data = await GetAsync("x/v2/reply", new()
        {
            ["type"] = "1", ["oid"] = aid.ToString(), ["pn"] = page.ToString(), ["ps"] = size.ToString(),
            ["sort"] = sort == CommentSort.Latest ? "0" : "2"
        }, false, ct);
        var rows = data.O("replies").A().ToArray();
        var comments = new List<BiliComment>();
        if (page == 1)
        {
            foreach (var top in data.O("top_replies").A()) if (ParseComment(top,true) is { } comment) comments.Add(comment);
            foreach (var key in new[] { "upper", "admin", "vote" }) if (ParseComment(data.O("top").O(key),true) is { } comment) comments.Add(comment);
        }
        foreach (var row in rows) if (ParseComment(row) is { } comment) comments.Add(comment);
        var total = Math.Max(0,data.O("page").N("count"));
        return new(comments.DistinctBy(comment=>comment.Id).ToArray(), total,
            rows.Length > 0 && (total > 0 ? (long)page * size < total : rows.Length >= size));
    }
    public async Task<CommentPage> GetCommentRepliesAsync(long aid, long root, int page = 1, CancellationToken ct = default)
    {
        RequireAccount();
        if (aid <= 0) throw new ArgumentOutOfRangeException(nameof(aid));
        if (root <= 0) throw new ArgumentOutOfRangeException(nameof(root));
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        await EnsureCommentSessionAsync(ct);
        const int size = 20;
        var data = await GetAsync("x/v2/reply/reply",new()
        { ["type"]="1",["oid"]=aid.ToString(),["root"]=root.ToString(),["pn"]=page.ToString(),["ps"]=size.ToString() },false,ct);
        var rows = data.O("replies").A().Select(item=>ParseComment(item)).OfType<BiliComment>().DistinctBy(item=>item.Id).ToArray();
        var total = Math.Max(0,data.O("page").N("count"));
        return new(rows,total,rows.Length>0 && (total>0 ? (long)page*size<total : rows.Length>=size));
    }
    public async Task SetCommentReactionAsync(long aid, long reply, CommentReaction reaction, bool enabled, CancellationToken ct = default)
    {
        RequireAccount();
        if (aid <= 0) throw new ArgumentOutOfRangeException(nameof(aid));
        if (reply <= 0) throw new ArgumentOutOfRangeException(nameof(reply));
        if (reaction is not (CommentReaction.Like or CommentReaction.Dislike)) throw new ArgumentOutOfRangeException(nameof(reaction));
        await EnsureCommentSessionAsync(ct);
        _ = await PostAsync(reaction == CommentReaction.Like ? "x/v2/reply/action" : "x/v2/reply/hate",new()
        { ["type"]="1",["oid"]=aid.ToString(),["rpid"]=reply.ToString(),["action"]=enabled?"1":"0" },ct);
    }
    public async Task<IReadOnlyList<CommentEmotePackage>> GetCommentEmotesAsync(CancellationToken ct = default)
    {
        RequireAccount(); await EnsureCommentSessionAsync(ct);
        var data = await GetAsync("x/emote/user/panel/web",new() { ["business"]="reply" },false,ct);
        var packages = data.O("packages").A().ToArray();
        if (packages.Length == 0)
        {
            data = await GetAsync("x/emote/package",new() { ["ids"]="1",["business"]="reply" },false,ct);
            packages = data.O("packages").A().ToArray();
        }
        return packages.Select(package=>new CommentEmotePackage(Text.Clean(package.S("text")),package.O("emote").A()
            .Where(item=>item.O("flags").O("no_access").ValueKind != JsonValueKind.True && item.O("flags").O("unlocked").ValueKind != JsonValueKind.False)
            .Select(item=>new CommentEmote(item.S("text"),ImageUrl(item.S("url"))))
            .Where(item=>item.Text.Length>0&&item.Url.Length>0).DistinctBy(item=>item.Text).ToArray()))
            .Where(package=>package.Items.Count>0).ToArray();
    }
    public async Task<BiliComment?> PostCommentAsync(long aid, string message, CancellationToken ct = default, long root = 0, long parent = 0, IReadOnlyList<CommentPicture>? pictures = null)
    {
        RequireAccount();
        if (aid <= 0) throw new ArgumentOutOfRangeException(nameof(aid));
        if (root < 0 || parent < 0 || (root == 0) != (parent == 0)) throw new ArgumentException("回复的根评论和目标评论无效。 ");
        message = message.Trim();
        if (message.Length is 0 or > 1000) throw new ArgumentException("评论应为 1 到 1000 个字符。",nameof(message));
        if (pictures is { Count: > CommentImagePayload.MaxPictures } || pictures?.Any(picture => !picture.IsValid) == true)
            throw new ArgumentException("评论图片无效，单条评论最多 9 张图片。", nameof(pictures));
        await EnsureCommentSessionAsync(ct);
        // POST is sent once. Do not retry: a timeout can happen after the server has accepted the comment.
        var form = new Dictionary<string,string> { ["type"]="1", ["oid"]=aid.ToString(), ["message"]=message, ["plat"]="1" };
        if (root > 0) { form["root"] = root.ToString(); form["parent"] = parent.ToString(); }
        if (pictures is { Count: > 0 })
        {
            form["pictures"] = JsonSerializer.Serialize(pictures.Select(picture => picture.ToRequest()).ToArray(), InternalCoreJsonContext.Default.CommentPictureRequestArray);
            form["at_name_to_mid"] = "{}"; form["gaia_source"] = "main_web";
        }
        var data = await PostAsync("x/v2/reply/add",form,ct);
        var posted = ParseComment(data.O("reply"));
        return posted is not null && pictures is { Count: > 0 } && posted.Pictures?.Count is not > 0 ? posted with { Pictures = pictures.ToArray() } : posted;
    }

    public async Task<CommentPicture> UploadCommentImageAsync(CommentImagePayload image, CancellationToken ct = default)
    {
        RequireAccount(); image.Validate();
        await EnsureCommentSessionAsync(ct);
        var csrf = _cookies.GetCookies(Api)["bili_jct"]?.Value;
        if (string.IsNullOrEmpty(csrf)) throw new BilibiliException(-111, "登录凭据不完整，请重新扫码登录。");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(csrf), "csrf"); form.Add(new StringContent("daily"), "category"); form.Add(new StringContent("new_dyn"), "biz");
        using var bytes = new ByteArrayContent(image.Bytes);
        bytes.Headers.ContentType = new(image.MediaType);
        // Never send a local filename, path or the original image's metadata.
        form.Add(bytes, "file_up", image.MediaType == "image/jpeg" ? "comment.jpg" : "comment.png");
        using var response = await _http.PostAsync(new Uri(Api, "x/dynamic/feed/draw/upload_bfs"), form, ct);
        var data = await ReadResponseAsync(response, ct); // Writes are sent once, including uploads.
        if (!CommentPicture.TryUrl(data.S("image_url"), out var uri) || data.N("image_width") != image.Width || data.N("image_height") != image.Height)
            throw new InvalidDataException("图片上传返回的信息无效，请重试。");
        return new(uri!.AbsoluteUri, image.Width, image.Height, image.Bytes.Length / 1024d);
    }
}
