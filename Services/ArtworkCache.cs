using BiliPlay.Core;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BiliPlay.Services;

public static class ArtworkCache
{
    private static Lazy<ImageDiskCache> Cache = NewCache();
    private static Lazy<ImageDiskCache> NewCache() => new(()=>new(DebugSession.DirectoryPath is { } root ? Path.Combine(root,"cache","images") : App.IsVisualCheck
        ? Path.Combine(App.VisualCheckDirectory!,"data","cache","images")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppProfile.DataDirectoryName,"cache","images")));
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapImage,Uri> Sources = new();
    private static readonly Dictionary<string,WeakReference<BitmapImage>> Memory = [];
    private static readonly LinkedList<(string Key, BitmapImage Image)> Avatars = new();
    private static int _formatVersion;
    private static Task _clearing = Task.CompletedTask;
    public static async Task InitializeAsync(bool compatibilityMode)
    {
        var supported = await ImageCodecSupport.SupportsWebpAsync();
        ArtworkFormats.Configure(supported, compatibilityMode);
        Memory.Clear(); Avatars.Clear(); _formatVersion++;
    }
    public static void SetCompatibilityMode(bool enabled)
    {
        if(ArtworkFormats.CompatibilityMode==enabled)return;
        ArtworkFormats.Configure(ArtworkFormats.WebpSupported, enabled);
        Memory.Clear(); Avatars.Clear(); _formatVersion++;
    }
    public static Uri? SourceUri(BitmapImage? image) => image is null?null:Sources.TryGetValue(image,out var uri)?uri:image.UriSource;
    public static async Task<Uri> ResolveUriAsync(Uri uri)
    {
        uri=Text.CompatibleImageUri(uri);
        if(uri.Scheme!="https"||App.IsVisualCheck||DebugSession.IsActive)return uri;
        var cache=Cache.Value;
        try { await _clearing; }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ObjectDisposedException) { return uri; }
        var path=await cache.GetAsync(uri);return path is null?uri:new Uri(path);
    }
    public static BitmapImage CreateAvatar(Uri uri, int width)
    {
        uri=Text.CompatibleImageUri(uri);
        var key=$"{width}:{uri.AbsoluteUri}";
        for(var node=Avatars.First;node is not null;node=node.Next)
        {
            if(node.Value.Key!=key) continue;
            var image=node.Value.Image;Avatars.Remove(node);Avatars.AddFirst((key,image));return image;
        }
        var created=Create(uri,width);Avatars.AddFirst((key,created));
        if(Avatars.Count>32)Avatars.RemoveLast();
        return created;
    }
    public static async Task WarmAsync(IEnumerable<string> images, CancellationToken ct=default)
    {
        if(App.IsVisualCheck||DebugSession.IsActive)return;
        var cache=Cache.Value;
        var urls=images.Distinct(StringComparer.Ordinal).Take(96)
            .Select(value=>Uri.TryCreate(value,UriKind.Absolute,out var uri)?uri:null)
            .OfType<Uri>().Where(Text.IsMediaHost).ToArray();
        try {await Task.WhenAll(urls.Select(uri=>cache.GetAsync(uri,ct)));}
        catch(Exception e) when(e is OperationCanceledException or ObjectDisposedException or IOException) { }
    }
    public static BitmapImage Create(Uri uri,int width)
    {
        uri=Text.CompatibleImageUri(uri);
        var key=$"{width}:{uri.AbsoluteUri}";
        if(Memory.TryGetValue(key,out var weak)&&weak.TryGetTarget(out var existing))return existing;
        var image=new BitmapImage { DecodePixelWidth=width };Sources.Add(image,uri);
        if(Memory.Count>=96)Memory.Clear();Memory[key]=new(image);
        if(uri.Scheme!="https" || App.IsVisualCheck) image.UriSource=uri;
        else _=LoadAsync(image,uri);
        return image;
    }
    private static async Task LoadAsync(BitmapImage image,Uri uri)
    {
        var version=_formatVersion;
        var resolved=await ResolveUriAsync(uri);
        if(version!=_formatVersion)return;
        try {image.UriSource=resolved;}
        catch(Exception e) when(e is System.Runtime.InteropServices.COMException or ArgumentException) { }
    }
    public static void TrimMemory()
    {
        Memory.Clear();
        while (Avatars.Count > 8) Avatars.RemoveLast();
    }
    public static void ResetScope() { _formatVersion++; Memory.Clear(); Avatars.Clear(); Sources.Clear(); _clearing=Task.CompletedTask; if(Cache.IsValueCreated)Cache.Value.Dispose(); Cache=NewCache(); }
    public static async Task<long> GetSizeAsync()
    {
        var cache = Cache.Value;
        await _clearing;
        return await Task.Run(() => cache.GetSizeAsync());
    }
    public static Task ClearAsync()
    {
        _formatVersion++; Memory.Clear(); Avatars.Clear();
        return _clearing=ClearAfterAsync(_clearing,Cache.Value);
    }
    private static async Task ClearAfterAsync(Task previous,ImageDiskCache cache)
    {
        try { await previous.ConfigureAwait(false); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
        await Task.Run(()=>cache.ClearAsync()).ConfigureAwait(false);
    }
}
