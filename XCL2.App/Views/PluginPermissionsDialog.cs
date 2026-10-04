using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

internal sealed class PluginPermissionsDialog : OverlayDialogControl
{
    internal ImmutableArray<string> Granted { get; private set; } = ImmutableArray<string>.Empty;
    internal PluginPermissionsDialog(PluginMetadata metadata, IEnumerable<string> granted)
    {
        Width = 620;
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = Loc.F("Str_Plugin_AuthorizeTitle", "{0} · 插件授权", metadata.Name), FontSize = 20, Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(new TextBlock { Text = Loc.T("Str_Plugin_TrustBoundary", "仅安装可信来源的插件。DLL 与启动器拥有相同系统权限；授权只限制宿主 API 的访问。撤销后立即停止该权限的新调用，已写入的文件不会自动恢复。"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var choices = new List<(string Permission, CheckBox Box)>();
        var list = new StackPanel();
        foreach (var permission in metadata.Permissions)
        {
            var available = PluginPermissionService.IsAvailable(permission);
            var box = new CheckBox
            {
                Content = permission + " — " + PluginPermissionText.Describe(permission) + (available ? "" : "（当前宿主不可用）"),
                IsChecked = available && granted.Contains(permission, StringComparer.Ordinal),
                IsEnabled = available,
                Margin = new Thickness(0, 4, 0, 4)
            };
            choices.Add((permission, box)); list.Children.Add(box);
        }
        if (choices.Count == 0) list.Children.Add(new TextBlock { Text = Loc.T("Str_Plugin_NoPermissions", "未请求额外权限，保存后可加载。") });
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 350, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var clear = new Button { Content = Loc.T("Str_Plugin_RevokeAll", "全部取消"), Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6) };
        clear.Click += (_, _) => { foreach (var choice in choices) choice.Box.IsChecked = false; };
        var cancel = new Button { Content = Loc.T("Str_Cancel", "取消"), Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6) };
        cancel.Click += (_, _) => CloseWith(false);
        var save = new Button { Content = Loc.T("Str_Plugin_SavePermissions", "保存授权"), Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6) };
        save.Click += (_, _) => { Granted = choices.Where(c => c.Box.IsChecked == true).Select(c => c.Permission).ToImmutableArray(); CloseWith(true); };
        buttons.Children.Add(clear); buttons.Children.Add(cancel); buttons.Children.Add(save); root.Children.Add(buttons);
        Content = root;
    }
}

internal static class PluginPermissionText
{
    internal static string Describe(string permission) => Loc.T("Str_Plugin_Permission_" + permission.Replace('.', '_'), permission switch
    {
        "accounts.read" => "读取账户名称与 UUID，不含凭据",
        "instances.read" => "读取实例、版本及目录信息",
        "mods.read" => "读取已装 Mod 的元数据",
        "downloads.read" => "读取下载状态和进度",
        "settings.read" => "读取设置白名单",
        "environment.read" => "读取启动器版本、语言和数据目录",
        "events.lifecycle" => "订阅启动和退出通知",
        "launch.modify" => "修改启动参数、环境变量或取消启动",
        "downloads.intercept" => "改写公开下载地址或取消下载",
        "instances.validate" => "校验或取消实例创建",
        "network" => "使用宿主的网络请求",
        "providers.download" => "注册版本清单和下载镜像",
        "providers.mod" => "注册 Mod 来源",
        "providers.loader" => "注册加载器安装器",
        "auth.provider" => "注册登录方式，凭据由宿主管理",
        "files.instances.read" => "通过宿主读取实例文件",
        "files.instances.write" => "通过宿主写入实例文件",
        "files.external.read" => "读取用户单独选择的背景文件",
        "files.external.write" => "保留权限：当前没有通用外部写入接口",
        "process.install" => "在安装流程中运行安装程序",
        "ui.basic" => "注册弹窗、扩展区及插件配置面板",
        "ui.components" => "修改现有控件树和渲染扩展",
        "ui.navigation" => "注册独立导航页",
        "ui.instanceActions" => "添加实例操作",
        "ui.settings" => "注册插件设置项",
        "ui.background" => "提供启动器背景",
        "plugin.messaging" => "与清单声明的依赖插件通信",
        _ => permission
    });
}
