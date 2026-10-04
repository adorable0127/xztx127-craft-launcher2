# XCL2 启动器完整 API 接入说明

## 接入总览

| 目的 | 入口 | 调用方向 |
| --- | --- | --- |
| 扩展界面和业务 | DLL + `IPlugin` + `PluginContext` | 启动器调用插件，插件经 `ctx.*` 请求能力 |
| 独立进程插件 | EXE + `plugin.json` + 本地命名管道协议 1 | 双向 JSON 请求/响应/事件 |
| 外部自动化 | 命令行与可选 `--result-file` | 外部程序启动 XCL2 |
| AI 服务 | Chat Completions 兼容 HTTP | 启动器请求用户配置的服务 |
| 插件管理 / 帮助文档 | `PluginManager` / `EmbeddedDocumentationService` | 宿主源码调用 |

以下 C# 方法是本地接口，不是 HTTP 路由；权限仅对新清单插件的新能力生效。

## DLL 插件契约：IPlugin

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

清单插件必须声明 `ui.basic` 才能使用旧的弹窗/按钮/选项/面板 API；没有清单的插件继续按旧版兼容模式获得这组能力。基础 `Config`、`DataDirectory` 和 `Log` 不要求业务权限。

## PluginContext：宿主提供的能力

`Initialize` 和 `CreateSettingsPanel` 的上下文由宿主创建并传入。

| 成员 | 类型 | 用法 |
| --- | --- | --- |
| `PluginId` | `string` | 与 `IPlugin.Id` 一致 |
| `Config` | `PluginConfigService` | 读写本插件配置 |
| `DataDirectory` | `string` | 已创建的专属可写目录，保存插件缓存、下载内容或其他数据 |
| `Log` | `Action<string>` | `ctx.Log("消息")`，宿主自动添加插件标识并写入启动器日志 |
| `ApiVersion` | `Version` | 插件 API 版本，当前源码返回 1.9 |
| `Ui` | `PluginUiService` | 注册弹窗、选项、按钮、面板；详见第 10 节 |
| `Stopping` | `CancellationToken` | 插件停止时取消的令牌 |
| `Metadata` | `PluginMetadata` | 清单或兼容元数据；`IsLegacy` 指示旧模式 |
| `Permissions` | `PluginPermissionService` | 授权查询与强制检查 |
| `Launcher` | `PluginLauncherService` | 只读业务服务 |
| `Events` | `PluginEventService` | 通知订阅和可干预事件 |
| `Providers` | `PluginProviderService` | 下载源、Mod 来源、加载器、登录方式 |
| `Network` | `PluginNetworkService` | 不带宿主凭据的 HTTP GET |
| `Messages` | `PluginMessageService` | 依赖插件之间的 JSON 消息 |
| `Components` / `Runtime` | `PluginComponentService` / `PluginRuntimeService` | 可撤销 UI 组件补丁、兼容运行时渲染入口 |

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

此段是**无清单旧插件示例**，仍可按 1.1 兼容模式加载。示例的配置写入由 UI 线程顺序执行。实际插件如有后台任务，在 `Shutdown` 中取消任务并释放资源；不要将密钥、令牌或登录凭据写入 `ctx.Log`。

## plugin.json 清单与依赖

新插件将 `plugin.json` 与入口 DLL/EXE 放在同一安装目录（打包导入支持 `.xclplugin`）；清单格式示例：

```json
{
  "schemaVersion": 1,
  "id": "example.reader",
  "name": "实例读取示例",
  "version": "1.0.0",
  "author": "Example",
  "description": "只读取实例及生命周期事件",
  "entry": "Example.Reader.dll",
  "kind": "dll",
  "minApiVersion": "1.9",
  "maxApiVersion": "1.9",
  "minLauncherVersion": "1.0",
  "dependencies": [
    { "id": "example.core", "versionRange": ">=1.0.0 <2.0.0", "optional": true }
  ],
  "conflicts": [],
  "permissions": ["instances.read", "events.lifecycle"],
  "priority": 0
}
```

`entry` 必须是包根目录的文件名；`kind` 为 `dll` 或 `exe`，与文件扩展名一致。`id` 为小写字母/数字开头、后接小写字母/数字/点/下划线/连字符，最多 128 字符。版本是 SemVer，如 `1.0.0`；API 范围使用 `major.minor`，`maxApiVersion` 可省略；`minLauncherVersion` 可省略。依赖版本范围使用空格分隔的比较项，如 `>=1.2.0 <2.0.0`，不是 npm 的 `^`/`~` 写法。`priority` 范围 `-1000` 至 `1000`；干预链先按优先级降序，再按插件实例 Id 字典序、注册顺序执行。宿主在加载前校验依赖、冲突、版本和环；按依赖顺序初始化，反向停止；失败原因进入管理页 `LoadError`/`Diagnostics`。

