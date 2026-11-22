namespace BiliPlay.Services;

/// <summary>Build-time boundaries between everyday use and development data.</summary>
public static class AppProfile
{
    private static string _profileId = "default";
    public static string ProfileId => _profileId;
    public static void SetProfile(string id) => _profileId = ProfileManager.ValidateId(id);
#if DEVELOPMENT_BUILD
    public static string DisplayName => "Bili Play";
    public static string BaseDataDirectoryName => "BiliPlay.Dev";
    public static string DataDirectoryName => _profileId == "default" ? BaseDataDirectoryName : Path.Combine(BaseDataDirectoryName, "profiles", _profileId);
    public static string SessionResource => _profileId == "default" ? "BiliPlay.BilibiliSession.Development" : $"BiliPlay.BilibiliSession.Development.{_profileId}";
    public static string DownloadDirectoryName => _profileId == "default" ? "Bili_Play_Dev" : $"Bili_Play_Dev_{_profileId}";
    public static string InstanceScope => "Development";
#else
    public static string DisplayName => "Bili Play";
    public static string BaseDataDirectoryName => "BiliPlay";
    public static string DataDirectoryName => _profileId == "default" ? BaseDataDirectoryName : Path.Combine(BaseDataDirectoryName, "profiles", _profileId);
    public static string SessionResource => _profileId == "default" ? "BiliPlay.BilibiliSession" : $"BiliPlay.BilibiliSession.{_profileId}";
    public static string DownloadDirectoryName => _profileId == "default" ? "Bili_Play" : $"Bili_Play_{_profileId}";
    public static string InstanceScope => "Main";
#endif
}
