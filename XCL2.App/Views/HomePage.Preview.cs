using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;
using XCL2.Preview;

namespace XCL2.App.Views;

public partial class HomePage
{
    private Style? _defaultTileStyle;
    private Style? _defaultLanguageStyle;
    private readonly DispatcherTimer _previewCardsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private PreviewCard[] _lastPreviewCards = Array.Empty<PreviewCard>();
    private PreviewAppearance? _lastPreviewAppearance;
    private bool _lastPreviewRestricted;

    private void InitializePreviewHome()
    {
        _defaultTileStyle = LaunchTile.Style;
        _defaultLanguageStyle = LanguageEntryButton.Style;
        _previewCardsTimer.Tick += (_, _) => RefreshPreviewCards();
        SyncNativePreview();
    }

    private void ApplyPreviewHomeStyles()
    {
        HomeSurface.CornerRadius = new CornerRadius(_owner.IsNativePreviewOpen ? 8 : 10);
        HomeSurface.Padding = new Thickness(_owner.IsNativePreviewOpen ? 24 : 30);
        // StaticResource 会保留本页的旧样式对象；全窗模式切换时显式切换这两类局部样式。
        var tileStyle = _owner.IsNativePreviewOpen
            ? Application.Current.TryFindResource("TileButton") as Style ?? _defaultTileStyle
            : _defaultTileStyle;
        var languageStyle = _owner.IsNativePreviewOpen
            ? Application.Current.TryFindResource("LanguageEntryButtonStyle") as Style ?? _defaultLanguageStyle
            : _defaultLanguageStyle;
        foreach (var tile in new[] { LaunchTile, AccountsTile, DownloadTile, QuickStartTile, ServerManagerTile, SettingsTile })
            tile.Style = tileStyle;
        LanguageEntryButton.Style = languageStyle;
    }

    private void RefreshPreviewCards()
    {
        if (!_owner.IsNativePreviewOpen)
        {
            PreviewPluginSection.Visibility = Visibility.Collapsed;
            PreviewPluginCards.Children.Clear();
            _lastPreviewCards = Array.Empty<PreviewCard>();
            _lastPreviewAppearance = null;
            return;
        }

        var snapshot = PluginPreviewService.Snapshot();
        var restricted = _owner.ConfigService.Config.RestrictedMode;
        PreviewPluginSection.Visibility = snapshot.Cards.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_lastPreviewAppearance == snapshot.Appearance && _lastPreviewRestricted == restricted
            && _lastPreviewCards.SequenceEqual(snapshot.Cards)) return;

        _lastPreviewCards = snapshot.Cards;
        _lastPreviewAppearance = snapshot.Appearance;
        _lastPreviewRestricted = restricted;
        PreviewPluginHeading.Text = snapshot.Appearance.Title == "XCL2"
            ? "插件扩展" : snapshot.Appearance.Title + " · 插件扩展";
        if (ColorConverter.ConvertFromString(snapshot.Appearance.Accent) is Color appearanceAccent)
            PreviewPluginHeading.Foreground = new SolidColorBrush(appearanceAccent);
        PreviewPluginCards.Children.Clear();
        foreach (var card in snapshot.Cards)
        {
            var content = new StackPanel();
            var source = new TextBlock { Text = card.Plugin, FontSize = 11, Margin = new Thickness(0, 0, 0, 6) };
            source.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            content.Children.Add(source);
            var title = new TextBlock { Text = card.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            content.Children.Add(title);
            var description = new TextBlock { Text = card.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            description.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            content.Children.Add(description);

            var button = new Button
            {
                Content = content,
                Style = Application.Current.TryFindResource("TileButton") as Style ?? _defaultTileStyle,
                Width = snapshot.Appearance.Compact ? 220 : 260,
                Padding = snapshot.Appearance.Compact ? new Thickness(14) : new Thickness(18),
                Margin = new Thickness(0, 0, 10, 10),
                BorderBrush = ColorConverter.ConvertFromString(card.Accent) is Color accent
                    ? new SolidColorBrush(accent) : new SolidColorBrush(Colors.ForestGreen),
                IsEnabled = !restricted || card.Destination is "home" or "instances" or "ai",
                ToolTip = card.Description
            };
            button.Click += (_, _) => NavigatePreviewCard(card.Destination);
            PreviewPluginCards.Children.Add(button);
        }
    }

    private void NavigatePreviewCard(string destination)
    {
        // 使用真实页面入口，与原侧边栏、首页磁贴共享业务与权限设置。
        switch (destination)
        {
            case "home": _owner.NavigateToHome(); break;
            case "instances": _owner.NavigateToVersions(); break;
            case "worlds": _owner.NavigateToWorlds(); break;
            case "settings": _owner.NavigateToSettings(); break;
            case "ai": _owner.NavigateToAiAssistant(); break;
            case "downloads": _owner.NavigateToDownloadCenter(); break;
            case "plugins": OverlayDialogService.ShowModal(new PluginManagerDialog(_owner)); break;
        }
    }
}
