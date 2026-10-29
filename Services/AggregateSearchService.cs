using Huanying.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Huanying.Services;

/// <summary>聚合搜索命中的一条结果：影片内容 + 来自哪个源</summary>
public sealed class AggregateSearchHit
{
    /// <summary>影片数据</summary>
    public VodItem Item { get; init; } = new();

    /// <summary>来源源的名称</summary>
    public string SourceName { get; init; } = string.Empty;

    /// <summary>来源源的接口地址（点进去要按这个地址查详情）</summary>
    public string SourceUrl { get; init; } = string.Empty;

    /// <summary>来源源的 Id</summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>命中该片的源名列表（同一影片在多个源都有时列出全部）</summary>
    public List<string> SourceNames { get; init; } = new();

    /// <summary>界面上显示的来源标注（多源命中时压缩成「源A 等3源」，避免角标过长）</summary>
    public string SourceLabel =>
        SourceNames.Count > 1 ? $"{SourceNames[0]} 等{SourceNames.Count}源" : SourceName;

    /// <summary>完整来源说明（鼠标悬停提示用）</summary>
    public string SourceDetail => SourceNames.Count > 0 ? string.Join("、", SourceNames) : SourceName;
}

/// <summary>单个源的搜索结果</summary>
public sealed class AggregateSourceResult
{
    public string SourceId { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public string SourceUrl { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? Error { get; init; }
    public List<VodItem> Items { get; init; } = new();
}

/// <summary>一次聚合搜索的完整结果</summary>
public sealed class AggregateSearchResult
{
    /// <summary>合并去重后的命中</summary>
    public List<AggregateSearchHit> Hits { get; init; } = new();

    /// <summary>每个源的搜索情况（含失败的源，便于提示）</summary>
    public List<AggregateSourceResult> SourceResults { get; init; } = new();

    /// <summary>参与搜索的源数量</summary>
    public int SourceCount => SourceResults.Count;

    /// <summary>成功返回的源数量</summary>
    public int SuccessCount => SourceResults.Count(r => r.Success);

    /// <summary>失败返回的源数量</summary>
    public int FailedCount => SourceResults.Count(r => !r.Success);

    /// <summary>合并前的原始条目数</summary>
    public int RawCount => SourceResults.Sum(r => r.Items.Count);
}

/// <summary>
/// 多源聚合搜索：同时向多个视频源发关键词查询，把结果合并去重后返回。
///
/// 设计要点：
///   • 每个源用独立的 <see cref="VodApiService"/> 实例，互不影响（也不污染当前源的连接状态）
///   • 并行请求，单个源失败不影响其他源
///   • 去重键 = 片名 + 年份，同一部片在多个源出现时合并成一条，标注全部来源
/// </summary>
public sealed class AggregateSearchService
{
    private readonly AppSettings _settings;

    public AggregateSearchService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// 并行搜索多个源。
    /// </summary>
    /// <param name="keyword">关键词</param>
    /// <param name="sources">要搜索的源（为空则用当前全部源）</param>
    /// <param name="maxPerSource">每个源最多取多少条</param>
    /// <param name="ct">取消令牌</param>
    public async Task<AggregateSearchResult> SearchAsync(
        string keyword,
        IReadOnlyList<VodSource>? sources = null,
        int maxPerSource = 40,
        CancellationToken ct = default)
    {
        var targets = (sources ?? AppServices.Sources.Items)
            .Where(s => !string.IsNullOrWhiteSpace(s.Url))
            .ToList();

        if (targets.Count == 0 || string.IsNullOrWhiteSpace(keyword))
        {
            return new AggregateSearchResult();
        }

        // 并行搜索各源
        var tasks = targets.Select(source => SearchOneAsync(source, keyword, maxPerSource, ct)).ToArray();
        var results = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();

        return new AggregateSearchResult
        {
            SourceResults = results,
            Hits = Merge(results)
        };
    }

    /// <summary>搜索单个源（失败只记录，不抛出）</summary>
    private async Task<AggregateSourceResult> SearchOneAsync(
        VodSource source, string keyword, int maxPerSource, CancellationToken ct)
    {
        try
        {
            // 独立实例：不污染当前源的 BaseUrl 与 LastUsedFallback
            var probe = new VodApiService(source.Url)
            {
                AllowSniFallback = _settings.AllowSniFallback
            };

            var response = await probe.GetListAsync(1, null, keyword, ct).ConfigureAwait(false);

            return new AggregateSourceResult
            {
                SourceId = source.Id,
                SourceName = source.Name,
                SourceUrl = source.Url,
                Success = true,
                Items = response.List.Take(maxPerSource).ToList()
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AggregateSourceResult
            {
                SourceId = source.Id,
                SourceName = source.Name,
                SourceUrl = source.Url,
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>合并去重：片名 + 年份 相同视为同一部片</summary>
    private static List<AggregateSearchHit> Merge(List<AggregateSourceResult> results)
    {
        var merged = new Dictionary<string, AggregateSearchHit>(StringComparer.Ordinal);

        foreach (var sourceResult in results)
        {
            if (!sourceResult.Success) continue;

            foreach (var item in sourceResult.Items)
            {
                if (item.Id <= 0) continue;

                var key = DedupKey(item);

                if (merged.TryGetValue(key, out var existing))
                {
                    // 同一影片已在别的源命中：补上来源名
                    if (!existing.SourceNames.Contains(sourceResult.SourceName))
                    {
                        existing.SourceNames.Add(sourceResult.SourceName);
                    }
                    continue;
                }

                merged[key] = new AggregateSearchHit
                {
                    Item = item,
                    SourceId = sourceResult.SourceId,
                    SourceName = sourceResult.SourceName,
                    SourceUrl = sourceResult.SourceUrl,
                    SourceNames = new List<string> { sourceResult.SourceName }
                };
            }
        }

        // 保持各源内部的原始顺序（先出现的排前面）
        return merged.Values.ToList();
    }

    /// <summary>去重键：片名（去空格）+ 年份</summary>
    private static string DedupKey(VodItem item)
    {
        var name = NormalizeName(item.Name);
        var year = string.IsNullOrWhiteSpace(item.Year) ? string.Empty : item.Year.Trim();
        return $"{name}|{year}";
    }

    /// <summary>片名归一化：去掉空格与常见修饰符号，便于跨源比对</summary>
    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch is '（' or '）' or '(' or ')' or '[' or ']' or '【' or '】') continue;
            if (ch == ' ') continue;

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }
}
