using Huanying.Models;
using Huanying.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Huanying.Controls;

/// <summary>
/// 走队列的图片控件：所有实例共用一个加载队列（<see cref="ImageQueue"/>），
/// 只有被创建出来（进入可视区）的图片才会入队，离开时取消等待。
/// 解码完成后会把真实宽高回报给绑定的 <see cref="IPosterAspectHost"/>（瀑布流卡片）。
/// </summary>
public sealed class QueuedImage : UserControl
{
    public static readonly DependencyProperty SourceUrlProperty = DependencyProperty.Register(
        nameof(SourceUrl),
        typeof(string),
        typeof(QueuedImage),
        new PropertyMetadata(null, OnSourceUrlChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.Register(
        nameof(DecodeWidth),
        typeof(int),
        typeof(QueuedImage),
        new PropertyMetadata(320));

    private readonly Image _image = new() { Stretch = Stretch.UniformToFill, Opacity = 0 };
    private readonly FontIcon _placeholder = new()
    {
        Glyph = "\uE714",
        FontSize = 26,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.5 }
    };

    private CancellationTokenSource? _cts;
    private double _ratio;

    public QueuedImage()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        var root = new Grid();
        root.Children.Add(_placeholder);
        root.Children.Add(_image);
        Content = root;

        Unloaded += (_, _) => Cancel();
        Loaded += (_, _) =>
        {
            if (_image.Source == null && !string.IsNullOrWhiteSpace(SourceUrl)) Load();
        };
        DataContextChanged += (_, _) => ReportAspect();
    }

    /// <summary>图片地址</summary>
    public string? SourceUrl
    {
        get => (string?)GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
    }

    /// <summary>解码宽度（逻辑像素），越小越省内存</summary>
    public int DecodeWidth
    {
        get => (int)GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    private static void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is QueuedImage image) image.Load();
    }

    private void Cancel()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _cts = null;
    }

    private void Load()
    {
        Cancel();

        var url = SourceUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            _image.Source = null;
            _image.Opacity = 0;
            _placeholder.Visibility = Visibility.Visible;
            return;
        }

        _image.Source = null;
        _image.Opacity = 0;
        _placeholder.Visibility = Visibility.Visible;

        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = LoadAsync(url!, cts.Token);
    }

    private async Task LoadAsync(string url, CancellationToken ct)
    {
        var bitmap = await ImageQueue.Current.LoadAsync(url, DecodeWidth, ct);
        if (bitmap == null || ct.IsCancellationRequested) return;

        _image.Source = bitmap;
        _placeholder.Visibility = Visibility.Collapsed;

        if (bitmap.PixelWidth > 0)
        {
            _ratio = bitmap.PixelHeight / (double)bitmap.PixelWidth;
        }

        FadeIn();
        ReportAspect();
    }

    private void FadeIn()
    {
        try
        {
            var animation = new DoubleAnimation
            {
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EnableDependentAnimation = true
            };

            Storyboard.SetTarget(animation, _image);
            Storyboard.SetTargetProperty(animation, "Opacity");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
        catch
        {
            _image.Opacity = 1;
        }
    }

    /// <summary>把真实比例回报给海报卡片，让瀑布流高度贴合图片</summary>
    private void ReportAspect()
    {
        if (_ratio <= 0) return;
        if (DataContext is IPosterAspectHost host) host.AcceptImageSize(1, _ratio);
    }
}
