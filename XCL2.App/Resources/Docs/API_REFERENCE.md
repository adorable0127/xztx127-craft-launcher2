# XCL2 启动器 API 接入说明

本文只说明当前源码已有的接入接口、参数、返回值和调用方式。

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

返回 `null` 的设置面板由宿主替换为已有配置的只读 JSON 预览，并在下方展示本插件已注册的选项、按钮和面板。仅本轮已运行的 DLL 能打开配置面板。

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
| `ApiVersion` | `Version` | UI API 版本，本版为 1.1 |
| `Ui` | `PluginUiService` | 注册弹窗、选项、按钮、面板；详见第 10 节 |
| `Stopping` | `CancellationToken` | 插件停止时取消的令牌 |

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

## 10. UI 注册 API（1.1）

本节接口均位于 `XCL2.App.Services.Plugins`，适用于本轮已经运行的 DLL 插件。原有 `IPlugin` 和 `PluginContext` 构造签名保持兼容。新插件通过 `ctx.ApiVersion` 判断 UI API 版本；`ctx.Ui` 是当前插件实例的注册表；`ctx.Stopping` 在插件停止时取消。

### 注册、调用与注销

| API | 返回值 | 行为 |
| --- | --- | --- |
| `ctx.ApiVersion` | `Version` | 本版为 `1.1`，与启动器产品版本独立 |
| `ctx.Ui` | `PluginUiService` | 当前实例的 UI 注册入口 |
| `ctx.Stopping` / `ctx.Ui.Stopping` | `CancellationToken` | 停止、重载、初始化失败时取消；可传给后台任务 |
| `Ui.RegisterDialog(PluginDialogDefinition definition)` | `IDisposable` | 注册可多次打开的弹窗工厂 |
| `Ui.RegisterButton(PluginButtonDefinition definition)` | `IDisposable` | 向目标位置注册异步操作按钮 |
| `Ui.RegisterOption(PluginOptionDefinition definition)` | `IDisposable` | 自动生成配置控件并保存到本插件 Config |
| `Ui.RegisterPanel(PluginPanelDefinition definition)` | `IDisposable` | 向目标位置注册任意 WPF 内容工厂 |
| `Ui.ShowDialogAsync(string id, object? parameter = null)` | `Task<bool?>` | 打开本插件注册的弹窗，关闭后返回结果 |
| `Ui.GetDialogTarget(string id)` | `string` | 返回本插件某个弹窗的扩展位置 `plugin:<PluginId>/<id>` |
| `Ui.ShowMessageAsync(string title, string message)` | `Task<bool?>` | 通用提示弹窗；确定返回 true，关闭返回 null |
| `Ui.ConfirmAsync(string title, string message)` | `Task<bool>` | 通用确认弹窗；仅点确定返回 true；取消、Esc、关闭均返回 false |
| `Ui.SubscribeDialogs(Action<PluginDialogEvent> observer)` | `IDisposable` | 订阅 Overlay 弹窗打开/关闭通知 |
| `Ui.RunOnUiAsync(Action action)` | `Task` | 将操作安排到宿主 UI 线程；异常通过任务返回 |
| `Ui.Dispose()` | `void` | 停止当前 UI 服务；通常由宿主自动调用，重复调用无副作用 |
| 注册返回值的 `Dispose()` | `void` | 提前注销对应条目或订阅；重复调用无副作用 |

注册 ID 在“当前插件实例 + 条目种类”范围内唯一，空白 ID 和同种类重复 ID 抛出 `ArgumentException`。不同插件可以使用相同局部 ID。弹窗、按钮、选项、面板的同名 ID 也互不冲突。宿主拒绝同时加载会映射到同一插件配置路径的重复插件 ID。

注册、注销、打开弹窗会自动切回 UI 线程；控件工厂、按钮回调、校验、配置保存、通知回调均在 UI 线程执行。按钮回调可以 `await`，执行期间该按钮禁用，异常显示在按钮旁并写日志。需要 CPU 密集工作时使用 `Task.Run`，不要在回调中同步等待 `.Wait()` / `.Result`，也不要在 UI 线程阻塞等待正在调用 UI API 的后台线程。