没有清单的旧插件按 1.1 兼容模式载入；仅能调用旧时代的配置/日志/数据目录/UI 基础能力，不能使用新业务服务。清单中所列权限由用户在插件管理页逐项授权；权限变更后立即按当前授权检查，撤销会使相应注册项不可用。新增能力的权限在 `PluginPermissionNames` 中列出，调用前可用 `ctx.Permissions.Has(name)` 或 `GetSnapshot()` 查询。未获准调用抛 `PluginPermissionException`，没有宿主连接抛 `PluginHostUnavailableException`，业务错误使用带 `Code` 的 `PluginOperationException`。

同目录的 `plugin.json` 是插件发布清单；`.xclplugin` 是导入时使用的包格式。`PluginMetadata` 包含 `DeclaredId`、`InstanceId`、`Name`、`Version`、`Author`、`Description`、API 兼容范围、启动器最低版本、`Dependencies`、`Conflicts`、`Permissions`、`Priority`、`IsLegacy`。`PluginDependency` 字段为 `Id`、`VersionRange`、`Optional`。清单校验失败时插件不会正常初始化，管理页 `LoadedPluginInfo.LoadError` / `Diagnostics` 提示原因；插件启动顺序依赖拓扑、停止逆序。首次启用和权限变更需在管理页授权，宿主配置保存授权，插件自己的 `Config` 不存授权。

## 只读业务服务

所有业务读取可从任意线程 `await`，返回独立快照或只读 DTO；异步方法接受 `CancellationToken`。`PluginListSnapshot<T>` 含 `Revision`、`CapturedAtUtc`、`Items`。空选中项返回 `null`；读操作期间撤销权限会报 `AuthorizationChanged`，资源不存在时也可能返回 `null` 或抛 `PluginResourceNotFoundException`，由具体方法决定。

| 入口 | 方法 | 返回内容/权限 |
| --- | --- | --- |
| `ctx.Launcher.Accounts` | `GetSnapshotAsync(ct)`、`GetSelectedAsync(ct)` | `PluginAccount`；`accounts.read` |
| `ctx.Launcher.Instances` | `GetSnapshotAsync(query, ct)`、`GetDetailsAsync(id, ct)`、`GetSelectedAsync(ct)` | `PluginInstance` / `PluginInstanceDetails`；`instances.read` |
| `ctx.Launcher.Mods` | `GetSnapshotAsync(instanceId, ct)` | `PluginMod`；`mods.read` |
| `ctx.Launcher.Downloads` | `GetSnapshotAsync(ct)` | `PluginDownload`；`downloads.read` |
| `ctx.Launcher.Settings` | `GetSnapshotAsync(ct)` | `PluginSettingsSnapshot`；`settings.read` |
| `ctx.Launcher.Environment` | `GetSnapshotAsync(ct)` | `PluginEnvironmentSnapshot`；`environment.read` |

`PluginAccount` **仅**有 `Id`、`Kind`、`DisplayName`、`Uuid`、`IsSelected`、`IsGuest`；无密码、访问令牌、刷新令牌、Cookie。`PluginInstance` 提供 Id/名称/MC 版本/加载器标识和版本/安装、选中及隐藏标志；详情提供实例与游戏目录、隔离状态、配置的 Java 路径及 JVM 参数。`PluginMod` 提供文件名、展示名、可识别的 ModId/版本、启用状态、大小；ModId/版本可为空。设置快照白名单仅有语言、皮肤、深色/简洁模式、页面/UI 动画、缩放、最小/最大内存、默认隔离和内置下载源；不含敏感设置。

最小 DLL 用法：

```csharp
using XCL2.App.Services.Plugins;

public sealed class ReaderPlugin : IPlugin
{
    public string Id => "example.reader";
    public string DisplayName => "实例读取示例";
    public string Version => "1.0.0";
    public string Description => "读取实例并监听变化";
    public string Author => "Example";

    public void Initialize(PluginContext ctx)
    {
        ctx.Events.Subscribe<PluginInstanceCatalogChanged>(async (_, ct) =>
        {
            var snapshot = await ctx.Launcher.Instances.GetSnapshotAsync(ct: ct);
            ctx.Log($"现在有 {snapshot.Items.Length} 个实例");
        });
    }
    public void Shutdown() { }
}
```

此示例清单需要 `instances.read`；回调与宿主默认异常隔离，插件日志由宿主加 `[Plugin:<Id>]` 前缀。初始化时查询可以在插件自行启动的后台任务中执行，注意观察异常与 `ctx.Stopping`。

