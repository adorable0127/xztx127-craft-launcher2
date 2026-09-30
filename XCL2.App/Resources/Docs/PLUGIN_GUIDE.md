# XCL2 插件开发与导入指南

适用项目：`XCL2.App`（.NET 8 / WPF）。启动器支持两种第三方插件：实现 `IPlugin` 的托管 `.dll`，以及作为独立进程运行的 Windows `.exe`。两种格式的运行接口不同，下文分别说明。

## 一、从界面导入和查看指南

1. 将 `.dll` 或 `.exe` 文件拖入启动器窗口；也可以进入「更多 → 实验性功能 → 插件管理」，点「导入插件（DLL / EXE）」选择文件。导入插件不要求先选择游戏版本。
2. 启动器会展示文件名并提示：**该插件未经官方验证，使用之后如果出现任何问题或安全问题，均与启动器作者无关。建议你认真审查插件来源。** 只有确认后才复制和运行文件；取消则不会导入。
3. 插件复制到 `%APPDATA%\XCL2\Plugins\installed\<文件名（无扩展名）>\<原文件名>`。同名导入会显示「复制为副本」「替换」「取消」。副本有独立安装目录、配置与数据；替换停止旧实例后更新文件并保留配置与数据；取消不改变已有插件。DLL 的依赖 DLL 可以放在主 DLL 所在目录，不会被误扫为单独插件。
4. 导入后启动器自动重新扫描并按当前设置启动插件；「插件管理」显示启用状态和加载错误。手工复制到插件目录的旧版根目录 DLL 仍受支持，手工复制后需点「重新扫描 / 重新加载插件」。
5. 在「设置 → 第三方插件 → 插件启动方式」选择「每次打开启动器时启动」（默认）或「只在导入时启动一次」，然后保存设置。第二种方式会记住成功启动过的文件，下次打开启动器和重新扫描时均不会自动再运行；新导入的文件在本次确认后运行一次。
6. 在「插件管理」点「一键打开查看插件开发指南」：指南作为 `EmbeddedResource` 编进启动器，点击时写入 `%APPDATA%\XCL2\Docs\PLUGIN_GUIDE.md` 并用系统关联程序打开。单文件打包也能打开，不依赖 exe 同目录另放 MD 文件。

> 第三方代码会获得当前用户的程序权限。DLL 在启动器进程中运行；EXE 以独立进程运行。文件扩展名和托管程序集/EXE 文件头检查只能发现格式错误，不能证明来源安全。

## 二、DLL 插件：创建与构建

「插件管理 → 生成插件项目模板」可生成 `.csproj`、实现了接口的 `.cs` 和 `README.txt`。把项目里的 `<HintPath>` 改成当前宿主构建输出中的托管 `XCL2.dll` 路径（不能引用单文件发布的 EXE）；这个引用只在编译时使用，`<Private>false</Private>` 表示输出目录不复制启动器本体。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="XCL2">
      <HintPath>C:\path\to\XCL2.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

在模板目录运行 `dotnet build -c Release`。产物通常位于 `bin/Release/net8.0-windows10.0.19041.0/<项目名>.dll`；将这个主 DLL 拖入启动器，确认提示后即安装。一个主 DLL 应有一个公共、非抽象、带公共无参构造函数的 `IPlugin` 实现类。如果需要其他 NuGet DLL，请把这些依赖复制到主 DLL 安装后的同名子目录。宿主程序集 `XCL2.dll` 不要作为插件依赖再次复制。

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
| `CreateSettingsPanel(PluginContext ctx)` | `UserControl?` | 插件管理页点「配置」时调用。可返回 WPF `UserControl`，默认实现返回 `null`，宿主显示已保存配置的只读 JSON，并合并本插件注册的选项、按钮、面板。插件未在本轮运行时不可打开配置面板。 |

`Initialize` 和 `Shutdown` 在同一轮扫描中最多各调用一次；点「重新扫描」会先关停上一轮，再重新启动符合设置的插件。`Shutdown` 不保证插件程序集立刻从内存卸载；不要长期把插件类型挂在宿主静态事件上。仅导入运行一次模式下，后续启动不会调用该 DLL 的构造函数或 `Initialize`。