插件停止时，宿主先取消 `Stopping`、关闭本插件打开的弹窗、移除注册项及订阅，再调用插件自己的 `Shutdown()`。即使 `Initialize` 中途抛出异常，也会清理已注册的 UI；初始化失败不会额外调用 `Shutdown`。`Shutdown` 中仍可使用 `Config` 和 `Log`，不要重新注册 UI。停止后的注册/打开调用抛出 `ObjectDisposedException`。后台任务与插件自行订阅的外部事件仍由插件自己取消、解除。

### 可扩展的位置

| Target | 位置 |
| --- | --- |
| `PluginUiTargets.PluginSettings`（默认） | 当前插件的配置界面，仅显示自己的注册项 |
| `PluginUiTargets.PluginManager` | 插件管理弹窗底部 |
| `PluginUiTargets.LoaderChoice` | Minecraft 加载器选择弹窗底部 |
| `PluginUiTargets.MultiLoaderInstall` | 多加载器安装弹窗底部 |
| `PluginUiTargets.CreateServer` | 创建服务端弹窗底部 |
| `PluginUiTargets.ExperimentalFeatures` | 实验性功能弹窗底部 |
| `PluginUiTargets.ForDialog<T>()` | 任意实现 `IOverlayDialog` 的 `FrameworkElement` 弹窗；值为 `dialog:` 加类型短名称 |
| `Ui.GetDialogTarget(id)` | 本插件注册的自定义弹窗底部 |

所有经 `OverlayDialogService` 显示的弹窗都提供独立扩展区，包括已有的加载器选择、账户选择、Java 选择、帮助文档、插件管理等弹窗。可使用 `PluginUiTargets.ForDialog<XCL2.App.Views.AccountPickerDialog>()` 等类型安全写法。独立的系统文件选择窗口和没有经过 Overlay 宿主的 Window 不提供这个扩展位置。

扩展区支持按钮、配置选项和自定义面板，空间不足时可滚动。动态注册/注销会刷新当前扩展区；被子弹窗覆盖的扩展区在恢复显示时刷新。排列先按 `Order` 升序，再按插件 ID、条目 ID 排序。未知 Target 可以注册，但在对应宿主出现前不会显示；请优先使用常量和 `ForDialog<T>()`。

扩展区是附加 UI，不会把注册按钮自动变成 Minecraft 新加载器的安装实现，不会修改原弹窗的选中项、返回结果或确认按钮。插件如需完整业务交互，应注册自己的弹窗/面板，并在自己的回调中实现逻辑，无需反射宿主私有控件。

### 弹窗定义与结果

`PluginDialogDefinition`：

| 属性 | 类型 / 默认值 | 说明 |
| --- | --- | --- |
| `Id`、`Title` | 必填 `string` | 本插件局部 ID、显示标题 |
| `CreateContent` | 必填 `Func<PluginDialogContext, FrameworkElement>` | 每次打开创建全新的 WPF 内容，不可返回 null 或复用已有父控件的元素 |
| `Width` | `double`，560 | 允许 240～1600；显示宽度受主窗口可用宽度限制 |
| `DismissOnEscape` | `bool`，true | 是否允许 Esc 关闭 |
| `DismissOnBackgroundClick` | `bool`，false | 是否允许点击遮罩关闭 |

`PluginDialogContext` 提供 `Plugin`、`Parameter`、`Stopping`、`Close(bool? result = null)`。`Parameter` 原样传入，不会持久化。`Close(true)` 表示成功，`Close(false)` 表示取消，`Close()` 表示无结果关闭。每次打开都有独立上下文，支持同一种弹窗多次打开与弹窗内再开弹窗。宿主标题和关闭按钮始终可用，内容较多时滚动。

注销一个弹窗注册项会关闭该条目已打开的所有实例。插件停止也会关闭它的弹窗；关闭被另一层遮住的父弹窗只移除父层，不会误关当前子弹窗。关闭期间发出的后续重复关闭请求无效。

### 按钮、选项和面板定义

`PluginButtonDefinition`：必填 `Id`、`Text`、`OnClick: Func<PluginUiContext, Task>`；可选 `Target`（默认插件配置页）、`Description`（提示文字）、`Order`（默认 0）。

`PluginPanelDefinition`：必填 `Id`、`CreateContent: Func<PluginUiContext, FrameworkElement>`；可选 `Target`、`Order`。面板工厂应只创建 UI，不要在每次创建时重复注册条目。手工创建的控件和订阅由插件自己管理；宿主负责移除面板及自己的事件处理。注册表变化会重建扩展区，需保留的编辑状态应存放在插件自己的模型中。

