# XCL2 启动器 API 接入说明


## 1. 选择接入方式

| 接入目的 | 接口 | 调用方向 |
| --- | --- | --- |
| 在启动器内运行扩展、保存配置、展示设置面板 | `IPlugin` / `PluginContext` | 启动器加载 DLL 并调用插件 |
| 随启动器运行独立程序 | EXE 插件进程约定 | 启动器启动 EXE，传入数据目录环境变量 |
| 从脚本或其他软件打开页面、启动已有实例 | 命令行 | 外部程序启动 XCL2 并传参数 |
| 接入自建或第三方 AI 服务 | Chat Completions 兼容 HTTP 接口 | 启动器向你配置的服务器发送请求 |
| 在启动器源码中管理插件 | `PluginManager` | 宿主代码管理插件的安装与生命周期 |
| 读取或导出内置帮助 MD | `EmbeddedDocumentationService` | 从当前启动器程序集读取资源 |

下文的 C# 接口是进程内方法，不是 HTTP URL。AI 服务器地址是启动器访问的上游服务地址。

## 2. DLL 插件契约：IPlugin

命名空间：`XCL2.App.Services.Plugins`。主插件 DLL 提供一个公开、非抽象、具有公开无参构造函数的 `IPlugin` 实现类。插件使用 .NET 8 / WPF，并引用与所运行启动器匹配的托管程序集。

若从源码构建后引用宿主，使用构建输出中的托管 `XCL2.dll`；单文件发布的 EXE 不是可替代它的接口引用文件。引用项设置 `Private=false`，避免把宿主程序集当作插件依赖再次安装。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="XCL2">
      <HintPath>C:\XCL2\managed-build\XCL2.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

| 成员 | 类型 / 签名 | 语义 |
| --- | --- | --- |
| `Id` | `string`，只读 | 插件唯一标识；发布后保持稳定，供配置和数据目录使用 |
| `DisplayName` | `string`，只读 | 管理页显示名称 |
| `Version` | `string`，只读 | 管理页显示的插件版本 |
| `Description` | `string`，只读 | 插件功能描述 |
| `Author` | `string`，只读 | 插件作者 |
| `Initialize(PluginContext ctx)` | `void` | 符合启动策略时由宿主调用，建立上下文、订阅事件、启动任务 |
| `Shutdown()` | `void` | 本轮成功初始化的插件被停止、重新扫描或宿主退出时调用，释放资源 |
| `CreateSettingsPanel(PluginContext ctx)` | `UserControl?` | 用户打开插件配置时调用；返回 WPF 面板；默认实现返回 `null` |

`Initialize` 应尽快返回，网络和长时间任务采用异步方式。后台线程更新 WPF 控件时切回 UI Dispatcher。重新扫描会先关停上一轮实例，再按策略加载；`Shutdown` 不保证程序集立即完成垃圾回收。后台任务、定时器和事件订阅应由插件自行取消或解除。

返回 `null` 的设置面板由宿主替换为已有配置的只读 JSON 预览。仅本轮已运行的 DLL 能打开配置面板。

### 插件管理界面入口

"插件管理"弹窗（`PluginManagerDialog`）本身不区分入口，只是一个可以从任意位置 `new PluginManagerDialog(owner).ShowDialog()` 打开的 `OverlayDialogControl`。当前有两个位置可以打开它：

| 入口 | 位置 | 是否受额外门控限制 |
| --- | --- | --- |
| 「设置」页 → 第三方插件 → 插件管理… | `SettingsPage.xaml` / `SettingsPage.xaml.cs` 的 `OpenPluginManagerButton_Click` | 无，任何语言、简洁模式开关状态下都能看到 |
| 「实验性功能」→ 插件管理 | `ExperimentalFeaturesWindow` 的 `PluginManager_Click` | 受 `LocalizationService.ExperimentalFeaturesLanguageGate`（仅简体中文）和简洁模式开关联动隐藏，且首次打开要先在 `ExperimentalGateWindow` 等待 10 秒确认 |