## 通知与干预事件

通知订阅签名为 `ctx.Events.Subscribe<TEvent>(Func<TEvent,CancellationToken,ValueTask>)`，返回 `IDisposable`。回调在插件后台域串行执行，不在 UI 线程；需要更新 WPF 时使用 Dispatcher。单插件队列容量 256，过载时丢弃新通知并写日志；通过 `Sequence` 发现缺口后应重新查询快照。下载进度通知按宿主节流，事件数据只给出只读 DTO；插件没有伪造业务通知的公开入口。

| 通知类型 | 触发类别 | 所需权限 |
| --- | --- | --- |
| `PluginLauncherReady`、`PluginLauncherStopping` | 启动完成、即将退出 | `events.lifecycle` |
| `PluginAccountAdded`、`PluginAccountUpdated`、`PluginAccountRemoved`、`PluginAccountSelectionChanged` | 账户列表与选中变化 | `accounts.read` |
| `PluginInstanceCreated`、`PluginInstanceRemoved`、`PluginInstanceRenamed`、`PluginInstanceSelectionChanged`、`PluginInstanceCatalogChanged`、`PluginInstanceVisibilityChanged` | 实例变化 | `instances.read` |
| `PluginDownloadCreated`、`PluginDownloadProgressChanged`、`PluginDownloadCompleted`、`PluginDownloadFailed`、`PluginDownloadCanceled`、`PluginDownloadRemoved` | 下载任务变化 | `downloads.read` |
| `PluginModInstalled`、`PluginModRemoved`、`PluginModEnabledChanged`、`PluginModCatalogChanged` | Mod 变化 | `mods.read` |
| `PluginGameStarted`、`PluginGameExited`、`PluginGameCrashed` | 游戏 PID、退出码和崩溃 | `instances.read` |
| `PluginSettingsChanged` | 设置键名变化 | `settings.read` |
| `PluginPeerUnavailable` | 依赖插件失去连接 | `plugin.messaging` |

所有通知继承 `PluginNotification`，带 `Sequence`、`Revision`、`OccurredAtUtc`；具体字段以类型的公开 record 为准。订阅不保证补发过去事件。

干预注册签名：

```csharp
ctx.Events.InterceptLaunch((PluginLaunchPlan plan, CancellationToken ct) =>
    ValueTask.FromResult(PluginInterception<PluginLaunchPlan>.Continue(
        plan with { JvmArguments = plan.JvmArguments.Add("-Xmx4G") })));
ctx.Events.InterceptDownload((PluginDownloadPlan plan, CancellationToken ct) =>
    ValueTask.FromResult(PluginInterception<PluginDownloadPlan>.Continue()));
ctx.Events.InterceptInstanceCreation((PluginInstanceCreationPlan plan, CancellationToken ct) =>
    ValueTask.FromResult(PluginInterception<PluginInstanceCreationPlan>.Continue()));
```

需分别声明 `launch.modify`、`downloads.intercept`、`instances.validate`。`Continue(replacement)` 将合法的新计划传给后续插件；`Continue()` 保持原计划；`Cancel(reason)` 抛出带插件 Id 和原因的 `PluginFlowCanceledException`，宿主应向用户展示。启动计划可改 JVM/游戏参数及环境变量，受保护的身份/令牌参数不得修改；下载计划只可改公开 HTTP(S) URL，含查询串或用户信息的链接不会送入插件；实例创建计划只允许原样通过或取消。每插件默认 3 秒，超时或异常记日志并继续链；超时的 DLL 代码不能强杀，宿主会暂停该插件后续业务回调。干预行为写 `[Plugin:<Id>]` 日志。

## 原有 UI 注册 API（1.1 契约，1.9 仍保留）

本节接口均位于 `XCL2.App.Services.Plugins`，适用于本轮运行的 DLL 插件。无清单的旧插件沿用 1.1 UI 能力；有清单的新插件须声明并获授权 `ui.basic`。原有 `IPlugin` 和 `PluginContext` 构造签名保持兼容。新插件通过 `ctx.ApiVersion` 判断 UI API 版本；`ctx.Ui` 是当前插件实例的注册表；`ctx.Stopping` 在插件停止时取消。

### 注册、调用与注销

| API | 返回值 | 行为 |
| --- | --- | --- |
| `ctx.ApiVersion` | `Version` | 当前源码为 `1.9`；1.1 的 UI 方法保持原有签名 |
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

