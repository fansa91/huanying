using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Huanying.Models;

/// <summary>
/// 一个视频源：苹果CMS（MacCMS V10）JSON 接口根地址，例如
/// <c>https://example.com/api.php/provide/vod</c>。
/// 持久化字段为 Id/Name/Url/Note/BuiltIn，其余为界面用的临时状态（不写盘）。
/// </summary>
public sealed class VodSource : INotifyPropertyChanged
{
    private bool _isActive;
    private bool _isTesting;
    private string _status = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>接口根地址（不含 ?ac= 参数）</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>备注，例如「需开启 SNI 静默回退」</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>内置源（只允许改地址，不允许删除）</summary>
    public bool BuiltIn { get; set; }

    /* ---------- 界面状态 ---------- */

    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            Raise();
            Raise(nameof(ActiveText));
            Raise(nameof(HasActiveBadge));
        }
    }

    [JsonIgnore]
    public bool IsTesting
    {
        get => _isTesting;
        set
        {
            if (_isTesting == value) return;
            _isTesting = value;
            Raise();
            Raise(nameof(TestButtonText));
        }
    }

    [JsonIgnore]
    public string StatusText
    {
        get => _status;
        set
        {
            _status = value ?? string.Empty;
            Raise();
            Raise(nameof(HasStatus));
        }
    }

    [JsonIgnore] public bool HasStatus => !string.IsNullOrWhiteSpace(_status);

    [JsonIgnore] public bool HasActiveBadge => _isActive;

    [JsonIgnore] public string ActiveText => _isActive ? "使用中" : string.Empty;

    [JsonIgnore] public string TestButtonText => _isTesting ? "测试中…" : "测试";

    /// <summary>头像头像文字（名称首字）</summary>
    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "源" : Name.Substring(0, 1);

    [JsonIgnore]
    public string HostText => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : Url;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