### `PluginContext` 完整公开成员

| 成员 | 类型 | 用法 |
| --- | --- | --- |
| `PluginContext(string pluginId, PluginConfigService config, string dataDirectory, Action<string> log)` | 构造函数 | 由宿主创建并传给 `Initialize`；一般不需要插件手动构造。 |
| `PluginId` | `string` | 原安装与 `IPlugin.Id` 相同；副本附加独立实例后缀。请用它作为实例级注册标识。 |
| `Config` | `PluginConfigService` | 当前插件的 JSON 设置入口。 |
| `DataDirectory` | `string` | 已创建的专属可写目录：`%APPDATA%\XCL2\Plugins\data\<Id>`。缓存和下载文件存这里。 |
| `Log` | `Action<string>` | 调用 `ctx.Log("消息")` 将带插件 ID 前缀的文字写入启动器日志。 |
| `ApiVersion` | `Version` | 本版扩展 API 为 1.2。 |
| `Ui` | `PluginUiService` | 原有弹窗、按钮、选项与面板入口。 |
| `Components` | `PluginComponentService` | 组件定位、增删、移动、替换和布局。 |
| `Runtime` | `PluginRuntimeService` | 游戏启动内核、头像渲染及实时模型渲染后端。 |
| `Stopping` | `CancellationToken` | 停止前取消，供后台任务响应。 |

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


## 七、开放 UI：弹窗、按钮、选项和面板

本版提供 `ctx.Ui` 注册 API，完整属性、默认值、异常约定和可直接使用的完整插件示例见 `API_REFERENCE.md` 第 10 节。

- `RegisterDialog` + `ShowDialogAsync`：用工厂注册自定义弹窗，接收参数，返回 true/false/null，可嵌套打开；宿主提供标题、关闭按钮和滚动区域。
- `RegisterButton`：注册异步按钮，支持执行中禁用、失败显示和日志。
- `RegisterOption`：注册开关、文本、数字、下拉选项，支持默认值、范围、校验和保存回调；自动使用当前插件配置文件。开关和下拉即时保存，文本和数字点击“应用”保存。
- `RegisterPanel`：在目标位置挂载自己创建的 WPF 内容，供更复杂的表单和交互使用。
- `ShowMessageAsync` / `ConfirmAsync`：直接使用宿主提示、确认弹窗。
- `SubscribeDialogs`：获取 Overlay 弹窗打开/关闭通知，不改写宿主确认流程。
- `RunOnUiAsync`：安排 UI 线程操作；后台任务使用 `ctx.Stopping` 响应停止。

按钮、选项、面板默认显示在自己的插件配置页，与原有 `CreateSettingsPanel` 共存。可通过 `Target` 指定加载器选择、插件管理、多加载器安装、创建服务端、实验性功能等弹窗，也可以用 `PluginUiTargets.ForDialog<T>()` 扩展其他 Overlay 弹窗。自己的弹窗使用 `ctx.Ui.GetDialogTarget(id)` 定位扩展区。注册 UI 并不自动实现新的 Minecraft 加载器安装流程；业务操作由插件回调实现。

注册返回的 `IDisposable` 可提前注销；若不保存返回值，宿主也会在停止、重载和初始化失败时清理注册项。宿主先取消令牌、关闭插件弹窗并移除 UI，再调用 `Shutdown()`；Shutdown 中仍可保存配置与写日志，不要重新注册 UI。

“生成插件项目模板”已同步包含文本选项、插件弹窗、配置页按钮及加载器选择弹窗按钮示例。模板引用当前构建输出的托管 `XCL2.dll`，不把宿主程序集复制进插件目录。


## 八、本次新增：管理操作与组件扩展（API 1.2）

### 插件管理

