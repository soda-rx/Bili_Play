using System.Text.Json;
using BiliPlay.Core;
using Microsoft.Data.Sqlite;
using Windows.Security.Credentials;

namespace BiliPlay.Services;

public sealed class LocalStore : IDisposable
{
    private readonly SqliteConnection _db;
    public LocalStore(string? dataDirectory = null, bool transient = false)
    {
        SQLitePCL.Batteries_V2.Init();
        var folder = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppProfile.DataDirectoryName);
        Directory.CreateDirectory(folder);
        _db = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "library.db"), Pooling = !transient }.ToString());
        _db.Open();
        using var command = _db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS settings (name TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS search_history (account INTEGER NOT NULL, query TEXT NOT NULL COLLATE NOCASE,
              searched_at INTEGER NOT NULL, PRIMARY KEY(account, query));
            CREATE TABLE IF NOT EXISTS history (account INTEGER NOT NULL, track_key TEXT NOT NULL,
              track_json TEXT NOT NULL, position REAL NOT NULL DEFAULT 0, played_at INTEGER NOT NULL,
              PRIMARY KEY(account, track_key));
            CREATE TABLE IF NOT EXISTS metadata (account INTEGER NOT NULL, track_key TEXT NOT NULL,
              title TEXT NOT NULL, artist TEXT NOT NULL, PRIMARY KEY(account, track_key));
            """;
        command.ExecuteNonQuery();
    }
    public string GetSetting(string key, string fallback)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT value FROM settings WHERE name=$key";
        c.Parameters.AddWithValue("$key", key);
        return c.ExecuteScalar() as string ?? fallback;
    }
    public void SetSetting(string key, string value)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "INSERT INTO settings VALUES($key,$value) ON CONFLICT(name) DO UPDATE SET value=excluded.value";
        c.Parameters.AddWithValue("$key", key); c.Parameters.AddWithValue("$value", value);
        c.ExecuteNonQuery();
    }
    public IReadOnlyList<Track> MusicCache(long account)
    {
        try { return JsonSerializer.Deserialize(GetSetting($"musicChannel:{account}", "[]"), BiliPlayJsonContext.Default.TrackArray) ?? []; }
        catch (JsonException) { return []; }
    }
    public void SaveMusicCache(long account, IEnumerable<Track> tracks) =>
        SetSetting($"musicChannel:{account}", JsonSerializer.Serialize(tracks.Take(120).ToArray(), BiliPlayJsonContext.Default.TrackArray));
    public IReadOnlyList<Track> ReadTracks(string key)
    {
        try { return JsonSerializer.Deserialize(GetSetting(key, "[]"), BiliPlayJsonContext.Default.TrackArray) ?? []; }
        catch (JsonException) { return []; }
    }
    public void WriteTracks(string key, IEnumerable<Track> tracks) => SetSetting(key, JsonSerializer.Serialize(tracks.ToArray(), BiliPlayJsonContext.Default.TrackArray));
    public void DeleteSearch(long account, string query)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "DELETE FROM search_history WHERE account=$a AND query=$q";
        c.Parameters.AddWithValue("$a", account); c.Parameters.AddWithValue("$q", query); c.ExecuteNonQuery();
    }
    public bool RecordRecentPlays
    {
        get => GetSetting("recordRecentPlays", "true") != "false";
        set => SetSetting("recordRecentPlays", value ? "true" : "false");
    }
    public void SaveProgress(long account, Track track, double position)
    {
        if (!RecordRecentPlays) return;
        using var c = _db.CreateCommand();
        c.CommandText = """
            INSERT INTO history VALUES($account,$key,$track,$position,$time)
            ON CONFLICT(account,track_key) DO UPDATE SET track_json=excluded.track_json,
              position=excluded.position, played_at=excluded.played_at
            """;
        c.Parameters.AddWithValue("$account", account); c.Parameters.AddWithValue("$key", track.Key);
        c.Parameters.AddWithValue("$track", JsonSerializer.Serialize(track, BiliPlayJsonContext.Default.Track)); c.Parameters.AddWithValue("$position", position);
        c.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        c.ExecuteNonQuery();
    }
    public IReadOnlyList<string> SearchHistory(long account)
    {
        if (!RecordSearchHistory) return [];
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT query FROM search_history WHERE account=$a ORDER BY searched_at DESC LIMIT 30";
        c.Parameters.AddWithValue("$a", account);
        using var reader = c.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
    public void RememberSearch(long account, string query)
    {
        if (!RecordSearchHistory || string.IsNullOrWhiteSpace(query)) return;
        using var c = _db.CreateCommand();
        c.CommandText = """
            INSERT INTO search_history VALUES($a,$q,$t) ON CONFLICT(account,query)
            DO UPDATE SET query=excluded.query,searched_at=excluded.searched_at;
            DELETE FROM search_history WHERE account=$a AND query NOT IN
              (SELECT query FROM search_history WHERE account=$a ORDER BY searched_at DESC LIMIT 30);
            """;
        c.Parameters.AddWithValue("$a", account); c.Parameters.AddWithValue("$q", query.Trim());
        c.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); c.ExecuteNonQuery();
    }
    public bool RecordSearchHistory
    {
        get => GetSetting("recordSearchHistory", "true") != "false";
        set => SetSetting("recordSearchHistory", value ? "true" : "false");
    }
    public IReadOnlyList<Track> History(long account)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT track_json FROM history WHERE account=$account ORDER BY played_at DESC LIMIT 200";
        c.Parameters.AddWithValue("$account", account);
        using var reader = c.ExecuteReader();
        var result = new List<Track>();
        while (reader.Read())
            try { if (JsonSerializer.Deserialize(reader.GetString(0), BiliPlayJsonContext.Default.Track) is { } track) result.Add(ApplyMetadata(account, track)); }
            catch (JsonException) { }
        return result;
    }
    public Track ApplyMetadata(long account, Track track)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT title,artist FROM metadata WHERE account=$account AND track_key IN ($key,$fallback) ORDER BY CASE WHEN track_key=$key THEN 0 ELSE 1 END LIMIT 1";
        c.Parameters.AddWithValue("$account", account); c.Parameters.AddWithValue("$key", track.Key);
        c.Parameters.AddWithValue("$fallback", $"{track.Bvid}:0");
        using var reader = c.ExecuteReader();
        return reader.Read() ? track with { Title = reader.GetString(0), Uploader = reader.GetString(1) } : track;
    }
    public IReadOnlyList<Track> ApplyMetadataBatch(long account,IReadOnlyList<Track> tracks)
    {
        if(tracks.Count==0) return [];
        using var command=_db.CreateCommand();
        command.CommandText="SELECT track_key,title,artist FROM metadata WHERE account=$account";
        command.Parameters.AddWithValue("$account",account);
        var metadata=new Dictionary<string,(string Title,string Artist)>(StringComparer.Ordinal);
        using(var reader=command.ExecuteReader())
            while(reader.Read()) metadata[reader.GetString(0)]=(reader.GetString(1),reader.GetString(2));
        return tracks.Select(track=>metadata.TryGetValue(track.Key,out var value)||metadata.TryGetValue($"{track.Bvid}:0",out value)
            ?track with {Title=value.Title,Uploader=value.Artist}:track).ToArray();
    }
    public void ClearHistory(long account)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "DELETE FROM history WHERE account=$a"; c.Parameters.AddWithValue("$a", account); c.ExecuteNonQuery();
    }
    public void Dispose() => _db.Dispose();
}

public static class SessionVault
{
    private static string Resource => AppProfile.SessionResource;
    private const string Username = "current";
    public static IReadOnlyList<SavedCookie> Read()
    {
        if(DebugSession.IsActive)return [];
        try
        {
            var item = new PasswordVault().Retrieve(Resource, Username);
            item.RetrievePassword();
            return JsonSerializer.Deserialize(item.Password, BiliPlayJsonContext.Default.SavedCookieArray) ?? [];
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or JsonException) { return []; }
    }
    public static void Save(IReadOnlyList<SavedCookie> cookies)
    {
        if(DebugSession.IsActive)return;
        Clear();
        new PasswordVault().Add(new(Resource, Username, JsonSerializer.Serialize(cookies.ToArray(), BiliPlayJsonContext.Default.SavedCookieArray)));
    }
    public static void Clear()
    {
        if(DebugSession.IsActive)return;
        var vault = new PasswordVault();
        try { vault.Remove(vault.Retrieve(Resource, Username)); }
        catch (System.Runtime.InteropServices.COMException) { }
    }
    public static void ClearForProfile(string profileId)
    {
        if (DebugSession.IsActive || profileId == "default") return;
        var vault = new PasswordVault();
        try { vault.Remove(vault.Retrieve($"BiliPlay.BilibiliSession.{profileId}", Username)); }
        catch (System.Runtime.InteropServices.COMException) { }
    }
}
