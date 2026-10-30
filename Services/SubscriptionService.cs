using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Huanying.Services;

/// <summary>订阅里的一条源（名称 / 备注可为空，导入时用主机名兜底）</summary>
public sealed record SubscriptionEntry(string Name, string Url)
{
    /// <summary>备注（JSON 里的 note / remark / description 字段，可省略）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 订阅链接：一个地址，返回若干条视频源配置，一次性导入到源列表。
///
/// 没有统一标准，常见的几种都认：
///   • JSON 数组：<c>[{"name":"A","api":"https://a/api.php/provide/vod"}, "https://b/..."]</c>
///   • JSON 对象包一层：<c>{"sites":[...]}</c> / <c>{"sources":[...]}</c> / <c>{"urls":[...]}</c>
///   • 纯文本：一行一条，<c>https://...</c> 或 <c>名称,https://...</c>（逗号/空格/竖线都可）
///   • Base64 编码的上面任意一种
///   • <c>#</c> 与 <c>//</c> 开头的行当注释忽略
/// </summary>
public static class SubscriptionService
{
    private static readonly HttpClient Http = CreateClient(useProxy: true);

    private static readonly HttpClient LoopbackHttp = CreateClient(useProxy: false);

    /// <summary>拉取并解析订阅，返回去重后的源列表（可能为空）</summary>
    public static async Task<IReadOnlyList<SubscriptionEntry>> FetchAsync(
        string subscriptionUrl, CancellationToken token = default)
    {
        if (!Uri.TryCreate(subscriptionUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("订阅链接需要以 http:// 或 https:// 开头");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        // 本地地址（自检 / 局域网）绕开系统代理，否则代理会把它当外网转发
        var client = uri.IsLoopback ? LoopbackHttp : Http;
        var text = await client.GetStringAsync(uri, timeout.Token);

        var entries = Parse(text);
        if (entries.Count == 0 && TryDecodeBase64(text) is { } decoded)
        {
            entries = Parse(decoded);
        }

        return entries;
    }

    /// <summary>解析订阅内容（纯函数，方便自检与测试）</summary>
    public static IReadOnlyList<SubscriptionEntry> Parse(string content)
    {
        var result = new List<SubscriptionEntry>();
        if (string.IsNullOrWhiteSpace(content)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = content.Trim();

        if (text.StartsWith('[') || text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                CollectJson(doc.RootElement, result, seen, 0);
                if (result.Count > 0) return result;
            }
            catch (JsonException)
            {
                // 不是合法 JSON，退回按纯文本解析
            }
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#') || line.StartsWith("//")) continue;

            AddLine(line, result, seen);
        }

        return result;
    }

    /* ================= 内部 ================= */

    private static HttpClient CreateClient(bool useProxy)
    {
        var handler = new HttpClientHandler { UseProxy = useProxy };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Huanying/1.0 (Windows)");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        return client;
    }

    private static void CollectJson(JsonElement element, List<SubscriptionEntry> result,
        HashSet<string> seen, int depth)
    {
        if (depth > 4) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectJson(item, result, seen, depth + 1);
                }
                break;

            case JsonValueKind.String:
                AddEntry(string.Empty, element.GetString(), result, seen);
                break;

            case JsonValueKind.Object:
                var url = Str(element, "api") ?? Str(element, "url") ?? Str(element, "address")
                          ?? Str(element, "endpoint") ?? Str(element, "link");
                if (url != null)
                {
                    var name = Str(element, "name") ?? Str(element, "title") ?? Str(element, "label") ?? string.Empty;
                    var note = Str(element, "note") ?? Str(element, "remark") ?? Str(element, "description") ?? string.Empty;
                    AddEntry(name, url, result, seen, note);
                }

                // 继续往下找 { sites: [...] } / { data: { list: [...] } } 这类包装
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    {
                        CollectJson(property.Value, result, seen, depth + 1);
                    }
                }
                break;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void AddLine(string line, List<SubscriptionEntry> result, HashSet<string> seen)
    {
        var index = line.IndexOf("http", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return;

        var url = line[index..].Trim('"', '\'', ' ', '\t', ',', '，', ';', '；', '<', '>');
        var cut = url.IndexOfAny([' ', '\t', ',', '，', '|', ';', '；', '"', '\'']);
        if (cut > 0) url = url[..cut];

        var name = index > 0
            ? line[..index].Trim(' ', '\t', ',', '，', '|', ';', '；', '-', '—', ':', '：')
            : string.Empty;

        AddEntry(name, url, result, seen, string.Empty);
    }

    private static void AddEntry(
        string? name, string? url, List<SubscriptionEntry> result, HashSet<string> seen, string? note = null)
    {
        var normalized = (url ?? string.Empty).Trim().TrimEnd('/');
        if (normalized.Length == 0) return;

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        if (!seen.Add(normalized)) return;

        result.Add(new SubscriptionEntry((name ?? string.Empty).Trim(), normalized)
        {
            Note = (note ?? string.Empty).Trim()
        });
    }

    private static string? TryDecodeBase64(string text)
    {
        var compact = text.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        if (compact.Length < 32) return null;

        foreach (var c in compact)
        {
            var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=';
            if (!ok) return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(compact);
            var decoded = Encoding.UTF8.GetString(bytes);
            // 解出来必须是可打印文本，否则当作误判
            return decoded.Contains("http", StringComparison.OrdinalIgnoreCase) ? decoded : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