插件的启用/禁用、扫描状态查看属于基础管理能力，不属于"实验性"功能，因此不应该只挂在有语言/模式门控的「实验性功能」入口下——「设置」页的入口就是为解决"找不到插件管理"这个问题加的，不受任何门控影响。两个入口背后是同一个 `PluginManagerDialog` / 同一个 `MainWindow.Plugins`（`PluginManager` 单例），互不冲突，也不会重复扫描或重复加载。

### Shutdown 阶段的日志落盘顺序

`MainWindow` 在关闭时按固定顺序触发一批 `Closed` 事件处理器，其中两个和插件直接相关，顺序不能颠倒：

1. `Plugins.ShutdownAll()`：对本轮成功初始化的插件逐个调用 `Shutdown()`，插件在这一步里调用的 `ctx.Log(...)`（以及 `PluginManager` 自己捕获到的、插件 `Shutdown()` 抛出异常时记录的日志）会追加进本次会话的日志内存缓冲区。
2. `LauncherLogService.EndSessionAndFlush()`：把内存缓冲区一次性写入本次会话的日志文件，并把内部标记设为"已写入"（幂等设计，防止重复落盘）；写入之后再往缓冲区追加的任何内容都不会再有第二次落盘机会。

必须先执行第 1 步、再执行第 2 步，否则插件在 `Shutdown()` 里记的日志会因为会话日志已经落盘完毕而彻底丢失——插件作者看不到任何诊断信息，容易误以为"`Shutdown` 根本没被调用"。当前源码已按这个顺序注册。排查"插件在关闭时好像没生效"时，先去查这份会话日志（或直接看 `PluginContext.Log` 写入的启动器日志）里有没有 `[Plugin:你的插件Id]` 开头的行，能确认 `Shutdown()` 是否真的执行到、执行到哪一步、有没有抛异常。

## 3. PluginContext：宿主提供的能力

`Initialize` 和 `CreateSettingsPanel` 的上下文由宿主创建并传入。

| 成员 | 类型 | 用法 |
| --- | --- | --- |
| `PluginId` | `string` | 与 `IPlugin.Id` 一致 |
| `Config` | `PluginConfigService` | 读写本插件配置 |
| `DataDirectory` | `string` | 已创建的专属可写目录，保存插件缓存、下载内容或其他数据 |
| `Log` | `Action<string>` | `ctx.Log("消息")`，宿主自动添加插件标识并写入启动器日志 |

默认数据目录：`%APPDATA%\XCL2\Plugins\data\<PluginId>\`。配置目录：`%APPDATA%\XCL2\Plugins\config\<PluginId>.json`。使用上下文提供的实际路径，避免在插件中自行拼接固定路径。

### 配置接口

| 方法 | 返回值 | 行为 |
| --- | --- | --- |
| `Get<T>(string key, T fallback)` | `T` | 缺少键或反序列化失败时返回 `fallback` |
| `Set<T>(string key, T value)` | `void` | 序列化值并立即重写配置文件；序列化、写入失败可抛出异常 |
| `Remove(string key)` | `void` | 删除存在的键并保存；不存在时不写文件 |
| `Snapshot()` | `IReadOnlyDictionary<string, JsonElement>` | 返回当前配置的只读视图，不是独立拷贝 |

配置服务没有提供并发访问保证；同一插件的多个任务应串行访问配置。插件间的目录区分是数据组织方式，不是进程权限隔离。

### 最小调用示例

```csharp
using System.IO;
using System.Windows.Controls;
using XCL2.App.Services.Plugins;

public sealed class ExamplePlugin : IPlugin
{
    public string Id => "example.integration";
    public string DisplayName => "接口接入示例";
    public string Version => "1.0.0";
    public string Description => "演示配置、数据文件、日志和设置面板。";
    public string Author => "Example";
    private PluginContext? _context;

    public void Initialize(PluginContext ctx)
    {
        _context = ctx;
        var count = ctx.Config.Get("launchCount", 0);
        ctx.Config.Set("launchCount", count + 1);
        File.WriteAllText(Path.Combine(ctx.DataDirectory, "status.txt"), "ready");
        ctx.Log($"初始化完成，本插件累计运行 {count + 1} 次。");
    }

