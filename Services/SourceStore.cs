using Huanying.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

    /// <summary>测试某个源是否可用，返回给界面展示的结果文字</summary>
    public async Task<string> TestAsync(VodSource source)
    {
        source.IsTesting = true;

        try
        {
            // 用独立实例测试，避免影响正在使用的连接通道与 LastUsedFallback
            var probe = new VodApiService(source.Url)
            {
                AllowSniFallback = _settings.AllowSniFallback
            };

            var response = await probe.GetListAsync(1);
            var first = response.List.Count > 0 ? response.List[0].Name : "-";
            var channel = probe.LastUsedFallback ? "SNI 静默直连" : "常规通道";

            return $"可用 · {channel} · 共 {response.Total} 条，首条《{first}》";
        }
        catch (Exception ex)
        {
            return $"不可用 · {FirstLine(ex.Message)}";
        }
        finally
        {
            source.IsTesting = false;
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
