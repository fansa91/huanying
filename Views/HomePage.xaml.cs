using Huanying.Controls;
using Huanying.Models;
using Huanying.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Huanying.Views;

/// <summary>
/// 首页：直接对接苹果CMS JSON 接口（ac=detail 分页 + t 分类 + wd 搜索）。
/// 展示用「瀑布流」：<see cref="MasonryLayout"/> 按最短列摆放卡片，卡片高度由海报真实比例决定；
/// 图片统一走 <see cref="ImageQueue"/> 队列加载（避免几百个请求同时打出）。
/// </summary>
public sealed partial class HomePage : Page
{
    private const int MinColumnWidth = 172;
    private const double ColumnSpacing = 14;

    private readonly ObservableCollection<PosterCard> _cards = new();
    private readonly HashSet<int> _seenIds = new();

    private int _page;
    private int _pageCount = 1;
    private int _total;
    private int? _typeId;
    private string? _keyword;
    private bool _loading;
    private string? _loadedBaseUrl;
    private bool _initialized;

    /* ---------- 筛选（地区 / 年份 / 排序） ---------- */

    /// <summary>地区筛选值，null 表示不限</summary>
    private string? _area;

    /// <summary>年份筛选值，null 表示不限</summary>
    private string? _year;

    /// <summary>排序方式（time / hits / score），null 表示接口默认</summary>
    private string? _sortBy;

    /// <summary>正在初始化筛选下拉，避免触发 SelectionChanged 引发多余请求</summary>
    private bool _suppressFilter;

    /// <summary>地区选项（苹果CMS 的 area 参数用的是中文名）</summary>
    private static readonly string[] Areas =
    {
        "全部地区", "大陆", "香港", "台湾", "美国", "韩国", "日本", "泰国", "英国", "法国", "印度", "其它"
    };

    /// <summary>排序选项：显示名 → by 参数</summary>
    private static readonly (string Label, string? By)[] Sorts =
    {
        ("默认排序", null), ("最新", "time"), ("最热", "hits"), ("评分", "score")
    };

    private int _columnCount;
    private double _columnWidth = MinColumnWidth;

    /* ---------- 聚合搜索 ---------- */

    /// <summary>是否启用聚合搜索（同时查多个源）</summary>
    private bool _aggregateEnabled;

    /// <summary>参与聚合搜索的源 Id 集合（空 = 全部源）</summary>
    private readonly HashSet<string> _aggregateSourceIds = new();

    /// <summary>聚合搜索服务</summary>
    private AggregateSearchService? _aggregateService;

    /// <summary>当前聚合搜索是否正在进行</summary>
    private bool _aggregating;

    public HomePage()
    {
        this.InitializeComponent();

        WaterfallRepeater.ItemsSource = _cards;
        ListRows.ItemsSource = _cards;

        WaterfallScroll.SizeChanged += (_, _) => UpdateColumnMetrics();

        ImageQueue.Current.StatsChanged += (_, _) => UpdateQueueText();

        this.Loaded += HomePage_Loaded;
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        App.Log($"HOME Loaded: initialized={_initialized} hasSource={AppServices.Vod.HasSource} base={AppServices.Vod.BaseUrl}");

        if (_initialized)
        {
            UpdateColumnMetrics();
            return;
        }

        _initialized = true;

        _loadedBaseUrl = AppServices.Vod.BaseUrl;

        BuildFilterOptions();
        RefreshContinueRow();

        if (!AppServices.Vod.HasSource)
        {
            ShowNoSource();
            return;
        }

        await BuildCategoriesAsync();
        await LoadAsync(reset: true);

        // 诊断用：HY_AGGTEST=关键词 时自动开启聚合搜索并跑一次，便于截图核对界面
        if (Environment.GetEnvironmentVariable("HY_AGGTEST") is { Length: > 0 } aggKeyword)
        {
            AggregateToggle.IsChecked = true;
            AggregateToggle_Click(AggregateToggle, new RoutedEventArgs());

            SearchBox.Text = aggKeyword;
            await RunAggregateSearchAsync(aggKeyword);
        }
    }

    /* ================= 无源状态 ================= */