    public UserControl? CreateSettingsPanel(PluginContext ctx)
    {
        var enabled = new CheckBox
        {
            Content = "启用示例功能",
            IsChecked = ctx.Config.Get("enabled", true)
        };
        enabled.Click += (_, _) => ctx.Config.Set("enabled", enabled.IsChecked == true);
        return new UserControl { Content = enabled };
    }

    public void Shutdown()
    {
        _context?.Log("插件停止。");
        _context = null;
    }
}
```

示例的配置写入由 UI 线程顺序执行。实际插件如有后台任务，在 `Shutdown` 中取消任务并释放资源；不要将密钥、令牌或登录凭据写入 `ctx.Log`。

## 4. EXE 插件进程接口

EXE 不实现 `IPlugin`，不接收 `PluginContext`。宿主以 `UseShellExecute=false` 启动程序，工作目录为安装后的 EXE 所在目录，当前不传入额外命令行参数。

| 项目 | 约定 |
| --- | --- |
| 数据目录 | 环境变量 `XCL2_PLUGIN_DATA_DIR` |
| 运行策略 | `EachLaunch`：每次启动器启动或重新扫描时运行；`OnceOnImport`：成功启动后记住状态，不在后续启动中重复运行 |
| 停止 | 宿主退出或重新加载插件时，结束仍由宿主管理的 EXE 进程树 |
| 配置和日志 | 由 EXE 自行保存到自己的数据目录；DLL 的 `Config`、`Log` 和设置面板不能直接用于 EXE |

```csharp
using System;
using System.IO;

var dataDir = Environment.GetEnvironmentVariable("XCL2_PLUGIN_DATA_DIR");
if (string.IsNullOrWhiteSpace(dataDir))
    throw new InvalidOperationException("未收到 XCL2 插件数据目录。");
Directory.CreateDirectory(dataDir);
File.AppendAllText(Path.Combine(dataDir, "plugin.log"), "独立插件已启动\n");
```

## 5. 外部程序与脚本：命令行入口

启动器的参数解析位于 `CommandLineService.Parse(string[] args)`。页面和启动动作由主窗口执行；参数解析结果本身不等于已完成操作。

| 调用 | 用途 |
| --- | --- |
| `XCL2.exe -help` | 显示命令行说明 |
| `XCL2.exe -r --account "账户名" --instance "实例名"` | 使用已有账户启动已有实例 |
| `XCL2.exe -r --账户名 "账户名" --实例名称 "实例名"` | 上一条的中文参数写法 |
| `XCL2.exe -gui "设置"` | 打开对应页面，页面名使用启动器导航/搜索支持的名称 |
| `XCL2.exe --d --version "1.21.1" --loader "Fabric"` | 打开下载入口并传入版本和加载器选择 |
| `XCL2.exe --d` | 打开下载入口，交由用户选择版本 |

页面简写：

| 参数 | 页面 | 参数 | 页面 |
| --- | --- | --- | --- |
| `--home` | 首页 | `--versions` | 版本管理 |
| `--downloads` | 下载中心 | `--multiplayer` | 联机 |
| `--mods` | Mod 管理 | `--servers` | 服务端管理 |
| `--accounts` | 账户 | `--settings` | 设置 |
| `--logs` | 日志 | `--toolbox` | 百宝箱 |
| `--bedrock` | 基岩版启动 | `--about` | 鸣谢与帮助 |
| `--experimental` | 实验性功能 | `--ai` | AI 助手 |

一次指定一个页面。参数名不区分大小写；带值的长选项支持 `--参数 值` 或 `--参数=值`，包含空格的值应作为一个参数传入。C# 调用方使用 `ArgumentList`，无需自己拼接引号：

```csharp
using System.Diagnostics;