扩展区是附加 UI，不会修改原弹窗的选中项、返回结果或确认按钮。加载器真正的安装能力通过下文 `RegisterLoaderInstaller` 提供；不要把 `LoaderChoice` 位置的普通按钮误认为安装器注册。

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
| 当前模板（尚未更新 1.9） | `Services/Plugins/PluginTemplateGenerator.cs` |

## 业务提供者、导航与插件消息

| 注册入口 | 实现接口 | 作用/权限 |
| --- | --- | --- |
| `ctx.Providers.RegisterDownloadProvider(def, provider)` | `IPluginDownloadProvider`：`GetManifestAsync`、`ResolveFileAsync` | 版本清单和公开文件镜像；`providers.download` + `network` |
| `RegisterModProvider` | `IPluginModProvider`：搜索、详情、文件列表 | Mod 来源；`providers.mod` + `network` |
| `RegisterLoaderInstaller` | `IPluginLoaderInstaller`：构建列表、`InstallAsync` | 加载器选择与安装；`providers.loader`，工作区写入/联网/运行安装器需另行授权 |
| `RegisterLoginProvider` | `IPluginLoginProvider.LoginAsync` | 通过 `PluginLoginBroker` 发起离线、Microsoft Device Code、Yggdrasil 宿主授权并拿到不透明票据；`auth.provider` |

提供者定义 `new PluginProviderDefinition(id, name)`；注册返回 `IDisposable`，停止时自动解除。UI 按来源显示给用户选择；插件回调抛错、超时或无效时记录并回退内置来源，具体下载/安装动作须遵循各自 UI 的错误反馈。`ctx.Network.GetTextAsync(url, ct)` 仅以无宿主凭据的 HTTP GET 获取最多 4 MiB 文本，需要 `network`。加载器安装使用 `PluginInstallWorkspace.WriteVersionJsonAsync`、`DownloadFileAsync`、`RunJavaInstallerAsync`，受工作区路径和权限约束。任意自定义登录协议不在当前 broker 支持列表。

旧版 `ctx.Ui.RegisterDialog`、`RegisterButton`、`RegisterOption`、`RegisterPanel`、`SubscribeDialogs`、`ShowMessageAsync`、`ConfirmAsync` 仍按原有弹窗扩展区语义工作，不会改写宿主原弹窗的确认结果。新 UI 入口为 `RegisterNavigationPage(PluginNavigationPageDefinition)`、`RegisterInstanceAction(PluginInstanceActionDefinition)`、`RegisterSetting(PluginOptionDefinition)`、`RegisterBackground(PluginBackgroundDefinition)` 和 `ImportBackgroundImageAsync(ct)`；对应权限见下表。导航页面支持简洁模式可见性与语言名单；背景图片须存于插件数据目录，支持 png/jpg/jpeg/bmp/gif，不超过 32 MiB。`ImportBackgroundImageAsync` 使用系统文件选择器并额外需要 `files.external.read`。WPF 创建工厂在 UI 线程调用，应避免阻塞；回调异常由宿主记录。实例卡片操作由实例详情 DTO 驱动，不传递内部 ViewModel。

`ctx.Messages.GetPeers()`、`Subscribe(channel, callback)`、`SendAsync(targetPluginId, channel, JsonElement, ct)` 以清单声明的依赖为可通信对象，需要双方 `plugin.messaging`；JSON 载荷最多 64 KiB，不共享对象引用，停止依赖方会发 `PluginPeerUnavailable`。

### 提供者接口签名与选择

| 接口 | 成员签名（返回 `ValueTask`） | 用户选择与失败行为 |
| --- | --- | --- |
| `IPluginDownloadProvider` | `GetManifestAsync(CancellationToken)` → `PluginVersionManifest`；`ResolveFileAsync(PluginFileRequest,CancellationToken)` → `string?` | 下载中心选择来源；空镜像地址沿用原 URL；无效清单/回调失败回落到内置下载 |
| `IPluginModProvider` | `SearchAsync(PluginModQuery,CancellationToken)` → `PluginModSearchResult`；`GetDetailsAsync(string,CancellationToken)` → `PluginModProject?`；`GetFilesAsync(string,string?,string?,CancellationToken)` → `ImmutableArray<PluginModFile>` | Mod 来源页选择提供者；失败提示或回到内置搜索 |
| `IPluginLoaderInstaller` | `GetBuildsAsync(string,CancellationToken)` → `ImmutableArray<PluginLoaderBuild>`；`InstallAsync(PluginInstallWorkspace,PluginLoaderInstallRequest,CancellationToken)` | 加载器选择弹窗选择插件项；安装只在受限工作区执行，失败不应提交实例 |
| `IPluginLoginProvider` | `LoginAsync(PluginLoginBroker,CancellationToken)` → `PluginLoginTicket?` | 登录页选择插件入口；取消返回 null，凭据经宿主登录代理保存 |

