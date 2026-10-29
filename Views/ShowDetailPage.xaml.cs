using Huanying.Models;
using Huanying.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Huanying.Views;

/// <summary>
/// 节目详情：背景大图 + 海报 + 收藏 / 继续观看 + 播放线路 + 分段选集 + 简介。
/// 数据来自 CMS 接口 ac=detail&amp;ids=，进度与收藏来自 LibraryStore。
/// </summary>
public sealed partial class ShowDetailPage : Page
{
    /// <summary>一屏最多放多少集，超过则分「1-60 / 61-120」区间</summary>
    private const int SegmentSize = 60;

    private int _vodId;
    private VodItem? _item;
    private List<VodPlaySource> _sources = new();
    private int _sourceIndex;
    private HistoryEntry? _history;
    private bool _suppressSegmentEvent;

    /// <summary>聚合搜索进来的来源源（空 = 用当前激活的源）</summary>
    private string _sourceUrl = string.Empty;
    private string _sourceName = string.Empty;

    /// <summary>指定来源时用的独立接口实例（不影响全局当前源）</summary>
    private VodApiService? _scopedApi;

    public ShowDetailPage()
    {
        this.InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var args = DetailArgs.From(e.Parameter);
        _vodId = args.VodId;
        _sourceUrl = args.SourceUrl;
        _sourceName = args.SourceName;

        // 指定了来源源：用独立实例查详情，不改动全局当前源
        _scopedApi = string.IsNullOrWhiteSpace(_sourceUrl)
            ? null
            : new VodApiService(_sourceUrl)
            {
                AllowSniFallback = AppServices.Settings.AllowSniFallback
            };

        if (_vodId > 0)
        {
            await LoadAsync();
        }
    }

    /* ================= 加载 ================= */

    private async Task LoadAsync()
    {
        if (_vodId <= 0) return;

        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        DetailRoot.Visibility = Visibility.Collapsed;

        try
        {
            // 聚合搜索进来的走指定源，否则用当前激活的源
            var api = _scopedApi ?? AppServices.Vod;
            var item = await api.GetDetailAsync(_vodId);
            if (item == null)
            {
                ShowError("接口没有返回该节目的详情，可能已被删除。");
                return;
            }

            _item = item;
            BindItem(item);
            DetailRoot.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void BindItem(VodItem item)
    {
        HeaderText.Text = item.Name;
        TitleText.Text = item.Name;
        MetaText.Text = item.MetaLine;
        StatusText.Text = item.RemarksText;
        DirectorText.Text = $"导演：{item.DirectorText}";
        ActorText.Text = $"主演：{item.ActorText}";
        ContentText.Text = string.IsNullOrWhiteSpace(item.PlainContent) ? "暂无简介" : item.PlainContent;

        if (item.HasScore)
        {
            ScoreText.Text = $"评分 {item.ScoreText}";
            ScoreBadge.Visibility = Visibility.Visible;
        }
        else
        {
            ScoreBadge.Visibility = Visibility.Collapsed;
        }

        if (DateTime.TryParse(item.UpdateTime, out var updated))
        {
            UpdateText.Text = $"更新于 {updated:MM-dd HH:mm}";
            UpdateBadge.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateBadge.Visibility = Visibility.Collapsed;
        }

        // 聚合搜索进来的：标出这部片来自哪个源
        if (!string.IsNullOrWhiteSpace(_sourceName))
        {
            SourceBadgeText.Text = $"来源：{_sourceName}";
            SourceBadge.Visibility = Visibility.Visible;
        }
        else
        {
            SourceBadge.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(item.Pic) && Uri.TryCreate(item.Pic, UriKind.Absolute, out var picUri))
        {
            var bitmap = new BitmapImage(picUri);
            PosterImage.Source = bitmap;
            BackdropImage.Source = bitmap;
        }

        _sources = item.Sources;
        BuildSourcePills();

        bool playable = _sources.Count > 0;
        PlayButton.IsEnabled = playable;
        SourceBar.Visibility = playable ? Visibility.Visible : Visibility.Collapsed;
        NoSourceText.Visibility = playable ? Visibility.Collapsed : Visibility.Visible;
        EpisodeGrid.Visibility = playable ? Visibility.Visible : Visibility.Collapsed;
        SegmentBox.Visibility = Visibility.Collapsed;

        EpisodeCountText.Text = playable
            ? $"共 {item.EpisodeCount} 集 · {item.RemarksText}"
            : "暂无可播放地址";

        UpdateFavoriteUi();
        UpdateResumeUi();
        LoadSkipSettings();
    }

    /* ================= 跳过片头 / 片尾 ================= */

    private void LoadSkipSettings()
    {
        var skip = _vodId > 0 ? LibraryStore.Current.GetSkip(_vodId) : null;

        if (skip != null)
        {
            IntroNumberBox.Value = (int)Math.Round(skip.IntroSeconds);
            OutroNumberBox.Value = (int)Math.Round(skip.OutroSeconds);
            ClearSkipButton.Visibility = Visibility.Visible;
            SkipHintText.Text = "当前已设置片头 / 片尾，保存后立即生效。";
        }
        else
        {
            IntroNumberBox.Value = 0;
            OutroNumberBox.Value = 0;
            ClearSkipButton.Visibility = Visibility.Collapsed;
            SkipHintText.Text = "设置后，播放到对应位置会出现跳过按钮。";
        }
    }

    private void SaveSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_vodId <= 0) return;

        var intro = Math.Max(0, (double)IntroNumberBox.Value);
        var outro = Math.Max(0, (double)OutroNumberBox.Value);

        if (intro == 0 && outro == 0)
        {
            LibraryStore.Current.RemoveSkip(_vodId);
            ClearSkipButton.Visibility = Visibility.Collapsed;
            SkipHintText.Text = "设置后，播放到对应位置会出现跳过按钮。";
            return;
        }

        LibraryStore.Current.SetSkip(_vodId, intro, outro);
        ClearSkipButton.Visibility = Visibility.Visible;
        SkipHintText.Text = "已保存，播放时按 J 可快速跳过。";
    }

