using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Huanying.Services;

/// <summary>HLS 主播放列表里声明的编码参数（Media Foundation 对自适应流往往只给分辨率）</summary>
public sealed class HlsPlaylistInfo
{
    public string? Resolution { get; init; }
    public string? Codecs { get; init; }
    public string? FrameRate { get; init; }
    public long Bandwidth { get; init; }

    /// <summary>主播放列表里声明的字幕轨</summary>
    public List<HlsSubtitleTrack> Subtitles { get; init; } = new();
}

/// <summary>m3u8 里声明的一条字幕轨（EXT-X-MEDIA TYPE=SUBTITLES）</summary>
public sealed class HlsSubtitleTrack
{
    /// <summary>轨道标识（NAME 属性）</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>语言代码（LANGUAGE 属性）</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>字幕文件地址（URI 属性，可能是相对路径）</summary>
    public string Uri { get; init; } = string.Empty;

    /// <summary>是否默认启用（DEFAULT=YES）</summary>
    public bool IsDefault { get; init; }

    /// <summary>是否强制显示（FORCED=YES）</summary>
    public bool IsForced { get; init; }

    /// <summary>界面上显示的名称</summary>
    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Name) ? "字幕" : Name;
            return string.IsNullOrWhiteSpace(Language) ? name : $"{name} ({Language})";
        }
    }
}

/// <summary>
/// 读取 m3u8 主播放列表的 EXT-X-STREAM-INF 信息，补上「帧率 / 声明码率 / 编码组合」。
/// 只做一次小请求，失败不影响播放。
/// </summary>
public static class HlsPlaylist
{
    private static readonly HttpClient Primary = Build(useSilentSni: false);
    private static readonly HttpClient SilentSni = Build(useSilentSni: true);

    /// <summary>最近一次探测失败的原因（诊断用）</summary>
    public static string LastError { get; private set; } = string.Empty;

    public static async Task<HlsPlaylistInfo?> ProbeAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) return null;

        LastError = string.Empty;

        try
        {
            string text;
            try
            {
                text = await Primary.GetStringAsync(url, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TaskCanceledException)
            {
                text = await SilentSni.GetStringAsync(url, ct);
            }

            var parsed = Parse(text);
            if (parsed == null) LastError = "播放列表里没有 EXT-X-STREAM-INF";

            return parsed;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>解析第一条 #EXT-X-STREAM-INF 的属性，并收集字幕轨</summary>
    public static HlsPlaylistInfo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string? line = null;
        foreach (var raw in text.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                line = trimmed["#EXT-X-STREAM-INF:".Length..];
                break;
            }
        }

        if (line == null) return null;

        var attributes = ParseAttributes(line);

        long bandwidth = 0;
        if (attributes.TryGetValue("BANDWIDTH", out var bandwidthText))
        {
            long.TryParse(bandwidthText, out bandwidth);
        }

        return new HlsPlaylistInfo
        {
            Resolution = attributes.GetValueOrDefault("RESOLUTION"),
            Codecs = attributes.GetValueOrDefault("CODECS"),
            FrameRate = attributes.GetValueOrDefault("FRAME-RATE"),
            Bandwidth = bandwidth,
            Subtitles = ParseSubtitles(text)
        };
    }

    /// <summary>解析主播放列表里声明的所有字幕轨</summary>
    public static List<HlsSubtitleTrack> ParseSubtitles(string? text)
    {
        var result = new List<HlsSubtitleTrack>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var raw in text.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (!trimmed.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase)) continue;

            var attributes = ParseAttributes(trimmed["#EXT-X-MEDIA:".Length..]);

            var type = attributes.GetValueOrDefault("TYPE");
            if (!string.Equals(type, "SUBTITLES", StringComparison.OrdinalIgnoreCase)) continue;

            var uri = attributes.GetValueOrDefault("URI");
            if (string.IsNullOrWhiteSpace(uri)) continue;

            result.Add(new HlsSubtitleTrack
            {
                Name = attributes.GetValueOrDefault("NAME") ?? string.Empty,
                Language = attributes.GetValueOrDefault("LANGUAGE") ?? string.Empty,
                Uri = uri,
                IsDefault = string.Equals(attributes.GetValueOrDefault("DEFAULT"), "YES", StringComparison.OrdinalIgnoreCase),
                IsForced = string.Equals(attributes.GetValueOrDefault("FORCED"), "YES", StringComparison.OrdinalIgnoreCase)
            });
        }

        return result;
    }

    /// <summary>
    /// 只取字幕轨，不关心编码信息。主播放列表没有 #EXT-X-STREAM-INF 时也能用。
    /// </summary>
    public static List<HlsSubtitleTrack> ParseSubtitleTracks(string? text) => ParseSubtitles(text);

    /// <summary>把播放列表里的相对 URI 补成绝对地址</summary>
    public static string ResolveUri(string baseUrl, string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return string.Empty;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var absolute)) return absolute.ToString();
        if (string.IsNullOrWhiteSpace(baseUrl)) return uri;

        try
        {
            var baseUri = new Uri(baseUrl);
            return new Uri(baseUri, uri).ToString();
        }
        catch
        {
            return uri;
        }
    }

    /// <summary>下载并解析字幕文本（WebVTT / SRT 都走同一个解析器）</summary>
    public static async Task<string?> FetchSubtitleAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            return await Primary.GetStringAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TaskCanceledException)
        {
            try
            {
                return await SilentSni.GetStringAsync(url, ct);
            }
            catch
            {
                return null;
            }
        }
    }

    private static Dictionary<string, string> ParseAttributes(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        while (index < line.Length)
        {
            var equals = line.IndexOf('=', index);
            if (equals < 0) break;

            var key = line[index..equals].Trim().Trim(',');

            var valueStart = equals + 1;
            string value;

            if (valueStart < line.Length && line[valueStart] == '"')
            {
                var close = line.IndexOf('"', valueStart + 1);
                if (close < 0) break;
                value = line[(valueStart + 1)..close];
                index = close + 1;
            }
            else
            {
                var comma = line.IndexOf(',', valueStart);
                if (comma < 0)
                {
                    value = line[valueStart..].Trim();
                    index = line.Length;
                }
                else
                {
                    value = line[valueStart..comma].Trim();
                    index = comma + 1;
                }
            }

            if (key.Length > 0) result[key] = value;

            // 跳过逗号
            while (index < line.Length && (line[index] == ',' || line[index] == ' ')) index++;
        }

        return result;
    }

    private static HttpClient Build(bool useSilentSni)
    {
        var client = new HttpClient(useSilentSni
            ? new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
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
            }
            : new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

        return client;
    }
}