`PluginVersionManifest` 包含 `LatestRelease`、`LatestSnapshot`、`Versions`；`PluginVersionEntry` 含 `Id`、`Type`、`Url`、`ReleaseTime`。`PluginFileRequest` 是公开原 URL、文件名和可选 SHA1。Mod 查询包含查询文本、游戏版本、加载器、Offset、Limit；结果含 `Items` 与 `Total`，项目/文件 DTO 包含可展示及下载的公开字段。`PluginLoginBroker.AuthorizeAsync(PluginLoginRequest, ct)` 只支持 `Offline`、`MicrosoftDeviceCode`、`Yggdrasil`；票据仅供宿主消费。工作区方法接受取消令牌；目录外写入与带敏感信息的 URL 会拒绝。提供者注册可在任意线程调用，实际提供者方法在插件后台域执行并有超时；错误经 `PluginOperationException.Code` 或宿主日志反馈，不能假定每一种业务错误都自动切换到其他提供者。

### 完整下载源注册示例

下例从宿主源码已经使用的官方版本清单地址读取公开 JSON，并可为 `launchermeta.mojang.com` 返回镜像 URL。清单需声明并获授权 `providers.download` 与 `network`；网络服务不附带宿主账户凭据。

```csharp
using System.Collections.Immutable;
using System.Text.Json;
using XCL2.App.Services.Plugins;

public sealed class MirrorPlugin : IPlugin, IPluginDownloadProvider
{
    private PluginContext? _ctx;
    public string Id => "example.mirror";
    public string DisplayName => "镜像来源示例";
    public string Version => "1.0.0";
    public string Description => "提供版本清单和公开镜像";
    public string Author => "Example";

    public void Initialize(PluginContext ctx)
    {
        _ctx = ctx;
        ctx.Providers.RegisterDownloadProvider(new PluginProviderDefinition("mirror", "镜像来源"), this);
    }
    public void Shutdown() => _ctx = null;

    public async ValueTask<PluginVersionManifest> GetManifestAsync(CancellationToken ct)
    {
        var json = await _ctx!.Network.GetTextAsync(
            "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json", ct);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var latest = root.GetProperty("latest");
        var versions = root.GetProperty("versions").EnumerateArray()
            .Select(v => new PluginVersionEntry(
                v.GetProperty("id").GetString()!, v.GetProperty("type").GetString()!,
                v.GetProperty("url").GetString()!, v.GetProperty("releaseTime").GetString()!))
            .ToImmutableArray();
        return new PluginVersionManifest(latest.GetProperty("release").GetString()!,
            latest.GetProperty("snapshot").GetString()!, versions);
    }
    public ValueTask<string?> ResolveFileAsync(PluginFileRequest request, CancellationToken ct)
    {
        const string origin = "https://launchermeta.mojang.com/";
        return ValueTask.FromResult<string?>(request.OriginalUrl.StartsWith(origin, StringComparison.Ordinal)
            ? "https://bmclapi2.bangbang93.com/" + request.OriginalUrl[origin.Length..]
            : null);
    }
}
```

示例的远程响应字段、大小、权限和网络可用性必须在实际运行时处理；完整性校验仍由下载流程负责。注册/反注册不会改变旧版扩展区按钮行为。

## 组件布局与渲染兼容入口

`ctx.Components.Register(PluginComponentDefinition)` 在 UI 线程应用可撤销组件补丁；`Inspect(target)` 返回 `PluginComponentInfo` 结构快照；`Refresh()` 请求重绘。`PluginComponentAction` 为 `Add`、`Move`、`Remove`、`Replace`、`Configure`。定义包括 `Id`、`Target`、`Action`、`Selector`、`Parent`、`Index`、`Order`、`Layout`、`CreateContent`；`PluginComponentLayout` 有 Grid 的 Row/Column/Span、绝对定位 Left/Top/Right/Bottom、尺寸、对齐、ZIndex、透明度、变换等可选属性。`$overlay` 是覆盖指定视图的画布；目标可为 `view:MainWindow`、`view:SettingsPage` 或 `dialog:<类型名>`。需要 `ui.components`；错误 ID/目标/选择器会拒绝或记日志，插件停止时撤销补丁。组件工厂需返回全新的 WPF 元素，不应阻塞 UI。

