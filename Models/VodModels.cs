using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Huanying.Models;

/// <summary>苹果CMS / MacCMS V10 JSON 接口的通用响应</summary>
public sealed class VodApiResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("pagecount")] public int PageCount { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("list")] public List<VodItem> List { get; set; } = new();

    /// <summary>仅 ac=list 的响应会带分类表</summary>
    [JsonPropertyName("class")] public List<VodCategory>? Class { get; set; }
}

public sealed class VodCategory
{
    [JsonPropertyName("type_id")] public int TypeId { get; set; }
    [JsonPropertyName("type_pid")] public int TypePid { get; set; }
    [JsonPropertyName("type_name")] public string TypeName { get; set; } = string.Empty;
}

public sealed class VodItem
{
    [JsonPropertyName("vod_id")] public int Id { get; set; }
    [JsonPropertyName("type_id")] public int TypeId { get; set; }
    [JsonPropertyName("type_id_1")] public int TypeId1 { get; set; }
    [JsonPropertyName("type_name")] public string TypeName { get; set; } = string.Empty;

    [JsonPropertyName("vod_name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("vod_sub")] public string Sub { get; set; } = string.Empty;
    [JsonPropertyName("vod_class")] public string ClassTags { get; set; } = string.Empty;
    [JsonPropertyName("vod_pic")] public string Pic { get; set; } = string.Empty;

    [JsonPropertyName("vod_actor")] public string Actor { get; set; } = string.Empty;
    [JsonPropertyName("vod_director")] public string Director { get; set; } = string.Empty;
    [JsonPropertyName("vod_content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("vod_blurb")] public string Blurb { get; set; } = string.Empty;

    [JsonPropertyName("vod_remarks")] public string Remarks { get; set; } = string.Empty;
    [JsonPropertyName("vod_year")] public string Year { get; set; } = string.Empty;
    [JsonPropertyName("vod_area")] public string Area { get; set; } = string.Empty;
    [JsonPropertyName("vod_lang")] public string Lang { get; set; } = string.Empty;
    [JsonPropertyName("vod_duration")] public string Duration { get; set; } = string.Empty;
    [JsonPropertyName("vod_pubdate")] public string PubDate { get; set; } = string.Empty;
    [JsonPropertyName("vod_time")] public string UpdateTime { get; set; } = string.Empty;
    [JsonPropertyName("vod_score")] public string Score { get; set; } = "0.0";
    [JsonPropertyName("vod_total")] public int Total { get; set; }
    [JsonPropertyName("vod_isend")] public int IsEnd { get; set; }

    [JsonPropertyName("vod_play_from")] public string PlayFrom { get; set; } = string.Empty;
    [JsonPropertyName("vod_play_url")] public string PlayUrl { get; set; } = string.Empty;

    /* ---------- 供界面绑定的派生属性 ---------- */

    public string Title => Name;

    public string PicUrl => Pic;

    /// <summary>「2026 · 中国大陆 · 国产剧」样式的副标题</summary>
    public string MetaLine
    {
        get
        {
            var parts = new[] { Year, Area, TypeName }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            return parts.Length == 0 ? TypeName : string.Join(" · ", parts);
        }
    }

    public string RemarksText => string.IsNullOrWhiteSpace(Remarks) ? (IsEnd == 1 ? "已完结" : "更新中") : Remarks;

    public double ScoreValue =>
        double.TryParse(Score, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    public string ScoreText => ScoreValue > 0 ? ScoreValue.ToString("0.0") : string.Empty;

    public bool HasScore => ScoreValue > 0;

    /// <summary>去掉 HTML 标签后的简介</summary>
    public string PlainContent
    {
        get
        {
            var text = Regex.Replace(Content ?? string.Empty, "<[^>]+>", " ");
            text = Regex.Replace(text, "&nbsp;?", " ");
            text = Regex.Replace(text, "[ \\t\\r\\n]+", " ").Trim();
            if (text.Length == 0) text = Regex.Replace(Blurb ?? string.Empty, "[ \\t\\r\\n]+", " ").Trim();
            return text;
        }
    }

    public string ActorText => string.IsNullOrWhiteSpace(Actor) ? "—" : Actor;
    public string DirectorText => string.IsNullOrWhiteSpace(Director) ? "—" : Director;

    private List<VodPlaySource>? _sources;

    /// <summary>解析后的播放线路（首次访问时解析并缓存）</summary>
    public List<VodPlaySource> Sources => _sources ??= VodParser.ParseSources(PlayFrom, PlayUrl);

    public bool HasPlaySource => Sources.Count > 0;

    public int EpisodeCount => Sources.Sum(s => s.Episodes.Count);
}

public sealed class VodPlaySource
{
    public string Name { get; set; } = string.Empty;
    public List<VodEpisode> Episodes { get; set; } = new();

    /// <summary>界面上显示「线路名 (N)」</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? "默认线路" : $"{Name} ({Episodes.Count})";
}

public sealed class VodEpisode
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

/// <summary>播放页参数</summary>
public sealed class PlayerArgs
{
    public int VodId { get; set; }

    /// <summary>影片名（写历史用，不含集名）</summary>
    public string VodName { get; set; } = string.Empty;

    /// <summary>顶部标题（影片名 + 集名）</summary>
    public string Title { get; set; } = string.Empty;

    public string PosterUrl { get; set; } = string.Empty;

    public List<VodEpisode> Episodes { get; set; } = new();

    /// <summary>起始集下标，从 0 开始</summary>
    public int StartIndex { get; set; }

    /// <summary>指定续播秒数（从历史页进入时使用），0 表示按本地记录自动续播</summary>
    public double ResumeSeconds { get; set; }
}

/// <summary>详情页参数：从聚合搜索进来时带上来源源</summary>
public sealed class DetailArgs
{
    public int VodId { get; set; }

    /// <summary>来源源的接口地址，空串表示用当前激活的源</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>来源源的名称（界面显示用）</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>兼容旧的只传 int 的导航参数</summary>
    public static DetailArgs From(object? parameter)
    {
        return parameter switch
        {
            DetailArgs args => args,
            int id => new DetailArgs { VodId = id },
            _ => new DetailArgs()
        };
    }
}

/// <summary>vod_play_from / vod_play_url 的解析器（$$$ 分线路，# 分集，$ 分名称与地址）</summary>
public static class VodParser
{
    public static List<VodPlaySource> ParseSources(string? playFrom, string? playUrl)
    {
        var result = new List<VodPlaySource>();
        if (string.IsNullOrWhiteSpace(playUrl)) return result;

        var names = SplitSources(playFrom);
        var blocks = playUrl.Split(new[] { "$$$" }, StringSplitOptions.None);

        for (int i = 0; i < blocks.Length; i++)
        {
            var episodes = ParseEpisodes(blocks[i]);
            if (episodes.Count == 0) continue;

            var name = i < names.Count && !string.IsNullOrWhiteSpace(names[i])
                ? names[i]
                : $"线路{i + 1}";

            result.Add(new VodPlaySource { Name = name, Episodes = episodes });
        }

        return result;
    }

    private static List<string> SplitSources(string? playFrom)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(playFrom)) return list;

        if (playFrom.Contains("$$$", StringComparison.Ordinal))
        {
            list.AddRange(playFrom.Split(new[] { "$$$" }, StringSplitOptions.None).Select(s => s.Trim()));
        }
        else if (playFrom.Contains(',', StringComparison.Ordinal))
        {
            // 部分接口的 ac=list 摘要用逗号连接线路名
            list.AddRange(playFrom.Split(',').Select(s => s.Trim()));
        }
        else
        {
            list.Add(playFrom.Trim());
        }

        return list;
    }

    private static List<VodEpisode> ParseEpisodes(string? block)
    {
        var episodes = new List<VodEpisode>();
        if (string.IsNullOrWhiteSpace(block)) return episodes;

        foreach (var raw in block.Split('#'))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;

            string name;
            string url;
            var sep = entry.IndexOf('$');

            if (sep > 0 && sep < entry.Length - 1)
            {
                name = entry[..sep].Trim();
                url = entry[(sep + 1)..].Trim();
            }
            else if (sep == 0)
            {
                name = string.Empty;
                url = entry[1..].Trim();
            }
            else
            {
                name = string.Empty;
                url = entry;
            }

            if (url.Length == 0) continue;

            var index = episodes.Count + 1;
            if (string.IsNullOrWhiteSpace(name)) name = $"第{index:00}集";

            episodes.Add(new VodEpisode { Index = index, Name = name, Url = url });
        }

        return episodes;
    }
}
