using Huanying.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Huanying.Services;

/// <summary>
/// 收藏 / 观看历史 / 断点位置 / 跳过片头片尾的本地存储。
/// 非打包应用用不了 ApplicationData.Current，直接读写 %LOCALAPPDATA%\Huanying\library.json。
/// 所有写入做 2 秒节流，避免播放器每秒落盘。
/// </summary>
public sealed class LibraryStore
{
    public static LibraryStore Current { get; } = new();

    private const int MaxHistory = 200;

    /* ================= 跳过片头 / 片尾 ================= */

    /// <summary>获取某节目的跳过设置（片头 / 片尾秒数），未设置时返回 null</summary>
    public SkipEntry? GetSkip(int vodId) =>
        _data.Skips.FirstOrDefault(s => s.VodId == vodId);

    /// <summary>保存某节目的跳过设置。intro/outro 为 0 表示该侧未启用</summary>
    public void SetSkip(int vodId, double introSeconds, double outroSeconds)
    {
        if (vodId <= 0) return;

        var existing = _data.Skips.FirstOrDefault(s => s.VodId == vodId);
        if (existing == null)
        {
            existing = new SkipEntry { VodId = vodId };
            _data.Skips.Add(existing);
        }

        existing.IntroSeconds = Math.Max(0, introSeconds);
        existing.OutroSeconds = Math.Max(0, outroSeconds);
        Save(force: true);
    }

    /// <summary>删除某节目的跳过设置</summary>
    public void RemoveSkip(int vodId)
    {
        var existing = _data.Skips.FirstOrDefault(s => s.VodId == vodId);
        if (existing == null) return;

        _data.Skips.Remove(existing);
        Save(force: true);
    }

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Huanying");

    private static string FilePath => Path.Combine(Dir, "library.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // 中文不转义，方便直接看文件
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private LibraryData _data = new();
    private DateTime _lastSave = DateTime.MinValue;

    /// <summary>收藏或历史发生变化（供页面刷新）</summary>
    public event EventHandler? Changed;

    private LibraryStore()
    {
        Load();
    }

    /* ================= 收藏 ================= */

    public IReadOnlyList<FavoriteEntry> Favorites =>
        _data.Favorites.OrderByDescending(f => f.AddedAt).ToList();

    public bool IsFavorite(int vodId) => _data.Favorites.Any(f => f.VodId == vodId);

    /// <summary>切换收藏状态，返回切换后的状态（true = 已收藏）</summary>
    public bool ToggleFavorite(VodItem item)
    {
        if (item.Id <= 0) return false;

        var existing = _data.Favorites.FirstOrDefault(f => f.VodId == item.Id);
        bool nowFavorite;

        if (existing != null)
        {
            _data.Favorites.Remove(existing);
            nowFavorite = false;
        }
        else
        {
            _data.Favorites.Add(new FavoriteEntry
            {
                VodId = item.Id,
                Name = item.Name,
                Pic = item.Pic,
                Remarks = item.RemarksText,
                AddedAt = DateTime.Now
            });
            nowFavorite = true;
        }

        Save(force: true);
        Changed?.Invoke(this, EventArgs.Empty);
        return nowFavorite;
    }

    public void RemoveFavorite(int vodId)
    {
        var existing = _data.Favorites.FirstOrDefault(f => f.VodId == vodId);
        if (existing == null) return;

        _data.Favorites.Remove(existing);
        Save(force: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /* ================= 历史 / 断点 ================= */

    public IReadOnlyList<HistoryEntry> History =>
        _data.History.OrderByDescending(h => h.WatchedAt).ToList();

    public HistoryEntry? GetHistory(int vodId) =>
        _data.History.FirstOrDefault(h => h.VodId == vodId);

    /// <summary>记录播放进度。position/duration 单位为秒</summary>
    public void RecordProgress(
        int vodId, string name, string pic,
        int episodeIndex, string episodeName,
        double position, double duration,
        bool forceSave = false)
    {
        if (vodId <= 0 || position < 1) return;

        var entry = _data.History.FirstOrDefault(h => h.VodId == vodId);
        if (entry == null)
        {
            entry = new HistoryEntry { VodId = vodId, Name = name, Pic = pic };
            _data.History.Add(entry);
        }

        entry.Name = string.IsNullOrWhiteSpace(name) ? entry.Name : name;
        entry.Pic = string.IsNullOrWhiteSpace(pic) ? entry.Pic : pic;
        entry.EpisodeIndex = episodeIndex;
        entry.EpisodeName = episodeName;
        entry.Position = position;
        entry.Duration = duration > 0 ? duration : entry.Duration;
        entry.WatchedAt = DateTime.Now;

        // 只保留最近 200 条
        if (_data.History.Count > MaxHistory)
        {
            foreach (var stale in _data.History.OrderByDescending(h => h.WatchedAt).Skip(MaxHistory).ToList())
            {
                _data.History.Remove(stale);
            }
        }

        Save(forceSave);
    }

    public void RemoveHistory(int vodId)
    {
        var entry = _data.History.FirstOrDefault(h => h.VodId == vodId);
        if (entry == null) return;

        _data.History.Remove(entry);
        Save(force: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearHistory()
    {
        _data.History.Clear();
        Save(force: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /* ================= 读写 ================= */

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<LibraryData>(json, JsonOptions);
            if (loaded != null) _data = loaded;
        }
        catch
        {
            // 文件损坏时从空库开始，不影响启动
            _data = new LibraryData();
        }
    }

    private void Save(bool force = false)
    {
        // 播放器每秒都可能调用，节流到 2 秒一次
        if (!force && (DateTime.Now - _lastSave).TotalSeconds < 2) return;

        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, JsonOptions));
            _lastSave = DateTime.Now;
        }
        catch
        {
            // 写失败不影响播放
        }
    }

    private sealed class LibraryData
    {
        public List<FavoriteEntry> Favorites { get; set; } = new();
        public List<HistoryEntry> History { get; set; } = new();
        public List<SkipEntry> Skips { get; set; } = new();
    }
}