所有现有插件管理入口共用同一列表。每行新增独立的「选择」框和「删除」按钮；工具栏支持全选、启用所选、禁用所选和删除所选。启用/禁用先暂存，点击底部「保存」落盘并重新加载；「取消」丢弃未保存的开关变更。删除有一次确认，确认后立即停止并删除安装文件，不受底部「取消」影响。禁用、加载失败、DLL、EXE 均可选择和删除。

删除独立插件会删除整个安装子目录（含依赖），旧版根目录安装只删除对应入口文件。配置和 data 目录保留，避免丢失用户内容。批量删除逐项执行并报告失败，失败项仍可重试；文件占用导致残留时会明确提示。EXE 停止失败时不删除其文件。

同名导入有三个实际按钮：

- **复制为副本**：分配 `名称-copy-N` 安装目录，保留主二进制原名；安装清单记录独立实例后缀。同一 DLL 的 `IPlugin.Id` 不需要改写，宿主传入的 `ctx.PluginId` 带后缀，配置、数据、UI 注册与原件隔离。若原安装有独立依赖目录，会复制现有依赖，再写入本次导入的主文件。插件自行使用硬编码全局路径或单例资源的行为仍需插件作者处理。
- **替换**：先在暂存目录准备文件，再停止旧实例并交换安装目录；保留原配置、数据及已有依赖。成功替换重置运行记录，因此「只在导入时启动一次」也会运行新版本。导入按钮和拖拽共用流程。
- **取消**：不复制文件、不停止原实例、不改变状态。

DLL 从 `Plugins/runtime` 下的临时副本加载，因此 ALC 尚未完全释放也不会锁住原安装文件。插件应通过 `ctx.DataDirectory` 写数据，不要依赖程序集所在目录持久存储。临时文件清理失败只记录日志；卸载残留则在管理界面报告。

新增服务接口：

| 接口 | 行为 |
| --- | --- |
| `FindInstallConflict(sourcePath)` | 返回冲突安装目录或旧版入口，无冲突返回 null。 |
| `InstallFromFile(sourcePath, PluginInstallConflictAction)` | Copy / Replace / Cancel；取消冲突返回 null，成功返回入口路径。 |
| `SetEnabled(IEnumerable<string>, bool)` | 批量保存启用状态；调用 `ScanAndLoad()` 应用生命周期。 |
| `Uninstall(IEnumerable<string>)` | 返回逐项 `PluginOperationResult(FilePath, Success, Error)`；成功但有清理残留时 Error 是提示。 |
| `Changed` | 扫描、删除后通知列表更新。 |

原有单参数 `InstallFromFile` 与单文件 `SetEnabled` 保留；有重名选择需求的调用方应改用新重载。

### 任意视图的组件定位与布局

`ctx.Components.Register(PluginComponentDefinition)` 返回可撤销的 `IDisposable`。停止插件、初始化失败或删除插件时，宿主自动撤回注册的组件操作。原 `ctx.Ui` 固定扩展区 API 保持兼容。

| 字段 | 含义 |
| --- | --- |
| `Id` | 当前插件内唯一的注册 ID。 |
| `Target` | `view:MainWindow`、`view:SettingsPage`、`dialog:PluginManagerDialog` 等实际类名。 |
| `Action` | Add、Move、Remove、Replace、Configure。 |
| `Selector` | 被操作控件的唯一 Name，或 Inspect 返回的 `$root/0/1` 路径。Add 不需要。 |
| `Parent` | Add/Move 目标容器的选择器。 |
| `Index` | 插入 Panel 的位置，-1 表示末尾。 |
| `Order` | 小值先执行；同值按插件实例 ID、注册 ID 排序。 |
| `CreateContent` | Add/Replace 必填，`Func<PluginUiContext, FrameworkElement>`；每次返回全新控件。 |
| `Layout` | Grid 行列和跨度、Canvas 四边坐标、宽高、Margin、水平/垂直对齐、ZIndex、Dock。未指定值保持现状。 |

