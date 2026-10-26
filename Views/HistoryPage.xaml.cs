using Huanying.Models;
using Huanying.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;

namespace Huanying.Views;

/// <summary>历史页：读取 LibraryStore，支持从上次的集数与秒数继续播放</summary>
public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Load();
    }

    private void Load()
    {
        var items = LibraryStore.Current.History;
        HistoryList.ItemsSource = items;

        bool isEmpty = items.Count == 0;
        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

        SubtitleText.Text = isEmpty
            ? "继续上次未看完的内容"
            : $"共 {items.Count} 条记录 · 点击「继续」从上次位置接着看";
    }

    private void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryEntry entry && entry.VodId > 0)
        {
            Frame.Navigate(typeof(ShowDetailPage), entry.VodId);
        }
    }

    private async void ContinuePlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not HistoryEntry entry) return;
        await PlayFromHistoryAsync(entry);
    }

    /// <summary>重新拉一次详情拿到剧集列表，再带着集数和秒数进入播放器</summary>
    private async Task PlayFromHistoryAsync(HistoryEntry entry)
    {
        try
        {
            var item = await AppServices.Vod.GetDetailAsync(entry.VodId);
            if (item == null || !item.HasPlaySource)
            {
                Frame.Navigate(typeof(ShowDetailPage), entry.VodId);
                return;
            }

            var episodes = item.Sources[0].Episodes;
            if (episodes.Count == 0)
            {
                Frame.Navigate(typeof(ShowDetailPage), entry.VodId);
                return;
            }

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
        catch
        {
            // 接口失败时退回详情页，至少能手动选集
            Frame.Navigate(typeof(ShowDetailPage), entry.VodId);
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        LibraryStore.Current.ClearHistory();
        Load();
    }
}
