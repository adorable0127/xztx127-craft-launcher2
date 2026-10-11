using System.IO;
using System.Text.Json;

namespace XCL2.App.Services.Plugins;

/// <summary>
/// "插件管理"里"生成插件项目模板"这个开发命令背后的实现：在用户选择的目录下
/// 生成一份最小的、可以直接用 dotnet build 编译出插件 dll 的 C# 类库项目骨架，
/// 免去插件作者从零翻文档、手写 csproj/接口签名的门槛。具体每个文件怎么改、
/// 改完怎么装进启动器，模板里的注释和仓库根目录的 PLUGIN_GUIDE.md 会再说明一遍。
/// </summary>
public static class PluginTemplateGenerator
{
    /// <summary>在 targetDir（必须是一个空目录或者不存在的目录）下生成模板项目。
    /// pluginId 建议形如 "yourname.plugin-name"，会被用作类名、Id 默认值和项目名。</summary>
    public static void Generate(string targetDir, string pluginId, string displayName)
    {
        Directory.CreateDirectory(targetDir);
        var className = ToPascalCase(pluginId);
        var projectName = $"{className}Plugin";
        var idLiteral = JsonSerializer.Serialize(pluginId);
        var displayNameLiteral = JsonSerializer.Serialize(displayName);

        File.WriteAllText(Path.Combine(targetDir, "plugin.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, id = pluginId, name = displayName, version = "0.1.0",
            entry = projectName + ".dll", kind = "dll", minApiVersion = "2.2",
            permissions = new[] { "ui.basic", "ui.components", "ui.navigation" }, dependencies = Array.Empty<object>(), conflicts = Array.Empty<string>()
        }, new JsonSerializerOptions { WriteIndented = true }));

