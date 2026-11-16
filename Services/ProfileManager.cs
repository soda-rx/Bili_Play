using System.Text.Json;
using System.Text.Json.Serialization;

namespace BiliPlay.Services;

public sealed record UserProfile(string Id, string Name, long AccountId, string Avatar);

[JsonSerializable(typeof(UserProfile[]))]
internal partial class ProfileJsonContext : JsonSerializerContext;

public static class ProfileManager
{
    private static readonly object Gate = new();
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppProfile.BaseDataDirectoryName);
    private static string FilePath => Path.Combine(Root, "profiles.json");
    public static string ValidateId(string id) => string.IsNullOrWhiteSpace(id) || id.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_') ? "default" : id;
    public static bool IsNewProfile => AppProfile.ProfileId != "default" && Read().FirstOrDefault(p => p.Id == AppProfile.ProfileId)?.AccountId == 0;
    public static string? PendingPreviousProfileId { get; private set; }
    public static IReadOnlyList<UserProfile> Read()
    {
        lock (Gate)
        {
            try { return JsonSerializer.Deserialize(File.Exists(FilePath) ? File.ReadAllText(FilePath) : "[]", ProfileJsonContext.Default.UserProfileArray) ?? []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
        }
    }
    public static IReadOnlyList<UserProfile> WithDefault()
        => Read().Prepend(new UserProfile("default", "默认配置文件", 0, "")).GroupBy(p => p.Id, StringComparer.Ordinal).Select(g => g.First()).ToArray();
    public static UserProfile Create(string? previousProfileId = null)
    {
        PendingPreviousProfileId = previousProfileId;
        var profile = new UserProfile(Guid.NewGuid().ToString("N")[..10], "新配置文件", 0, ""); Save(profile); return profile;
    }
    public static void ClearPending() => PendingPreviousProfileId = null;
    public static void Delete(string id)
    {
        id = ValidateId(id); if (id == "default" || DebugSession.IsActive) return;
        var profile = Read().FirstOrDefault(p => p.Id == id);
        lock (Gate)
        {
            Directory.CreateDirectory(Root);
            SaveList(Read().Where(p => p.Id != id));
        }
        if (profile is not null)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppProfile.BaseDataDirectoryName, "profiles", id);
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { SessionVault.ClearForProfile(id); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public static void Save(UserProfile profile)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Root);
            SaveList(Read().Where(p => p.Id != profile.Id).Append(profile));
        }
    }
    private static void SaveList(IEnumerable<UserProfile> list) => File.WriteAllText(FilePath, JsonSerializer.Serialize(list.ToArray(), ProfileJsonContext.Default.UserProfileArray));
    public static void UpdateAccount(Core.Account account)
    { if (DebugSession.IsActive) return; var digits = account.Id.ToString(); var label = $"UID {digits[..Math.Min(5, digits.Length)]}"; Save(new(AppProfile.ProfileId, label, account.Id, account.Avatar)); ClearPending(); }
}