先在视图打开后用 `ctx.Components.Inspect("view:MainWindow")` 查看路径、Name、类型、父路径和实际尺寸。优先使用稳定的 Name；匿名路径依赖当前组件树顺序。名称重复或不存在会记录错误，不会猜测目标。当前支持对 Panel、Decorator、ContentControl 中的子组件作结构操作；ItemsSource 生成的列表条目等应修改数据源或替换其外层宿主。根视图本身不能删除或移动，可操作根视图的内容组件。动态重新生成的控件可调用 `ctx.Components.Refresh()` 重新应用注册。

示例：向主窗口添加精确坐标的画布及按钮，并移动现有组件：

```csharp
ctx.Components.Register(new PluginComponentDefinition
{
    Id = "canvas", Target = "view:MainWindow", Action = PluginComponentAction.Add,
    Parent = "AppBodyGrid", Order = 0,
    CreateContent = _ => new System.Windows.Controls.Canvas { Name = "MyPluginCanvas" },
    Layout = new PluginComponentLayout { Row = 0, Column = 1, ZIndex = 10 }
});
ctx.Components.Register(new PluginComponentDefinition
{
    Id = "button", Target = "view:MainWindow", Action = PluginComponentAction.Add,
    Parent = "MyPluginCanvas", Order = 1,
    CreateContent = _ => new System.Windows.Controls.Button { Content = "自定义按钮" },
    Layout = new PluginComponentLayout { Left = 120, Top = 64, Width = 140, Height = 40 }
});
ctx.Components.Register(new PluginComponentDefinition
{
    Id = "move-title", Target = "view:MainWindow", Action = PluginComponentAction.Configure,
    Selector = "TitleBarText", Layout = new PluginComponentLayout { Margin = new System.Windows.Thickness(20, 0, 0, 0) }
});
```

Remove 使用 Selector 指定组件；Move 额外指定 Parent、Index 和 Layout；Replace 额外提供 CreateContent，默认继承旧控件的布局再应用显式 Layout。多插件修改依序应用、逆序撤回；控件工厂错误会记录日志并回滚当前操作。新控件的计时器、外部订阅及原生渲染资源仍由插件响应 Unloaded / Stopping 释放。

### 渲染器与启动内核

本次提供可运行的替换接入点，并非只有配置字段：

- `ctx.Runtime.RegisterLaunchMiddleware(id, middleware, order)` 已接入 `LauncherService.Launch`。回调收到 `PluginLaunchRequest`（Plugin、Options）和 `next()`。可修改选项后调用 next，也可完全接管 Java 游戏进程创建并返回 `GameProcessInfo`。每个回调最多调用一次 next，避免重复启动。异常记录后传播，不自动重启另一套内核。回调必须同步完成，不得保留 next 在后台调用。替换内核负责自己创建的进程、输出接管和异常清理。导出启动脚本和基岩版启动流程仍使用原有实现。
- `ctx.Runtime.RegisterAvatarRenderer(id, renderer, order)` 已接入 `SkinAvatarRenderService.RenderFaceAvatar`。回调收到皮肤 PNG 字节、尺寸、帽子层选项和 next，可返回自定义 PNG。渲染器失败或未返回 PNG 签名时记录日志并回退后续渲染器。回调可能来自后台线程，不要直接操作 UI。
- 整块 UI/3D 渲染区域可用 Components 的 Replace 替换。例如 `dialog:SkinModelViewerDialog` 的 `ModelViewport`，可换成插件自己的 WPF 控件或原生图形宿主。替换后插件须实现自己的交互、数据更新和资源释放；旧控件的私有事件处理不会自动转移给新控件。

```csharp
ctx.Runtime.RegisterLaunchMiddleware("custom-launch", (request, next) =>
{
    request.Plugin.Log("自定义启动前处理");
    return next(); // 或返回由自有内核创建的 GameProcessInfo
});
ctx.Runtime.RegisterAvatarRenderer("custom-avatar", (request, next) =>
{
    return next(); // 改为返回自有渲染器生成的 PNG 字节即可接管
});
```

原件和副本分别注册，各自停止时只注销自己的扩展。正在执行的同步回调不会被强制中断，后台工作应响应 `ctx.Stopping`。