var start = new ProcessStartInfo(@"C:\XCL2\XCL2.exe") { UseShellExecute = false };
start.ArgumentList.Add("-r");
start.ArgumentList.Add("--account");
start.ArgumentList.Add("我的账户");
start.ArgumentList.Add("--instance");
start.ArgumentList.Add("我的整合包");
Process.Start(start);
```

这里只发起启动器操作，不返回游戏启动成功的结构化响应，也不会借参数创建账户。调用方不能将启动器进程成功创建等同于游戏已成功进入主菜单。

## 6. 自定义 AI HTTP 接口

用户在 AI 助手设置中开启使用自定义 API Key，填写 Base URL、API Key 和请求用模型 ID。当前客户端实现位于 `AiAssistantService`。

| 设置字段 | 含义 |
| --- | --- |
| `UseCustomApiKey` | `true` 时使用自定义供应商配置 |
| `BaseUrl` | API 根地址，例如 `https://api.example.com/v1` |
| `ApiKey` | 自定义服务密钥 |
| `CustomModels[].Id` | 请求中的 `model` 字段，必须是提供方接受的 ID |
| `CustomModels[].DisplayName` | 本地显示名称，不替代请求模型 ID |
| `CustomModels[].ProviderBaseUrl` / `ProviderApiKey` | 某个模型的独立供应商覆盖配置；未提供完整覆盖时使用公共供应商 |

启动器拼接 `BaseUrl.TrimEnd('/') + "/chat/completions"`，因此 Base URL 不要再填写完整的 `/chat/completions` 路径。

请求：`POST <BaseUrl>/chat/completions`。

```http
Authorization: Bearer <用户填写的 API Key>
Content-Type: application/json; charset=utf-8
```

普通模式的请求结构示意：

```json
{
  "model": "your-model-id",
  "messages": [
    { "role": "system", "content": "启动器生成的系统提示" },
    { "role": "user", "content": "用户输入" }
  ],
  "stream": false,
  "temperature": 0.6
}
```

对话可包含多条 `system`、`user`、`assistant` 消息。开启深度思考时客户端附加 `reasoning_effort: "high"`；开启联网搜索时附加 `tools: [{"type":"web_search"}]`。这些扩展字段需要上游支持，不支持的服务应在设置中关闭对应选项。

成功响应需要返回 JSON，并提供字符串 `choices[0].message.content`：

```json
{
  "choices": [
    { "message": { "role": "assistant", "content": "返回给用户的 Markdown 文本" } }
  ]
}
```

当前实现使用非流式 JSON 响应，不按 SSE 的 `data:` / `[DONE]` 帧读取，也不按 Responses API 的响应结构读取。接入其他协议时，需要供应商兼容层或修改客户端适配逻辑。

客户端会对部分临时失败进行重试；可识别的配置或鉴权错误会直接反馈。日志交给 AI 的入口还受 `AllowCrashLogReading` 控制，普通插件配置接口不能替代用户的这项授权。

## 7. 宿主侧插件管理接口

命名空间仍为 `XCL2.App.Services.Plugins`。这些方法供维护启动器源码时接入 UI，普通插件优先使用宿主传入的 `PluginContext`。

| API | 返回类型 | 调用约定 |
| --- | --- | --- |
| `new PluginManager(Action<string>? logSink = null)` | `PluginManager` | 创建管理器并提供日志回调 |
| `InstalledDirectory` | `string` | 已安装插件目录 |
| `Plugins` | `IReadOnlyList<LoadedPluginInfo>` | 最近一次扫描结果 |
| `StartPolicy` | `PluginLaunchMode` | 扫描前设置为 `EachLaunch` 或 `OnceOnImport` |
| `InstallFromFile(string sourcePath)` | `string` | 复制并返回安装路径；本方法本身不启动插件；同名目标存在时拒绝覆盖 |
| `ScanAndLoad()` | `void` | 停止上一轮、扫描、按启用状态和策略启动 |
| `SetEnabled(string filePath, bool enabled)` | `void` | 保存开关；随后重新扫描使运行状态生效 |
| `ShutdownAll()` | `void` | 结束当前托管插件并释放加载上下文 |
| `PluginTemplateGenerator.Generate(string targetDir, string pluginId, string displayName)` | `void` | 在指定目录生成插件源码模板 |

安装和启用操作沿用宿主界面对用户选择的确认流程。典型调用顺序：

```csharp
// UI 已完成用户选择和导入确认后：
manager.StartPolicy = config.PluginLaunchMode;
var installedPath = manager.InstallFromFile(selectedPluginPath);
manager.ScanAndLoad();

// 用户禁用该插件后：
manager.SetEnabled(installedPath, false);
manager.ScanAndLoad();

// 宿主退出时：
manager.ShutdownAll();
```

