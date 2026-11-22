using System.Globalization;
using System.Text.Json;
namespace BiliPlay.Core;
internal sealed record MessageContentPayload(string content);
public sealed partial class BilibiliClient
{
    private readonly string _messageDevice = Guid.NewGuid().ToString("D").ToUpperInvariant();
    public async Task SendMessageAsync(long talker, string text, CancellationToken ct = default)
    {
        RequireAccount();
        var account = Account!.Id;
        if (talker <= 0 || talker == account || string.IsNullOrWhiteSpace(text) || text.Length > 1000) throw new ArgumentException("消息不能为空或超过 1000 字。");
        var verified = await GetAccountAsync(ct);
        if (verified.Id != account) throw new InvalidOperationException("账号已变化，请重新打开会话。");
        var csrf = _cookies.GetCookies(Api)["bili_jct"]?.Value;
        if (string.IsNullOrEmpty(csrf)) throw new BilibiliException(-111, "登录凭据不完整，请重新登录。");
        var query = await SignedQueryAsync(new() { ["w_sender_uid"] = account.ToString(CultureInfo.InvariantCulture), ["w_receiver_id"] = talker.ToString(CultureInfo.InvariantCulture), ["w_dev_id"] = _messageDevice }, ct);
        if (Account?.Id != account) throw new InvalidOperationException("账号已变化。");
        var fields = new Dictionary<string, string> {
            ["msg[sender_uid]"] = account.ToString(CultureInfo.InvariantCulture), ["msg[receiver_id]"] = talker.ToString(CultureInfo.InvariantCulture),
            ["msg[receiver_type]"] = "1", ["msg[msg_type]"] = "1", ["msg[msg_status]"] = "0", ["msg[content]"] = JsonSerializer.Serialize(new MessageContentPayload(text), InternalCoreJsonContext.Default.MessageContentPayload),
            ["msg[dev_id]"] = _messageDevice, ["msg[timestamp]"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["msg[new_face_version]"] = "0", ["from_firework"] = "0", ["build"] = "0", ["mobi_app"] = "web", ["csrf"] = csrf, ["csrf_token"] = csrf };
        using var response = await _http.PostAsync(new Uri("https://api.vc.bilibili.com/web_im/v1/web_im/send_msg?" + query), new FormUrlEncodedContent(fields), ct);
        // A write may have succeeded even when the acknowledgement is lost. Never retry automatically.
        _ = await ReadResponseAsync(response, ct);
    }
}
