using Huanying.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace Huanying.Views;

public sealed partial class SettingsPage : Page
{
    private bool _initializing;

    public SettingsPage()
    {
        this.InitializeComponent();

        this.Loaded += (_, _) =>
        {
            _initializing = true;
            ApiUrlBox.Text = AppServices.Settings.VodBaseUrl;
            SniFallbackSwitch.IsOn = AppServices.Settings.AllowSniFallback;
            _initializing = false;

            RefreshSourceSummary();

            if (!AppServices.Vod.HasSource)
            {
                ApiStatusText.Text = "尚未添加视频源。本项目不内置接口地址，请填入你自己的苹果CMS 接口后点「测试连接」。";
            }
        };
    }

    private void RefreshSourceSummary()
    {
        var store = AppServices.Sources;
        SourceCountText.Text = store.Active == null
            ? $"还没有可用源，共 {store.Items.Count} 个"
            : $"共 {store.Items.Count} 个源 · 当前：{store.Active.Name}";
    }

    private void ManageSources_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(SourceListPage));
    }

    private void SniFallback_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        AppServices.Settings.AllowSniFallback = SniFallbackSwitch.IsOn;
        AppServices.Settings.Save();
        AppServices.Vod.AllowSniFallback = SniFallbackSwitch.IsOn;
    }

    /// <summary>保存接口地址并立即请求一次验证连通性</summary>
    private async void TestApi_Click(object sender, RoutedEventArgs e)
    {
        var url = ApiUrlBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(url))
        {
            ApiStatusText.Text = "请先填入接口地址，例如 https://你的域名/api.php/provide/vod";
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ApiStatusText.Text = "地址格式不正确，需要以 http:// 或 https:// 开头。";
            return;
        }

        var normalized = url.TrimEnd('/');

        // 走源管理更新：当前源的地址同步改掉，接口立即生效
        AppServices.Sources.SetActiveUrl(normalized);
        RefreshSourceSummary();

        ApiStatusText.Text = $"正在连接 {normalized} …";

        try
        {
            var response = await AppServices.Vod.GetListAsync(1);
            var first = response.List.Count > 0 ? response.List[0].Name : "-";
            var channel = AppServices.Vod.LastUsedFallback ? "（走 SNI 静默直连通道）" : "（走常规通道）";

            ApiStatusText.Text =
                $"连接成功{channel}：共 {response.Total} 条 / {response.PageCount} 页，首条《{first}》。地址已保存，回首页即可看到数据。";
        }
        catch (Exception ex)
        {
            ApiStatusText.Text = $"连接失败：{ex.Message}";
        }
    }
}
