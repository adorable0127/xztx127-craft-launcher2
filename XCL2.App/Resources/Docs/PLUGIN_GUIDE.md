# XCL2 插件开发与导入指南

适用项目：`XCL2.App`（.NET 8 / WPF）。启动器支持两种第三方插件：实现 `IPlugin` 的托管 `.dll`，以及作为独立进程运行的 Windows `.exe`。两种格式的运行接口不同，下文分别说明。

## 一、从界面导入和查看指南

1. 将 `.dll` 或 `.exe` 文件拖入启动器窗口；也可以进入「更多 → 实验性功能 → 插件管理」，点「导入插件（DLL / EXE）」选择文件。导入插件不要求先选择游戏版本。
2. 启动器会展示文件名并提示：**该插件未经官方验证，使用之后如果出现任何问题或安全问题，均与启动器作者无关。建议你认真审查插件来源。** 只有确认后才复制和运行文件；取消则不会导入。
3. 插件复制到 `%APPDATA%\XCL2\Plugins\installed\<文件名（无扩展名）>\<原文件名>`。同名已安装文件不会被自动覆盖；更新时先退出启动器、备份旧文件并替换，再重新扫描。DLL 的依赖 DLL 可以放在主 DLL 所在目录，不会被误扫为单独插件。
4. 导入后启动器自动重新扫描并按当前设置启动插件；「插件管理」显示启用状态和加载错误。手工复制到插件目录的旧版根目录 DLL 仍受支持，手工复制后需点「重新扫描 / 重新加载插件」。
5. 在「设置 → 第三方插件 → 插件启动方式」选择「每次打开启动器时启动」（默认）或「只在导入时启动一次」，然后保存设置。第二种方式会记住成功启动过的文件，下次打开启动器和重新扫描时均不会自动再运行；新导入的文件在本次确认后运行一次。
6. 在「插件管理」点「一键打开查看插件开发指南」：指南作为 `EmbeddedResource` 编进启动器，点击时写入 `%APPDATA%\XCL2\Docs\PLUGIN_GUIDE.md` 并用系统关联程序打开。单文件打包也能打开，不依赖 exe 同目录另放 MD 文件。

> 第三方代码会获得当前用户的程序权限。DLL 在启动器进程中运行；EXE 以独立进程运行。文件扩展名和托管程序集/EXE 文件头检查只能发现格式错误，不能证明来源安全。

## 二、DLL 插件：创建与构建

「插件管理 → 生成插件项目模板」可生成 `.csproj`、实现了接口的 `.cs` 和 `README.txt`。把项目里的 `<HintPath>` 改成安装后的 `XCL2.exe` 路径；这个引用只在编译时使用，`<Private>false</Private>` 表示输出目录不复制启动器本体。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="XCL2">
      <HintPath>C:\path\to\XCL2.exe</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

在模板目录运行 `dotnet build -c Release`。产物通常位于 `bin/Release/net8.0-windows/<项目名>.dll`；将这个主 DLL 拖入启动器，确认提示后即安装。一个主 DLL 应有一个公共、非抽象、带公共无参构造函数的 `IPlugin` 实现类。如果需要其他 NuGet DLL，请把这些依赖复制到主 DLL 安装后的同名子目录。宿主程序集 `XCL2.exe` 不要作为插件依赖再次复制。

最小实现：

```csharp
using System.Windows.Controls;
using XCL2.App.Services.Plugins;

public sealed class ExamplePlugin : IPlugin
{
    public string Id => "example.demo";
    public string DisplayName => "演示插件";
    public string Version => "1.0.0";
    public string Description => "保存一次计数并写日志。";
    public string Author => "Example";
    private PluginContext? _context;

    public void Initialize(PluginContext ctx)
    {
        _context = ctx;
        int count = ctx.Config.Get("starts", 0);
        ctx.Config.Set("starts", count + 1);
        ctx.Log($"本插件累计启动 {count + 1} 次");
    }

    public void Shutdown()
    {
        _context?.Log("停止运行");
        _context = null;
    }

    public UserControl? CreateSettingsPanel(PluginContext ctx)
        => new UserControl { Content = new TextBlock { Text = $"数据目录：{ctx.DataDirectory}" } };
}
```

### `IPlugin` 完整接口

| 成员 / 函数 | 类型 / 入参 → 返回 | 约定 |
| --- | --- | --- |
| `Id` | `string` | 唯一且稳定的标识，用于配置文件和数据目录；建议 `作者.插件名`，发布后不要更改。 |
| `DisplayName` | `string` | 列表显示名称，可修改。 |
| `Version` | `string` | 展示版本号，建议 `1.0.0`。 |
| `Description` | `string` | 列表中的功能描述。 |
| `Author` | `string` | 作者名称；可返回空字符串。 |
| `Initialize(PluginContext ctx)` | `void` | 符合启动策略时在 UI 线程调用一次；保存上下文、挂接事件、启动任务。避免同步阻塞 UI；异常会在管理页标为加载失败。 |
| `Shutdown()` | `void` | 启动器退出、禁用后重新扫描、手动重新扫描时调用；解除事件订阅、取消任务并释放资源。仅对本轮成功初始化的 DLL 调用。 |
| `CreateSettingsPanel(PluginContext ctx)` | `UserControl?` | 插件管理页点「配置」时调用。可返回 WPF `UserControl`，默认实现返回 `null`，宿主改为显示已保存配置的只读 JSON。插件未在本轮运行时不可打开配置面板。 |