`ctx.Runtime.RegisterAvatarRenderer(id, renderer, order)` 和 `RegisterSkinModelRenderer(id, factory, order)` 保留头像/模型渲染扩展，需要 `ui.components`；必须实现相应委托或模型渲染工厂，并管理自身渲染资源。`RegisterLaunchMiddleware(id, middleware, order)` **仅为旧插件兼容接口**：新清单插件调用会得到 `LegacyApiOnly`，新插件应使用 `ctx.Events.InterceptLaunch` 的受限启动计划。旧中间件接收宿主类型 `LauncherService.LaunchOptions`、同步 `next()`，异常可能直接影响启动，绝非新的安全干预接口。以上注册都返回 `IDisposable` 并在插件停止时解除。

## 权限一览

新清单插件必须声明并获得授权。以下是源码中公开的权限名；风险表示该能力可能接触的数据或造成的变化。

| 权限 | 用途/风险 |
| --- | --- |
| `accounts.read` | 账户基本信息和事件；不含凭据 |
| `instances.read` | 实例详情、路径、JVM 参数与游戏进程事件 |
| `mods.read` | 实例 Mod 列表与变化 |
| `downloads.read` | 下载状态与进度 |
| `settings.read` | 设置白名单快照与变更键名 |
| `environment.read` | 启动器版本、API 版本、数据目录、语言 |
| `events.lifecycle` | 启动/退出通知 |
| `launch.modify` | 修改启动参数/环境并可取消游戏启动 |
| `downloads.intercept` | 改写公开下载 URL 或取消 |
| `instances.validate` | 取消实例创建 |
| `network` | 无凭据的 HTTP GET；需留意插件控制的网络目的地 |
| `providers.download`、`providers.mod` | 注册下载源、Mod 来源 |
| `providers.loader` | 注册加载器安装器 |
| `auth.provider` | 注册宿主支持的账户登录入口 |
| `files.instances.read`、`files.instances.write` | 已声明的实例文件权限；当前公开工作区方法主要使用写权限，读取能力不等于任意实例目录访问 |
| `process.install` | 在受限安装工作区运行 Java 安装器 |
| `files.external.read` | 用户选择背景文件后由宿主读取 |
| `files.external.write` | 名称已保留，当前权限服务判为不可用；不要申请并假设可写任意外部文件 |
| `ui.basic`、`ui.components` | 旧弹窗扩展与组件/渲染入口 |
| `ui.navigation`、`ui.instanceActions`、`ui.settings`、`ui.background` | 页面、实例操作、设置、背景贡献 |
| `plugin.messaging` | 向声明的依赖传递 JSON 消息 |

授权仅约束**宿主提供的 API**。进程内 DLL 与宿主有相同的操作系统权限，恶意 DLL 可自行调用系统 API；仅安装可信来源插件。登录秘密应由宿主保管，业务 DTO、事件载荷和宿主日志不能放账户密码或令牌；插件自有代码和外部程序的系统行为不受进程内权限隔离。

## EXE 通信协议 1

宿主保留 `XCL2_PLUGIN_DATA_DIR`，另传 `XCL2_PLUGIN_PIPE`（管道名）、`XCL2_PLUGIN_SESSION_TOKEN`（本次会话随机令牌）、`XCL2_PLUGIN_PROTOCOL_VERSION`。EXE 连接同一 Windows 用户的命名管道；宿主校验客户端 PID 与其启动的进程 PID。每帧是 **4 字节小端长度 + UTF-8 JSON**，长度 `1..1048576` 字节、JSON 最大深度 32。首个请求必须握手：

```json
{"protocol":1,"kind":"request","id":"hello-1","method":"hello","params":{"token":"从环境变量读取的会话令牌"}}
```

普通请求为 `{ "protocol":1, "kind":"request", "id":"唯一请求Id", "method":"instances.list", "params":{} }`；响应为 `{ "protocol":1, "kind":"response", "id":"唯一请求Id", "result":... }`，失败改用 `"error":{"code":"...","message":"..."}`。宿主向 EXE 推送 `{ "protocol":1, "kind":"event", "subscriptionId":"...", "event":"PluginInstanceCatalogChanged", "payload":... }`；先请求 `events.subscribe`，其 `params` 包含 `event` 类型名，返回 `registrationId`。`registrations.remove` 以该 Id 解除订阅。提供者和干预回调采用反向 `callbacks.invoke` 请求，EXE 必须回响应。

