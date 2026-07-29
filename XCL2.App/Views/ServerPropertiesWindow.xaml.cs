using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// "服务器设置"窗口：对 server.properties 里最常用的一批字段做图形化快捷编辑，
/// 字段清单和分组对照用户提供的截图（另一个面板工具的"服务器设置"页），不是凭空设计的。
///
/// 游戏模式/难度在 MC 1.14 前后是两套独立字段（老版本用数字 0-3，新版本用字符串名），
/// 服务端实际只会读其中一套（由核心自身的 MC 版本决定），这里两组都展示、都可编辑，
/// 保存的时候两组都写回文件——不强行按当前实例的 MC 版本隐藏一半，一是避免用户切换核心
/// 版本后发现"设置没了"，二是本来 server.properties 文件里两套字段本来就可能同时存在
/// （旧文件升级过版本），保留用户自己填的值更安全。
///
/// 不做输入合法性校验（比如"最大玩家数量"必须是数字）：server.properties 本身是纯文本
/// K-V，Minecraft 服务端自己启动时会做校验/给默认值，这里过度校验反而可能跟服务端实际
/// 接受的格式不一致（拦住合法输入），保存原样透传交给服务端处理更省事也更不容易出错。
/// </summary>
public partial class ServerPropertiesWindow : Window
{
    private readonly string _serverDir;

    public ServerPropertiesWindow(string serverDir)
    {
        _serverDir = serverDir;
        InitializeComponent();
        Loaded += (_, _) => LoadFromFile();
    }

    private void LoadFromFile()
    {
        var props = ServerPropertiesService.Load(_serverDir);

        MaxPlayersBox.Text = props.GetValueOrDefault("max-players", "20");
        ViewDistanceBox.Text = props.GetValueOrDefault("view-distance", "10");
        SpawnProtectionBox.Text = props.GetValueOrDefault("spawn-protection", "16");
        MotdBox.Text = props.GetValueOrDefault("motd", "A Minecraft Server");
        OpPermissionLevelBox.Text = props.GetValueOrDefault("op-permission-level", "4");
        LevelNameBox.Text = props.GetValueOrDefault("level-name", "world");
        LevelTypeBox.Text = props.GetValueOrDefault("level-type", "minecraft\\:normal");
        LevelSeedBox.Text = props.GetValueOrDefault("level-seed", "");
        MaxWorldSizeBox.Text = props.GetValueOrDefault("max-world-size", "29999984");
        NetworkCompressionBox.Text = props.GetValueOrDefault("network-compression-threshold", "256");
        PlayerIdleTimeoutBox.Text = props.GetValueOrDefault("player-idle-timeout", "0");
        ResourcePackBox.Text = props.GetValueOrDefault("resource-pack", "");
        GeneratorSettingsBox.Text = props.GetValueOrDefault("generator-settings", "{}");

        SelectComboByTag(DifficultyOldCombo, props.GetValueOrDefault("difficulty", "easy"));
        SelectComboByTag(DifficultyNewCombo, props.GetValueOrDefault("difficulty", "easy"));
        SelectComboByTag(GameModeOldCombo, props.GetValueOrDefault("gamemode", "survival"));
        SelectComboByTag(GameModeNewCombo, props.GetValueOrDefault("gamemode", "survival"));

        AllowFlightCheck.IsChecked = IsTrue(props, "allow-flight");
        OnlineModeCheck.IsChecked = props.TryGetValue("online-mode", out var om) ? om == "true" : true; // 默认值应偏向安全，未知时按"开启"处理
        EnableCommandBlockCheck.IsChecked = IsTrue(props, "enable-command-block");
        WhiteListCheck.IsChecked = IsTrue(props, "white-list");
        ForceGameModeCheck.IsChecked = IsTrue(props, "force-gamemode");
        GenerateStructuresCheck.IsChecked = props.TryGetValue("generate-structures", out var gs) ? gs == "true" : true;
    }

    private static bool IsTrue(Dictionary<string, string> props, string key)
        => props.TryGetValue(key, out var v) && v == "true";

    private static void SelectComboByTag(ComboBox combo, string value)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((string)item.Tag == value) { combo.SelectedItem = item; return; }
        }
        combo.SelectedIndex = 0;
    }

    private static string? TagOf(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 两套游戏模式/难度选框理论上应该保持一致（同一个字段，只是给不同 MC 版本时代的两种
        // 展示），但用户完全可能只改了其中一侧就点保存。这里以"新版本(1.14+)"那一侧为准，
        // 因为当前新建的服务器绝大多数都是 1.14+，老字段那一侧仅用于兼容极老版本核心，
        // 冲突时优先信任更常用的那一侧，而不是报错强行让用户对齐两边。
        var difficulty = TagOf(DifficultyNewCombo) ?? TagOf(DifficultyOldCombo) ?? "easy";
        var gamemode = TagOf(GameModeNewCombo) ?? TagOf(GameModeOldCombo) ?? "survival";

        var updates = new Dictionary<string, string?>
        {
            ["max-players"] = MaxPlayersBox.Text.Trim(),
            ["view-distance"] = ViewDistanceBox.Text.Trim(),
            ["spawn-protection"] = SpawnProtectionBox.Text.Trim(),
            ["motd"] = MotdBox.Text,
            ["op-permission-level"] = OpPermissionLevelBox.Text.Trim(),
            ["level-name"] = LevelNameBox.Text.Trim(),
            ["level-type"] = LevelTypeBox.Text.Trim(),
            ["level-seed"] = LevelSeedBox.Text.Trim(),
            ["max-world-size"] = MaxWorldSizeBox.Text.Trim(),
            ["network-compression-threshold"] = NetworkCompressionBox.Text.Trim(),
            ["player-idle-timeout"] = PlayerIdleTimeoutBox.Text.Trim(),
            ["resource-pack"] = ResourcePackBox.Text.Trim(),
            ["generator-settings"] = GeneratorSettingsBox.Text.Trim(),
            ["difficulty"] = difficulty,
            ["gamemode"] = gamemode,
            ["allow-flight"] = (AllowFlightCheck.IsChecked == true).ToString().ToLowerInvariant(),
            ["online-mode"] = (OnlineModeCheck.IsChecked == true).ToString().ToLowerInvariant(),
            ["enable-command-block"] = (EnableCommandBlockCheck.IsChecked == true).ToString().ToLowerInvariant(),
            ["white-list"] = (WhiteListCheck.IsChecked == true).ToString().ToLowerInvariant(),
            ["force-gamemode"] = (ForceGameModeCheck.IsChecked == true).ToString().ToLowerInvariant(),
            ["generate-structures"] = (GenerateStructuresCheck.IsChecked == true).ToString().ToLowerInvariant(),
        };

        try
        {
            ServerPropertiesService.Save(_serverDir, updates);
            StatusText.Text = "已保存，重启服务器后生效。";
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("保存服务器配置失败，可能是文件正被占用（比如服务器还在运行），请停止服务器后重试。",
                ex.ToString(), "保存配置失败");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