这里的「内核」是启动器的游戏启动实现，「渲染」包括头像生成和可替换视图宿主；不等于运行时替换 .NET/WPF 底层渲染引擎，也不自动修改 Minecraft JVM 内部渲染器。后者需要对应的游戏模组或原生后端实现。

### API 1.3：全视图绝对定位

Add / Move 的 `Parent = "$overlay"` 会为该视图自动建立覆盖客户内容区域的 Canvas。
`Left/Top/Right/Bottom` 均为该视图内的 DIP，不再依赖原控件所在 Grid 行列或 StackPanel 的排列。
画布空白区域不拦截输入；卸载插件会移回原控件，并撤掉画布及临时宿主。

```csharp
ctx.Components.Register(new PluginComponentDefinition
{
    Id = "move-title-anywhere", Target = "view:MainWindow",
    Action = PluginComponentAction.Move, Selector = "TitleBarText", Parent = "$overlay",
    Layout = new PluginComponentLayout
    {
        Left = 140, Top = 70, Width = 300, Height = 40,
        Margin = new System.Windows.Thickness(0), ZIndex = 20
    }
});
ctx.Components.Register(new PluginComponentDefinition
{
    Id = "new-widget", Target = "view:MainWindow", Action = PluginComponentAction.Add,
    Parent = "$overlay", Order = 10,
    Layout = new PluginComponentLayout { Right = 24, Bottom = 24, Width = 160, Height = 44 },
    CreateContent = _ => new System.Windows.Controls.Button { Content = "我的组件" }
});
```

删除使用 `Action = Remove` 和 `Selector`；替换使用 `Action = Replace` 和 `CreateContent`。
`Configure` 还可控制 Min/MaxWidth、Min/MaxHeight、Opacity、Visibility、IsHitTestVisible、
ClipToBounds、RenderTransform 和 RenderTransformOrigin。Inspect 增加 X、Y（相对于目标视图）及 ZIndex。
自定义布局可以保存在插件 Config 中，再据此注册上述操作；返回的句柄 Dispose 后即可撤销并重新注册。

### API 1.3：实时模型渲染后端

`ctx.Runtime.RegisterSkinModelRenderer(id, factory, order)` 已接入皮肤纸娃娃的 `ModelRenderHost`。
该接口替换整个渲染控件，可使用 WPF、D3DImage 或插件自行实现的原生图形宿主。
工厂收到 `PluginSkinModelRenderRequest(Plugin, Skin, SlimModel, Username)`；不传递账户令牌。
工厂及后端方法均在 UI 线程调用，每个打开的预览独立创建实例。

```csharp
ctx.Runtime.RegisterSkinModelRenderer("custom-engine", request => new MyRenderer(request), order: -10);
```

`MyRenderer` 实现 `IPluginSkinModelRenderer`：

| 成员 | 责任 |
| --- | --- |
| `FrameworkElement View` | 未挂载的新控件；自行实现旋转、缩放等交互。 |
| `SetSkin(BitmapSource, bool slimModel)` | 接收初始皮肤及在线皮肤加载后的原始贴图更新。 |
| `SetMotion(string)` | idle / walk / attack / hurt / sprint；状态变化时调用。 |
| `RenderFrame(TimeSpan elapsed)` | 每帧经过时间，独立渲染循环可留空。 |
| `Dispose()` | 释放 GPU、原生窗口、订阅和后台任务；应可重复安全调用。 |

小 Order 优先；工厂失败后尝试下一个后端，全部不可用则使用内置 WPF 3D。
运行中的后端异常会释放并回退内置渲染。禁用、删除或重新加载插件会刷新已打开的预览；
视图卸载时释放后端。启动内核仍使用 RegisterLaunchMiddleware，可完全接管进程创建。
这里开放的是启动器的组件、启动实现与渲染宿主；Minecraft 游戏内部的渲染器仍需游戏模组实现。

### 本次交付状态

按要求仅修改并打包源码，未编译，未运行检测或测试。
