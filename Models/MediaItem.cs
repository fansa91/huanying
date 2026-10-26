using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Huanying.Models;

/// <summary>
/// 节目卡片模型（首页网格 / 收藏 / 历史共用）
/// </summary>
public class MediaItem
{
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    /// <summary>观看进度 0~100，0 表示未观看</summary>
    public double Progress { get; set; }

    public string LastWatched { get; set; } = string.Empty;

    /// <summary>海报占位渐变（真实项目里换成网络图片）</summary>
    public Brush PosterBrush { get; set; } = new SolidColorBrush(Colors.DimGray);

    public static MediaItem Create(
        string title, string subtitle, string category,
        Color from, Color to,
        double progress = 0, string lastWatched = "")
    {
        return new MediaItem
        {
            Title = title,
            Subtitle = subtitle,
            Category = category,
            Progress = progress,
            LastWatched = lastWatched,
            PosterBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop { Color = from, Offset = 0 },
                    new GradientStop { Color = to, Offset = 1 }
                }
            }
        };
    }
}
