using Huanying.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Huanying.Services;

/// <summary>
/// 苹果CMS (MacCMS V10) JSON 接口客户端。
/// 接口约定：?ac=detail&amp;pg=页码&amp;t=分类ID&amp;wd=关键词，?ac=detail&amp;ids=ID 取详情。
///
/// 额外处理：部分网络会按 TLS SNI 阻断资源站域名（TCP 通、握手无响应）。
/// 这类站点通常按 HTTP Host 头路由，因此提供一条「SNI 静默」直连回退通道：
/// 不发送 SNI 完成 TLS 握手，HTTP 层仍带正确 Host 头。仅在常规请求失败时启用。
/// </summary>
public sealed class VodApiService
{
    /// <summary>没有任何视频源时的提示语（本项目不内置任何接口地址）</summary>
    public const string NoSourceMessage =
        "尚未添加视频源。请到侧边栏「源管理」添加一个苹果CMS（MacCMS V10）JSON 接口地址。";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private HttpClient? _primary;
    private bool _primaryUsesProxy = true;
    private readonly HttpClient _silentSni;

    public VodApiService(string? baseUrl = null)
    {
        BaseUrl = baseUrl ?? string.Empty;

        // 回退通道：直连、不发送 SNI、跳过证书校验（目标站证书多为过期/自签）
        _silentSni = CreateClient(CreateSilentSniHandler(), TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// 常规通道：走系统代理、正常校验证书。
    /// 例外：目标是本机地址时强制直连——系统代理常把 127.0.0.1 也代理走，
    /// 结果本地接口返回 502（调试本地服务时最容易踩的坑）。
    /// </summary>
    private HttpClient Primary
    {
        get
        {
            var useProxy = !IsLoopbackHost(_baseUrl);
            if (_primary != null && _primaryUsesProxy == useProxy) return _primary;

            _primary?.Dispose();

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                UseProxy = useProxy
            };

            _primaryUsesProxy = useProxy;
            _primary = CreateClient(handler, TimeSpan.FromSeconds(20));
            return _primary;
        }
    }

    private static bool IsLoopbackHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>接口根地址，不含 ?ac= 参数（空串表示尚未配置源）</summary>
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            _baseUrl = (value ?? string.Empty).Trim().TrimEnd('/');
            _preferFallback = false;
        }
    }

    private string _baseUrl = string.Empty;

    /// <summary>是否已配置可用的接口地址</summary>
    public bool HasSource => !string.IsNullOrWhiteSpace(_baseUrl);

    /// <summary>是否允许在常规请求失败后启用 SNI 静默回退</summary>
    public bool AllowSniFallback { get; set; } = true;

    /// <summary>最近一次成功请求是否走了回退通道</summary>
    public bool LastUsedFallback { get; private set; }

    /// <summary>常规通道被阻断过，后续优先走回退通道</summary>
    private bool _preferFallback;

    /// <summary>最近一次请求的完整 URL，便于排查问题</summary>
    public string? LastRequestUrl { get; private set; }

    /// <summary>拉取列表（含海报等完整字段）</summary>
    public async Task<VodApiResponse> GetListAsync(
        int page = 1,
        int? typeId = null,
        string? keyword = null,
        CancellationToken ct = default)
    {
        EnsureConfigured();

        var query = $"/?ac=detail&pg={page}";
        if (typeId is > 0) query += $"&t={typeId}";
        if (!string.IsNullOrWhiteSpace(keyword)) query += $"&wd={Uri.EscapeDataString(keyword.Trim())}";

        var json = await GetStringWithFallbackAsync(BaseUrl + query, ct).ConfigureAwait(false);

        var response = JsonSerializer.Deserialize<VodApiResponse>(json, JsonOptions)
                       ?? throw new VodApiException("接口返回内容为空，无法解析。", null);

        if (response.Code != 1)
        {
            throw new VodApiException(
                string.IsNullOrWhiteSpace(response.Msg) ? $"接口返回 code={response.Code}" : response.Msg!, null);
        }

        return response;
    }

    /// <summary>按 ID 取详情</summary>
    public async Task<VodItem?> GetDetailAsync(int vodId, CancellationToken ct = default)
    {
        EnsureConfigured();

        var json = await GetStringWithFallbackAsync($"{BaseUrl}/?ac=detail&ids={vodId}", ct).ConfigureAwait(false);

        var response = JsonSerializer.Deserialize<VodApiResponse>(json, JsonOptions)
                       ?? throw new VodApiException("接口返回内容为空，无法解析。", null);

        if (response.Code != 1)
        {
            throw new VodApiException(
                string.IsNullOrWhiteSpace(response.Msg) ? $"接口返回 code={response.Code}" : response.Msg!, null);
        }

        return response.List.Count > 0 ? response.List[0] : null;
    }

    /// <summary>取分类表（仅 ac=list 返回 class 字段）</summary>
    public async Task<List<VodCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        if (!HasSource) return new List<VodCategory>();

        try
        {
            var json = await GetStringWithFallbackAsync($"{BaseUrl}/?ac=list", ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<VodApiResponse>(json, JsonOptions);
            if (response?.Class is { Count: > 0 }) return response.Class;
        }
        catch (Exception ex) when (ex is VodApiException or JsonException)
        {
            // 分类拉取失败不影响主流程，回退到内置分类
        }

        return FallbackCategories();
    }

    /* ================= 内部实现 ================= */

    private static HttpClient CreateClient(HttpMessageHandler handler, TimeSpan timeout)
    {
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        return client;
    }

    private static SocketsHttpHandler CreateSilentSniHandler()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseProxy = false,                 // 回退通道必须直连
            ConnectTimeout = TimeSpan.FromSeconds(8),
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);

                    // TargetHost 传空串 => 不发送 SNI 扩展；证书校验跳过（目标站多为自签/过期）
                    var ssl = new SslStream(
                        new NetworkStream(socket, ownsSocket: true),
                        leaveInnerStreamOpen: false,
                        userCertificateValidationCallback: static (_, _, _, _) => true);

                    await ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions { TargetHost = string.Empty }, ct).ConfigureAwait(false);

                    return ssl;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return handler;
    }

    private async Task<string> GetStringWithFallbackAsync(string url, CancellationToken ct)
    {
        LastRequestUrl = url;

        // 已知该网络需要回退时，直接用回退通道，省掉一次注定失败的握手
        if (_preferFallback && AllowSniFallback)
        {
            try
            {
                var text = await SendWithRetryAsync(_silentSni, url, ct).ConfigureAwait(false);
                LastUsedFallback = true;
                return text;
            }
            catch
            {
                _preferFallback = false;
            }
        }

        string primaryError;
        try
        {
            var text = await SendAsync(Primary, url, ct).ConfigureAwait(false);
            LastUsedFallback = false;
            return text;
        }
        catch (VodApiException)
        {
            throw; // HTTP 状态码错误，回退没有意义
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            primaryError = ex.Message;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            primaryError = "请求超时";
        }

        if (!AllowSniFallback)
        {
            throw new VodApiException($"网络请求失败：{primaryError}\n目标地址：{url}", null);
        }

        // 常规通道失败 → SNI 静默直连（目标站偶发 RST，内部带重试）
        try
        {
            var text = await SendWithRetryAsync(_silentSni, url, ct).ConfigureAwait(false);
            LastUsedFallback = true;
            _preferFallback = true;
            return text;
        }
        catch (Exception ex)
        {
            throw new VodApiException(
                $"网络请求失败。\n常规通道：{primaryError}\n直连回退：{ex.Message}\n目标地址：{url}", ex);
        }
    }

    /// <summary>回退通道重试：该链路首次连接偶发被 RST，重试即可恢复</summary>
    private static async Task<string> SendWithRetryAsync(HttpClient client, string url, CancellationToken ct)
    {
        Exception? last = null;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                return await SendAsync(client, url, ct).ConfigureAwait(false);
            }
            catch (VodApiException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
            {
                last = ex;
                if (attempt < 4) await Task.Delay(300 * attempt, ct).ConfigureAwait(false);
            }
        }

        throw last ?? new HttpRequestException("直连回退失败");
    }

    private static async Task<string> SendAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new VodApiException(
                $"接口返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n目标地址：{url}", null);
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 接口取不到分类时的兜底。各站的 type_id 并不通用，本项目不内置任何具体站点的分类表，
    /// 因此返回空列表，界面只保留「全部」。
    /// </summary>
    public static List<VodCategory> FallbackCategories() => new();

    private void EnsureConfigured()
    {
        if (!HasSource) throw new VodApiException(NoSourceMessage, null);
    }
}

public sealed class VodApiException : Exception
{
    public VodApiException(string message, Exception? inner) : base(message, inner) { }
}