`Initialize` 和 `Shutdown` 在同一轮扫描中最多各调用一次；点「重新扫描」会先关停上一轮，再重新启动符合设置的插件。`Shutdown` 不保证插件程序集立刻从内存卸载；不要长期把插件类型挂在宿主静态事件上。仅导入运行一次模式下，后续启动不会调用该 DLL 的构造函数或 `Initialize`。

### `PluginContext` 完整公开成员

| 成员 | 类型 | 用法 |
| --- | --- | --- |
| `PluginContext(string pluginId, PluginConfigService config, string dataDirectory, Action<string> log)` | 构造函数 | 由宿主创建并传给 `Initialize`；一般不需要插件手动构造。 |
| `PluginId` | `string` | 与 `IPlugin.Id` 相同。 |
| `Config` | `PluginConfigService` | 当前插件的 JSON 设置入口。 |
| `DataDirectory` | `string` | 已创建的专属可写目录：`%APPDATA%\XCL2\Plugins\data\<Id>`。缓存和下载文件存这里。 |
| `Log` | `Action<string>` | 调用 `ctx.Log("消息")` 将带插件 ID 前缀的文字写入启动器日志。 |

### `PluginConfigService` 完整公开方法

配置文件为 `%APPDATA%\XCL2\Plugins\config\<Id>.json`。它只隔离文件路径，不是进程权限隔离；请不要向不可信插件提供敏感数据。

| 方法 | 输入 → 输出 | 失败/保存行为 |
| --- | --- | --- |
| `Get<T>(string key, T fallback)` | 键及默认值 → `T` | 键不存在或反序列化失败时返回 `fallback`，不抛反序列化异常。 |
| `Set<T>(string key, T value)` | 键及可 JSON 序列化的值 → `void` | 覆盖键并同步重写配置文件；序列化或文件 IO 失败可能抛异常。 |
| `Remove(string key)` | 键 → `void` | 存在时删除并同步保存，不存在时不写文件。 |
| `Snapshot()` | 无参 → `IReadOnlyDictionary<string, JsonElement>` | 获取当前配置视图，用于管理页只读预览；调用者不要修改其中的 `JsonElement`。 |

例如：`var enabled = ctx.Config.Get("enabled", true); ctx.Config.Set("enabled", false); ctx.Config.Remove("old-key");`。配置的内部构造和保存函数由宿主调用，并不是对插件开放的公共 API。

### 配置面板的"保存"按钮：`IPluginSettingsPanel`（可选）

插件管理页打开某个插件的配置面板时，宿主弹窗（`PluginSettingsHostDialog`）自带「保存」「关闭」两个按钮。「保存」按钮的行为：

- 如果 `CreateSettingsPanel` 返回的 `UserControl` 实现了 `IPluginSettingsPanel`（只有一个 `void Save()` 方法），点「保存」时会调用它，方便你把面板上多个输入控件的当前值一次性批量写入 `ctx.Config`（例如表单里有校验逻辑、或者几个字段要互相配合校验时，比"改一个存一个"更合适）。`Save()` 里抛出的异常会被宿主捕获并以警告弹窗提示用户，配置弹窗不会关闭，方便用户改正后再保存。
- 如果面板没有实现这个接口——包括"每个控件的事件里直接调用 `ctx.Config.Set`，改一个存一个"这种风格的面板，以及没有提供图形界面时宿主生成的只读 JSON 预览——点「保存」不会报错，只是弹出一次"配置已保存"的提示，不会做额外的事：因为 `Set` 本身每次调用都已经同步落盘，不需要再补一次保存动作。

## 三、EXE 插件：进程协议与限制

EXE 不实现 `IPlugin`，不加载到启动器进程，也没有 `PluginContext`、配置面板或可调用的宿主 API。用户确认导入后，宿主通过 `ProcessStartInfo` 启动安装目录中的 exe，工作目录是 exe 所在目录，不附加命令行参数，`UseShellExecute=false`。

| 进程输入 / 生命周期 | 约定 |
| --- | --- |
| 环境变量 `XCL2_PLUGIN_DATA_DIR` | 启动器为该 EXE 创建的专属可写目录，可用 `Environment.GetEnvironmentVariable` 读取。 |
| 标准命令行参数 | 没有，EXE 不必解析特殊参数。 |
| 启动时机 | 默认每次打开启动器及用户点「重新扫描」时运行；「只在导入时启动一次」在成功启动后记住状态。 |
| 关闭时机 | 启动器退出、重新扫描/重载时会结束仍由启动器托管的 EXE 进程树；已经自行退出的程序无需处理。 |
| 错误反馈 | 进程启动失败显示在插件管理页并写启动器日志；启动后的内部错误由 EXE 自己记录。 |