开放的请求方法包括 `permissions.snapshot`、`accounts.list`/`accounts.selected`、`instances.list`/`instances.details`/`instances.selected`、`mods.list`、`downloads.list`、`settings.snapshot`、`environment.snapshot`、`config.get`/`config.set`、`log`、`network.getText`、`events.subscribe`、`events.interceptLaunch`/`events.interceptDownload`/`events.interceptInstanceCreation`、`providers.register`、`messages.peers`/`messages.send`/`messages.subscribe`、`ui.showMessage`/`ui.confirm`/`ui.registerPage`/`ui.registerSetting`/`ui.registerInstanceAction`/`ui.registerBackground`/`ui.importBackgroundImage`，以及安装工作区和登录代理的相应方法。所有访问走同一清单授权检查；EXE 页面内容限声明式 `text`/`button`，不能发送 WPF 元素。连接断开、插件停止、进程崩溃时宿主清理注册项；消息和回调不得携带账户秘密。

协议字段和方法大小写与上面的 JSON 一致。`events.subscribe` 参数例：`{"event":"PluginInstanceCatalogChanged"}`；`instances.details` 需要 `{"instanceId":"..."}`。`callbacks.invoke` 是**宿主发给 EXE 的 request**，含 `callbackId` 和 `payload`；EXE 必须用相同 `id` 的 `response` 返回 `PluginInterception<T>` 或提供者方法的结果。协议消息最大 1 MiB；同时最多处理 8 个 EXE 入站请求。错误响应只给 `code` 和安全文本，不给宿主内部异常。连接与所启动 EXE 的 PID/用户绑定；断线后注册项清理。握手和实例读取的最小请求顺序为：连接 → 发送 `hello` → 接收成功响应 → 发 `instances.list`（需要 `instances.read`）→ 可选发 `events.subscribe`。不要把会话令牌写进日志或插件配置。

## 外部程序与脚本：命令行入口

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

不指定 `--result-file` 时，仍按原行为发起 GUI 操作；也不会借参数创建账户。调用方不能将启动器进程成功创建等同于游戏已成功进入主菜单。

新增只读查询和可选 JSON 结果：

| 调用 | 行为 |
| --- | --- |
| `XCL2.exe --list-instances --result-file C:\out\instances.json` | 将实例基本信息写入结构化结果，不含账户凭据 |
| `XCL2.exe --list-accounts --result-file C:\out\accounts.json` | 将账户基本信息写入结构化结果，无令牌或密码 |
| `XCL2.exe -r --account 名称 --instance 名称 --result-file C:\out\launch.json` | 记录实际进程结果/状态，不代表进入游戏主菜单 |

结果类型 `LauncherCommandResult` 的公开字段为 `SchemaVersion`、`Operation`、`Status`、`Code`、可选 `Pid`、`InstanceId`、`AccountId`、`Data`；JSON 字段以 camelCase 输出。结果文件由 GUI 生命周期写入，目前没有额外的无界面退出码协议。**已知源码风险：**当前实现对序列化后的整段 JSON 调用带 16,384 字符截断的脱敏方法；大列表可能使输出成为不完整 JSON。修复前请勿依赖大批量结果文件的完整性。

## 自定义 AI HTTP 接口

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

## 宿主侧插件管理接口

命名空间仍为 `XCL2.App.Services.Plugins`。这些方法供维护启动器源码时接入 UI，普通插件优先使用宿主传入的 `PluginContext`。

| API | 返回类型 | 调用约定 |
| --- | --- | --- |
| `new PluginManager(Action<string>? logSink = null)` | `PluginManager` | 创建管理器并提供日志回调 |
| `InstalledDirectory` | `string` | 已安装插件目录 |
| `Plugins` | `IReadOnlyList<LoadedPluginInfo>` | 最近一次扫描结果 |
| `StartPolicy` | `PluginLaunchMode` | 扫描前设置为 `EachLaunch` 或 `OnceOnImport` |
| `FindInstallConflict(string sourcePath)` | `string?` | 返回冲突的已安装路径或 null |
| `InstallFromFile(string sourcePath)` | `string` | 复制并返回安装路径；冲突时拒绝覆盖 |
| `InstallFromFile(string sourcePath, PluginInstallConflictAction action)` | `string?` | 显式选择 `Cancel`/`Copy`/`Replace`；支持 `.xclplugin` 导入 |
| `ScanAndLoad()` | `void` | 停止上一轮、扫描、按启用状态和策略启动 |
| `SetEnabled(string filePath, bool enabled)` | `void` | 保存开关；随后重新扫描使运行状态生效 |
| `SetPermissions(string filePath, IEnumerable<string>)` | `void` | 存储逐项授权；撤销立即影响宿主 API 与注册项 |
| `GetGrantedPermissions(string filePath)` | `ImmutableArray<string>` | 查看当前授权 |
| `Uninstall(IEnumerable<string>)` | `IReadOnlyList<PluginOperationResult>` | 移除指定已安装插件并分别返回状态 |
| `ShutdownAll()` | `void` | 结束当前托管插件并释放加载上下文 |
| `PluginTemplateGenerator.Generate(string targetDir, string pluginId, string displayName)` | `void` | 当前生成旧版插件源码模板，尚未附带 1.9 清单 |

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

