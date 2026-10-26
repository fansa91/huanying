using System;

namespace Huanying.Services;

/// <summary>全局服务入口（轻量替代 DI 容器）</summary>
public static class AppServices
{
    public static AppSettings Settings { get; }

    public static VodApiService Vod { get; }

    /// <summary>视频源管理（含当前选中源）</summary>
    public static SourceStore Sources { get; }

    static AppServices()
    {
        Settings = AppSettings.Load();
        Vod = new VodApiService(Settings.VodBaseUrl) { AllowSniFallback = Settings.AllowSniFallback };
        Sources = new SourceStore(Settings);

        // 切换源后把接口地址落盘（HomePage 检测到 BaseUrl 变化会自动重新加载）
        Sources.ActiveChanged += (_, _) =>
        {
            Settings.VodBaseUrl = Sources.Active?.Url ?? Vod.BaseUrl;
            Settings.Save();
        };
    }
}
