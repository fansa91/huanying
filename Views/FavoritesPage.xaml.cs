using Huanying.Models;
using Huanying.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Huanying.Views;

/// <summary>收藏页：读取 LibraryStore，点击进入详情</summary>
public sealed partial class FavoritesPage : Page
{
    public FavoritesPage()
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
        var items = LibraryStore.Current.Favorites;
        FavoriteGrid.ItemsSource = items;

        bool isEmpty = items.Count == 0;
        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        FavoriteGrid.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        SubtitleText.Text = isEmpty ? "暂无内容" : $"共 {items.Count} 部";
    }

    private void FavoriteGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FavoriteEntry entry && entry.VodId > 0)
        {
            Frame.Navigate(typeof(ShowDetailPage), entry.VodId);
        }
    }
}
