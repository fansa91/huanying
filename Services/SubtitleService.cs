using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Huanying.Services;

/// <summary>一条字幕（起止时间以秒计）</summary>
public sealed class SubtitleCue
{
    public double StartSeconds { get; init; }

    public double EndSeconds { get; init; }

    public string Text { get; init; } = string.Empty;

    /// <summary>该时刻是否处于这条字幕的显示区间</summary>
    public bool Contains(double seconds) => seconds >= StartSeconds && seconds < EndSeconds;
}

/// <summary>
/// 字幕文件解析与查询。
///
/// 支持两种常见格式：
///   • SRT：<c>1\n00:00:01,000 --> 00:00:04,000\n文本</c>
///   • VTT：<c>WEBVTT\n\n00:00:01.000 --> 00:00:04.000\n文本</c>
///
/// 解析器对格式比较宽容：时间码用逗号或小数点分隔都行，序号行可有可无。
/// </summary>
public sealed class SubtitleTrack
{
    private readonly List<SubtitleCue> _cues;

    /// <summary>字幕文件名（显示用）</summary>
    public string Name { get; }

    /// <summary>字幕条数</summary>
    public int Count => _cues.Count;

    /// <summary>字幕总时长（最后一条的结束时间）</summary>
    public double Duration => _cues.Count > 0 ? _cues[^1].EndSeconds : 0;

    public SubtitleTrack(string name, IEnumerable<SubtitleCue> cues)
    {
        Name = name;
        _cues = cues.OrderBy(c => c.StartSeconds).ToList();
    }

    /// <summary>
    /// 取当前时间应该显示的字幕文本，没有则返回空串。
    /// 字幕条数不多时从头扫一遍即可（每条只是两个 double 比较）。
    /// </summary>
    public string TextAt(double seconds)
    {
        if (_cues.Count == 0) return string.Empty;

        // 从后往前找：播放位置单调递增，最后一条命中的概率最高
        for (var i = _cues.Count - 1; i >= 0; i--)
        {
            var cue = _cues[i];

            if (cue.StartSeconds > seconds) continue;   // 还没开始
            if (cue.Contains(seconds)) return cue.Text;   // 正在显示

            // 已经结束且不重叠，后面的更早，不必再找
            if (cue.EndSeconds <= seconds) break;
        }

        return string.Empty;
    }

    /// <summary>取所有字幕文本（供导出 / 诊断用）</summary>
    public IReadOnlyList<SubtitleCue> Cues => _cues;
}

/// <summary>字幕解析器</summary>
public static class SubtitleParser
{
    /// <summary>时间码：00:00:01,000 / 00:00:01.000 / 00:01.000</summary>
    private static readonly Regex TimePattern = new(
        @"(?<h>\d{1,3}):(?<m>\d{1,2}):(?<s>\d{1,2})[,.](?<ms>\d{1,3})",
        RegexOptions.Compiled);

    /// <summary>短时间码：00:01.000（VTT 里常见）</summary>
    private static readonly Regex ShortTimePattern = new(
        @"(?<m>\d{1,3}):(?<s>\d{1,2})[,.](?<ms>\d{1,3})",
        RegexOptions.Compiled);

    /// <summary>行内标签：<i>、{\an8} 等，清掉只留纯文本</summary>
    private static readonly Regex InlineTags = new(
        @"<[^>]+>|\{\\[^}]*\}",
        RegexOptions.Compiled);

    /// <summary>解析字幕文本</summary>
    /// <param name="content">字幕文件内容</param>
    /// <param name="name">字幕名（显示用）</param>
    public static SubtitleTrack Parse(string? content, string name = "字幕")
    {
        var cues = new List<SubtitleCue>();

        if (string.IsNullOrWhiteSpace(content))
            return new SubtitleTrack(name, cues);

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index].Trim();

            // 跳过 WEBVTT 头、NOTE、STYLE 等元数据块
            if (line.Length == 0 || line.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("REGION", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            // 序号行（纯数字）可以跳过
            if (line.All(char.IsDigit) && line.Length > 0)
            {
                index++;
                if (index >= lines.Length) break;
                line = lines[index].Trim();
            }

            // 找时间码行
            if (!line.Contains("-->", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            var parts = line.Split("-->", StringSplitOptions.None);
            var start = ParseTime(parts[0]);
            var end = ParseTime(parts.Length > 1 ? parts[1] : string.Empty);

            if (start < 0 || end <= start)
            {
                index++;
                continue;
            }

            // 收集文本行，直到空行
            index++;
            var textLines = new List<string>();

            while (index < lines.Length)
            {
                var textLine = lines[index].TrimEnd();
                if (textLine.Trim().Length == 0) break;

                textLines.Add(textLine);
                index++;
            }

            var text = CleanText(string.Join("\n", textLines));
            if (text.Length > 0)
            {
                cues.Add(new SubtitleCue
                {
                    StartSeconds = start,
                    EndSeconds = end,
                    Text = text
                });
            }
        }

        return new SubtitleTrack(name, cues);
    }

    /// <summary>从文件读取并解析</summary>
    public static SubtitleTrack ParseFile(string path)
    {
        var content = System.IO.File.ReadAllText(path, Encoding.UTF8);
        return Parse(content, System.IO.Path.GetFileName(path));
    }

    /// <summary>解析时间码，返回秒数；解析不出来返回 -1</summary>
    private static double ParseTime(string text)
    {
        text = text.Trim();

        var match = TimePattern.Match(text);
        if (match.Success)
        {
            var hours = double.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minutes = double.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
            var seconds = double.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
            var ms = NormalizeMs(match.Groups["ms"].Value);

            return hours * 3600 + minutes * 60 + seconds + ms;
        }

        var shortMatch = ShortTimePattern.Match(text);
        if (shortMatch.Success)
        {
            var minutes = double.Parse(shortMatch.Groups["m"].Value, CultureInfo.InvariantCulture);
            var seconds = double.Parse(shortMatch.Groups["s"].Value, CultureInfo.InvariantCulture);
            var ms = NormalizeMs(shortMatch.Groups["ms"].Value);

            return minutes * 60 + seconds + ms;
        }

        return -1;
    }

    /// <summary>毫秒位补齐到 3 位（.5 表示 500 毫秒）</summary>
    private static double NormalizeMs(string value) =>
        int.Parse(value.PadRight(3, '0'), CultureInfo.InvariantCulture) / 1000.0;

    /// <summary>清掉样式标签，保留换行</summary>
    private static string CleanText(string text)
    {
        if (text.Length == 0) return string.Empty;

        var cleaned = InlineTags.Replace(text, string.Empty);
        cleaned = cleaned.Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ");

        return cleaned.Trim();
    }
}