        File.WriteAllText(Path.Combine(targetDir, projectName + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
                <Nullable>enable</Nullable>
                <Xcl2HostPath Condition="'$(Xcl2HostPath)' == ''">C:\path\to\your\XCL2\XCL2.dll</Xcl2HostPath>
                <UseWPF>true</UseWPF>
                <ImplicitUsings>enable</ImplicitUsings>
                <!-- 插件运行在 XCL2 主程序进程里，不需要（也不应该）把 XCL2.App.dll 打进插件
                     自己的输出目录——启动器加载插件时，插件能直接看到主程序已经加载的那些
                     程序集。这里只是为了编译期能识别 IPlugin/PluginContext 这两个类型，
                     引用方式用 Reference 而不是 PackageReference，且不随插件一起发布：
                     把下面这行的路径改成当前宿主构建输出中的托管 XCL2.dll；不能引用单文件 EXE。 -->
              </PropertyGroup>

              <ItemGroup>
                <None Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />
                <Reference Include="XCL2">
                  <HintPath>$(Xcl2HostPath)</HintPath>
                  <Private>false</Private>
                </Reference>
              </ItemGroup>

            </Project>
            """);

        File.WriteAllText(Path.Combine(targetDir, projectName + ".cs"), $$"""
            using System.Windows.Controls;
            using XCL2.App.Services.Plugins;

            namespace {{className}}Plugin;

            /// <summary>{{displayName}} —— 由“生成插件项目模板”命令生成的起始骨架，
            /// 具体每个成员的含义见 XCL2.App.Services.Plugins.IPlugin 上的注释，
            /// 更完整的说明见仓库根目录 PLUGIN_GUIDE.md。</summary>
            public sealed class {{className}} : IPlugin
            {
                public string Id => {{idLiteral}};
                public string DisplayName => {{displayNameLiteral}};
                public string Version => "0.1.0";
                public string Description => "在这里写一两句话描述这个插件是做什么的。";
                public string Author => "";

                private PluginContext? _ctx;

                public void Initialize(PluginContext ctx)
                {
                    _ctx = ctx;
                    ctx.Log("插件已加载。");
                    // API 2.2：模板默认允许导航；窗口和设置写入需另声明并由用户授权。
                    ctx.Ui.RegisterButton(new PluginButtonDefinition
                    {
                        Id = "api-center", Text = "API 开发中心与样例",
                        OnClick = async ui => { await ui.Plugin.Control.NavigateAsync("api", ui.Stopping); }
                    });
                    // API 2.0：先在 plugin.json 声明 ui.preview，再由用户在 API 设置开启并授权。
                    // ctx.Preview.RegisterCard("hello", "你好，世界", "原生 WinUI 3 卡片", "worlds");
                    // ctx.Preview.SetAppearance("我的 XCL2", "#1BD96A", compact: false);
                    // await ctx.Worlds.GetSnapshotAsync(); // 需要 worlds.read


                    // 任意坐标：Parent = "$overlay" 自动覆盖整个视图，不受原 Grid 行列限制。
                    // 可用 Action = Move / Remove / Replace 操作 Inspect 返回的现有组件。
                    // ctx.Components.Register(new PluginComponentDefinition
                    // {
                    //     Id = "floating-widget", Target = "view:MainWindow",
                    //     Action = PluginComponentAction.Add, Parent = "$overlay",
                    //     Layout = new PluginComponentLayout { Left = 120, Top = 80, Width = 160, Height = 44 },
                    //     CreateContent = _ => new Button { Content = "自定义组件" }
                    // });
                    // ctx.Runtime.RegisterLaunchMiddleware("kernel", (request, next) => next());
                    // ctx.Runtime.RegisterSkinModelRenderer("renderer", request => new YourRenderer(request));
                    // YourRenderer 实现 IPluginSkinModelRenderer，负责 View、皮肤、动作、帧更新和 Dispose。

                    // 示例：读取/写入插件自己的配置。
                    var greeting = ctx.Config.Get("greeting", "Hello, XCL2!");
                    ctx.Config.Set("greeting", greeting);

                    // 注册项由宿主在插件停止/重新加载时自动释放；返回的 IDisposable 可提前注销。
                    ctx.Ui.RegisterOption(new PluginOptionDefinition
                    {
                        Id = "greeting", Title = "问候语", Kind = PluginOptionKind.Text,
                        DefaultValue = greeting, Description = "编辑后点击应用保存。"
                    });
                    ctx.Ui.RegisterDialog(new PluginDialogDefinition
                    {
                        Id = "welcome", Title = "插件弹窗",
                        CreateContent = dialog => new TextBlock
                        {
                            Text = ctx.Config.Get("greeting", "Hello, XCL2!"),
                            TextWrapping = System.Windows.TextWrapping.Wrap
                        }
                    });
                    ctx.Ui.RegisterButton(new PluginButtonDefinition
                    {
                        Id = "open-welcome", Text = "打开示例弹窗",
                        OnClick = async action => { await action.Plugin.Ui.ShowDialogAsync("welcome"); }
                    });
                    ctx.Ui.RegisterButton(new PluginButtonDefinition
                    {
                        Id = "loader-help", Text = "插件加载器说明", Target = PluginUiTargets.LoaderChoice,
                        OnClick = async action => { await action.Plugin.Ui.ShowMessageAsync("插件说明", "这里可以提供加载器相关的扩展操作。"); }
                    });
                }

                public void Shutdown()
                {
                    _ctx?.Log("插件已关闭。");
                }

                // 不需要图形配置界面的话，把这个方法整个删掉即可（IPlugin 里已经有默认实现返回 null）。
                public UserControl? CreateSettingsPanel(PluginContext ctx)
                {
                    return new UserControl { Content = new TextBlock
                    {
                        Text = $"这是 {DisplayName} 的配置面板示例，换成你自己的 UserControl 即可。",
                        Margin = new System.Windows.Thickness(12),
                        TextWrapping = System.Windows.TextWrapping.Wrap
                    } };
                }
            }
            """);

        File.WriteAllText(Path.Combine(targetDir, "README.txt"),
            "1. 把 csproj 里 HintPath 改成当前宿主构建输出中的托管 XCL2.dll 路径（不是单文件 EXE）。\r\n" +
            "2. dotnet build -c Release\r\n" +
            "3. 将 bin/Release/net8.0-windows10.0.19041.0/" + projectName + ".dll 拖入启动器并审查来源提醒。\r\n" +
            "4. 确认后自动安装并运行；需要的依赖 DLL 请放进 installed/" + projectName + "/。\r\n" +
            "5. 插件配置页可编辑问候语、打开示例弹窗；加载器选择弹窗显示扩展按钮。\r\n" +
            "6. Examples.cs 含可选启动台页面与 API 2.2 调用。按注释声明并授权所需权限后再调用。\r\n" +
            "更完整的 UI 注册接口见 API_REFERENCE.md，开发流程见 PLUGIN_GUIDE.md。\r\n");

        File.WriteAllText(Path.Combine(targetDir, "Examples.cs"), """
            using XCL2.App.Services.Plugins;

            // 这些示例不会自动执行。调用前在 plugin.json 声明相应权限，并由用户授权。
            public static class LauncherExamples
            {
                // 需要 ui.navigation、settings.write、ui.window。
                public static async Task CustomizeAsync(PluginContext ctx)
                {
                    if (!await ctx.Control.NavigateAsync("home", ctx.Stopping)) return;
                    await ctx.Control.UpdateSettingsAsync(new PluginSettingsPatch
                    {
                        UiSkin = "Custom", CustomAccentColor = "#27AE60", IsDarkMode = true,
                        UiZoomPercent = 110, CallbackTimeoutSeconds = 300
                    }, ctx.Stopping);
                    await ctx.Control.UpdateWindowAsync(new PluginWindowPatch
                    {
                        Title = "我的 XCL2 工作台", Width = 1200, Height = 780,
                        SidebarCollapsed = false
                    }, ctx.Stopping);
                }

                // 需要 instances.read、accounts.read、launcher.control；保留宿主启动确认。
                public static async Task<PluginLaunchAcceptance> LaunchFirstAsync(PluginContext ctx)
                {
                    var accounts = await ctx.Launcher.Accounts.GetSnapshotAsync(ctx.Stopping);
                    var instances = await ctx.Launcher.Instances.GetSnapshotAsync(ct: ctx.Stopping);
                    var account = accounts.Items.FirstOrDefault(a => a.IsSelected) ?? accounts.Items.FirstOrDefault();
                    var instance = instances.Items.FirstOrDefault(i => i.IsSelected && i.IsInstalled)
                        ?? instances.Items.FirstOrDefault(i => i.IsInstalled);
                    if (account == null) return new(false, "AccountNotFound");
                    if (instance == null) return new(false, "InstanceNotFound");
                    await ctx.Control.SelectAccountAsync(account.Id, ctx.Stopping);
                    await ctx.Control.SelectInstanceAsync(instance.Id, ctx.Stopping);
                    return await ctx.Control.LaunchSelectedAsync(ctx.Stopping);
                }

                // 需要 ui.navigation、instances.read、downloads.read、launcher.control。
                // 用户授权这些权限后，在 Initialize 中调用 RegisterDashboard(ctx)。
                public static IDisposable RegisterDashboard(PluginContext ctx)
                {
                    return ctx.Ui.RegisterNavigationPage(new PluginNavigationPageDefinition(
                        "launcher-dashboard", "我的启动台", ui =>
                        {
                            var panel = new System.Windows.Controls.StackPanel();
                            var status = new System.Windows.Controls.TextBlock
                            { Text = "点击刷新读取实例与下载任务。", TextWrapping = System.Windows.TextWrapping.Wrap };
                            var instances = new System.Windows.Controls.ComboBox
                            { DisplayMemberPath = "Name", Margin = new System.Windows.Thickness(0, 8, 0, 8) };
                            var refresh = new System.Windows.Controls.Button { Content = "刷新实例与下载" };
                            var launch = new System.Windows.Controls.Button { Content = "启动所选实例" };
                            panel.Children.Add(status); panel.Children.Add(instances);
                            panel.Children.Add(refresh); panel.Children.Add(launch);
                            async Task RunAsync(Func<Task> operation)
                            {
                                refresh.IsEnabled = launch.IsEnabled = false;
                                try { await operation(); }
                                catch (OperationCanceledException) { status.Text = "操作已取消。"; }
                                catch (PluginOperationException ex) { status.Text = ex.Code + ": " + ex.SafeMessage; }
                                catch (Exception ex) { status.Text = "操作失败：" + ex.GetType().Name; }
                                finally { refresh.IsEnabled = launch.IsEnabled = true; }
                            }
                            refresh.Click += async (_, _) => await RunAsync(async () =>
                            {
                                var snapshot = await ui.Plugin.Launcher.Instances.GetSnapshotAsync(ct: ui.Stopping);
                                instances.ItemsSource = snapshot.Items.Where(i => i.IsInstalled).ToArray();
                                instances.SelectedItem = snapshot.Items.FirstOrDefault(i => i.IsSelected && i.IsInstalled);
                                var downloads = await ui.Plugin.Launcher.Downloads.GetSnapshotAsync(ui.Stopping);
                                status.Text = $"已安装 {instances.Items.Count} 个实例；下载列表 {downloads.Items.Length} 项。";
                            });
                            launch.Click += async (_, _) => await RunAsync(async () =>
                            {
                                if (instances.SelectedItem is not PluginInstance selected)
                                { status.Text = "请先选择一个实例。"; return; }
                                await ui.Plugin.Control.SelectInstanceAsync(selected.Id, ui.Stopping);
                                var accepted = await ui.Plugin.Control.LaunchSelectedAsync(ui.Stopping);
                                status.Text = "启动请求：" + accepted.Code;
                            });
                            return panel;
                        }, ShowInSimplifiedMode: true));
                }

                // API 2.2；需要 instances.write、resources.install。
                // 仅在用户点击补全按钮后调用，返回排队结果；后续用 downloads.read 查询进度。
                public static Task<PluginDownloadAcceptance> RepairAsync(PluginContext ctx, string instanceId) =>
                    ctx.Management.RepairInstanceAsync(instanceId, ctx.Stopping);

                // API 2.2；需要 resources.read。先让用户从来源和项目列表选择，再查询文件。
                public static async Task<IReadOnlyList<PluginResourceFile>> ListResourceFilesAsync(
                    PluginContext ctx, string sourceId, PluginResourceKind kind, string projectId,
                    string? gameVersion = null, string? loader = null) =>
                    await ctx.Resources.GetFilesAsync(new PluginResourceFileQuery(
                        sourceId, kind, projectId, gameVersion, loader), ctx.Stopping);

                // 需要 resources.install；默认不替换同名文件，也不自动安装依赖。
                public static Task<PluginDownloadAcceptance> InstallResourceAsync(
                    PluginContext ctx, PluginResourceFile selectedFile, string instanceId) =>
                    ctx.Resources.InstallAsync(new PluginResourceInstallRequest(
                        selectedFile.Token, instanceId), ctx.Stopping);
            }
            """);
        foreach (var file in new[] { EmbeddedDocumentationService.ApiFileName,
            EmbeddedDocumentationService.LauncherControlFileName, EmbeddedDocumentationService.CompositionApiFileName,
            EmbeddedDocumentationService.ApiExamplesFileName,
            EmbeddedDocumentationService.WorldsApiFileName, EmbeddedDocumentationService.PluginExeProtocolFileName,
            EmbeddedDocumentationService.PluginGuideFileName })
            EmbeddedDocumentationService.Export(file, Path.Combine(targetDir, file));
    }

    private static string ToPascalCase(string id)
    {
        var parts = id.Split(new[] { '.', '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var name = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..] : "")));
        if (name.Length == 0 || char.IsDigit(name[0])) name = "My" + name;
        return name;
    }
}
