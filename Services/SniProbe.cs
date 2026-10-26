using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;

namespace Huanying.Services;

/// <summary>
/// 诊断用：依次尝试多种 TLS 握手变体（SNI 有无 / TLS 版本 / ALPN），
/// 找出在当前网络下能真正连通的组合。由 HY_APITEST=2 触发。
/// </summary>
public static class SniProbe
{
    public static async Task<string> RunAsync(string url)
    {
        var report = new StringBuilder();
        var variants = new (string Name, string? Sni, SslProtocols Protocols, bool Alpn)[]
        {
            ("默认 (无SNI, TLS任意)", null, SslProtocols.None, false),
            ("无SNI + TLS1.2", null, SslProtocols.Tls12, false),
            ("无SNI + ALPN http/1.1", null, SslProtocols.None, true),
            ("假SNI baidu", "www.baidu.com", SslProtocols.None, false),
            ("假SNI baidu + TLS1.2", "www.baidu.com", SslProtocols.Tls12, false),
            ("正常SNI (对照)", new Uri(url).Host, SslProtocols.None, false)
        };

        foreach (var (name, sni, protocols, alpn) in variants)
        {
            try
            {
                using var http = Build(sni, protocols, alpn);
                using var response = await http.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                report.AppendLine($"PROBE {name,-26} => HTTP {(int)response.StatusCode} LEN={body.Length}");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException?.Message;
                report.AppendLine($"PROBE {name,-26} => FAIL {ex.GetType().Name}: {ex.Message}" +
                                  (inner == null ? "" : $" | inner: {inner}"));
            }
        }

        return report.ToString();
    }

    private static HttpClient Build(string? sni, SslProtocols protocols, bool alpn)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
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

                    var options = new SslClientAuthenticationOptions
                    {
                        TargetHost = sni ?? string.Empty,
                        EnabledSslProtocols = protocols
                    };

                    if (alpn) options.ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 };

                    await ssl.AuthenticateAsClientAsync(options, ct);
                    return ssl;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }
}
