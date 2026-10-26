using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace Huanying.Services;

/// <summary>
/// 队列式图片加载：所有海报统一进同一条队列（默认 3 个并发、先进先出），
/// 避免一次性发起上百个请求把连接打满，也避免界面被解码拖慢。
///
/// 三级缓存：内存（BitmapImage，LRU）→ 磁盘（%LOCALAPPDATA%\Huanying\cache\img）→ 网络。
/// 相同 URL 的并发请求会合并成一次下载。
/// </summary>
public sealed class ImageQueue
{
    public static ImageQueue Current { get; } = new();

    /// <summary>队列并发上限（先进先出，超出的请求排队等待）</summary>
    private const int MaxConcurrency = 3;

    private const int MemoryLimit = 280;

    private readonly HttpClient _http;

    /// <summary>SNI 静默直连通道（部分网络按 SNI 阻断图片域名，常规通道会 SSL 失败）</summary>
    private readonly HttpClient _httpFallback;
    private readonly HttpClient _httpLocal;
    private readonly SemaphoreSlim _gate = new(MaxConcurrency, MaxConcurrency);
    private readonly Dictionary<string, BitmapImage> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly ConcurrentDictionary<string, Task<BitmapImage?>> _inflight = new(StringComparer.Ordinal);
    private readonly string _cacheDir;

    private int _pending;
    private int _done;
    private int _failed;

    private ImageQueue()
    {
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        // 部分图床按 Referer 判盗链，这里给一个中性值
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/*,*/*;q=0.8");

        _httpFallback = new HttpClient(CreateSilentSniHandler()) { Timeout = TimeSpan.FromSeconds(20) };
        _httpFallback.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        _httpFallback.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/*,*/*;q=0.8");

        // 本机 / 内网图片：直连，避免系统代理把 127.0.0.1 拦成 502
        _httpLocal = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseProxy = false
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpLocal.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        _httpLocal.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/*,*/*;q=0.8");

        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Huanying", "cache", "img");

        try { Directory.CreateDirectory(_cacheDir); } catch { /* 忽略 */ }

        // 后台清理一次磁盘缓存，避免长期使用后无限增长
        _ = Task.Run(PruneDiskCache);
    }

    /// <summary>排队中的请求数</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>已完成的请求数</summary>
    public int Completed => Volatile.Read(ref _done);

    /// <summary>失败的请求数</summary>
    public int Failed => Volatile.Read(ref _failed);

    /// <summary>最近一次失败原因（诊断用）</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>队列状态变化（用于页脚展示队列进度）</summary>
    public event EventHandler? StatsChanged;