`LoadedPluginInfo` 提供 `FilePath`、`Plugin`、`LoadError`、`Enabled`、`Context`、`IsExecutable`、`RunningProcess`、`StartedOnce`、`Metadata`、`Diagnostics` 和 `GrantedPermissions`。未在本轮运行或加载失败时，`Plugin` / `Context` 可以为 `null`，调用方应先判断。

## 内置 MD 的拉取与导出接口

两份主要帮助文档在仓库根目录和 `XCL2.App/Resources/Docs/` 各保留一份。项目内副本需要作为相应名称的 `EmbeddedResource` 编入程序集。**当前交付的 `XCL2.App.csproj` 没有为下表 MD 显式配置资源项；是否由其他构建配置包含仍需验证。**更新文档后应同步根目录副本并核实实际嵌入资源名，不能只修改 Markdown 就断言应用内查看生效。外部 MD 不在场、单文件发布、离线运行时，查看和导出仍从当前程序集读取。

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

插件指南也位于 `Resources/Docs/PLUGIN_GUIDE.md`；其历史兼容资源名为 `XCL2.App.PLUGIN_GUIDE.md`。当前指南仍偏旧版，待与本文同步。

## 源码对应表

| 接口 | 定义 / 调用位置 |
| --- | --- |
| 插件契约 | `Services/Plugins/IPlugin.cs` |
| 上下文与配置 | `Services/Plugins/PluginContext.cs`、`PluginConfigService.cs` |
| 插件安装与进程协议 | `Services/Plugins/PluginManager.cs` |
| 清单/权限/依赖 | `Services/Plugins/PluginMetadata.cs`、`PluginPermissionService.cs` |
| 业务快照/事件/干预 | `PluginBusinessContracts.cs`、`PluginLauncherService.cs`、`PluginEventService.cs`、`PluginInterceptionService.cs` |
| 业务提供者/EXE 协议/插件消息 | `PluginProviderService.cs`、`PluginPipeSession.cs`、`PluginMessageService.cs` |
| UI 新贡献/组件/渲染 | `PluginUiContributions.cs`、`PluginComponentService.cs`、`PluginRuntimeService.cs` |
| 模板生成 | `Services/Plugins/PluginTemplateGenerator.cs` |
| 命令行解析 / 执行 | `Services/CommandLineService.cs`、`App.xaml.cs`、`Views/MainWindow.xaml.cs` |
| AI 请求与配置 | `Services/AiAssistantService.cs`、`Models/AiAssistantModels.cs` |
| MD 读取和导出 | `Services/EmbeddedDocumentationService.cs` |
| 文档界面 | `Views/AboutHelpPage.xaml`、`Views/HelpDocumentDialog.xaml` |

## 迁移、线程与失败语义

从 1.1 迁移：先保持原 `IPlugin` 不变，在入口旁加入 `plugin.json`、写 `minApiVersion` 和实际需要的权限，再在 `Initialize` 用 `ctx.Permissions.Has(...)` 决定是否注册新能力；保留旧版运行分支。新注册返回的 `IDisposable` 可主动释放，宿主停止时也会释放。读操作可在任意线程 `await`，取消通过传入令牌或 `ctx.Stopping`；通知/提供者/干预在插件后台域执行；UI 控件和旧 UI 注册走 UI Dispatcher。插件超时/异常被宿主记录并放弃其本次结果，但进程内死循环及阻塞 UI 工厂不能安全强制终止。授权被撤销或上下文停止后，新调用分别报权限/对象已释放错误；提供者操作失败时使用内置回退路径。

当前已融合为一份可独立阅读的 API 文档。源码仍在开发，模板更新、完整示例项目、1.1 二进制兼容测试、EXE 客户端样例、端到端运行和构建验收尚待完成；源码接口使用前需以实际运行结果复核。

## 宿主维护备注

原文记录的关机日志次序仍需维护：先调用 `Plugins.ShutdownAll()`，让插件 `Shutdown()` 的日志进入本次会话缓冲；再调用 `LauncherLogService.EndSessionAndFlush()` 落盘。顺序颠倒会丢失停止阶段的诊断信息。管理页的“设置 → 第三方插件”入口与实验性功能入口连接到同一个管理器；导航门控可能影响后者。新增宿主行为涉及清单加载、权限 UI、下载/实例/启动流程接入，均应在构建与实机测试中核对。