EXE 的运行不等于 DLL 的 API 插件能力。如果要用 `PluginContext.Config`、`Log` 或设置面板，请编译成实现 `IPlugin` 的 DLL。

## 四、宿主侧管理 API（供启动器开发者）

以下 API 属于启动器内部集成入口；普通 DLL 插件只需实现第二节的接口。

| API | 参数 → 返回 | 说明 |
| --- | --- | --- |
| `new PluginManager(Action<string>? logSink = null)` | 日志回调 → `PluginManager` | 创建目录，初始化管理器。主窗口保留一个实例。 |
| `PluginManager.InstalledDirectory` | 属性 → `string` | 已安装插件根目录。 |
| `PluginManager.Plugins` | 属性 → `IReadOnlyList<LoadedPluginInfo>` | 最近一次扫描结果；用于渲染状态。 |
| `PluginManager.StartPolicy` | `PluginLaunchMode` 读写 | 扫描前设置为配置里的策略。`EachLaunch` 默认；`OnceOnImport` 只启动尚未成功启动过的文件。 |
| `PluginManager.InstallFromFile(string sourcePath)` | DLL/EXE 的绝对路径 → 已复制目标路径 | 校验格式，复制到插件子目录；同名已存在会抛 `IOException`。本函数本身不运行插件，调用方必须先确认用户意愿，随后调用 `ScanAndLoad()`。 |
| `PluginManager.ScanAndLoad()` | 无参 → `void` | 结束上轮、扫描插件、按策略启动、持久化状态。单个插件失败记录 `LoadError`，不阻断其他插件。 |
| `PluginManager.SetEnabled(string filePath, bool enabled)` | 安装路径及布尔值 → `void` | 立即持久化启用开关；再调用 `ScanAndLoad()` 才实际启动/停止。 |
| `PluginManager.ShutdownAll()` | 无参 → `void` | 逐个调用已启动 DLL 的 `Shutdown`、结束托管 EXE、卸载 DLL 加载上下文。 |
| `PluginTemplateGenerator.Generate(string targetDir, string pluginId, string displayName)` | 目录、插件 ID、显示名 → `void` | 在目标目录生成 .NET 8 WPF 插件项目及说明；目标目录应为空或不存在。 |

`LoadedPluginInfo` 向管理页公开的字段：`FilePath`（安装路径）、`Plugin`（已运行 DLL 实例；未运行或失败为 `null`）、`LoadError`（错误文本）、`Enabled`（用户开关）、`Context`（本轮运行的 DLL 上下文）、`IsExecutable`（是否 EXE）、`RunningProcess`（本轮托管进程）、`StartedOnce`（是否已成功启动）。`PluginLaunchMode` 的枚举值为 `EachLaunch` 与 `OnceOnImport`；保存位置是全局 `AppConfig.PluginLaunchMode`，插件运行记录保存在 `%APPDATA%\XCL2\Plugins\plugins-state.json`。旧版单纯布尔值的启用记录可以读取并升级。

**调用顺序示例**（提示确认由 UI 层负责）：

```csharp
manager.StartPolicy = config.PluginLaunchMode;
manager.InstallFromFile(selectedPluginPath);
manager.ScanAndLoad();
// 程序退出时：manager.ShutdownAll();
```

## 五、快捷启动指令

命令行 `-gui <页面名>` 仍受支持。新增简写与其使用同一份页面搜索索引；一次指定一个页面即可：

| 指令 | 打开的页面/面板 | 指令 | 打开的页面/面板 |
| --- | --- | --- | --- |
| `--home` | 首页 | `--versions` | 版本管理 |
| `--downloads` | 下载中心 | `--multiplayer` | 联机 |
| `--mods` | Mod 管理 | `--servers` | 服务端管理 |
| `--accounts` | 账户 | `--settings` | 设置 |
| `--logs` | 日志 | `--toolbox` | 百宝箱 |
| `--bedrock` | 基岩版启动 | `--about` | 鸣谢与帮助 |
| `--experimental` | 实验性功能 | `--ai` | AI 助手 |

例如 `XCL2.exe --settings`、`XCL2.exe --logs`。输入 `XCL2.exe -help` 可查看完整参数说明；命令行语法由 `CommandLineService.Parse(string[] args)` 解析，页面导航由主窗口在首帧显示后执行。

## 六、排查

- **DLL 显示加载失败**：确认是 .NET 8 WPF 类库，公共实现类有公共无参构造函数，引用了当前启动器程序集，依赖 DLL 与主 DLL 在同一子目录。管理页 `LoadError` 和启动器日志会提供具体异常。
- **没有再运行**：检查「设置 → 第三方插件」是否选了「只在导入时启动一次」。切回「每次打开启动器时启动」并保存，下次启动或手动重新扫描即可运行。
- **更改启用开关没立即生效**：点「重新扫描 / 重新加载插件」。只有当前运行中的 DLL 才能打开它的配置界面。
- **打开指南失败**：确认系统为 `.md` 文件设置了关联程序，并检查 `%APPDATA%\XCL2\Docs` 是否可写。源码仓库中的 `PLUGIN_GUIDE.md` 也可以直接阅读。