    /// <summary>
    /// 取一张图：命中缓存直接返回，否则排队下载并解码。
    /// 调用方取消只影响自己的等待，不影响队列里正在跑的下载。
    /// </summary>
    public async Task<BitmapImage?> LoadAsync(string? url, int decodeWidth, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var key = $"{url}|{decodeWidth}";

        if (_memory.TryGetValue(key, out var cached))
        {
            Touch(key);
            return cached;
        }

        if (!_inflight.TryGetValue(key, out var task))
        {
            task = LoadCoreAsync(url!, decodeWidth, key);
            _inflight[key] = task;

            // 结束后从飞行表移除（失败也移除，允许后续重试）
            _ = task.ContinueWith(
                t => _inflight.TryRemove(key, out var _),
                TaskScheduler.Default);
        }

        try
        {
            return await task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<BitmapImage?> LoadCoreAsync(string url, int decodeWidth, string key)
    {
        Interlocked.Increment(ref _pending);
        RaiseStats();

        // 排队等一个并发位（先进先出）
        await _gate.WaitAsync();
        try
        {
            var bytes = await TryReadDiskAsync(url);

            if (bytes == null)
            {
                bytes = await DownloadAsync(url);
                await TryWriteDiskAsync(url, bytes);
            }

            var bitmap = await DecodeAsync(bytes, decodeWidth);
            if (bitmap == null) throw new InvalidOperationException("图片解码失败（系统缺少该格式的解码器）");

            Remember(key, bitmap);
            Interlocked.Increment(ref _done);
            return bitmap;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failed);
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
        finally
        {
            _gate.Release();
            Interlocked.Decrement(ref _pending);
            RaiseStats();
        }
    }

    /// <summary>下载：常规通道失败（SSL 被阻断 / 连接重置）时改走 SNI 静默直连</summary>
    private async Task<byte[]> DownloadAsync(string url)
    {
        // 本机 / 内网图床不走系统代理（代理会把 127.0.0.1 也拦掉）
        var client = IsLoopback(url) ? _httpLocal : _http;

        try
        {
            return await client.GetByteArrayAsync(url);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TaskCanceledException)
        {
            return await _httpFallback.GetByteArrayAsync(url);
        }
    }

    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>不发送 SNI 的直连 handler（该链路首次连接偶发被 RST，交给上层重试）</summary>
    private static SocketsHttpHandler CreateSilentSniHandler() => new()
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        AllowAutoRedirect = true,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, ct);

                var ssl = new SslStream(
                    new NetworkStream(socket, ownsSocket: true),
                    leaveInnerStreamOpen: false,
                    userCertificateValidationCallback: static (_, _, _, _) => true);

                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = string.Empty }, ct);

                return ssl;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    /// <summary>
    /// 解码：优先走系统解码器（WIC，JPEG/PNG 最快）；
    /// 遇到本机没有解码扩展的格式（资源站海报大量用 WebP）时，用 SkiaSharp 转成 PNG 再解码。
    /// </summary>
    private static async Task<BitmapImage?> DecodeAsync(byte[] bytes, int decodeWidth)
    {
        if (!IsWebP(bytes))
        {
            try
            {
                var native = new BitmapImage
                {
                    DecodePixelType = DecodePixelType.Logical,
                    DecodePixelWidth = Math.Max(80, decodeWidth)
                };

                await native.SetSourceAsync(await ToRandomAccessStreamAsync(bytes));
                return native;
            }
            catch
            {
                // 落到 Skia 兜底
            }
        }

        var png = TranscodeToPng(bytes, decodeWidth);
        if (png == null) return null;

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(await ToRandomAccessStreamAsync(png));
        return bitmap;
    }

    private static bool IsWebP(byte[] buffer) =>
        buffer.Length > 12 &&
        buffer[0] == (byte)'R' && buffer[1] == (byte)'I' && buffer[2] == (byte)'F' && buffer[3] == (byte)'F' &&
        buffer[8] == (byte)'W' && buffer[9] == (byte)'E' && buffer[10] == (byte)'B' && buffer[11] == (byte)'P';

    /// <summary>SkiaSharp 解码 + 等比缩放到目标宽度，输出 PNG 字节</summary>
    private static byte[]? TranscodeToPng(byte[] bytes, int targetWidth)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec == null) return null;

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0) return null;

            var scale = Math.Min(1.0, Math.Max(80, targetWidth) / (double)info.Width);
            var width = Math.Max(1, (int)Math.Round(info.Width * scale));
            var height = Math.Max(1, (int)Math.Round(info.Height * scale));

            var targetInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(targetInfo);
            var result = codec.GetPixels(targetInfo, bitmap.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 90);
            return png?.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把字节流包成 WinRT 随机访问流（BitmapImage.SetSourceAsync 需要）</summary>
    private static async Task<InMemoryRandomAccessStream> ToRandomAccessStreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);

        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();

        stream.Seek(0);
        return stream;
    }

    /* ================= 内存缓存（LRU） ================= */

    private void Remember(string key, BitmapImage bitmap)
    {
        if (_memory.ContainsKey(key)) Touch(key);
        else _lru.AddFirst(key);

        _memory[key] = bitmap;

        while (_lru.Count > MemoryLimit)
        {
            var oldest = _lru.Last;
            if (oldest == null) break;
            _lru.RemoveLast();
            _memory.Remove(oldest.Value);
        }
    }

    private void Touch(string key)
    {
        var node = _lru.Find(key);
        if (node == null) return;
        _lru.Remove(node);
        _lru.AddFirst(node);
    }

    /* ================= 磁盘缓存 ================= */

    private string PathFor(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        var name = Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        return Path.Combine(_cacheDir, name);
    }

    private async Task<byte[]?> TryReadDiskAsync(string url)
    {
        try
        {
            var path = PathFor(url);
            if (!File.Exists(path)) return null;

            var bytes = await File.ReadAllBytesAsync(path);
            return bytes.Length > 0 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task TryWriteDiskAsync(string url, byte[] bytes)
    {
        if (bytes.Length == 0) return;

        try
        {
            await File.WriteAllBytesAsync(PathFor(url), bytes);
        }
        catch
        {
            // 磁盘写失败只是下次重新下载，忽略
        }
    }

    /// <summary>磁盘缓存超过上限时，按最后访问时间删掉最旧的一部分</summary>
    private void PruneDiskCache()
    {
        const int keep = 300;
        const int trigger = 600;

        try
        {
            var files = new DirectoryInfo(_cacheDir).GetFiles();
            if (files.Length <= trigger) return;

            foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc).Take(files.Length - keep))
            {
                try { file.Delete(); } catch { /* 单个文件失败忽略 */ }
            }
        }
        catch
        {
            // 忽略
        }
    }

    private void RaiseStats() => StatsChanged?.Invoke(this, EventArgs.Empty);
}
