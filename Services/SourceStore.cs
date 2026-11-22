using Huanying.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Huanying.Services;

/// <summary>
/// 视频源管理：维护源列表与「当前使用中的源」，并负责把选择结果同步给
/// <see cref="VodApiService"/>（切换后接口地址立即生效）与 settings.json。
/// </summary>
public sealed class SourceStore
{
    private readonly AppSettings _settings;

    public SourceStore(AppSettings settings)
    {
        _settings = settings;

        foreach (var source in settings.Sources.Where(s => !string.IsNullOrWhiteSpace(s.Url)))
        {
            Items.Add(source);
        }

        // 注意：这里不预置任何源。本项目不内置接口地址，首次启动源列表就是空的，
        // 由用户自己在「源管理」里添加。

        // 迁移：老版本只存了 VodBaseUrl，按地址找回对应源；都没有就用第一条
        var active = Items.FirstOrDefault(s => s.Id == settings.ActiveSourceId)
                     ?? Items.FirstOrDefault(s => SameUrl(s.Url, settings.VodBaseUrl))
                     ?? Items.FirstOrDefault();

        if (active != null)
        {
            SetActiveInternal(active);
        }
        else
        {
            // 一个源都没有：接口地址必须清空，避免残留上一次的值
            AppServices.Vod.BaseUrl = string.Empty;
            settings.VodBaseUrl = string.Empty;
            Persist();
        }
    }

    /// <summary>源列表（可直接绑定到界面）</summary>
    public ObservableCollection<VodSource> Items { get; } = new();

    /// <summary>当前使用中的源</summary>
    public VodSource? Active => Items.FirstOrDefault(s => s.IsActive);

    /// <summary>当前源名（无源时回退显示主机名）</summary>
    public string ActiveName => Active?.Name ?? "未选择";

    /// <summary>切换源之后触发（页面据此刷新数据）</summary>
    public event EventHandler? ActiveChanged;

    /* ================= 增删改 ================= */

    public VodSource Add(string name, string url, string note = "", bool makeActive = true)
    {
        var source = new VodSource
        {
            Name = string.IsNullOrWhiteSpace(name) ? HostOf(url) : name.Trim(),
            Url = Normalize(url),
            Note = note?.Trim() ?? string.Empty
        };

        Items.Add(source);
        if (makeActive || Active == null) SetActiveInternal(source);
        else Persist();

        return source;
    }

    /// <summary>
    /// 订阅导入：把订阅里的源并入列表，同地址的跳过。
    /// 列表原本是空的，第一个导入的源自动成为当前源。
    /// </summary>
    /// <returns>新增数量、跳过（已存在）数量</returns>
    public (int Added, int Skipped) Import(IEnumerable<SubscriptionEntry> entries, string subscriptionUrl)
        => ImportCore(entries, subscriptionUrl, fromSubscription: true);

    /// <summary>
    /// 从本地文件（JSON / 纯文本）导入源。与订阅导入的区别：不关联订阅链接，
    /// 所以之后这些源不会出现在「更新订阅」里，备注统一写成导入来源。
    /// </summary>
    /// <param name="entries">解析出来的源条目</param>
    /// <param name="originLabel">导入来源描述，如「sources.example.json」</param>
    public (int Added, int Skipped) ImportFromFile(IEnumerable<SubscriptionEntry> entries, string originLabel)
        => ImportCore(entries, originLabel, fromSubscription: false);

    private (int Added, int Skipped) ImportCore(
        IEnumerable<SubscriptionEntry> entries, string origin, bool fromSubscription)
    {
        var added = 0;
        var skipped = 0;
        var originHost = HostOf(origin);
        var hadActive = Active != null;

        foreach (var entry in entries)
        {
            var existing = Items.FirstOrDefault(s => SameUrl(s.Url, entry.Url));
            if (existing != null)
            {
                // 手动加过的同地址源：顺手补上订阅来源，之后也能跟着订阅更新
                if (fromSubscription && string.IsNullOrEmpty(existing.SubUrl)) existing.SubUrl = origin;
                skipped++;
                continue;
            }

            Items.Add(new VodSource
            {
                Name = string.IsNullOrWhiteSpace(entry.Name) ? HostOf(entry.Url) : entry.Name.Trim(),
                Url = Normalize(entry.Url),
                Note = string.IsNullOrWhiteSpace(entry.Note)
                    ? (fromSubscription ? $"订阅：{originHost}" : $"来自 {origin}")
                    : entry.Note.Trim(),
                SubUrl = fromSubscription ? origin : string.Empty
            });

            added++;
        }

        if (added > 0 && !hadActive) SetActiveInternal(Items[0]);
        else Persist();

        RaiseActiveChanged();
        return (added, skipped);
    }

    public void Update(VodSource source, string name, string url, string note)
    {
        source.Name = string.IsNullOrWhiteSpace(name) ? HostOf(url) : name.Trim();
        source.Url = Normalize(url);
        source.Note = note?.Trim() ?? string.Empty;

        // 改的是当前源 → 立即换接口地址
        if (source.IsActive) Apply(source);

        Persist();
        RaiseActiveChanged();
    }

    public void Remove(VodSource source)
    {
        if (source.BuiltIn) return;

        var wasActive = source.IsActive;
        Items.Remove(source);

        if (wasActive && Items.Count > 0) SetActiveInternal(Items[0]);
        else Persist();

        RaiseActiveChanged();
    }

    public bool SetActive(VodSource source)
    {
        if (source.IsActive) return false;
        if (!Items.Contains(source)) return false;

        SetActiveInternal(source);
        return true;
    }

