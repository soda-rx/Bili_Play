using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BiliPlay.Core;

/// <summary>Bounded, credential-free artwork cache. Clearing invalidates in-flight writes.</summary>
public sealed class ImageDiskCache(string directory, HttpMessageHandler? handler = null) : IDisposable
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect=false, UseCookies=false }) { Timeout=TimeSpan.FromSeconds(12) };
    private readonly ConcurrentDictionary<string,Lazy<Task<string?>>> _pending = new();
    private readonly SemaphoreSlim _network = new(4), _disk = new(1);
    private int _generation;
    private volatile bool _disposed;
    private const long Budget = 256L*1024*1024;
    public async Task<string?> GetAsync(Uri uri, CancellationToken ct = default)
    {
        if(_disposed || !Text.IsMediaHost(uri)) return null;
        uri=Text.CompatibleImageUri(uri);
        var key=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        var generation=Volatile.Read(ref _generation);
        // Warm-up callers can be on the UI thread, including on a disk cache hit.
        var job=_pending.GetOrAdd(key,_=>new(()=>Task.Run(()=>FetchAsync(uri,key,generation))));
        try { return await job.Value.WaitAsync(ct).ConfigureAwait(false); }
        finally { if(job.IsValueCreated && job.Value.IsCompleted) _pending.TryRemove(new KeyValuePair<string,Lazy<Task<string?>>>(key,job)); }
    }
    private void EnsureDirectory()
    {
        Directory.CreateDirectory(_directory);
        if((File.GetAttributes(_directory)&FileAttributes.ReparsePoint)!=0) throw new IOException("图片缓存目录不能是链接。");
    }
    private async Task<string?> FetchAsync(Uri uri,string key,int generation)
    {
        var path=Path.Combine(_directory,key+".img");
        try
        {
            await _disk.WaitAsync().ConfigureAwait(false);
            try { if(_disposed)return null; EnsureDirectory(); if(File.Exists(path)) { File.SetLastWriteTimeUtc(path,DateTime.UtcNow); return path; } }
            finally { _disk.Release(); }
            await _network.WaitAsync().ConfigureAwait(false);
            try
            {
                using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(12));
                for(var redirects=0;redirects<4 && Text.IsMediaHost(uri);redirects++)
                {
                    using var request=new HttpRequestMessage(HttpMethod.Get,uri);
                    request.Headers.UserAgent.ParseAdd(BilibiliClient.UserAgent);request.Headers.Referrer=new("https://www.bilibili.com/");
                    using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,limit.Token).ConfigureAwait(false);
                    if((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location) {uri=Text.CompatibleImageUri(new(uri,location));continue;}
                    response.EnsureSuccessStatusCode();
                    if(response.Content.Headers.ContentLength>10*1024*1024) return null;
                    await using var input=await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
                    var capacity=response.Content.Headers.ContentLength is >0 and <=10*1024*1024 ? (int)response.Content.Headers.ContentLength : 0;
                    using var bytes=capacity>0 ? new MemoryStream(capacity) : new MemoryStream();
                    var buffer=ArrayPool<byte>.Shared.Rent(16384);
                    try
                    {
                        int count;
                        while((count=await input.ReadAsync(buffer.AsMemory(0,16384),limit.Token).ConfigureAwait(false))>0)
                        { if(bytes.Length+count>10*1024*1024) return null; bytes.Write(buffer,0,count); }
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                    var data=bytes.ToArray();
                    if(data.Length<12 || !(data[0]==0xff&&data[1]==0xd8 || data[0]==137&&data[1]==80 || Encoding.ASCII.GetString(data,0,3)=="GIF" || Encoding.ASCII.GetString(data,0,4)=="RIFF" || data[0]==66&&data[1]==77)) return null;
                    await _disk.WaitAsync(limit.Token).ConfigureAwait(false);
                    try
                    {
                        if(_disposed || generation!=_generation) return null;
                        EnsureDirectory(); var temporary=path+".part";
                        try {await File.WriteAllBytesAsync(temporary,data,limit.Token).ConfigureAwait(false);File.Move(temporary,path,true);}
                        finally {File.Delete(temporary);}
                        var files=new DirectoryInfo(_directory).GetFiles("*.img").OrderByDescending(f=>f.LastWriteTimeUtc).ToArray();long size=0;
                        foreach(var file in files) {size+=file.Length;if(size>Budget || file.LastWriteTimeUtc<DateTime.UtcNow.AddDays(-30)) {try{file.Delete();}catch(IOException){}}}
                        return File.Exists(path)?path:null;
                    }
                    finally {_disk.Release();}
                }
                return null;
            }
            finally {_network.Release();}
        }
        catch(Exception e) when(e is IOException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException or ObjectDisposedException) {return null;}
    }
    public async Task<long> GetSizeAsync()
    {
        await _disk.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Directory.Exists(_directory)) return 0;
            if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("图片缓存目录不能是链接。");
            long bytes = 0;
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles())
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && System.Text.RegularExpressions.Regex.IsMatch(file.Name, "^[a-f0-9]{64}\\.img(\\.part)?$")) bytes += file.Length;
            return bytes;
        }
        finally { _disk.Release(); }
    }
    public async Task ClearAsync()
    {
        await _disk.WaitAsync().ConfigureAwait(false);
        try
        {
            if(_disposed)return;
            Interlocked.Increment(ref _generation);_pending.Clear();EnsureDirectory();
            foreach(var file in Directory.EnumerateFiles(_directory))
            {var name=Path.GetFileName(file);if(System.Text.RegularExpressions.Regex.IsMatch(name,"^[a-f0-9]{64}\\.img(\\.part)?$")) File.Delete(file);}
        }
        finally {_disk.Release();}
    }
    public void Dispose() {if(_disposed)return;_disposed=true;Interlocked.Increment(ref _generation);_http.Dispose();}
}