`PluginUiContext` 提供 `Plugin`、`Target`、`Stopping`。操作按钮和面板工厂可据此访问当前插件配置、数据目录、日志或打开弹窗。

`PluginOptionDefinition`：

| 属性 | 类型 / 默认值 | 说明 |
| --- | --- | --- |
| `Id`、`Title` | 必填 `string` | 注册 ID、控件标题 |
| `Target`、`Order`、`Description` | 同按钮定义 | 位置、顺序、说明 |
| `ConfigKey` | `string?`，null | null 时使用 Id 作为本插件 Config 的键；显式键不可为空 |
| `Kind` | `PluginOptionKind`，Toggle | Toggle、Text、Number、Choice |
| `DefaultValue` | `object?`，null | 需要与类型匹配：bool、string、double、string |
| `Minimum` / `Maximum` | `double` 最小/最大有限值 | Number 的合法范围，要求下限不大于上限 |
| `Choices` | `IReadOnlyList<PluginChoice>`，空列表 | Choice 必填非空列表；Value 唯一，Value/Label 均非空 |
| `Validate` | `Func<object?, string?>?` | 保存前校验，null/空字符串通过；错误文本显示在控件旁 |
| `OnChanged` | `Action<object?>?` | 成功持久化后调用；异常记录日志，不撤销已保存值 |

`PluginChoice(string Value, string Label)` 将机器使用的字符串值与界面标签分开。下拉保存 Value，不保存标签。默认值未提供时，Toggle=false、Text=空字符串、Number=范围内最接近 0 的数、Choice=第一项 Value。数字默认值要写成 `2d` 等 double，NaN/无穷大无效。已存储值类型错误、超出范围或下拉项已移除时，界面使用默认值；直到用户操作才写回。

开关和下拉选择立即保存；文本和数字在点击各自的“应用”按钮后保存，未应用的文本不会因为关闭弹窗而写入。输入不合法或配置文件写入失败会显示错误。原有 `IPluginSettingsPanel.Save()` 仍由配置弹窗的“保存”按钮调用，自动注册选项遵循上述独立保存规则。

### 弹窗生命周期通知

`PluginDialogEvent` 包含 `Target: string`、`IsOpen: bool`、`Result: bool?`。打开通知的 Result 为 null；关闭通知的 Result 为实际结果，Esc/关闭/停止通常为 null。通知只覆盖订阅之后经 Overlay 宿主打开/关闭的弹窗，不回放历史。它是只读通知，不提供取消或替代宿主流程的权力。回调异常记录日志，不阻断其他订阅者。避免在通知里无条件再打开相同弹窗形成递归。

### 完整 UI 注册示例