    /// <summary>设置页直接改地址时调用：写回当前源并立即生效</summary>
    public void SetActiveUrl(string url)
    {
        var normalized = Normalize(url);

        var active = Active;
        if (active != null) active.Url = normalized;

        _settings.VodBaseUrl = normalized;
        AppServices.Vod.BaseUrl = normalized;
        Persist();
        RaiseActiveChanged();
    }

    /* ================= 连通性测试 ================= */

    /// <summary>测活结果的慢速阈值（毫秒）：超过它算「可用但慢」，显示为橙色</summary>
    private const double SlowThresholdMs = 2500;

    /// <summary>并发测活的最大并行数：源动辄二三十个，全放开会把自己的网络打满</summary>
    private const int MaxParallelProbes = 4;

    /// <summary>
    /// 测试某个源是否可用：既写入界面状态（状态点 / 耗时 / 结果文字），也把结果返回。
    /// 并发测活时本方法跑在线程池上，所有界面属性的写入都切回 UI 线程。
    /// </summary>
    public async Task<SourceProbeResult> TestAsync(VodSource source)
    {
        SetStatus(source, () =>
        {
            source.IsTesting = true;
            source.StatusText = "测试中…";
            source.StatusLevel = string.Empty;
            source.LatencyMs = 0;
        });

        try
        {
            // 用独立实例测试，避免影响正在使用的连接通道与 LastUsedFallback
            var probe = new VodApiService(source.Url)
            {
                AllowSniFallback = _settings.AllowSniFallback
            };

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var response = await probe.GetListAsync(1);
            watch.Stop();

            var latency = watch.Elapsed.TotalMilliseconds;
            var first = response.List.Count > 0 ? response.List[0].Name : "-";
            var channel = probe.LastUsedFallback ? "SNI 静默直连" : "常规通道";

            // 极慢的源标橙：能用，但换个源可能体验更好
            var level = latency > SlowThresholdMs ? "warn" : "ok";
            var status = $"可用 · {channel} · {latency:0} ms · 共 {response.Total} 条，首条《{first}》";

            SetStatus(source, () =>
            {
                source.LatencyMs = latency;
                source.StatusLevel = level;
                source.StatusText = status;
            });

            return new SourceProbeResult(true, status, latency);
        }
        catch (Exception ex)
        {
            var status = $"不可用 · {FirstLine(ex.Message)}";

            SetStatus(source, () =>
            {
                source.LatencyMs = 0;
                source.StatusLevel = "fail";
                source.StatusText = status;
            });

            return new SourceProbeResult(false, status, 0);
        }
        finally
        {
            SetStatus(source, () => source.IsTesting = false);
        }
    }

    /// <summary>把界面状态写入切到 UI 线程（并发测活时本方法跑在线程池上）</summary>
    private static void SetStatus(VodSource source, Action apply)
    {
        var queue = App.MainWindow?.DispatcherQueue;

        if (queue == null || queue.HasThreadAccess) apply();
        else queue.TryEnqueue(() => apply());
    }

    /// <summary>
    /// 并发测活全部源（最多 4 个同时进行），返回（可用数, 失败数）。
    /// 逐个 await 的串行版本在二十多个源时要等一两分钟，并发后通常十几秒结束。
    /// </summary>
    public async Task<(int Ok, int Failed)> TestAllAsync(IProgress<(int Done, int Total)>? progress = null)
    {
        var targets = Items.Where(s => !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (targets.Count == 0) return (0, 0);

        var gate = new SemaphoreSlim(MaxParallelProbes);
        var done = 0;
        var ok = 0;
        var failed = 0;

        var tasks = targets.Select(async source =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var result = await TestAsync(source).ConfigureAwait(false);
                if (result.Ok) Interlocked.Increment(ref ok);
                else Interlocked.Increment(ref failed);
            }
            finally
            {
                gate.Release();
                var finished = Interlocked.Increment(ref done);
                progress?.Report((finished, targets.Count));
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        gate.Dispose();

        return (ok, failed);
    }

    /// <summary>按测活结果排序：可用的在前，同档按耗时升序，没测过的排最后</summary>
    public void SortByLatency()
    {
        var ordered = Items
            .OrderBy(s => s.StatusLevel switch { "ok" => 0, "warn" => 1, "fail" => 2, _ => 3 })
            .ThenBy(s => s.LatencyMs > 0 ? s.LatencyMs : double.MaxValue)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var from = Items.IndexOf(ordered[i]);
            if (from != i) Items.Move(from, i);
        }
    }

    /* ================= 内部 ================= */

    private void SetActiveInternal(VodSource source)
    {
        foreach (var item in Items) item.IsActive = ReferenceEquals(item, source);

        Apply(source);
        Persist();
        RaiseActiveChanged();
    }

    private void Apply(VodSource source)
    {
        _settings.ActiveSourceId = source.Id;
        _settings.VodBaseUrl = source.Url;
        AppServices.Vod.BaseUrl = source.Url;
    }

    private void Persist()
    {
        _settings.Sources = Items.ToList();
        _settings.ActiveSourceId = Active?.Id ?? string.Empty;
        // 无源时把接口地址一起清空，防止残留上一个源的地址
        _settings.VodBaseUrl = Active?.Url ?? string.Empty;
        _settings.Save();
    }

    private void RaiseActiveChanged() => ActiveChanged?.Invoke(this, EventArgs.Empty);

    private static string Normalize(string url) => (url ?? string.Empty).Trim().TrimEnd('/');

    private static bool SameUrl(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string HostOf(string url) =>
        Uri.TryCreate(Normalize(url), UriKind.Absolute, out var uri) ? uri.Host : "新视频源";

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 90 ? line[..90] + "…" : line;
    }
}

/// <summary>一次测活的结果：是否可用 / 展示文字 / 耗时</summary>
public sealed record SourceProbeResult(bool Ok, string Text, double LatencyMs);
