using Huanying.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Huanying.Services;

/// <summary>
/// 应用设置（非打包应用无法使用 ApplicationData.Current，这里直接读写
/// %LOCALAPPDATA%\Huanying\settings.json）
/// </summary>
public sealed class AppSettings
{
    /// <summary>当前生效的接口根地址（始终等于当前选中源的 Url；空串表示尚未添加源）</summary>
    public string VodBaseUrl { get; set; } = string.Empty;

    /// <summary>已添加的视频源列表</summary>
    public List<VodSource> Sources { get; set; } = new();

    /// <summary>当前选中的源 Id</summary>
    public string ActiveSourceId { get; set; } = string.Empty;

    /// <summary>
    /// 允许「SNI 静默」直连回退：部分网络按 TLS SNI 阻断资源站域名，此时不发送 SNI 反而能通。
    /// 该通道会跳过证书校验，仅在常规请求失败时使用。
    /// </summary>
    public bool AllowSniFallback { get; set; } = true;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Huanying");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // 中文不转义，方便直接看文件
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded != null)
                {
                    loaded.VodBaseUrl = (loaded.VodBaseUrl ?? string.Empty).TrimEnd('/');
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏时回退到默认值
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // 写失败不影响运行
        }
    }
}