    private void ShowNoSource()
    {
        _cards.Clear();
        _total = 0;
        _page = 0;
        _pageCount = 1;

        CategoryBar.Children.Clear();
        NoSourcePanel.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadMoreButton.Visibility = Visibility.Collapsed;
        CountText.Text = string.Empty;
        SetLoadingState(false);
    }

    private void GoSource_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(SourceListPage));
    }

    /* ================= 分类 ================= */

    private async Task BuildCategoriesAsync()
    {
        List<VodCategory> categories;
        try
        {
            categories = await AppServices.Vod.GetCategoriesAsync();
        }
        catch
        {
            categories = VodApiService.FallbackCategories();
        }

        CategoryBar.Children.Clear();
        CategoryBar.Children.Add(CreatePill("全部", null));
        foreach (var category in categories)
        {
            // 只放一级分类，避免胶囊过长
            if (category.TypePid != 0) continue;
            CategoryBar.Children.Add(CreatePill(category.TypeName, category.TypeId));
        }
    }

    private ToggleButton CreatePill(string text, int? typeId)
    {
        var pill = new ToggleButton
        {
            Content = text,
            Tag = typeId,
            IsChecked = typeId == null,
            Style = (Style)Resources["CategoryPillStyle"]
        };
        pill.Click += CategoryPill_Click;
        return pill;
    }

    private async void CategoryPill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        // 单选行为：其余胶囊取消选中
        foreach (var child in CategoryBar.Children)
        {
            if (child is ToggleButton pill && !ReferenceEquals(pill, clicked)) pill.IsChecked = false;
        }
        clicked.IsChecked = true;

        _typeId = clicked.Tag as int?;
        _keyword = string.Empty;
        SearchBox.Text = string.Empty;

        await LoadAsync(reset: true);
    }

    /* ================= 筛选与排序 ================= */

    /// <summary>初始化地区 / 年份 / 排序下拉（年份取最近 12 年）</summary>
    private void BuildFilterOptions()
    {
        _suppressFilter = true;

        AreaBox.Items.Clear();
        foreach (var area in Areas) AreaBox.Items.Add(area);
        AreaBox.SelectedIndex = 0;

        YearBox.Items.Clear();
        YearBox.Items.Add("全部年份");
        var thisYear = DateTime.Now.Year;
        for (var year = thisYear; year >= thisYear - 11; year--) YearBox.Items.Add(year.ToString());
        YearBox.SelectedIndex = 0;

        SortBox.Items.Clear();
        foreach (var (label, _) in Sorts) SortBox.Items.Add(label);
        SortBox.SelectedIndex = 0;

        _suppressFilter = false;
    }

    private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilter) return;
        await ApplyFiltersAsync();
    }

    /// <summary>把三个下拉的当前选择换算成接口参数并重新加载</summary>
    private async Task ApplyFiltersAsync()
    {
        _area = AreaBox.SelectedIndex > 0 ? AreaBox.SelectedItem as string : null;

        var year = YearBox.SelectedIndex > 0 ? YearBox.SelectedItem as string : null;
        _year = string.IsNullOrWhiteSpace(year) ? null : year;

        var sortIndex = SortBox.SelectedIndex;
        _sortBy = sortIndex >= 0 && sortIndex < Sorts.Length ? Sorts[sortIndex].By : null;

        UpdateFilterUi();

        // 筛选只在单源列表模式有意义；聚合搜索是跨源关键词查询
        if (_aggregateEnabled && !string.IsNullOrWhiteSpace(_keyword))
        {
            await RunAggregateSearchAsync(_keyword);
            return;
        }

        await LoadAsync(reset: true);
    }

    private void UpdateFilterUi()
    {
        var active = _area != null || _year != null || _sortBy != null;

        ClearFilterButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        var parts = new List<string>();
        if (_area != null) parts.Add(_area);
        if (_year != null) parts.Add(_year);
        if (_sortBy != null) parts.Add(Sorts.First(s => s.By == _sortBy).Label);

        FilterHint.Text = parts.Count == 0 ? string.Empty : $"筛选中：{string.Join(" · ", parts)}";
    }

    private async void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        _suppressFilter = true;
        AreaBox.SelectedIndex = 0;
        YearBox.SelectedIndex = 0;
        SortBox.SelectedIndex = 0;
        _suppressFilter = false;

        _area = null;
        _year = null;
        _sortBy = null;

        UpdateFilterUi();
        await LoadAsync(reset: true);
    }

    /* ================= 继续观看 ================= */

    /// <summary>按本地历史重建「继续观看」横向行（最多 10 条，最近的在前）</summary>
    private void RefreshContinueRow()
    {
        try
        {
            var history = LibraryStore.Current.History
                .Where(h => h.VodId > 0)
                .Take(10)
                .ToList();

            ContinueList.Children.Clear();

            if (history.Count == 0)
            {
                ContinueSection.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (var entry in history) ContinueList.Children.Add(BuildContinueCard(entry));

            ContinueSection.Visibility = Visibility.Visible;
            ContinueHint.Text = $"{history.Count} 部未看完 · 点海报直接续播";
        }
        catch
        {
            ContinueSection.Visibility = Visibility.Collapsed;
        }
    }

    private FrameworkElement BuildContinueCard(HistoryEntry entry)
    {
        var card = new Border
        {
            Width = 152,
            CornerRadius = new CornerRadius(10),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1)
        };

        // 两行写死：海报 200 + 文字自适应。横向 StackPanel 会把同一行的卡片拉成等高
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(200) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 海报区（图片没到之前用同色系占位）
        var poster = new Grid();
        poster.Children.Add(new Border
        {
            Background = entry.PosterBrush,
            CornerRadius = new CornerRadius(9, 9, 0, 0)
        });

        if (Uri.TryCreate(entry.Pic, UriKind.Absolute, out var picUri))
        {
            poster.Children.Add(new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(picUri),
                Stretch = Stretch.UniformToFill
            });
        }

        // 海报底部的细进度条（叠在海报上，不额外占高度）
        poster.Children.Add(new ProgressBar
        {
            Value = entry.Progress,
            Maximum = 100,
            Minimum = 0,
            Height = 3,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false
        });

        var textPanel = new StackPanel
        {
            Spacing = 2,
            Padding = new Thickness(9, 7, 9, 9),
            // 固定文字区高度：一行标题 + 一行副标题，多张卡片才能齐平
            MinHeight = 46
        };

        var title = new TextBlock
        {
            Text = entry.Name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
        };

        var subtitle = new TextBlock
        {
            Text = entry.Subtitle,
            FontSize = 11,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };

        textPanel.Children.Add(title);
        textPanel.Children.Add(subtitle);

        Grid.SetRow(poster, 0);
        Grid.SetRow(textPanel, 1);

        layout.Children.Add(poster);
        layout.Children.Add(textPanel);
        card.Child = layout;

        ToolTipService.SetToolTip(card, $"继续观看：{entry.Name} · {entry.Subtitle}");
        card.Tapped += async (_, _) => await ResumeFromHistoryAsync(entry);

        return card;
    }

    /// <summary>点「继续观看」：拉一次详情拿到分集地址，直接带着进度进播放页</summary>
    private async Task ResumeFromHistoryAsync(HistoryEntry entry)
    {
        ShowHint($"正在打开《{entry.Name}》…");

        if (!AppServices.Vod.HasSource)
        {
            Frame.Navigate(typeof(ShowDetailPage), entry.VodId);
            return;
        }

        try
        {
            var item = await AppServices.Vod.GetDetailAsync(entry.VodId);

            if (item == null || !item.HasPlaySource)
            {
                // 详情拿不到（换源了 / 被删了）：退回到详情页，用户自己挑线路
                Frame.Navigate(typeof(ShowDetailPage), entry.VodId);
                return;
            }

            var episodes = item.Sources[0].Episodes;
            var index = Math.Clamp(entry.EpisodeIndex - 1, 0, episodes.Count - 1);

            Frame.Navigate(typeof(PlayerPage), new PlayerArgs
            {
                VodId = item.Id,
                VodName = item.Name,
                Title = $"{item.Name} · {episodes[index].Name}",
                PosterUrl = item.Pic,
                Episodes = episodes,
                StartIndex = index,
                ResumeSeconds = entry.Position
            });
        }
        catch (Exception ex)
        {
            ShowHint($"续播失败：{ex.Message}");
            Frame.Navigate(typeof(ShowDetailPage), entry.VodId);
        }
    }

    /* ================= 聚合搜索 ================= */

    /// <summary>开启 / 关闭聚合搜索</summary>
    private void AggregateToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle) return;

        _aggregateEnabled = toggle.IsChecked == true;

        AggregateSourceButton.Visibility = _aggregateEnabled ? Visibility.Visible : Visibility.Collapsed;
        AggregateSourceBar.Visibility = _aggregateEnabled ? Visibility.Visible : Visibility.Collapsed;

        // 聚合搜索是跨源关键词查询，地区 / 年份 / 排序对它不生效，先收起来免得误会
        FilterBar.Visibility = _aggregateEnabled ? Visibility.Collapsed : Visibility.Visible;

        if (_aggregateEnabled)
        {
            BuildAggregateSourceChips();

            // 没勾关键词时给个提示，让用户知道开关生效了
            if (string.IsNullOrWhiteSpace(_keyword))
            {
                ShowHint("聚合搜索已开启，输入关键词后回车即可同时搜索多个源");
            }
        }
    }

    /// <summary>构建参与搜索的源选择胶囊</summary>
    private void BuildAggregateSourceChips()
    {
        AggregateSourceChips.Children.Clear();

        var sources = AppServices.Sources.Items.Where(s => !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (sources.Count == 0) return;

        // 默认全选：用户开启聚合搜索后直接能用
        if (_aggregateSourceIds.Count == 0)
        {
            foreach (var source in sources) _aggregateSourceIds.Add(source.Id);
        }

        foreach (var source in sources)
        {
            var sourceId = source.Id;
            var chip = new ToggleButton
            {
                Content = source.Name,
                IsChecked = _aggregateSourceIds.Contains(sourceId),
                Style = (Style)Resources["CategoryPillStyle"]
            };
            chip.Click += (_, _) =>
            {
                if (chip.IsChecked == true) _aggregateSourceIds.Add(sourceId);
                else _aggregateSourceIds.Remove(sourceId);
            };
            AggregateSourceChips.Children.Add(chip);
        }
    }

    private void SelectAllSources_Click(object sender, RoutedEventArgs e)
    {
        _aggregateSourceIds.Clear();
        foreach (var source in AppServices.Sources.Items)
        {
            if (!string.IsNullOrWhiteSpace(source.Url)) _aggregateSourceIds.Add(source.Id);
        }
        BuildAggregateSourceChips();
    }

    private void SelectCurrentSource_Click(object sender, RoutedEventArgs e)
    {
        _aggregateSourceIds.Clear();
        var active = AppServices.Sources.Active;
        if (active != null) _aggregateSourceIds.Add(active.Id);
        BuildAggregateSourceChips();
    }

    /// <summary>打开「选择源」对话框</summary>
    private async void AggregateSourceButton_Click(object sender, RoutedEventArgs e)
    {
        BuildAggregateSourceChips();

        var sources = AppServices.Sources.Items.Where(s => !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (sources.Count == 0)
        {
            ShowHint("还没有可用的视频源，请先到「源管理」添加");
            return;
        }

        var checkboxes = new List<Microsoft.UI.Xaml.Controls.CheckBox>();
        var panel = new StackPanel { Spacing = 8 };

        foreach (var source in sources)
        {
            var checkBox = new Microsoft.UI.Xaml.Controls.CheckBox
            {
                Content = source.Name,
                IsChecked = _aggregateSourceIds.Contains(source.Id)
            };
            checkboxes.Add(checkBox);
            panel.Children.Add(checkBox);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "选择参与聚合搜索的源",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消"
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _aggregateSourceIds.Clear();
        foreach (var checkBox in checkboxes)
        {
            if (checkBox.IsChecked == true && checkBox.Content is string name)
            {
                var source = sources.FirstOrDefault(s => s.Name == name);
                if (source != null) _aggregateSourceIds.Add(source.Id);
            }
        }

        // 一个都没选就退回全部源，免得搜索无声无息
        if (_aggregateSourceIds.Count == 0)
        {
            foreach (var source in sources) _aggregateSourceIds.Add(source.Id);
        }

        BuildAggregateSourceChips();
        ShowHint($"聚合搜索将使用 {_aggregateSourceIds.Count} 个源");
    }

    /// <summary>执行聚合搜索</summary>
    private async Task RunAggregateSearchAsync(string keyword)
    {
        if (_aggregating) return;

        _aggregating = true;
        SetLoadingState(true);

        try
        {
            _aggregateService ??= new AggregateSearchService(AppServices.Settings);

            var sources = AppServices.Sources.Items
                .Where(s => !string.IsNullOrWhiteSpace(s.Url))
                .Where(s => _aggregateSourceIds.Count == 0 || _aggregateSourceIds.Contains(s.Id))
                .ToList();

            var result = await _aggregateService.SearchAsync(keyword, sources);

            _cards.Clear();
            _seenIds.Clear();

            UpdateColumnMetrics();

            foreach (var hit in result.Hits)
            {
                var card = new PosterCard(hit) { ColumnWidth = _columnWidth };
                _cards.Add(card);
            }

            _total = result.Hits.Count;

            ErrorPanel.Visibility = Visibility.Collapsed;

            // 有源失败时把情况说清楚
            if (result.FailedCount > 0)
            {
                var failed = result.SourceResults
                    .Where(r => !r.Success)
                    .Select(r => r.SourceName)
                    .ToList();

                ShowHint($"{result.SuccessCount}/{result.SourceCount} 个源返回成功，" +
                         $"合并 {result.RawCount} 条为 {result.Hits.Count} 部；失败：{string.Join("、", failed)}");
            }
            else
            {
                ShowHint($"{result.SourceCount} 个源合并 {result.RawCount} 条，得到 {result.Hits.Count} 部");
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _aggregating = false;
            SetLoadingState(false);
            UpdateState();
        }
    }

    /* ================= 数据加载 ================= */

    private async Task LoadAsync(bool reset)
    {
        if (_loading) return;

        // 没有任何源时不要去请求接口，直接给引导
        if (!AppServices.Vod.HasSource)
        {
            ShowNoSource();
            return;
        }

        NoSourcePanel.Visibility = Visibility.Collapsed;
        _loading = true;

        App.Log($"HOME LoadAsync: page={_page} reset={reset} base={AppServices.Vod.BaseUrl}");

        if (reset)
        {
            _page = 0;
            _pageCount = 1;
            _total = 0;
            _seenIds.Clear();
            _cards.Clear();
            EmptyHint.Visibility = Visibility.Collapsed;
            WaterfallScroll.ChangeView(null, 0, null, disableAnimation: true);
        }

        SetLoadingState(true);

        try
        {
            var nextPage = _page + 1;
            var response = await AppServices.Vod.GetListAsync(
                nextPage,
                _typeId,
                string.IsNullOrWhiteSpace(_keyword) ? null : _keyword,
                _area,
                _year,
                _sortBy);

            _page = response.Page > 0 ? response.Page : nextPage;
            _pageCount = response.PageCount > 0 ? response.PageCount : _page;
            _total = response.Total;

            // 新数据先套用当前列宽，保证首帧高度就对
            UpdateColumnMetrics();

            foreach (var item in response.List)
            {
                if (item.Id <= 0 || !_seenIds.Add(item.Id)) continue;

                var card = new PosterCard(item) { ColumnWidth = _columnWidth };
                _cards.Add(card);
            }

            ErrorPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _loading = false;
            SetLoadingState(false);
            UpdateState();
        }
    }

    private void SetLoadingState(bool loading)
    {
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreButton.IsEnabled = !loading;
    }

    private void UpdateState()
    {
        var count = _cards.Count;

        CountText.Text = _total > 0 ? $"已加载 {count} / 共 {_total} 部" : $"已加载 {count} 部";

        EmptyHint.Visibility = count == 0 && ErrorPanel.Visibility != Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

        LoadMoreButton.Visibility = count > 0 && _page < _pageCount
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateQueueText();
    }

    private void UpdateQueueText()
    {
        var queue = ImageQueue.Current;
        QueueText.Text = $"图片队列：排队 {queue.Pending} · 已完成 {queue.Completed} · 失败 {queue.Failed}";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Collapsed;
        LoadMoreButton.Visibility = Visibility.Collapsed;
    }

    /* ================= 瀑布流列宽 ================= */

    /// <summary>可用宽度 → 列数 / 列宽，并把列宽推给每张卡片（海报高度 = 比例 × 列宽）</summary>
    private void UpdateColumnMetrics()
    {
        var available = WaterfallScroll.ViewportWidth;
        if (available <= 0 && ActualWidth > 0) available = ActualWidth - 64;
        if (available <= 0) return;

        var (columns, columnWidth) = MasonryLayout.Compute(available, MinColumnWidth, ColumnSpacing);

        if (columns == _columnCount && Math.Abs(columnWidth - _columnWidth) < 0.5) return;

        _columnCount = columns;
        _columnWidth = columnWidth;

        foreach (var card in _cards) card.ColumnWidth = columnWidth;
    }

    /* ================= 交互 ================= */

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _keyword = args.QueryText?.Trim() ?? string.Empty;

        // 聚合搜索：并发查多个源，合并去重
        if (_aggregateEnabled && !string.IsNullOrWhiteSpace(_keyword))
        {
            await RunAggregateSearchAsync(_keyword);
            return;
        }

        await LoadAsync(reset: true);
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e) => await LoadAsync(reset: false);

    private void WaterfallScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // 滚到接近底部自动补下一页，瀑布流不需要手动点
        if (_loading || _page >= _pageCount || _cards.Count == 0) return;
        if (ListRows.Visibility == Visibility.Visible) return;

        var remaining = WaterfallScroll.ScrollableHeight - WaterfallScroll.VerticalOffset;
        if (remaining < 720) _ = LoadAsync(reset: false);
    }

    private void ViewToggle_Click(object sender, RoutedEventArgs e)
    {
        bool showWaterfall = ReferenceEquals(sender, GridToggle);

        GridToggle.IsChecked = showWaterfall;
        ListToggle.IsChecked = !showWaterfall;

        WaterfallScroll.Visibility = showWaterfall ? Visibility.Visible : Visibility.Collapsed;
        ListRows.Visibility = showWaterfall ? Visibility.Collapsed : Visibility.Visible;

        if (showWaterfall) UpdateColumnMetrics();
    }

    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is PosterCard card && card.Id > 0)
        {
            NavigateToDetail(card);
        }
    }

    private void List_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PosterCard card && card.Id > 0)
        {
            NavigateToDetail(card);
        }
    }

    /// <summary>进入详情页；聚合搜索的结果要带上来源源，否则会用错源查详情</summary>
    private void NavigateToDetail(PosterCard card)
    {
        if (string.IsNullOrWhiteSpace(card.SourceUrl))
        {
            Frame.Navigate(typeof(ShowDetailPage), card.Id);
            return;
        }

        Frame.Navigate(typeof(ShowDetailPage), new DetailArgs
        {
            VodId = card.Id,
            SourceUrl = card.SourceUrl,
            SourceName = card.SourceName
        });
    }

    /// <summary>顶部轻提示</summary>
    private void ShowHint(string text)
    {
        HintText.Text = text;
        HintText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync(reset: true);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 从详情页点演员 / 导演，或任意页面带关键词跳回来：参数形如 "search:周星驰"
        if (e.Parameter is string spec && spec.StartsWith("search:", StringComparison.Ordinal))
        {
            var keyword = spec["search:".Length..].Trim();
            if (keyword.Length > 0)
            {
                await ApplyExternalSearchAsync(keyword);
                return;
            }
        }

        // 页面已缓存（NavigationCacheMode=Required），从详情页返回时保留列表与滚动位置；
        // 但源被切换过（接口地址变了）时需要重新加载。
        if (_initialized && _loadedBaseUrl != AppServices.Vod.BaseUrl)
        {
            _loadedBaseUrl = AppServices.Vod.BaseUrl;
            await ReloadForNewSourceAsync();
            return;
        }

        // 从播放页回来时历史可能变了，「继续观看」要跟着更新
        if (_initialized) RefreshContinueRow();
    }

    /// <summary>外部带关键词跳进来：清掉分类与筛选，保证搜得到东西</summary>
    private async Task ApplyExternalSearchAsync(string keyword)
    {
        _keyword = keyword;
        _typeId = null;
        SearchBox.Text = keyword;

        // 分类胶囊回到「全部」
        foreach (var child in CategoryBar.Children)
        {
            if (child is ToggleButton pill) pill.IsChecked = pill.Tag is null;
        }

        _suppressFilter = true;
        AreaBox.SelectedIndex = 0;
        YearBox.SelectedIndex = 0;
        SortBox.SelectedIndex = 0;
        _suppressFilter = false;

        _area = null;
        _year = null;
        _sortBy = null;
        UpdateFilterUi();

        if (_aggregateEnabled)
        {
            await RunAggregateSearchAsync(keyword);
            return;
        }

        await LoadAsync(reset: true);
    }

    private async Task ReloadForNewSourceAsync()
    {
        if (!AppServices.Vod.HasSource)
        {
            ShowNoSource();
            return;
        }

        NoSourcePanel.Visibility = Visibility.Collapsed;
        await BuildCategoriesAsync();
        await LoadAsync(reset: true);
    }

    /// <summary>诊断用：模拟点击第一张「继续观看」卡片，验证 拉详情 → 进播放页 整条链路</summary>
    internal async Task<string> DebugResumeFirstAsync()
    {
        var entry = LibraryStore.Current.History.FirstOrDefault();
        if (entry == null) return "no-history";

        await ResumeFromHistoryAsync(entry);
        await Task.Delay(2000);

        return $"clicked='{entry.Name}' 第{entry.EpisodeIndex}集 pos={entry.Position:0}s " +
               $"navigated={Frame.CurrentSourcePageType?.Name ?? "-"} " +
               $"historyCount={ContinueList.Children.Count}";
    }

    /// <summary>诊断用：设置筛选条件并重新加载（HY_FILTER=大陆,2025,score）</summary>
    internal async Task<string> DebugApplyFilterAsync(string area, string year, string by)
    {
        _suppressFilter = true;

        var areaIndex = Array.IndexOf(Areas, area);
        AreaBox.SelectedIndex = areaIndex > 0 ? areaIndex : 0;

        var yearIndex = string.IsNullOrWhiteSpace(year) ? 0 : YearBox.Items.IndexOf(year);
        YearBox.SelectedIndex = yearIndex > 0 ? yearIndex : 0;

        var sortIndex = 0;
        for (var i = 0; i < Sorts.Length; i++)
        {
            if (Sorts[i].By == by) sortIndex = i;
        }
        SortBox.SelectedIndex = sortIndex;

        _suppressFilter = false;

        await ApplyFiltersAsync();
        return DebugState();
    }

    /// <summary>诊断用：清掉筛选并重新加载</summary>
    internal async Task<string> DebugClearFilterAsync()
    {
        _suppressFilter = true;
        AreaBox.SelectedIndex = 0;
        YearBox.SelectedIndex = 0;
        SortBox.SelectedIndex = 0;
        _suppressFilter = false;

        await ApplyFiltersAsync();
        return DebugState();
    }

    /// <summary>诊断用：供 HY_NAV / HY_SMOKE 读取首页状态</summary>
    internal string DebugState() =>
        $"cards={_cards.Count} cols={_columnCount} colWidth={_columnWidth:0.0} " +
        $"page={_page}/{_pageCount} queue p={ImageQueue.Current.Pending} ok={ImageQueue.Current.Completed} fail={ImageQueue.Current.Failed} " +
        $"pic={_cards.FirstOrDefault()?.PicUrl} err={ImageQueue.Current.LastError} base={AppServices.Vod.BaseUrl} " +
        $"aggregate={(_aggregateEnabled ? "on" : "off")} aggSources={_aggregateSourceIds.Count} " +
        $"srcLabel='{_cards.FirstOrDefault(c => c.HasSource)?.SourceLabel ?? "-"}' " +
        $"filter(area='{_area}' year='{_year}' by='{_sortBy}' options={AreaBox.Items.Count}/{YearBox.Items.Count}/{SortBox.Items.Count}) " +
        $"continue={ContinueList.Children.Count} " +
        $"heights=[{string.Join(",", ContinueList.Children.OfType<FrameworkElement>().Select(c => $"{c.ActualHeight:0}"))}] " +
        $"keyword='{_keyword}'";
}
