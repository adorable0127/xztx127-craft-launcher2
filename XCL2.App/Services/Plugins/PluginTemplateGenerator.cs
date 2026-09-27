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

        File.WriteAllText(Path.Combine(targetDir, projectName + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net8.0-windows</TargetFramework>
                <Nullable>enable</Nullable>
                <UseWPF>true</UseWPF>
                <!-- 插件运行在 XCL2 主程序进程里，不需要（也不应该）把 XCL2.App.dll 打进插件
                     自己的输出目录——启动器加载插件时，插件能直接看到主程序已经加载的那些
                     程序集。这里只是为了编译期能识别 IPlugin/PluginContext 这两个类型，
                     引用方式用 Reference 而不是 PackageReference，且不随插件一起发布：
                     把下面这行的路径改成你本机 XCL2 安装目录下的 XCL2.exe 实际路径。 -->
              </PropertyGroup>

              <ItemGroup>
                <Reference Include="XCL2">
                  <HintPath>C:\path\to\your\XCL2\XCL2.exe</HintPath>
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

                    // 示例：读取/写入插件自己的配置。
                    var greeting = ctx.Config.Get("greeting", "Hello, XCL2!");
                    ctx.Config.Set("greeting", greeting);
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
            "1. 把 csproj 里 HintPath 改成你本机 XCL2.exe 的实际路径。\r\n" +
            "2. dotnet build -c Release\r\n" +
            "3. 将 bin/Release/net8.0-windows/" + projectName + ".dll 拖入启动器并审查来源提醒。\r\n" +
            "4. 确认后自动安装并运行；需要的依赖 DLL 请放进 installed/" + projectName + "/。\r\n" +
            "更完整的接口说明、生命周期、注意事项见仓库根目录的 PLUGIN_GUIDE.md。\r\n");
    }

    private static string ToPascalCase(string id)
    {
        var parts = id.Split(new[] { '.', '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var name = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..] : "")));
        if (name.Length == 0 || char.IsDigit(name[0])) name = "My" + name;
        return name;
    }
}
