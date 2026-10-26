using System;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Huanying.Models;

/// <summary>收藏条目（持久化到 library.json）</summary>
public sealed class FavoriteEntry
{
    public int VodId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Pic { get; set; } = string.Empty;
    public string Remarks { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.Now;

    [JsonIgnore] public string Title => Name;

    [JsonIgnore] public string Subtitle => string.IsNullOrWhiteSpace(Remarks) ? "已收藏" : Remarks;

    [JsonIgnore] public Brush PosterBrush => PosterPalette.FromSeed(VodId);
}

/// <summary>观看历史条目：记录看到第几集、看到第几秒</summary>
public sealed class HistoryEntry
{
    public int VodId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Pic { get; set; } = string.Empty;

    /// <summary>集序号，从 1 开始</summary>
    public int EpisodeIndex { get; set; } = 1;

    public string EpisodeName { get; set; } = string.Empty;

    /// <summary>已观看秒数</summary>
    public double Position { get; set; }

    /// <summary>该集总时长（秒），未知时为 0</summary>
    public double Duration { get; set; }

    public DateTime WatchedAt { get; set; } = DateTime.Now;

    [JsonIgnore] public string Title => Name;

    [JsonIgnore]
    public double Progress =>
        Duration > 0 ? Math.Clamp(Position / Duration * 100, 0, 100) : 0;

    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var episode = string.IsNullOrWhiteSpace(EpisodeName) ? $"第 {EpisodeIndex} 集" : EpisodeName;
            var time = Position > 0 ? $" · {FormatClock(Position)}" : string.Empty;
            return Progress > 0 ? $"{episode}{time} · 已看 {Progress:0}%" : $"{episode}{time}";
        }
    }

    [JsonIgnore]
    public string LastWatched
    {
        get
        {
            var span = DateTime.Now - WatchedAt;
            if (span.TotalMinutes < 1) return "刚刚观看";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
            if (WatchedAt.Date == DateTime.Today) return $"今天 {WatchedAt:HH:mm}";
            if (WatchedAt.Date == DateTime.Today.AddDays(-1)) return $"昨天 {WatchedAt:HH:mm}";
            return WatchedAt.ToString("MM-dd HH:mm");
        }
    }

    [JsonIgnore] public Brush PosterBrush => PosterPalette.FromSeed(VodId);

    [JsonIgnore]
    public string ResumeText => Position > 0 ? $"从 {FormatClock(Position)} 继续" : "从头播放";

    private static string FormatClock(double seconds)
    {
        var total = (int)Math.Max(0, seconds);
        return total >= 3600
            ? $"{total / 3600}:{total % 3600 / 60:00}:{total % 60:00}"
            : $"{total / 60:00}:{total % 60:00}";
    }
}

/// <summary>海报占位渐变：按 vodId 稳定取色，避免同屏卡片颜色重复</summary>
public static class PosterPalette
{
    private static readonly Color[] Colors =
    {
        Color.FromArgb(255, 62, 84, 138),
        Color.FromArgb(255, 122, 62, 96),
        Color.FromArgb(255, 52, 108, 104),
        Color.FromArgb(255, 118, 88, 52),
        Color.FromArgb(255, 78, 66, 128),
        Color.FromArgb(255, 48, 92, 124),
        Color.FromArgb(255, 110, 72, 64),
        Color.FromArgb(255, 66, 100, 76),
    };

    public static Brush FromSeed(int seed)
    {
        var index = Math.Abs(seed) % Colors.Length;
        var next = (index + 3) % Colors.Length;

        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop { Color = Colors[index], Offset = 0 },
                new GradientStop { Color = Colors[next], Offset = 1 }
            }
        };
    }
}