    private void ClearSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_vodId <= 0) return;

        LibraryStore.Current.RemoveSkip(_vodId);
        IntroNumberBox.Value = 0;
        OutroNumberBox.Value = 0;
        ClearSkipButton.Visibility = Visibility.Collapsed;
        SkipHintText.Text = "设置后，播放到对应位置会出现跳过按钮。";
    }

    /* ================= 收藏 / 续播 ================= */

    private void UpdateFavoriteUi()
    {
        bool favorite = _item != null && LibraryStore.Current.IsFavorite(_item.Id);

        // EB51 = 空心，EB52 = 实心
        FavoriteIcon.Glyph = favorite ? "\uEB52" : "\uEB51";
        FavoriteLabel.Text = favorite ? "已收藏" : "收藏";
    }

    private void UpdateResumeUi()
    {
        _history = LibraryStore.Current.GetHistory(_vodId);

        if (_history == null || _sources.Count == 0)
        {
            ResumeButton.Visibility = Visibility.Collapsed;
            return;
        }

        var name = string.IsNullOrWhiteSpace(_history.EpisodeName)
            ? $"第 {_history.EpisodeIndex} 集"
            : _history.EpisodeName;

        ResumeButtonText.Text = $"继续观看 · {name}";
        ResumeButton.Visibility = Visibility.Visible;
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_item == null) return;

        LibraryStore.Current.ToggleFavorite(_item);
        UpdateFavoriteUi();
    }

    /* ================= 线路 ================= */

    private void BuildSourcePills()
    {
        SourceBar.Children.Clear();

        for (int i = 0; i < _sources.Count; i++)
        {
            var index = i;
            var pill = new ToggleButton
            {
                Content = _sources[i].DisplayName,
                IsChecked = i == 0,
                Style = (Style)Resources["SourcePillStyle"]
            };
            pill.Click += (s, e) =>
            {
                if (s is ToggleButton clicked)
                {
                    foreach (var child in SourceBar.Children)
                    {
                        if (child is ToggleButton other && !ReferenceEquals(other, clicked)) other.IsChecked = false;
                    }
                    clicked.IsChecked = true;
                }
                SelectSource(index);
            };
            SourceBar.Children.Add(pill);
        }

        if (_sources.Count > 0) SelectSource(0);
    }

    private void SelectSource(int index)
    {
        if (index < 0 || index >= _sources.Count) return;

        _sourceIndex = index;
        BuildSegments();
        UpdateResumeUi();
    }

    /* ================= 分段选集 ================= */

    private void BuildSegments()
    {
        if (_sourceIndex < 0 || _sourceIndex >= _sources.Count) return;

        var episodes = _sources[_sourceIndex].Episodes;
        EpisodeSummaryText.Text = $"本线路共 {episodes.Count} 集";

        _suppressSegmentEvent = true;
        SegmentBox.Items.Clear();

        if (episodes.Count <= SegmentSize)
        {
            SegmentBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            var segments = (int)Math.Ceiling(episodes.Count / (double)SegmentSize);
            for (int i = 0; i < segments; i++)
            {
                var start = i * SegmentSize + 1;
                var end = Math.Min((i + 1) * SegmentSize, episodes.Count);
                SegmentBox.Items.Add($"{start}-{end}");
            }
            SegmentBox.Visibility = Visibility.Visible;
            SegmentBox.SelectedIndex = 0;
        }
        _suppressSegmentEvent = false;

        ShowSegment(0);
    }

    private void Segment_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSegmentEvent) return;
        ShowSegment(Math.Max(0, SegmentBox.SelectedIndex));
    }

    private void ShowSegment(int segmentIndex)
    {
        if (_sourceIndex < 0 || _sourceIndex >= _sources.Count) return;

        var episodes = _sources[_sourceIndex].Episodes;
        var skip = segmentIndex * SegmentSize;
        if (skip >= episodes.Count) skip = 0;

        EpisodeGrid.ItemsSource = episodes.Skip(skip).Take(SegmentSize).ToList();
    }

    /* ================= 播放 ================= */

    private void Episode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not VodEpisode episode) return;
        PlayEpisode(episode, resumeSeconds: 0);
    }

    private void PlayFirst_Click(object sender, RoutedEventArgs e)
    {
        if (_sources.Count == 0 || _sources[_sourceIndex].Episodes.Count == 0) return;
        PlayEpisode(_sources[_sourceIndex].Episodes[0], resumeSeconds: 0);
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (_sources.Count == 0 || _history == null) return;

        var episodes = _sources[_sourceIndex].Episodes;
        if (episodes.Count == 0) return;

        var index = Math.Clamp(_history.EpisodeIndex - 1, 0, episodes.Count - 1);
        PlayEpisode(episodes[index], _history.Position);
    }

    private void PlayEpisode(VodEpisode episode, double resumeSeconds)
    {
        if (_item == null || string.IsNullOrWhiteSpace(episode.Url)) return;

        var args = new PlayerArgs
        {
            VodId = _item.Id,
            VodName = _item.Name,
            Title = $"{_item.Name} · {episode.Name}",
            PosterUrl = _item.Pic,
            Episodes = _sources[_sourceIndex].Episodes,
            StartIndex = episode.Index - 1,
            ResumeSeconds = resumeSeconds
        };

        Frame.Navigate(typeof(PlayerPage), args);
    }

    /* ================= 杂项 ================= */

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        DetailRoot.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    /// <summary>诊断用：供 HY_NAV 自检读取详情页绑定结果</summary>
    internal string DebugState()
    {
        var error = ErrorPanel.Visibility == Visibility.Visible ? ErrorText.Text : "none";
        var resume = ResumeButton.Visibility == Visibility.Visible ? ResumeButtonText.Text : "-";

        return $"title='{TitleText.Text}' meta='{MetaText.Text}' sources={_sources.Count} " +
               $"episodes={_item?.EpisodeCount ?? 0} resume='{resume}' err={error} " +
               $"grid={EpisodeGrid.ActualWidth:0}x{EpisodeGrid.ActualHeight:0} " +
               $"gridItems={((EpisodeGrid.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Count() ?? 0)}";
    }
}