```csharp
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services.Plugins;

public sealed class UiExamplePlugin : IPlugin
{
    public string Id => "example.open-ui";
    public string DisplayName => "开放界面示例";
    public string Version => "1.0.0";
    public string Description => "注册弹窗、按钮、选项和自定义面板。";
    public string Author => "Example";

    public void Initialize(PluginContext ctx)
    {
        ctx.Ui.RegisterOption(new PluginOptionDefinition
        {
            Id = "enabled", Title = "启用扩展", DefaultValue = true,
            OnChanged = value => ctx.Log($"开关已更新：{value}")
        });
        ctx.Ui.RegisterOption(new PluginOptionDefinition
        {
            Id = "parallelism", Title = "并发数", Kind = PluginOptionKind.Number,
            DefaultValue = 2d, Minimum = 1, Maximum = 8,
            Validate = value => value is double n && n == Math.Truncate(n)
                ? null : "并发数必须是整数。"
        });
        ctx.Ui.RegisterOption(new PluginOptionDefinition
        {
            Id = "profile", Title = "配置方案", Kind = PluginOptionKind.Choice,
            DefaultValue = "normal",
            Choices = new[] { new PluginChoice("normal", "默认"), new PluginChoice("fast", "快速") }
        });
        ctx.Ui.RegisterDialog(new PluginDialogDefinition
        {
            Id = "details", Title = "插件详情", Width = 520,
            CreateContent = dialog =>
            {
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock
                {
                    Text = dialog.Parameter as string ?? "来自插件的弹窗",
                    TextWrapping = TextWrapping.Wrap
                });
                var ok = new Button { Content = "完成", Margin = new Thickness(0, 12, 0, 0) };
                ok.Click += (_, _) => dialog.Close(true);
                panel.Children.Add(ok);
                return panel;
            }
        });
        ctx.Ui.RegisterButton(new PluginButtonDefinition
        {
            Id = "details-button", Text = "打开详情",
            OnClick = async ui =>
            {
                var accepted = await ui.Plugin.Ui.ShowDialogAsync("details", "可以嵌套打开其他插件弹窗。");
                if (accepted == true) ui.Plugin.Log("用户已完成操作。");
            }
        });
        ctx.Ui.RegisterButton(new PluginButtonDefinition
        {
            Id = "loader-action", Text = "插件操作", Target = PluginUiTargets.LoaderChoice,
            OnClick = async ui =>
            {
                if (await ui.Plugin.Ui.ConfirmAsync("插件操作", "是否运行示例操作？"))
                    await ui.Plugin.Ui.ShowMessageAsync("已完成", "在这里接入自己的业务逻辑。");
            }
        });
        ctx.Ui.RegisterPanel(new PluginPanelDefinition
        {
            Id = "help-panel", Target = ctx.Ui.GetDialogTarget("details"), Order = 100,
            CreateContent = ui => new TextBlock { Text = "附加说明：" + ui.Plugin.PluginId }
        });
        ctx.Ui.SubscribeDialogs(change => ctx.Log($"{change.Target} 打开={change.IsOpen} 结果={change.Result}"));
    }

    // 宿主会自动清理本示例中的注册项。自行创建的后台任务需响应 ctx.Stopping。
    public void Shutdown() { }
}
```

本例省略 `CreateSettingsPanel`，宿主仍会展示已注册选项和按钮；原有自定义设置面板可以同时保留。只需提前移除某项时，保留 `Register*` 返回的 `IDisposable` 并调用 `Dispose()`。

### 新增源码对应表

| 功能 | 源文件 |
| --- | --- |
| 公开定义、目标位置、参数与事件 | `Services/Plugins/PluginUiContracts.cs` |
| 注册、调用、线程切换、生命周期清理 | `Services/Plugins/PluginUiService.cs`、`PluginContext.cs`、`PluginManager.cs` |
| 自动控件、插件弹窗、宿主扩展区 | `Views/PluginExtensionPanel.cs` |
| 弹窗栈接入、按实例关闭、事件通知 | `Services/OverlayDialogService.cs` |
| 插件配置页合并显示 | `Views/PluginManagerDialog.xaml.cs` |
| 新插件模板 | `Services/Plugins/PluginTemplateGenerator.cs` |


## API 1.3 补充：绝对布局与模型渲染

- `PluginContext.ApiVersion` 为 1.3，原有插件接口保留。
- 组件 Add/Move 可指定 `Parent = "$overlay"`，直接以目标视图为坐标系放置。
- `PluginComponentLayout` 新增 MinWidth、MinHeight、MaxWidth、MaxHeight、Opacity、Visibility、
  IsHitTestVisible、ClipToBounds、RenderTransform、RenderTransformOrigin。
- `PluginComponentInfo` 新增 X、Y、ZIndex。
- `PluginRuntimeService.RegisterSkinModelRenderer(string id, PluginSkinModelRendererFactory factory, int order = 0)`
  返回 IDisposable。工厂返回 `IPluginSkinModelRenderer`，完整替换皮肤预览渲染后端。
- `PluginSkinModelRenderRequest`：Plugin、Skin（原始 BitmapSource）、SlimModel、Username。
- 后端提供 View、SetSkin、SetMotion、RenderFrame、Dispose；所有方法在 UI 线程执行。
- 多个渲染后端按 Order、插件 ID、注册 ID 排序选择，工厂异常尝试后续后端；
  运行异常回退内置后端。卸载插件或关闭预览自动释放。
- `RegisterLaunchMiddleware` 继续支持完全替换 Java 游戏启动内核；`RegisterAvatarRenderer` 继续支持 PNG 渲染替换。

详见 PLUGIN_GUIDE.md 的 API 1.3 示例。本次交付未编译、未检测、未测试。
