using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;

namespace Huanying.Views;

public sealed partial class ShellPage : Page
{
    /// <summary>播放器全屏时把整个外壳（侧边栏 + 页头）收掉</summary>
    private bool _chromeHidden;
    private object? _savedHeader;

    public ShellPage()
    {
        this.InitializeComponent();

        // 默认进入首页
        ContentFrame.Navigate(typeof(HomePage), null, new SuppressNavigationTransitionInfo());
        NavView.SelectedItem = HomeItem;

        // 页面内部跳转（例如设置 → 源管理）时同步侧边栏选中项
        ContentFrame.Navigated += (_, e) => SyncSelection(e.SourcePageType);

        if (App.MainWindow != null)
        {
            App.MainWindow.SizeChanged += OnWindowSizeChanged;
        }
    }

    private void SyncSelection(Type? pageType)
    {
        NavigationViewItem? item = pageType switch
        {
            _ when pageType == typeof(HomePage) => HomeItem,
            _ when pageType == typeof(FavoritesPage) => FavoriteItem,
            _ when pageType == typeof(HistoryPage) => HistoryItem,
            _ when pageType == typeof(SourceListPage) => SourceItem,
            _ when pageType == typeof(SettingsPage) => SettingsItem,
            _ => null
        };

        if (item != null && !ReferenceEquals(NavView.SelectedItem, item))
        {
            NavView.SelectedItem = item;
        }
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer != null)
        {
            NavigateToPage(args.InvokedItemContainer.Tag?.ToString());
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer != null)
        {
            NavigateToPage(args.SelectedItemContainer.Tag?.ToString());
        }
    }

    private void NavigateToPage(string? tag)
    {
        Type? pageType = tag switch
        {
            "HomePage" => typeof(HomePage),
            "FavoritesPage" => typeof(FavoritesPage),
            "HistoryPage" => typeof(HistoryPage),
            "SettingsPage" => typeof(SettingsPage),
            "SourceListPage" => typeof(SourceListPage),
            _ => typeof(HomePage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, null, new DrillInNavigationTransitionInfo());
        }

        // 窄窗口下点击后自动收起侧边栏（macOS 紧凑模式行为）
        if (App.MainWindow != null && App.MainWindow.Bounds.Width < 800)
        {
            NavView.IsPaneOpen = false;
        }
    }

    private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        // 全屏播放时外壳是收起的，别让窗口尺寸变化把侧边栏又打开
        if (_chromeHidden) return;

        ApplyResponsiveLayout(args.Size.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (width < 700)
        {
            NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact;
            NavView.IsPaneOpen = false;
        }
        else if (width < 1000)
        {
            NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
            NavView.IsPaneOpen = false;
        }
        else
        {
            NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
            NavView.IsPaneOpen = true;
        }
    }

    /// <summary>
    /// 播放器全屏 / 退出全屏时调用：false 收起侧边栏与页头，让画面真正铺满窗口。
    /// </summary>
    internal void SetChromeVisible(bool visible)
    {
        if (visible == !_chromeHidden) return;

        _chromeHidden = !visible;

        NavView.IsPaneVisible = visible;
        NavView.IsPaneToggleButtonVisible = visible;

        if (visible)
        {
            if (NavView.Header == null && _savedHeader != null)
            {
                NavView.Header = _savedHeader;
                _savedHeader = null;
            }

            ApplyResponsiveLayout(App.MainWindow?.Bounds.Width ?? 1200);
        }
        else
        {
            NavView.IsPaneOpen = false;

            // 页头（"Huanying" 标题条）也会占一条高度，一并收掉
            if (NavView.Header != null)
            {
                _savedHeader = NavView.Header;
                NavView.Header = null;
            }
        }
    }

    /// <summary>从任意页面收起 / 还原外壳（播放页全屏用）</summary>
    internal static void SetShellChrome(bool visible)
    {
        if (App.MainWindow?.Content is Frame root && root.Content is ShellPage shell)
        {
            shell.SetChromeVisible(visible);
        }
    }

    /// <summary>诊断用：外壳当前是否处于收起状态</summary>
    internal bool IsChromeHidden => _chromeHidden;

    /// <summary>诊断用：外壳当前是否处于收起状态</summary>
    internal static bool ShellChromeHidden =>
        App.MainWindow?.Content is Frame root && root.Content is ShellPage shell && shell._chromeHidden;

    /// <summary>诊断用：把当前内嵌页面的状态串出来（HY_SHOT 外壳截图时打印）</summary>
    internal string DebugCurrentPage() => ContentFrame.Content switch
    {
        HomePage home => home.DebugState(),
        SourceListPage sources => sources.DebugState(),
        _ => string.Empty
    };

    /// <summary>诊断用：在外壳里导航（保留侧边栏），供 HY_NAV=shell:&lt;page&gt; 截图</summary>
    internal void NavigateTo(Type pageType, object? parameter = null)
        => ContentFrame.Navigate(pageType, parameter);

    /// <summary>诊断用：内容区当前页面</summary>
    internal object? CurrentContent => ContentFrame.Content;
}
