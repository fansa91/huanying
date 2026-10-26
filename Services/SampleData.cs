using Huanying.Models;
using System.Collections.Generic;
using Windows.UI;

namespace Huanying.Services;

/// <summary>
/// 示例数据（接入 CMS JSON API 后可整体替换为真实数据）
/// </summary>
public static class SampleData
{
    private static Color C(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    public static List<MediaItem> Posters() => new()
    {
        MediaItem.Create("星际穿越", "2014 · 科幻 · 2小时49分", "科幻",
            C(0x1B, 0x2A, 0x4A), C(0x4A, 0x6E, 0xA8)),
        MediaItem.Create("沙丘", "2021 · 科幻 · 2小时35分", "科幻",
            C(0x8A, 0x5A, 0x2B), C(0xD9, 0xA3, 0x5C)),
        MediaItem.Create("银翼杀手 2049", "2017 · 科幻 · 2小时44分", "科幻",
            C(0x5B, 0x1F, 0x2E), C(0xB8, 0x4A, 0x3C)),
        MediaItem.Create("布达佩斯大饭店", "2014 · 喜剧 · 1小时39分", "喜剧",
            C(0xC9, 0x6E, 0x8A), C(0xF2, 0xB8, 0xC4)),
        MediaItem.Create("荒野猎人", "2015 · 冒险 · 2小时36分", "冒险",
            C(0x2E, 0x3B, 0x36), C(0x6E, 0x86, 0x74)),
        MediaItem.Create("爱乐之城", "2016 · 歌舞 · 2小时08分", "歌舞",
            C(0x2A, 0x2E, 0x5C), C(0x8E, 0x6B, 0xB0)),
        MediaItem.Create("降临", "2016 · 科幻 · 1小时56分", "科幻",
            C(0x3A, 0x4A, 0x50), C(0x7D, 0x99, 0x9E)),
        MediaItem.Create("敦刻尔克", "2017 · 战争 · 1小时46分", "战争",
            C(0x3D, 0x4C, 0x5A), C(0x9A, 0xA8, 0xB0)),
        MediaItem.Create("小丑", "2019 · 剧情 · 2小时02分", "剧情",
            C(0x2B, 0x1E, 0x1E), C(0x8A, 0x2B, 0x2B)),
        MediaItem.Create("寄生虫", "2019 · 剧情 · 2小时12分", "剧情",
            C(0x1F, 0x2B, 0x24), C(0x5C, 0x7A, 0x63)),
    };

    public static List<MediaItem> Favorites() => new()
    {
        MediaItem.Create("星际穿越", "2014 · 科幻", "科幻",
            C(0x1B, 0x2A, 0x4A), C(0x4A, 0x6E, 0xA8)),
        MediaItem.Create("爱乐之城", "2016 · 歌舞", "歌舞",
            C(0x2A, 0x2E, 0x5C), C(0x8E, 0x6B, 0xB0)),
        MediaItem.Create("布达佩斯大饭店", "2014 · 喜剧", "喜剧",
            C(0xC9, 0x6E, 0x8A), C(0xF2, 0xB8, 0xC4)),
        MediaItem.Create("小丑", "2019 · 剧情", "剧情",
            C(0x2B, 0x1E, 0x1E), C(0x8A, 0x2B, 0x2B)),
    };

    public static List<MediaItem> History() => new()
    {
        MediaItem.Create("沙丘", "第 1 季 · 第 3 集 · 已看 42 分钟", "科幻",
            C(0x8A, 0x5A, 0x2B), C(0xD9, 0xA3, 0x5C),
            58, "今天 19:24"),
        MediaItem.Create("银翼杀手 2049", "正片 · 已看 1 小时 12 分", "科幻",
            C(0x5B, 0x1F, 0x2E), C(0xB8, 0x4A, 0x3C),
            45, "今天 14:05"),
        MediaItem.Create("荒野猎人", "正片 · 已看 26 分钟", "冒险",
            C(0x2E, 0x3B, 0x36), C(0x6E, 0x86, 0x74),
            18, "昨天 22:41"),
        MediaItem.Create("降临", "正片 · 已看 1 小时 40 分", "科幻",
            C(0x3A, 0x4A, 0x50), C(0x7D, 0x99, 0x9E),
            86, "10月24日"),
        MediaItem.Create("寄生虫", "正片 · 已看完", "剧情",
            C(0x1F, 0x2B, 0x24), C(0x5C, 0x7A, 0x63),
            100, "10月21日"),
    };
}
