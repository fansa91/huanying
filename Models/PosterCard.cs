using System;
using System.ComponentModel;

namespace Huanying.Models;

/// <summary>能接收图片真实宽高的宿主（瀑布流用它校准卡片高度）</summary>
public interface IPosterAspectHost
{
    void AcceptImageSize(double width, double height);
}

/// <summary>
/// 首页瀑布流卡片的数据包装：在 <see cref="VodItem"/> 外面再加一层「海报高度」，
/// 由图片队列解出真实尺寸后回填，从而得到真正参差的瀑布流。
/// </summary>
public sealed class PosterCard : INotifyPropertyChanged, IPosterAspectHost
{
    /// <summary>海报常见比例 2:3，图片未到之前先用它占位</summary>
    private const double DefaultAspect = 1.44;

    /// <summary>裁切范围，避免个别超长竖图把某一列拉得过高</summary>
    private const double MinAspect = 1.12;
    private const double MaxAspect = 1.86;

    private double _aspect = DefaultAspect;
    private double _columnWidth = 176;
    private bool _aspectResolved;

    public PosterCard(VodItem item)
    {
        Item = item;
    }

    public VodItem Item { get; }

    public int Id => Item.Id;

    public string Name => Item.Name;

    public string MetaLine => Item.MetaLine;

    public string RemarksText => Item.RemarksText;

    public string PicUrl => Item.Pic;

    /// <summary>海报区高度 = 比例 × 当前列宽</summary>
    public double PosterHeight => Math.Round(_aspect * _columnWidth, 1);

    public bool HasImage => !string.IsNullOrWhiteSpace(Item.Pic);

    /// <summary>列宽变化（窗口缩放 / 列数变化）时由页面统一推送</summary>
    public double ColumnWidth
    {
        get => _columnWidth;
        set
        {
            if (Math.Abs(value - _columnWidth) < 0.5) return;
            _columnWidth = value;
            Raise(nameof(PosterHeight));
        }
    }

    /// <summary>图片解码完成后校准真实比例（每张图只校准一次）</summary>
    public void AcceptImageSize(double width, double height)
    {
        if (_aspectResolved || width <= 0 || height <= 0) return;

        _aspectResolved = true;

        var aspect = Math.Clamp(height / width, MinAspect, MaxAspect);
        if (Math.Abs(aspect - _aspect) < 0.03) return;

        _aspect = aspect;
        Raise(nameof(PosterHeight));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
