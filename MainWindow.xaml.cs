using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Huanying;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();

        ApplyIcon();

        // 导航到外壳页
        var rootFrame = new Frame();
        rootFrame.Navigate(typeof(Views.ShellPage));
        this.Content = rootFrame;
    }

    /// <summary>
    /// 非打包应用没有 MSIX 清单，标题栏 / 任务栏图标需要手动指到 Assets 里的 ico。
    /// </summary>
    private void ApplyIcon()
    {
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "Huanying.ico");
            if (File.Exists(ico)) AppWindow.SetIcon(ico);
        }
        catch
        {
            // 图标缺失不影响启动
        }
    }
}
