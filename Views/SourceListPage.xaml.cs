using Huanying.Models;
using Huanying.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace Huanying.Views;

/// <summary>
/// 源管理：查看 / 添加 / 编辑 / 删除 / 测试多个 CMS 接口，并选择当前使用哪一个。
/// 切换后 <see cref="AppServices.Vod"/> 的 BaseUrl 立即改变，首页返回时会自动重新加载。
/// </summary>
public sealed partial class SourceListPage : Page
{
    public SourceListPage()
    {
        this.InitializeComponent();

        SourceItems.ItemsSource = AppServices.Sources.Items;

        this.Loaded += (_, _) => RefreshHeader();
    }

    /* ================= 展示 ================= */

    private void RefreshHeader()
    {
        var active = AppServices.Sources.Active;

        ActiveSourceText.Text = active == null ? "当前没有可用源" : $"当前源：{active.Name}";
        ActiveUrlText.Text = active?.Url ?? "点「添加源」新增一个接口地址";

        EmptyHint.Visibility = AppServices.Sources.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    /* ================= 选择源 ================= */

    private void Source_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VodSource source) Activate(source);
    }

    private void UseSource_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSource(sender) is { } source) Activate(source);
    }

    private void Activate(VodSource source)
    {
        if (!AppServices.Sources.SetActive(source)) return;

        RefreshHeader();
        ShowNotice("已切换", $"当前使用「{source.Name}」，回首页即可看到该源的数据。");
    }

    /* ================= 增 / 改 / 删 ================= */

    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        var input = await ShowEditDialogAsync("添加视频源", string.Empty, string.Empty, string.Empty);
        if (input == null) return;

        var source = AppServices.Sources.Add(input.Value.Name, input.Value.Url, input.Value.Note);
        RefreshHeader();
        ShowNotice("已添加", $"「{source.Name}」已添加并设为当前源。");
    }

    private async void EditSource_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSource(sender) is not { } source) return;

        var input = await ShowEditDialogAsync($"编辑「{source.Name}」", source.Name, source.Url, source.Note);
        if (input == null) return;

        AppServices.Sources.Update(source, input.Value.Name, input.Value.Url, input.Value.Note);
        RefreshHeader();
        ShowNotice("已保存", "源信息已更新。" + (source.IsActive ? "当前源地址已立即生效。" : string.Empty));
    }

    private async void DeleteSource_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSource(sender) is not { } source) return;

        if (source.BuiltIn)
        {
            ShowNotice("内置源不可删除", "内置源只允许编辑地址；如不需要可以添加其他源并切换过去。",
                InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除视频源",
            Content = $"确定删除「{source.Name}」？\n{source.Url}",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        AppServices.Sources.Remove(source);
        RefreshHeader();
        ShowNotice("已删除", $"「{source.Name}」已从列表移除。", InfoBarSeverity.Informational);
    }

    private void CopySource_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSource(sender) is not { } source) return;

        try
        {
            var package = new DataPackage();
            package.SetText(source.Url);
            Clipboard.SetContent(package);
            ShowNotice("已复制", source.Url, InfoBarSeverity.Informational);
        }
        catch
        {
            // 剪贴板偶发占用失败，忽略
        }
    }

    /* ================= 测试 ================= */

    private async void TestOne_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSource(sender) is not { } source) return;

        source.StatusText = "测试中…";
        source.StatusText = await AppServices.Sources.TestAsync(source);

        Notice.IsOpen = false;
    }

    private async void TestAll_Click(object sender, RoutedEventArgs e)
    {
        var items = AppServices.Sources.Items;
        if (items.Count == 0) return;

        for (var i = 0; i < items.Count; i++)
        {
            var source = items[i];
            source.StatusText = "测试中…";
            source.StatusText = await AppServices.Sources.TestAsync(source);

            ShowNotice("正在测试", $"已完成 {i + 1}/{items.Count}", InfoBarSeverity.Informational);
        }

        ShowNotice("测试完成", "每个源的结果显示在卡片下方。", InfoBarSeverity.Informational);
    }

    /* ================= 编辑对话框 ================= */

    private async Task<(string Name, string Url, string Note)?> ShowEditDialogAsync(
        string title, string name, string url, string note)
    {
        var nameBox = new TextBox
        {
            Header = "名称",
            Text = name,
            PlaceholderText = "例如：XX 资源站"
        };

        var urlBox = new TextBox
        {
            Header = "接口地址",
            Text = url,
            PlaceholderText = "https://example.com/api.php/provide/vod"
        };

        var noteBox = new TextBox
        {
            Header = "备注（可选）",
            Text = note,
            PlaceholderText = "例如：速度较快 / 需要 SNI 回退"
        };

        var error = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            Visibility = Visibility.Collapsed
        };

        var panel = new StackPanel { Spacing = 12, Width = 440 };
        panel.Children.Add(nameBox);
        panel.Children.Add(urlBox);
        panel.Children.Add(noteBox);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        while (true)
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

            var text = urlBox.Text?.Trim() ?? string.Empty;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                error.Text = "接口地址需要以 http:// 或 https:// 开头，例如 https://example.com/api.php/provide/vod";
                error.Visibility = Visibility.Visible;
                continue;
            }

            return (nameBox.Text?.Trim() ?? string.Empty, text, noteBox.Text?.Trim() ?? string.Empty);
        }
    }

    private static VodSource? ResolveSource(object sender)
    {
        if (sender is FrameworkElement element)
        {
            if (element.Tag is VodSource tagged) return tagged;
            if (element.DataContext is VodSource bound) return bound;
        }

        return null;
    }

    /// <summary>诊断用：供 HY_NAV / HY_SMOKE 读取源数量</summary>
    internal string DebugState() =>
        $"sources={AppServices.Sources.Items.Count} active='{AppServices.Sources.ActiveName}' " +
        $"url={AppServices.Vod.BaseUrl}";
}