`LoadedPluginInfo` 提供 `FilePath`、`Plugin`、`LoadError`、`Enabled`、`Context`、`IsExecutable`、`RunningProcess` 和 `StartedOnce`。未在本轮运行或加载失败时，`Plugin` / `Context` 可以为 `null`，调用方应先判断。

## 8. 内置 MD 的拉取与导出接口

两份主要帮助文档在仓库根目录和 `XCL2.App/Resources/Docs/` 各保留一份。项目文件仅将项目内的副本编为 `EmbeddedResource`。更新文档时同步两份，随后编译即可更新应用内文档。外部 MD 不在场、单文件发布、离线运行时，查看和导出仍从当前程序集读取。

| 文档 | 项目内源文件 | 程序集资源名 |
| --- | --- | --- |
| 解决方法 | `Resources/Docs/PAPERDOLL_FIX.md` | `XCL2.App.Docs.PAPERDOLL_FIX.md` |
| API 接入说明 | `Resources/Docs/API_REFERENCE.md` | `XCL2.App.Docs.API_REFERENCE.md` |

命名空间：`XCL2.App.Services`。

| API | 返回值 | 用途 |
| --- | --- | --- |
| `EmbeddedDocumentationService.SolutionsFileName` | 常量 `PAPERDOLL_FIX.md` | 解决方法文档标识 |
| `EmbeddedDocumentationService.ApiFileName` | 常量 `API_REFERENCE.md` | API 文档标识 |
| `GetTitle(string fileName)` | `string` | 返回文档显示标题 |
| `Read(string fileName)` | `string` | 从程序集读取完整 Markdown |
| `Export(string fileName, string destination)` | `void` | 将内置原始字节写入指定目标文件 |

`fileName` 只接受以上两种标识，未知标识抛出 `ArgumentException`；缺少嵌入资源抛出 `FileNotFoundException`。导出目标的父目录必须存在，目标文件存在时会覆盖。界面的“导出 MD…”使用保存对话框确认目标和覆盖行为；直接调用接口的代码自行处理目标选择及 IO 异常。

```csharp
using System.IO;
using XCL2.App.Services;

string markdown = EmbeddedDocumentationService.Read(EmbeddedDocumentationService.ApiFileName);
// 可以传给自己的 Markdown 渲染控件。

// destination 是用户选定的保存位置。
EmbeddedDocumentationService.Export(EmbeddedDocumentationService.ApiFileName, destination);
```

无需服务封装时，也可从宿主程序集直接拉取：

```csharp
using System.IO;
using XCL2.App.Services.Plugins;

using var stream = typeof(IPlugin).Assembly.GetManifestResourceStream("XCL2.App.Docs.API_REFERENCE.md")
    ?? throw new FileNotFoundException("当前启动器构建未包含 API 文档。");
using var reader = new StreamReader(stream);
string markdown = reader.ReadToEnd();
```

用户入口位于“鸣谢与帮助 → 帮助文档”：两份文档各有“查看”和“导出 MD…”按钮。查看使用启动器内置 Markdown 查看器，不要求系统安装 MD 阅读器，不会自动联网拉取其他版本。

原有插件指南仍保留在根目录和 `Resources/Docs/PLUGIN_GUIDE.md`，其兼容资源名为 `XCL2.App.PLUGIN_GUIDE.md`，现有插件管理页的指南按钮继续使用该资源。

## 9. 源码对应表

| 接口 | 定义 / 调用位置 |
| --- | --- |
| 插件契约 | `Services/Plugins/IPlugin.cs` |
| 上下文与配置 | `Services/Plugins/PluginContext.cs`、`PluginConfigService.cs` |
| 插件安装与进程协议 | `Services/Plugins/PluginManager.cs` |
| 模板生成 | `Services/Plugins/PluginTemplateGenerator.cs` |
| 命令行解析 / 执行 | `Services/CommandLineService.cs`、`App.xaml.cs`、`Views/MainWindow.xaml.cs` |
| AI 请求与配置 | `Services/AiAssistantService.cs`、`Models/AiAssistantModels.cs` |
| MD 读取和导出 | `Services/EmbeddedDocumentationService.cs` |
| 文档界面 | `Views/AboutHelpPage.xaml`、`Views/HelpDocumentDialog.xaml` |
