using System.Windows;
using System.Windows.Controls;

namespace XCL2.App.Views;

public partial class DownloadCenterPage
{
    /// <summary>搜索定位仅切换分类，沿用正常分类事件及 API Key 提示，不触发下载。</summary>
    internal bool SelectFeatureSearchSection(string sectionKey)
    {
        RadioButton? category = sectionKey switch
        {
            "VersionPanel" => CatVersion,
            "ModPanel" => CatMod,
            "ResourcePanel" => CatResourcePack,
            "MapPanel" or "MapKeyMissingPanel" => CatMap,
            "FavoritesPanel" => CatFavorites,
            _ => null
        };
        if (category == null || category.Visibility != Visibility.Visible || !category.IsEnabled) return false;
        category.SetCurrentValue(RadioButton.IsCheckedProperty, true);
        return category.IsChecked == true;
    }
}
