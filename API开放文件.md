
## PluginContext

```csharp
public sealed class PluginContext : IDisposable
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `PluginConfigService Config { get; }` | — | 该插件独享的配置读写入口，落盘在 %AppData%/XCL2/Plugins/config/{PluginId}.json， 具体用法见 PluginConfigService 自己的注释。 |
| `string DataDirectory { get; }` | — | 该插件独享的数据目录（%AppData%/XCL2/Plugins/data/{PluginId}/）， 已在 Initialize 调用前确保存在。需要落盘缓存文件、下载文件的插件应该写在这里， 而不是写到 dll 自己所在目录（那个目录可能只读，或者被启动器整体当作只读区对待）。 |
| `Action<string> Log { get; }` | — | 把一行文字写进启动器日志（会自动带上"[Plugin:该插件Id]"前缀）， 排查问题时能直接在启动器"日志"页里看到，不需要插件自己另外弹窗/写文件。 |
| `Version ApiVersion { get; }` | — | UI 注册 API 版本；原有 IPlugin 契约保持兼容。 |
| `PluginMetadata Metadata { get; private set; }` | — | — |
| `PluginUiService Ui { get; }` | — | 注册弹窗、按钮、选项、面板和弹窗生命周期订阅。 |
| `PluginComponentService Components { get; }` | — | 任意已加载视图的组件定位、增加、移除、移动、替换与布局。 |
| `PluginRuntimeService Runtime { get; }` | — | 启动内核、头像和实时 3D 渲染后端扩展。 |
| `PluginPermissionService Permissions { get; }` | — | — |
| `PluginLauncherService Launcher { get; }` | — | — |
| `PluginControlService Control { get; }` | — | 页面导航、窗口布局、启动控制与经过校验的设置写入。 |
| `PluginEventService Events { get; }` | — | — |
| `PluginProviderService Providers { get; }` | — | — |
| `PluginNetworkService Network { get; }` | — | — |
| `PluginMessageService Messages { get; }` | — | — |
| `PluginPreviewService Preview { get; }` | — | — |
| `PluginWorldService Worlds { get; }` | — | — |
| `PluginManagementService Management { get; }` | — | API 2.2：实例、模组、Java 与下载队列管理。 |
| `PluginResourceService Resources { get; }` | — | API 2.2：宿主资源查询、文件安装及原版版本安装。 |
| `PluginProjectService Projects { get; }` | — | — |
| `PluginTaskService Tasks { get; }` | — | — |
| `PluginChangeService Changes { get; }` | — | — |
| `PluginQueryService Queries { get; }` | — | — |
| `void Dispose()` | — | — |
| `System.Threading.CancellationToken Stopping { get; }` | — | 插件停止时取消，后台任务应传入此令牌。 |
| `PluginContext(string pluginId, PluginConfigService config, string dataDirectory, Action<string> log)` | — | — |
| `string PluginId { get; }` | — | 原安装等于 IPlugin.Id，副本含独立实例后缀。使用此 Id 隔离注册项，方便插件代码里不用额外持有 IPlugin 引用 也能拿到自己的 Id（比如拼日志前缀、拼文件名）。 |

## IPlugin

```csharp
public interface IPlugin
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string Id { get; }` | — | 插件唯一 ID，建议用"作者名.插件名"这种反向域名风格的短字符串， 例如 "someone.better-skin-tools"。这个 ID 同时也是配置文件、日志前缀的 key， 上线之后不要再改，否则等于变成了一个新插件（旧配置对不上）。 |
| `string DisplayName { get; }` | — | 展示给用户看的插件名称，随时可以改，不影响配置归属。 |
| `string Version { get; }` | — | 插件版本号，纯展示用，建议跟随语义化版本（1.0.0 这种）。 |
| `string Description { get; }` | — | 一两句话说明这个插件是干什么的，显示在插件管理列表里。 |
| `string Author { get; }` | — | 插件作者，展示用，可以留空字符串。 |
| `void Initialize(PluginContext ctx)` | — | 启动器加载插件时调用一次。ctx 提供了配置读写、日志、以及向"插件"页面 挂一个自定义设置面板的能力——具体每个成员的用法见 PluginContext 自己的注释。 这里面不应该做耗时的同步操作（网络请求、大文件 IO 等），耗时工作请自己开 Task.Run，不要卡住启动器主界面的启动过程。 |
| `void Shutdown()` | — | 插件被用户关闭，或者启动器正常退出时调用一次，用来释放插件自己占用的资源 （文件句柄、定时器、后台线程等）。如果插件没有需要清理的东西，留空实现即可。 |
| `UserControl? CreateSettingsPanel(PluginContext ctx)` | — | 可选：返回一个插件自己的配置界面（一个 UserControl），显示在"插件管理"里 该插件条目的"配置"按钮之后。不需要图形配置界面的插件（比如只是后台跑点逻辑的） 可以直接返回 null——这时"配置"按钮会改成显示一个通用的、基于 PluginContext.Config 里已保存键值对的只读 JSON 预览，方便用户至少能看到当前保存了什么。 |

## ctx.Projects

```csharp
public sealed class PluginProjectService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<PluginProject?> ChooseFolderAsync(CancellationToken ct = default)` | `workbench.projects.chooseFolder` | projects.read |
| `PluginProject OpenDataProject(string name)` | `workbench.projects.openDataProject` | projects.read + projects.write |
| `IReadOnlyList<PluginProject> GetOpenProjects()` | `workbench.projects.list` | projects.read |
| `PluginProject GetProject(string projectId)` | `workbench.projects.get` | projects.read |
| `bool CloseProject(string projectId)` | `workbench.projects.close` | projects.read |
| `IReadOnlyList<ProjectEntry> GetFiles(string projectId, string glob = "*", int limit = 1000)` | `workbench.projects.files` | projects.read |
| `IReadOnlyList<string> GetDirectories(string projectId, string path = "")` | `workbench.projects.directories` | projects.read |
| `ProjectSearchResult SearchText(string projectId, string query, string glob = "*", int limit = 200)` | `workbench.projects.searchText` | projects.read |
| `ProjectSearchResult SearchRegex(string projectId, string pattern, string glob = "*", int limit = 200)` | `workbench.projects.searchRegex` | projects.read |
| `ProjectTextPage ReadRange(string projectId, string path, int startLine = 1, int lineCount = 200)` | `workbench.projects.readRange` | projects.read |
| `ProjectTextPage ReadHead(string projectId, string path, int lineCount = 100)` | `workbench.projects.readHead` | projects.read |
| `string ReadTail(string projectId, string path, int lineCount = 100)` | `workbench.projects.readTail` | projects.read |
| `string ReadText(string projectId, string path)` | `workbench.projects.readText` | projects.read |
| `byte[] ReadBytes(string projectId, string path, long offset = 0, int count = 4096)` | `workbench.projects.readBytes` | projects.read |
| `string GetHash(string projectId, string path)` | `workbench.projects.hash` | projects.read |
| `ProjectEntry GetFileInfo(string projectId, string path)` | `workbench.projects.fileInfo` | projects.read |
| `bool FileExists(string projectId, string path)` | `workbench.projects.fileExists` | projects.read |
| `IReadOnlyDictionary<string, int> GetExtensionStats(string projectId)` | `workbench.projects.extensionStats` | projects.read |
| `IReadOnlyList<ProjectEntry> GetLargestFiles(string projectId, int count = 20)` | `workbench.projects.largestFiles` | projects.read |
| `IReadOnlyList<ProjectEntry> FindFiles(string projectId, string name)` | `workbench.projects.findFiles` | projects.read |
| `int GetFileCount(string projectId)` | `workbench.projects.fileCount` | projects.read |
| `long GetTotalBytes(string projectId)` | `workbench.projects.totalBytes` | projects.read |
| `string GetNewlineStyle(string projectId, string path)` | `workbench.projects.newline` | projects.read |
| `string GetEncoding(string projectId, string path)` | `workbench.projects.encoding` | projects.read |
| `JsonElement ReadJson(string projectId, string path)` | `workbench.projects.readJson` | projects.read |
| `IReadOnlyList<string> GetJsonProperties(string projectId, string path)` | `workbench.projects.jsonProperties` | projects.read |
| `JsonElement ReadJsonPointer(string projectId, string path, string pointer)` | `workbench.projects.jsonPointer` | projects.read |

## ctx.Tasks

```csharp
public sealed class PluginTaskService(PluginContext context)
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `WorkTask Create(string title, string goal, string projectRoot = "")` | `workbench.tasks.create` | tasks.manage |
| `WorkTask Get(string id)` | `workbench.tasks.get` | tasks.manage |
| `IReadOnlyList<WorkTask> List()` | `workbench.tasks.list` | tasks.manage |
| `IReadOnlyList<WorkTask> Find(string query)` | `workbench.tasks.find` | tasks.manage |
| `IReadOnlyList<WorkTask> GetActive()` | `workbench.tasks.active` | tasks.manage |
| `IReadOnlyList<WorkTask> GetCompleted()` | `workbench.tasks.completed` | tasks.manage |
| `IReadOnlyList<WorkTask> GetArchived()` | `workbench.tasks.archived` | tasks.manage |
| `WorkTask Rename(string id, string title)` | `workbench.tasks.rename` | tasks.manage |
| `WorkTask SetGoal(string id, string goal)` | `workbench.tasks.goal` | tasks.manage |
| `WorkTask SetProjectRoot(string id, string projectRoot)` | `workbench.tasks.project` | tasks.manage |
| `WorkTask SetMode(string id, string mode)` | `workbench.tasks.mode` | tasks.manage |
| `WorkTask SetNextAction(string id, string nextAction)` | `workbench.tasks.nextAction` | tasks.manage |
| `WorkTask SetNotes(string id, string notes)` | `workbench.tasks.notes` | tasks.manage |
| `WorkTask AppendNotes(string id, string notes)` | `workbench.tasks.appendNotes` | tasks.manage |
| `WorkTask AddStep(string id, string title)` | `workbench.tasks.addStep` | tasks.manage |
| `WorkTask RemoveStep(string id, string stepId)` | `workbench.tasks.removeStep` | tasks.manage |
| `WorkTask RenameStep(string id, string stepId, string title)` | `workbench.tasks.renameStep` | tasks.manage |
| `WorkTask SetStepStatus(string id, string stepId, string status)` | `workbench.tasks.stepStatus` | tasks.manage |
| `WorkTask SetStepEvidence(string id, string stepId, string evidence)` | `workbench.tasks.stepEvidence` | tasks.manage |
| `WorkTask ReplaceSteps(string id, List<WorkStep> steps)` | `workbench.tasks.replaceSteps` | tasks.manage |
| `IReadOnlyList<WorkStep> GetSteps(string id)` | `workbench.tasks.steps` | tasks.manage |
| `IReadOnlyList<WorkStep> GetPendingSteps(string id)` | `workbench.tasks.pendingSteps` | tasks.manage |
| `IReadOnlyList<WorkStep> GetCompletedSteps(string id)` | `workbench.tasks.completedSteps` | tasks.manage |
| `WorkTask Checkpoint(string id, string text)` | `workbench.tasks.checkpoint` | tasks.manage |
| `IReadOnlyList<string> GetCheckpoints(string id)` | `workbench.tasks.checkpoints` | tasks.manage |
| `WorkTask Pause(string id)` | `workbench.tasks.pause` | tasks.manage |
| `WorkTask Resume(string id)` | `workbench.tasks.resume` | tasks.manage |
| `WorkTask Complete(string id)` | `workbench.tasks.complete` | tasks.manage |
| `WorkTask Archive(string id)` | `workbench.tasks.archive` | tasks.manage |
| `WorkTask AddChange(string id, string changeId)` | `workbench.tasks.addChange` | tasks.manage |
| `IReadOnlyList<string> GetChanges(string id)` | `workbench.tasks.changes` | tasks.manage |
| `double GetProgress(string id)` | `workbench.tasks.progress` | tasks.manage |
| `WorkTask Clone(string id, string title)` | `workbench.tasks.clone` | tasks.manage |

## ctx.Changes

```csharp
public sealed class PluginChangeService(PluginContext context)
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `FileChange PrepareWrite(string projectId, string path, string content, string? expectedHash = null)` | `workbench.changes.prepareWrite` | projects.read + projects.write |
| `FileChange PreparePatch(string projectId, string path, string sha256, string oldText, string newText)` | `workbench.changes.preparePatch` | projects.read + projects.write |
| `FileChange PrepareDelete(string projectId, string path, string sha256)` | `workbench.changes.prepareDelete` | projects.read + projects.write |
| `IReadOnlyList<FileChange> List(string projectId)` | `workbench.changes.list` | projects.read + projects.write |
| `FileChange Get(string projectId, string changeId)` | `workbench.changes.get` | projects.read + projects.write |
| `byte[]? GetBefore(string projectId, string changeId)` | `workbench.changes.before` | projects.read + projects.write |
| `byte[]? GetAfter(string projectId, string changeId)` | `workbench.changes.after` | projects.read + projects.write |
| `AiFileDiff GetDiff(string projectId, string changeId)` | `workbench.changes.diff` | projects.read + projects.write |
| `Task<FileChange> ApplyAsync(string projectId, string changeId, CancellationToken ct = default)` | `workbench.changes.apply` | projects.read + projects.write |
| `Task<FileChange> RollbackAsync(string projectId, string changeId, CancellationToken ct = default)` | `workbench.changes.rollback` | projects.read + projects.write |

## ctx.Queries

```csharp
public sealed class PluginQueryService(PluginContext context)
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<IReadOnlyList<PluginInstance>> GetInstalledInstances(CancellationToken ct = default)` | `workbench.query.instances.installed` | instances.read |
| `Task<IReadOnlyList<PluginInstance>> GetIncompleteInstances(CancellationToken ct = default)` | `workbench.query.instances.incomplete` | instances.read |
| `Task<IReadOnlyList<PluginInstance>> GetHiddenInstances(CancellationToken ct = default)` | `workbench.query.instances.hidden` | instances.read |
| `Task<IReadOnlyList<PluginInstance>> GetVisibleInstances(CancellationToken ct = default)` | `workbench.query.instances.visible` | instances.read |
| `Task<IReadOnlyList<PluginInstance>> GetVanillaInstances(CancellationToken ct = default)` | `workbench.query.instances.vanilla` | instances.read |
| `Task<IReadOnlyList<PluginInstance>> FindInstancesByName(string name, CancellationToken ct = default)` | `workbench.query.instances.byName` | instances.read |
| `Task<IReadOnlyList<PluginInstance>> FindInstancesByVersion(string version, CancellationToken ct = default)` | `workbench.query.instances.byVersion` | instances.read |
| `Task<IReadOnlyList<PluginInstance>> FindInstancesByLoader(string loader, CancellationToken ct = default)` | `workbench.query.instances.byLoader` | instances.read |
| `Task<PluginInstance?> GetInstance(string id, CancellationToken ct = default)` | `workbench.query.instances.get` | instances.read |
| `Task<bool> InstanceExists(string id, CancellationToken ct = default)` | `workbench.query.instances.exists` | instances.read |
| `Task<int> GetInstanceCount(CancellationToken ct = default)` | `workbench.query.instances.count` | instances.read |
| `Task<IReadOnlyList<string>> GetMinecraftVersions(CancellationToken ct = default)` | `workbench.query.instances.versions` | instances.read |
| `Task<IReadOnlyList<string>> GetLoaders(CancellationToken ct = default)` | `workbench.query.instances.loaders` | instances.read |
| `Task<IReadOnlyList<PluginInstance>> GetInstancePage(int offset = 0, int limit = 100, CancellationToken ct = default)` | `workbench.query.instances.page` | instances.read |
| `Task<IReadOnlyList<PluginMod>> GetEnabledMods(string instanceId, CancellationToken ct = default)` | `workbench.query.mods.enabled` | mods.read |
| `Task<IReadOnlyList<PluginMod>> GetDisabledMods(string instanceId, CancellationToken ct = default)` | `workbench.query.mods.disabled` | mods.read |
| `Task<IReadOnlyList<PluginMod>> FindModsById(string instanceId, string modId, CancellationToken ct = default)` | `workbench.query.mods.byId` | mods.read |
| `Task<IReadOnlyList<PluginMod>> FindModsByName(string instanceId, string name, CancellationToken ct = default)` | `workbench.query.mods.byName` | mods.read |
| `Task<PluginMod?> GetModByFile(string instanceId, string fileName, CancellationToken ct = default)` | `workbench.query.mods.file` | mods.read |
| `Task<int> GetModCount(string instanceId, CancellationToken ct = default)` | `workbench.query.mods.count` | mods.read |
| `Task<long> GetModBytes(string instanceId, CancellationToken ct = default)` | `workbench.query.mods.bytes` | mods.read |
| `Task<IReadOnlyList<PluginMod>> GetLargestMods(string instanceId, int count = 20, CancellationToken ct = default)` | `workbench.query.mods.largest` | mods.read |
| `Task<IReadOnlyList<string>> GetDuplicateModIds(string instanceId, CancellationToken ct = default)` | `workbench.query.mods.duplicateIds` | mods.read |
| `Task<IReadOnlyList<PluginMod>> GetModPage(string instanceId, int offset = 0, int limit = 100, CancellationToken ct = default)` | `workbench.query.mods.page` | mods.read |
| `Task<IReadOnlyList<PluginDownload>> GetActiveDownloads(CancellationToken ct = default)` | `workbench.query.downloads.active` | downloads.read |
| `Task<IReadOnlyList<PluginDownload>> GetPausedDownloads(CancellationToken ct = default)` | `workbench.query.downloads.paused` | downloads.read |
| `Task<IReadOnlyList<PluginDownload>> GetCompletedDownloads(CancellationToken ct = default)` | `workbench.query.downloads.completed` | downloads.read |
| `Task<IReadOnlyList<PluginDownload>> GetFailedDownloads(CancellationToken ct = default)` | `workbench.query.downloads.failed` | downloads.read |
| `Task<IReadOnlyList<PluginDownload>> GetCanceledDownloads(CancellationToken ct = default)` | `workbench.query.downloads.canceled` | downloads.read |
| `Task<IReadOnlyList<PluginDownload>> GetInstanceDownloads(string instanceId, CancellationToken ct = default)` | `workbench.query.downloads.instance` | downloads.read |
| `Task<PluginDownload?> GetDownload(Guid id, CancellationToken ct = default)` | `workbench.query.downloads.get` | downloads.read |
| `Task<int> GetDownloadCount(CancellationToken ct = default)` | `workbench.query.downloads.count` | downloads.read |
| `Task<IReadOnlyList<PluginAccount>> GetOfflineAccounts(CancellationToken ct = default)` | `workbench.query.accounts.offline` | accounts.read |
| `Task<IReadOnlyList<PluginAccount>> GetMicrosoftAccounts(CancellationToken ct = default)` | `workbench.query.accounts.microsoft` | accounts.read |
| `Task<IReadOnlyList<PluginAccount>> GetAuthServerAccounts(CancellationToken ct = default)` | `workbench.query.accounts.authserver` | accounts.read |
| `Task<IReadOnlyList<PluginAccount>> GetGuestAccounts(CancellationToken ct = default)` | `workbench.query.accounts.guest` | accounts.read |
| `Task<PluginAccount?> GetAccount(string id, CancellationToken ct = default)` | `workbench.query.accounts.get` | accounts.read |
| `Task<IReadOnlyList<PluginAccount>> FindAccounts(string name, CancellationToken ct = default)` | `workbench.query.accounts.find` | accounts.read |
| `Task<int> GetAccountCount(CancellationToken ct = default)` | `workbench.query.accounts.count` | accounts.read |

## ChangeJournal

```csharp
public sealed class ChangeJournal(string directory)
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `FileChange Get(string id)` | — | — |
| `IReadOnlyList<FileChange> List()` | — | — |
| `byte[]? Before(string id)` | — | — |
| `byte[]? After(string id)` | — | — |
| `FileChange Prepare(string path, byte[]? after, string? expectedHash = null)` | — | — |
| `FileChange Apply(string id, Action<string>? authorize = null)` | — | — |
| `FileChange Rollback(string id, Action<string>? authorize = null)` | — | — |

## CommandLineService

```csharp
public static class CommandLineService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `const string HelpText = "XCL2 启动器 命令行参数说明\n" + "\n" + " -help 显示本帮助并退出\n" + " -r --账户名 <账户名> --实例名称 <实例名> 使用指定账户与实例直接启动游戏\n" + " --result-file <路径> 将启动操作的实际进程结果写入 JSON\n" + " --list-instances / --list-accounts 查询实例 / 账户，不输出凭据；可配合 --result-file\n" + " -gui <页面名> 启动后跳转到对应页面，例如：-gui 设置 / -gui 下载中心 / -gui 百宝箱\n" + " --d --版本 <版本号> --加载器 <加载器> 打开下载中心；不填 --版本 时会直接弹出下载页面供手动选择，\n" + " 填写 --版本 后会自动跳转并按该版本号筛选，--加载器 可省略\n" + " -l 以访客模式启动本次会话（会要求重新阅读并同意协议）\n" + "\n" + "进阶/脚本化参数（可选，不填时行为与以前完全一致）：\n" + " --skip-update-check 本次启动跳过自动检查更新\n" + " --check-update-only 只检查一次更新，不进入主界面\n" + " --offline 强制以离线模式启动\n" + " --portable 强制使用便携模式（配置存在启动器目录）\n" + " --no-tray 不创建托盘图标\n" + " --minimized 启动后直接最小化\n" + " --maximized 启动后直接最大化\n" + " --reset-window 重置窗口位置/大小为默认值\n" + " --width <px> --height <px> 指定启动窗口大小\n" + " --lang <代码> 本次强制界面语言，例如 zh-CN / en-US\n" + " --theme <light\|dark> 本次强制主题\n" + " --zoom <百分比> 本次强制界面缩放，例如 125\n" + " --no-animation 关闭界面动效\n" + " --safe-mode 安全模式启动，跳过公告/AI 悬浮球等增强功能\n" + " --debug-console 额外弹出调试控制台窗口\n" + " --debug -yrj 本次会话无视日期开启愚人节彩蛋（仍尊重 noyrj 禁用）\n" + " --verbose 调试控制台输出更详细日志\n" + " --config-path <路径> 使用指定配置文件路径\n" + " --instance-dir <路径> 使用指定实例（.minecraft）根目录\n" + " --java <路径> 启动游戏强制使用指定 Java 路径\n" + " --min-memory <MB> --max-memory <MB> 启动游戏强制使用的 JVM 内存参数\n" + " --jvm-args <参数> 启动游戏额外追加的 JVM 参数\n" + " --game-args <参数> 启动游戏额外追加的游戏参数\n" + " --server <ip:port> 配合 -r 使用，启动后自动直连该服务器\n" + " --fullscreen 配合 -r 使用，游戏以全屏启动\n" + " --clear-cache 启动前先清理下载缓存/临时文件\n" + " --export-logs <路径> 启动后自动打包导出日志/崩溃报告到指定路径\n" + " --no-single-instance 跳过单实例检测，允许多开\n" + " --no-gpu-accel 关闭窗口硬件加速渲染\n" + "\n" + "示例：\n" + " XCL2.App.exe -r --账户名 Steve --实例名称 \"1.20.1-Fabric\"\n" + " XCL2.App.exe -gui 百宝箱\n" + " XCL2.App.exe --d --版本 1.21.1 --加载器 Fabric\n" + " XCL2.App.exe --d\n" + " XCL2.App.exe -l\n" + " XCL2.App.exe -r --账户名 Steve --实例名称 \"1.21.1-Fabric\" --server play.example.com:25565\n" + " XCL2.App.exe --safe-mode --no-animation --minimized\n"` | — | — |
| `static ParsedArgs Parse(string[] args)` | — | — |

## CommandLineService.ParsedArgs

```csharp
public sealed class ParsedArgs
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `bool ShowHelp` | — | 命中 -help，只需要打印帮助然后退出，不需要往下走任何其它逻辑。 |
| `bool LaunchGame` | — | 命中 -r，需要用指定账户+实例直接启动游戏。 |
| `string? AccountName` | — | — |
| `string? InstanceName` | — | — |
| `string? ResultFile` | — | — |
| `bool ListInstances` | — | — |
| `bool ListAccounts` | — | — |
| `string? GuiPage` | — | 命中 -gui &lt;page&gt;，需要跳转到的页面名（原样传入，交给 MainWindow 的页面关键字表去匹配）。 |
| `bool OpenDownload` | — | 命中 --d，需要打开下载中心；Version 为空时弹出下载页面让用户手动选。 |
| `string? DownloadVersion` | — | — |
| `string? DownloadLoader` | — | — |
| `bool GuestModeRelaunchRequested` | — | 用户显式输入 -l；先重启一次，再转换成隐藏的 --guest-session。 |
| `bool EnterGuestMode` | — | 隐藏参数：当前这个进程就是一次性访客会话，不再二次重启。 |
| `bool EnterPreviewMode` | — | 隐藏参数 --preview-session：仅由 MainWindow.RequestPreviewModeRestart 生成的重启 进程带上，标记"这次启动只用来展示新界面预览"。跟 --guest-session 同一套模式：磁盘配置 永远不记录这个状态，只影响当次进程——正常双击启动、或者用户在预览里点"关闭预览"触发的 下一次重启都不会再带这个参数，从而回到原有默认界面，不会重新进入/停留在预览里。 |
| `bool StartedByAutoStart` | — | 隐藏参数：开机 Run 项启动，用来应用“弹出/最小化/托盘”行为。 |
| `int WaitForPid` | — | 隐藏参数：重启接力时等待旧进程 PID 退出，避免误撞多开检测。 |
| `bool SkipUpdateCheck` | — | --skip-update-check：本次启动跳过自动检查更新。 |
| `bool CheckUpdateOnly` | — | --check-update-only：只检查一次更新（弹提示或打印结果），不进入主界面。 |
| `bool ForceOffline` | — | --offline：本次强制以离线模式启动（不请求微软账户相关网络接口）。 |
| `bool ForcePortable` | — | --portable：强制使用便携模式（配置读写启动器目录而非 AppData）。 |
| `bool DisableTray` | — | --no-tray：本次启动不创建托盘图标。 |
| `bool StartMinimized` | — | --minimized：启动后直接最小化到任务栏/托盘。 |
| `bool StartMaximized` | — | --maximized：启动后直接最大化窗口。 |
| `bool ResetWindowLayout` | — | --reset-window：忽略上次记住的窗口位置/大小，使用默认值。 |
| `int? WindowWidth` | — | --width &lt;px&gt;：启动窗口宽度。 |
| `int? WindowHeight` | — | --height &lt;px&gt;：启动窗口高度。 |
| `string? LanguageOverride` | — | --lang &lt;code&gt;：本次启动强制使用的界面语言（如 zh-CN / en-US），不写回配置。 |
| `string? ThemeOverride` | — | --theme &lt;light\\|dark&gt;：本次启动强制使用的主题，不写回配置。 |
| `int? UiZoomPercent` | — | --zoom &lt;percent&gt;：本次启动强制使用的界面缩放百分比（如 125）。 |
| `bool DisableAnimations` | — | --no-animation：关闭界面动效（磁贴轮播/过渡动画等），低性能设备/远程桌面场景用。 |
| `bool SafeMode` | — | --safe-mode：安全模式启动——跳过公告/AI 悬浮球/自定义主题等非核心增强功能，便于排障。 |
| `bool ShowDebugConsole` | — | --debug-console：额外弹出一个调试控制台窗口，显示启动器内部日志输出。 |
| `bool DebugAprilFools` | — | — |
| `string? ConfigPathOverride` | — | --config-path &lt;path&gt;：使用指定路径的配置文件而非默认 AppData 路径（多实例/测试用）。 |
| `string? InstanceDirOverride` | — | --instance-dir &lt;path&gt;：使用指定目录作为实例（.minecraft）根目录。 |
| `string? JavaPathOverride` | — | --java &lt;path&gt;：本次启动游戏强制使用指定 Java 可执行文件路径，跳过自动探测。 |
| `int? MinMemoryMb` | — | --min-memory &lt;MB&gt; / --max-memory &lt;MB&gt;：本次启动游戏强制使用的 JVM 内存参数。 |
| `int? MaxMemoryMb` | — | — |
| `string? ExtraJvmArgs` | — | --jvm-args &lt;args&gt;：本次启动游戏额外追加的 JVM 参数（原样拼接在自动生成参数之后）。 |
| `string? ExtraGameArgs` | — | --game-args &lt;args&gt;：本次启动游戏额外追加的游戏参数。 |
| `string? AutoJoinServer` | — | --server &lt;ip:port&gt;：随 -r 一起使用时，游戏启动后自动尝试直连该服务器。 |
| `bool LaunchFullscreen` | — | --fullscreen：随 -r 一起使用时，游戏以全屏模式启动。 |
| `bool ClearCacheOnStart` | — | --clear-cache：启动前先清理下载缓存/临时文件，再继续后续动作。 |
| `string? ExportLogsPath` | — | --export-logs &lt;path&gt;：启动后立即把日志/崩溃报告打包导出到指定路径，用于自动化收集。 |
| `bool DisableSingleInstance` | — | --no-single-instance：本次跳过单实例检测，允许多开（调试多账户场景用）。 |
| `bool DisableGpuAcceleration` | — | --no-gpu-accel：关闭窗口硬件加速渲染（部分老旧显卡驱动兼容性问题时用）。 |
| `bool VerboseLogging` | — | --verbose：本次启动打印更详细的启动器日志（不影响写盘日志级别，只影响调试控制台）。 |
| `bool JustUpdated` | — | 隐藏参数 --just-updated：只由 UpdateCheckService 生成的重启脚本自动带上， 标记\"这次启动是自动更新流程重启回来的那一次\"，不写进帮助文本、也不建议用户手动使用。 MainWindow 首帧后据此决定是否弹出「这次更新了什么」的内嵌变更日志弹窗，见 MainWindow.ApplyStartupArgs 和 AppConfig.ShowUpdateChangelogPopup。 |
| `bool HasAnyAction { get; }` | — | 是否解析出了任何一个需要 MainWindow 首帧后处理的有效动作。 |

## EmbeddedDocumentationService

```csharp
public static class EmbeddedDocumentationService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `const string SolutionsFileName = "PAPERDOLL_FIX.md"` | — | — |
| `const string ApiFileName = "API_REFERENCE.md"` | — | — |
| `const string PluginExeProtocolFileName = "PLUGIN_EXE_PROTOCOL.md"` | — | — |
| `const string PluginGuideFileName = "PLUGIN_GUIDE.md"` | — | — |
| `const string LauncherControlFileName = "API_LAUNCHER_CONTROL.md"` | — | — |
| `const string ApiExamplesFileName = "API_EXAMPLES.md"` | — | — |
| `const string WorldsApiFileName = "API_WORLDS.md"` | — | — |
| `const string CompositionApiFileName = "API_LAUNCHER_COMPOSITION.md"` | — | — |
| `static string GetTitle(string fileName)` | — | — |
| `static string Read(string fileName)` | — | — |
| `static void Export(string fileName, string destination)` | — | — |

## FileChange

```csharp
public sealed class FileChange
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string Id { get; set; }` | — | — |
| `string Path { get; set; }` | — | — |
| `string? BeforeHash { get; set; }` | — | — |
| `string? AfterHash { get; set; }` | — | — |
| `string State { get; set; }` | — | — |
| `DateTime CreatedUtc { get; set; }` | — | — |

## IPluginAccounts

```csharp
public interface IPluginAccounts
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<PluginListSnapshot<PluginAccount>> GetSnapshotAsync(CancellationToken ct = default)` | — | — |
| `Task<PluginAccount?> GetSelectedAsync(CancellationToken ct = default)` | — | — |

## IPluginDownloadProvider

```csharp
public interface IPluginDownloadProvider
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `ValueTask<PluginVersionManifest> GetManifestAsync(CancellationToken ct)` | — | — |
| `ValueTask<string?> ResolveFileAsync(PluginFileRequest request, CancellationToken ct)` | — | — |

## IPluginDownloads

```csharp
public interface IPluginDownloads
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<PluginListSnapshot<PluginDownload>> GetSnapshotAsync(CancellationToken ct = default)` | — | — |

## IPluginEnvironment

```csharp
public interface IPluginEnvironment
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<PluginEnvironmentSnapshot> GetSnapshotAsync(CancellationToken ct = default)` | — | — |

## IPluginInstances

```csharp
public interface IPluginInstances
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<PluginListSnapshot<PluginInstance>> GetSnapshotAsync(PluginInstanceQuery? query = null, CancellationToken ct = default)` | — | — |
| `Task<PluginInstanceDetails?> GetDetailsAsync(string instanceId, CancellationToken ct = default)` | — | — |
| `Task<PluginInstance?> GetSelectedAsync(CancellationToken ct = default)` | — | — |

## IPluginLoaderInstaller

```csharp
public interface IPluginLoaderInstaller
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `ValueTask<ImmutableArray<PluginLoaderBuild>> GetBuildsAsync(string minecraftVersion, CancellationToken ct)` | — | — |
| `ValueTask InstallAsync(PluginInstallWorkspace workspace, PluginLoaderInstallRequest request, CancellationToken ct)` | — | — |

## IPluginLoginProvider

```csharp
public interface IPluginLoginProvider
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `ValueTask<PluginLoginTicket?> LoginAsync(PluginLoginBroker broker, CancellationToken ct)` | — | — |

## IPluginModProvider

```csharp
public interface IPluginModProvider
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `ValueTask<PluginModSearchResult> SearchAsync(PluginModQuery query, CancellationToken ct)` | — | — |
| `ValueTask<PluginModProject?> GetDetailsAsync(string projectId, CancellationToken ct)` | — | — |
| `ValueTask<ImmutableArray<PluginModFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct)` | — | — |

## IPluginMods

```csharp
public interface IPluginMods
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<PluginListSnapshot<PluginMod>> GetSnapshotAsync(string instanceId, CancellationToken ct = default)` | — | — |

## IPluginSettings

```csharp
public interface IPluginSettings
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<PluginSettingsSnapshot> GetSnapshotAsync(CancellationToken ct = default)` | — | — |

## IPluginSettingsPanel

```csharp
public interface IPluginSettingsPanel
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `void Save()` | — | 把面板当前的输入状态批量写入 PluginContext.Config。抛出异常会被宿主弹窗 捕获并以警告形式提示用户，弹窗不会关闭，方便用户看到哪里没填对再改。 |

## IPluginSkinModelRenderer

```csharp
public interface IPluginSkinModelRenderer : IDisposable
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `FrameworkElement View { get; }` | — | — |
| `void SetSkin(BitmapSource skin, bool slimModel)` | — | — |
| `void SetMotion(string motion)` | — | — |
| `void RenderFrame(TimeSpan elapsed)` | — | 实际经过时间；可驱动自有引擎。使用独立渲染循环的实现可以留空。 |

## LauncherCommandResult

```csharp
public sealed record LauncherCommandResult(int SchemaVersion, string Operation, string Status, string Code, int? Pid = null, string? InstanceId = null, string? AccountId = null, JsonElement? Data = null);
```

## LoadedPluginInfo

```csharp
public sealed class LoadedPluginInfo
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `required string FilePath { get; init; }` | — | 插件 DLL / EXE 的安装路径。 |
| `IPlugin? Plugin { get; init; }` | — | 成功加载时的插件实例；加载失败（缺依赖/没有实现 IPlugin/构造函数抛异常等） 时为 null，具体原因看 LoadError。 |
| `string? LoadError { get; init; }` | — | 加载失败时的异常描述；成功加载时为 null。 |
| `bool Enabled { get; set; }` | — | 用户是否已启用这个插件（持久化在 plugins-state.json 里）。 被禁用的插件文件仍然会被扫描到、显示在列表里，但不会执行 Initialize。 |
| `PluginContext? Context { get; init; }` | — | 该插件对应的 PluginContext，仅在本轮 DLL 成功初始化时非 null， "配置"按钮据此拿到 ctx 传给 Plugin.CreateSettingsPanel。 |
| `bool IsExecutable { get; init; }` | — | 外部进程插件；不会按 .NET IPlugin 接口加载。 |
| `Process? RunningProcess { get; init; }` | — | 由启动器启动并托管的进程，退出/重新扫描时会结束。 |
| `bool StartedOnce { get; init; }` | — | 仅导入时运行模式下，该插件已成功启动过。 |
| `PluginMetadata? Metadata { get; init; }` | — | — |
| `ImmutableArray<PluginLoadDiagnostic> Diagnostics { get; init; }` | — | — |
| `ImmutableArray<string> GrantedPermissions { get; init; }` | — | — |

## PluginAccount

```csharp
public sealed record PluginAccount(string Id, PluginAccountKind Kind, string DisplayName, string? Uuid, bool IsSelected, bool IsGuest);
```

## PluginAccountAdded

```csharp
public sealed record PluginAccountAdded(PluginAccount Account) : PluginNotification;
```

## PluginAccountKind

```csharp
public enum PluginAccountKind
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Offline` | — | — |
| `Microsoft` | — | — |
| `AuthServer` | — | — |
| `Custom` | — | — |

## PluginAccountRemoved

```csharp
public sealed record PluginAccountRemoved(string AccountId) : PluginNotification;
```

## PluginAccountSelectionChanged

```csharp
public sealed record PluginAccountSelectionChanged(PluginAccount? Account) : PluginNotification;
```

## PluginAccountUpdated

```csharp
public sealed record PluginAccountUpdated(PluginAccount Account, ImmutableArray<string> ChangedFields) : PluginNotification;
```

## PluginAppearanceSnapshot

```csharp
public sealed record PluginAppearanceSnapshot(string UiSkin, bool IsDarkMode, string? CustomAccentColor, int UiZoomPercent, bool EnableWindowTransparency, int WindowOpacityPercent, bool EnableGlobalWindowTransparency, int GlobalWindowOpacityPercent, int TextOpacityPercent, int BrightnessPercent, double CallbackTimeoutSeconds, ImmutableArray<string> AvailableSkins);
```

## PluginAvatarRenderRequest

```csharp
public sealed record PluginAvatarRenderRequest(PluginContext Plugin, byte[] SkinPngBytes, int OutputSize, bool IncludeHatLayer);
```

## PluginBackgroundAsset

```csharp
public sealed record PluginBackgroundAsset(string RelativeImagePath);
```

## PluginBackgroundDefinition

```csharp
public sealed record PluginBackgroundDefinition(string Id, string Name, string RelativeImagePath);
```

## PluginButtonDefinition

```csharp
public sealed record PluginButtonDefinition
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `required string Id { get; init; }` | — | — |
| `required string Text { get; init; }` | — | — |
| `string Target { get; init; }` | — | — |
| `string? Description { get; init; }` | — | — |
| `int Order { get; init; }` | — | — |
| `required Func<PluginUiContext, Task> OnClick { get; init; }` | — | — |

## PluginChoice

```csharp
public sealed record PluginChoice(string Value, string Label);
```

## PluginComponentAction

```csharp
public enum PluginComponentAction
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Add` | — | — |
| `Move` | — | — |
| `Remove` | — | — |
| `Replace` | — | — |
| `Configure` | — | — |

## PluginComponentDefinition

```csharp
public sealed record PluginComponentDefinition
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `required string Id { get; init; }` | — | — |
| `required string Target { get; init; }` | — | view:MainWindow、view:SettingsPage 或 dialog:某弹窗类名。 |
| `PluginComponentAction Action { get; init; }` | — | — |
| `string? Selector { get; init; }` | — | 组件 Name，或 Inspect 返回的 $root/0/1 路径；Add 时不需要。 |
| `string? Parent { get; init; }` | — | Add/Move 的容器选择器；$overlay 为自动创建、覆盖整个视图的绝对定位画布。 |
| `int Index { get; init; }` | — | — |
| `int Order { get; init; }` | — | — |
| `PluginComponentLayout Layout { get; init; }` | — | — |
| `Func<PluginUiContext, FrameworkElement>? CreateContent { get; init; }` | — | Add/Replace 创建全新组件；可返回含自定义渲染后端的 WPF 宿主控件。 |

## PluginComponentInfo

```csharp
public sealed record PluginComponentInfo(string Path, string Name, string Type, string? ParentPath, double Width, double Height)
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `double X { get; init; }` | — | — |
| `double Y { get; init; }` | — | — |
| `int ZIndex { get; init; }` | — | — |

## PluginComponentLayout

```csharp
public sealed record PluginComponentLayout
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `int? Row { get; init; }` | — | — |
| `int? Column { get; init; }` | — | — |
| `int? RowSpan { get; init; }` | — | — |
| `int? ColumnSpan { get; init; }` | — | — |
| `int? ZIndex { get; init; }` | — | — |
| `double? Left { get; init; }` | — | — |
| `double? Top { get; init; }` | — | — |
| `double? Right { get; init; }` | — | — |
| `double? Bottom { get; init; }` | — | — |
| `double? Width { get; init; }` | — | — |
| `double? Height { get; init; }` | — | — |
| `Thickness? Margin { get; init; }` | — | — |
| `HorizontalAlignment? HorizontalAlignment { get; init; }` | — | — |
| `VerticalAlignment? VerticalAlignment { get; init; }` | — | — |
| `Dock? Dock { get; init; }` | — | — |
| `double? MinWidth { get; init; }` | — | — |
| `double? MinHeight { get; init; }` | — | — |
| `double? MaxWidth { get; init; }` | — | — |
| `double? MaxHeight { get; init; }` | — | — |
| `double? Opacity { get; init; }` | — | — |
| `Visibility? Visibility { get; init; }` | — | — |
| `bool? IsHitTestVisible { get; init; }` | — | — |
| `bool? ClipToBounds { get; init; }` | — | — |
| `Transform? RenderTransform { get; init; }` | — | — |
| `Point? RenderTransformOrigin { get; init; }` | — | — |

## PluginComponentService

```csharp
public sealed class PluginComponentService : IDisposable
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `IDisposable Register(PluginComponentDefinition definition)` | — | — |
| `IReadOnlyList<PluginComponentInfo> Inspect(string target)` | — | 检查当前已经加载的视图。命名选择器须唯一；匿名控件使用返回的路径。 |
| `void Refresh()` | — | 宿主动态重建组件后可显式重新应用当前注册。 |
| `void Dispose()` | — | — |

## PluginConfigService

```csharp
public sealed class PluginConfigService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `T Get<T>(string key, T fallback)` | — | 读取一个配置项，key 不存在或者反序列化失败时返回 fallback， 不抛异常——插件不需要为"第一次运行、配置文件还没任何内容"这种正常情况 专门写 try/catch。 |
| `void Set<T>(string key, T value)` | — | 写入一个配置项并立即落盘。落盘是同步的、按整份文件重写，配置文件通常很小， 不需要为此做增量写入或者异步优化。 |
| `void Remove(string key)` | — | 删除一个配置项并落盘；key 不存在时静默忽略。 |
| `IReadOnlyDictionary<string, JsonElement> Snapshot()` | — | 当前已保存的所有 key，供"插件管理"里给不提供图形配置界面的插件 生成一份只读的通用 JSON 预览用，插件自己一般用不上这个方法。 |

## PluginControlService

```csharp
public sealed class PluginControlService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<ImmutableArray<PluginPageInfo>> GetPagesAsync(CancellationToken ct = default)` | — | — |
| `Task<bool> NavigateAsync(string pageId, CancellationToken ct = default)` | — | 通过宿主正常页面导航，保留设置页的保存/放弃确认。 |
| `Task<PluginControlSnapshot> GetSnapshotAsync(CancellationToken ct = default)` | — | — |
| `Task<PluginWindowSnapshot> GetWindowAsync(CancellationToken ct = default)` | — | — |
| `Task<PluginWindowSnapshot> UpdateWindowAsync(PluginWindowPatch patch, CancellationToken ct = default)` | — | — |
| `Task<PluginWindowSnapshot> SetWindowStateAsync(PluginWindowAction action, CancellationToken ct = default)` | — | — |
| `Task<PluginAppearanceSnapshot> GetAppearanceAsync(CancellationToken ct = default)` | — | — |
| `Task<PluginAppearanceSnapshot> UpdateSettingsAsync(PluginSettingsPatch patch, CancellationToken ct = default)` | — | — |
| `Task<bool> SelectAccountAsync(string accountId, CancellationToken ct = default)` | — | — |
| `Task<bool> SelectInstanceAsync(string instanceId, CancellationToken ct = default)` | — | — |
| `Task<PluginLaunchAcceptance> LaunchSelectedAsync(CancellationToken ct = default)` | — | Accepted 只表示已进入宿主启动流程；成功与否订阅 PluginGameStarted/PluginGameExited。 |
| `Task<bool> CancelLaunchAsync(CancellationToken ct = default)` | — | — |

## PluginControlSnapshot

```csharp
public sealed record PluginControlSnapshot(PluginWindowSnapshot Window, bool IsLaunching, string? InstanceId, string? AccountId);
```

## PluginDependency

```csharp
public sealed record PluginDependency(string Id, string VersionRange, bool Optional = false);
```

## PluginDialogContext

```csharp
public sealed class PluginDialogContext
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `PluginContext Plugin { get; }` | — | — |
| `object? Parameter { get; }` | — | — |
| `CancellationToken Stopping { get; }` | — | — |
| `void Close(bool? result = null)` | — | — |

## PluginDialogDefinition

```csharp
public sealed record PluginDialogDefinition
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `required string Id { get; init; }` | — | — |
| `required string Title { get; init; }` | — | — |
| `required Func<PluginDialogContext, FrameworkElement> CreateContent { get; init; }` | — | — |
| `double Width { get; init; }` | — | — |
| `bool DismissOnEscape { get; init; }` | — | — |
| `bool DismissOnBackgroundClick { get; init; }` | — | — |

## PluginDialogEvent

```csharp
public sealed record PluginDialogEvent(string Target, bool IsOpen, bool? Result);
```

## PluginDownload

```csharp
public sealed record PluginDownload(Guid Id, string Name, PluginDownloadState State, double ProgressPercent, string? PhaseCode, string? InstanceId, string? ErrorCode);
```

## PluginDownloadAcceptance

```csharp
public sealed record PluginDownloadAcceptance(Guid DownloadId, string Code);
```

## PluginDownloadCanceled

```csharp
public sealed record PluginDownloadCanceled(Guid DownloadId, string ReasonCode) : PluginNotification;
```

## PluginDownloadCompleted

```csharp
public sealed record PluginDownloadCompleted(PluginDownload Download) : PluginNotification;
```

## PluginDownloadCreated

```csharp
public sealed record PluginDownloadCreated(PluginDownload Download) : PluginNotification;
```

## PluginDownloadDetails

```csharp
public sealed record PluginDownloadDetails(PluginDownload Download, bool CanPause, bool CanResume, bool CanCancel, bool IsRunning);
```

## PluginDownloadFailed

```csharp
public sealed record PluginDownloadFailed(PluginDownload Download) : PluginNotification;
```

## PluginDownloadPlan

```csharp
public sealed record PluginDownloadPlan(Guid OperationId, string Url, string FileName, string? ExpectedSha1);
```

## PluginDownloadProgressChanged

```csharp
public sealed record PluginDownloadProgressChanged(PluginDownload Download) : PluginNotification;
```

## PluginDownloadRemoved

```csharp
public sealed record PluginDownloadRemoved(Guid DownloadId) : PluginNotification;
```

## PluginDownloadState

```csharp
public enum PluginDownloadState
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Downloading` | — | — |
| `Paused` | — | — |
| `Completed` | — | — |
| `Failed` | — | — |
| `Canceled` | — | — |

## PluginEnvironmentSnapshot

```csharp
public sealed record PluginEnvironmentSnapshot(string LauncherVersion, Version ApiVersion, string DataRootDirectory, string LauncherLanguage);
```

## PluginEventService

```csharp
public sealed partial class PluginEventService : IDisposable
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> callback) where TEvent : PluginNotification` | — | — |
| `void Dispose()` | — | — |
| `IDisposable InterceptLaunch(Func<PluginLaunchPlan, CancellationToken, ValueTask<PluginInterception<PluginLaunchPlan>>> callback)` | — | — |
| `IDisposable InterceptDownload(Func<PluginDownloadPlan, CancellationToken, ValueTask<PluginInterception<PluginDownloadPlan>>> callback)` | — | — |
| `IDisposable InterceptInstanceCreation(Func<PluginInstanceCreationPlan, CancellationToken, ValueTask<PluginInterception<PluginInstanceCreationPlan>>> callback)` | — | — |

## PluginFileRequest

```csharp
public sealed record PluginFileRequest(string OriginalUrl, string FileName, string? ExpectedSha1);
```

## PluginFlowCanceledException

```csharp
public sealed class PluginFlowCanceledException : PluginOperationException
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string PluginId { get; }` | — | — |
| `string Reason { get; }` | — | — |

## PluginGameCrashed

```csharp
public sealed record PluginGameCrashed(string InstanceId, int Pid, int? ExitCode) : PluginNotification;
```

## PluginGameExited

```csharp
public sealed record PluginGameExited(string InstanceId, int Pid, int? ExitCode, bool UserRequestedClose) : PluginNotification;
```

## PluginGameFolder

```csharp
public sealed record PluginGameFolder(string Id, string Name, string Path, bool IsSelected);
```

## PluginGameStarted

```csharp
public sealed record PluginGameStarted(string InstanceId, int Pid) : PluginNotification;
```

## PluginGameVersion

```csharp
public sealed record PluginGameVersion(string Id, string Type, string ReleaseTime, bool IsLatestRelease, bool IsLatestSnapshot);
```

## PluginHostUnavailableException

```csharp
public sealed class PluginHostUnavailableException : PluginOperationException
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `PluginHostUnavailableException() : base("HostUnavailable", "此插件上下文未连接到运行中的启动器。")` | — | — |

## PluginInstallConflictAction

```csharp
public enum PluginInstallConflictAction
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Cancel` | — | — |
| `Copy` | — | — |
| `Replace` | — | — |

## PluginInstallWorkspace

```csharp
public sealed class PluginInstallWorkspace
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string VersionId { get; }` | — | — |
| `Task WriteVersionJsonAsync(string json, CancellationToken ct = default)` | — | — |
| `Task DownloadFileAsync(string url, string relativePath, string? sha1 = null, CancellationToken ct = default)` | — | — |
| `Task<int> RunJavaInstallerAsync(string relativeJar, ImmutableArray<string> arguments, CancellationToken ct = default)` | — | — |

## PluginInstance

```csharp
public sealed record PluginInstance(string Id, string Name, string MinecraftVersion, string? LoaderId, string? LoaderVersion, bool IsInstalled, bool IsSelected, bool IsHidden);
```

## PluginInstanceActionDefinition

```csharp
public sealed record PluginInstanceActionDefinition(string Id, string Title, Func<PluginInstanceDetails, CancellationToken, ValueTask> ExecuteAsync, int Order = 0);
```

## PluginInstanceCatalogChanged

```csharp
public sealed record PluginInstanceCatalogChanged(string ReasonCode) : PluginNotification;
```

## PluginInstanceCreated

```csharp
public sealed record PluginInstanceCreated(PluginInstance Instance) : PluginNotification;
```

## PluginInstanceCreationPlan

```csharp
public sealed record PluginInstanceCreationPlan(Guid OperationId, string InstanceId, string Name, string Kind);
```

## PluginInstanceDetails

```csharp
public sealed record PluginInstanceDetails(PluginInstance Instance, string InstanceDirectory, string GameDirectory, bool Isolated, string? ConfiguredJavaPath, PluginJavaResolutionState JavaResolutionState, ImmutableArray<string> ConfiguredJvmArguments);
```

## PluginInstanceDirectories

```csharp
public sealed record PluginInstanceDirectories(string InstanceId, string InstanceDirectory, string GameDirectory, string ModsDirectory, string ResourcePacksDirectory, string ShaderPacksDirectory, string SavesDirectory);
```

## PluginInstanceQuery

```csharp
public sealed record PluginInstanceQuery(bool IncludeHidden = false, bool IncludeIncomplete = true);
```

## PluginInstanceRemoved

```csharp
public sealed record PluginInstanceRemoved(string InstanceId) : PluginNotification;
```

## PluginInstanceRenamed

```csharp
public sealed record PluginInstanceRenamed(string OldInstanceId, string NewInstanceId, string OldName, string NewName) : PluginNotification;
```

## PluginInstanceSelectionChanged

```csharp
public sealed record PluginInstanceSelectionChanged(string? InstanceId) : PluginNotification;
```

## PluginInstanceVisibilityChanged

```csharp
public sealed record PluginInstanceVisibilityChanged(string InstanceId, bool IsHidden) : PluginNotification;
```

## PluginInterception

```csharp
public sealed record PluginInterception<T>(bool IsCanceled, string? Reason, T? Replacement)
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `static PluginInterception<T> Continue(T? replacement = default)` | — | — |
| `static PluginInterception<T> Cancel(string reason)` | — | — |

## PluginJavaResolutionState

```csharp
public enum PluginJavaResolutionState
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Configured` | — | — |
| `Unresolved` | — | — |

## PluginJavaRuntime

```csharp
public sealed record PluginJavaRuntime(string Id, string Name, string JavawPath, int? MajorVersion, string Source, int Priority, bool IsSelected, bool Exists);
```

## PluginLaunchAcceptance

```csharp
public sealed record PluginLaunchAcceptance(bool Accepted, string Code);
```

## PluginLaunchPlan

```csharp
public sealed record PluginLaunchPlan(Guid OperationId, string InstanceId, ImmutableArray<string> JvmArguments, ImmutableArray<string> GameArguments, ImmutableDictionary<string, string> EnvironmentVariables);
```

## PluginLaunchRequest

```csharp
public sealed record PluginLaunchRequest(PluginContext Plugin, LauncherService.LaunchOptions Options);
```

## PluginLauncherReady

```csharp
public sealed record PluginLauncherReady : PluginNotification
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string LauncherVersion { get; init; }` | — | — |
| `Version ApiVersion { get; init; }` | — | — |

## PluginLauncherService

```csharp
public sealed class PluginLauncherService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `IPluginAccounts Accounts { get; }` | — | — |
| `IPluginInstances Instances { get; }` | — | — |
| `IPluginMods Mods { get; }` | — | — |
| `IPluginDownloads Downloads { get; }` | — | — |
| `IPluginSettings Settings { get; }` | — | — |
| `IPluginEnvironment Environment { get; }` | — | — |

## PluginLauncherStopping

```csharp
public sealed record PluginLauncherStopping : PluginNotification
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string ReasonCode { get; init; }` | — | — |

## PluginListSnapshot

```csharp
public sealed record PluginListSnapshot<T>(long Revision, DateTimeOffset CapturedAtUtc, ImmutableArray<T> Items);
```

## PluginLoadDiagnostic

```csharp
public sealed record PluginLoadDiagnostic(string Code, ImmutableArray<string> RelatedPluginIds, string SafeMessage);
```

## PluginLoaderBuild

```csharp
public sealed record PluginLoaderBuild(string Id, string Name, string Version);
```

## PluginLoaderInstallRequest

```csharp
public sealed record PluginLoaderInstallRequest(string MinecraftVersion, string BuildId, string InstanceName);
```

## PluginLoginBroker

```csharp
public sealed class PluginLoginBroker
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<PluginLoginTicket?> AuthorizeAsync(PluginLoginRequest request, CancellationToken ct = default)` | — | — |

## PluginLoginKind

```csharp
public enum PluginLoginKind
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Offline` | — | — |
| `MicrosoftDeviceCode` | — | — |
| `Yggdrasil` | — | — |

## PluginLoginRequest

```csharp
public sealed record PluginLoginRequest(PluginLoginKind Kind, string? AuthServerUrl = null);
```

## PluginLoginTicket

```csharp
public sealed record PluginLoginTicket(Guid Id);
```

## PluginManagementService

```csharp
public sealed class PluginManagementService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<ImmutableArray<PluginGameFolder>> GetFoldersAsync(CancellationToken ct = default)` | — | — |
| `Task<PluginInstanceDirectories> GetInstanceDirectoriesAsync(string instanceId, CancellationToken ct = default)` | — | — |
| `Task<bool> SetInstanceHiddenAsync(string instanceId, bool hidden, CancellationToken ct = default)` | — | — |
| `Task<string> RenameInstanceAsync(string instanceId, string newName, CancellationToken ct = default)` | — | 返回改名后的新实例 ID；原 ID 随目录名变化而失效。 |
| `Task<bool> SetInstanceIsolationAsync(string instanceId, bool? isolated, CancellationToken ct = default)` | — | null 恢复全局隔离设置；只改变设置，不搬移已有存档和模组。 |
| `Task<PluginMod> SetModEnabledAsync(string instanceId, string fileName, bool enabled, CancellationToken ct = default)` | — | — |
| `Task<ImmutableArray<PluginJavaRuntime>> GetJavaRuntimesAsync(CancellationToken ct = default)` | — | — |
| `Task<bool> SelectJavaAsync(string? javaId, string? instanceId = null, CancellationToken ct = default)` | — | instanceId 为空时设置全局默认；javaId 为空则清除覆盖并恢复自动/全局选择。 |
| `Task<PluginDownloadDetails> GetDownloadAsync(Guid downloadId, CancellationToken ct = default)` | — | — |
| `Task<PluginDownloadDetails> PauseDownloadAsync(Guid downloadId, CancellationToken ct = default)` | — | — |
| `Task<PluginDownloadDetails> ResumeDownloadAsync(Guid downloadId, CancellationToken ct = default)` | — | — |
| `Task<bool> CancelDownloadAsync(Guid downloadId, CancellationToken ct = default)` | — | — |
| `Task<int> ClearFinishedDownloadsAsync(CancellationToken ct = default)` | — | 仅清理已完成、失败和取消的列表记录；不删除已下载文件。 |
| `Task<PluginDownloadAcceptance> RepairInstanceAsync(string instanceId, CancellationToken ct = default)` | — | — |

## PluginManager

```csharp
public sealed class PluginManager
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `IReadOnlyList<LoadedPluginInfo> Plugins { get; private set; }` | — | — |
| `event Action? Changed` | — | — |
| `PluginLaunchMode StartPolicy { get; set; }` | — | — |
| `PluginManager(Action<string>? logSink = null) : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XCL2", "Plugins"), logSink)` | — | — |
| `string InstalledDirectory { get; }` | — | — |
| `string BackupDirectory { get; }` | — | — |
| `string? LastBackupPath { get; private set; }` | — | — |
| `Task<string> BackupBeforeUpgradeAsync(CancellationToken ct)` | — | — |
| `string BackupBeforeUpgrade()` | — | — |
| `void RestoreUpgradeBackup(string backup)` | — | — |
| `string InstallUpgrade(string package, string targetEntry)` | — | — |
| `string? FindInstallConflict(string sourcePath)` | — | — |
| `string InstallFromFile(string sourcePath)` | — | 旧的一参数接口仍然保留；有冲突时由三参数调用方显式决定。 |
| `string? InstallFromFile(string sourcePath, PluginInstallConflictAction action)` | — | — |
| `void ScanAndLoad()` | — | — |
| `void SetEnabled(string filePath, bool enabled)` | — | — |
| `void SetPermissions(string filePath, IEnumerable<string> permissions)` | — | 由用户授权界面调用；仅可授予清单声明的权限。撤销立即影响当前上下文。 |
| `ImmutableArray<string> GetGrantedPermissions(string filePath)` | — | — |
| `void SetEnabled(IEnumerable<string> filePaths, bool enabled)` | — | — |
| `IReadOnlyList<PluginOperationResult> Uninstall(IEnumerable<string> filePaths)` | — | 实际移除安装目录和状态；配置/数据保留，避免丢失用户内容。批量逐项报告。 |
| `void ShutdownAll()` | — | — |

## PluginMessage

```csharp
public sealed record PluginMessage(string SenderPluginId, string Channel, JsonElement Payload);
```

## PluginMessageService

```csharp
public sealed class PluginMessageService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `ImmutableArray<PluginPeer> GetPeers()` | — | — |
| `IDisposable Subscribe(string channel, Func<PluginMessage, CancellationToken, ValueTask> callback)` | — | — |
| `Task<int> SendAsync(string targetPluginId, string channel, JsonElement payload, CancellationToken ct = default)` | — | — |

## PluginMetadata

```csharp
public sealed record PluginMetadata(string DeclaredId, string InstanceId, string Name, string Version, string Author, string Description, Version MinApiVersion, Version? MaxApiVersion, string? MinLauncherVersion, ImmutableArray<PluginDependency> Dependencies, ImmutableArray<string> Conflicts, ImmutableArray<string> Permissions, int Priority, bool IsLegacy);
```

## PluginMod

```csharp
public sealed record PluginMod(string FileName, string DisplayName, string? ModId, string? Version, bool IsEnabled, long FileSizeBytes);
```

## PluginModCatalogChanged

```csharp
public sealed record PluginModCatalogChanged(string InstanceId) : PluginNotification;
```

## PluginModEnabledChanged

```csharp
public sealed record PluginModEnabledChanged(string InstanceId, string FileName, bool IsEnabled) : PluginNotification;
```

## PluginModFile

```csharp
public sealed record PluginModFile(string Id, string Name, string GameVersion, string Loader, string FileName, string Url, string? Sha1);
```

## PluginModInstalled

```csharp
public sealed record PluginModInstalled(string InstanceId, PluginMod Mod) : PluginNotification;
```

## PluginModProject

```csharp
public sealed record PluginModProject(string Id, string Title, string Description, string Author);
```

## PluginModQuery

```csharp
public sealed record PluginModQuery(string Query, string? GameVersion, string? Loader, int Offset = 0, int Limit = 20);
```

## PluginModRemoved

```csharp
public sealed record PluginModRemoved(string InstanceId, string FileName) : PluginNotification;
```

## PluginModSearchResult

```csharp
public sealed record PluginModSearchResult(ImmutableArray<PluginModProject> Items, int Total);
```

## PluginNavigationPageDefinition

```csharp
public sealed record PluginNavigationPageDefinition(string Id, string Title, Func<PluginUiContext, FrameworkElement> CreateContent, bool ShowInSimplifiedMode = false, ImmutableArray<string> Languages = default, int Order = 0);
```

## PluginNetworkService

```csharp
public sealed class PluginNetworkService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<string> GetTextAsync(string url, CancellationToken ct = default)` | — | — |

## PluginNotification

```csharp
public abstract record PluginNotification
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `long Sequence { get; init; }` | — | — |
| `long Revision { get; init; }` | — | — |
| `DateTimeOffset OccurredAtUtc { get; init; }` | — | — |

## PluginOperationException

```csharp
public class PluginOperationException : Exception
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string Code { get; }` | — | — |
| `string SafeMessage { get; }` | — | — |
| `PluginOperationException(string code, string safeMessage) : base(safeMessage)` | — | — |

## PluginOperationResult

```csharp
public sealed record PluginOperationResult(string FilePath, bool Success, string? Error = null);
```

## PluginOptionDefinition

```csharp
public sealed record PluginOptionDefinition
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `required string Id { get; init; }` | — | — |
| `required string Title { get; init; }` | — | — |
| `string Target { get; init; }` | — | — |
| `string? ConfigKey { get; init; }` | — | — |
| `string? Description { get; init; }` | — | — |
| `int Order { get; init; }` | — | — |
| `PluginOptionKind Kind { get; init; }` | — | — |
| `object? DefaultValue { get; init; }` | — | — |
| `double Minimum { get; init; }` | — | — |
| `double Maximum { get; init; }` | — | — |
| `IReadOnlyList<PluginChoice> Choices { get; init; }` | — | — |
| `Func<object?, string?>? Validate { get; init; }` | — | 返回 null/空字符串表示合法，否则在控件旁显示错误且不保存。 |
| `Action<object?>? OnChanged { get; init; }` | — | 成功保存之后调用；回调异常只记录日志，不撤销已经保存的配置。 |

## PluginOptionKind

```csharp
public enum PluginOptionKind
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Toggle` | — | — |
| `Text` | — | — |
| `Number` | — | — |
| `Choice` | — | — |

## PluginPackageSignature

```csharp
public static class PluginPackageSignature
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `const string FileName = "plugin.signature.json"` | — | — |
| `static string SignLocal(string directory)` | — | — |
| `static string Verify(string directory)` | — | — |

## PluginPageInfo

```csharp
public sealed record PluginPageInfo(string Id, string Title, bool IsPluginPage);
```

## PluginPanelDefinition

```csharp
public sealed record PluginPanelDefinition
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `required string Id { get; init; }` | — | — |
| `string Target { get; init; }` | — | — |
| `int Order { get; init; }` | — | — |
| `required Func<PluginUiContext, FrameworkElement> CreateContent { get; init; }` | — | 每次创建新控件，不可复用已挂在其他父控件下的 WPF 元素。 |

## PluginPeer

```csharp
public sealed record PluginPeer(string InstanceId, string DeclaredId, string Version);
```

## PluginPeerUnavailable

```csharp
public sealed record PluginPeerUnavailable(string PluginId) : PluginNotification;
```

## PluginPermissionException

```csharp
public sealed class PluginPermissionException : PluginOperationException
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string Permission { get; }` | — | — |
| `PluginPermissionException(string permission) : base("PermissionDenied", "插件没有获准使用权限：" + permission)` | — | — |

## PluginPermissionGrant

```csharp
public sealed record PluginPermissionGrant(string Permission, bool Declared, bool Granted, bool Available);
```

## PluginPermissionNames

```csharp
public static class PluginPermissionNames
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `const string ProjectsRead = "projects.read", ProjectsWrite = "projects.write", TasksManage = "tasks.manage"` | — | — |
| `const string DownloadsControl = "downloads.control", InstancesWrite = "instances.write", ModsWrite = "mods.write", ResourcesRead = "resources.read", ResourcesInstall = "resources.install", JavaRead = "java.read", JavaSelect = "java.select"` | — | — |
| `const string LauncherControl = "launcher.control", SettingsWrite = "settings.write", UiWindow = "ui.window"` | — | — |
| `const string UiPreview = "ui.preview", WorldsRead = "worlds.read", WorldsWrite = "worlds.write"` | — | — |
| `const string AccountsRead = "accounts.read", InstancesRead = "instances.read", ModsRead = "mods.read", DownloadsRead = "downloads.read", SettingsRead = "settings.read", EnvironmentRead = "environment.read", Lifecycle = "events.lifecycle", LaunchModify = "launch.modify", DownloadIntercept = "downloads.intercept", InstanceValidate = "instances.validate", Network = "network", DownloadProvider = "providers.download", ModProvider = "providers.mod", LoaderProvider = "providers.loader", AuthProvider = "auth.provider", InstanceFilesRead = "files.instances.read", InstanceFilesWrite = "files.instances.write", ExternalFilesRead = "files.external.read", ExternalFilesWrite = "files.external.write", ProcessInstall = "process.install", UiBasic = "ui.basic", UiComponents = "ui.components", UiNavigation = "ui.navigation", UiInstanceActions = "ui.instanceActions", UiSettings = "ui.settings", UiBackground = "ui.background", Messaging = "plugin.messaging"` | — | — |
| `static ImmutableArray<string> All { get; }` | — | — |

## PluginPermissionService

```csharp
public sealed class PluginPermissionService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `bool Has(string permission)` | — | — |
| `ImmutableArray<PluginPermissionGrant> GetSnapshot()` | — | — |
| `void Demand(string permission)` | — | — |

## PluginPreviewService

```csharp
public sealed class PluginPreviewService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `IDisposable RegisterCard(string id, string title, string description, string destination = "worlds", string accent = "#1BD96A")` | — | — |
| `void UpdateCard(string id, string title, string description, string accent = "#1BD96A")` | — | — |
| `IDisposable SetAppearance(string title, string accent = "#1BD96A", bool compact = false)` | — | — |

## PluginProject

```csharp
public sealed record PluginProject(string Id, string Name, string Root);
```

## PluginProviderDefinition

```csharp
public sealed record PluginProviderDefinition(string Id, string Name);
```

## PluginProviderInfo

```csharp
public sealed record PluginProviderInfo(string Id, string PluginId, string Name, string Kind);
```

## PluginProviderService

```csharp
public sealed class PluginProviderService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `IDisposable RegisterDownloadProvider(PluginProviderDefinition definition, IPluginDownloadProvider provider)` | — | — |
| `IDisposable RegisterModProvider(PluginProviderDefinition definition, IPluginModProvider provider)` | — | — |
| `IDisposable RegisterLoaderInstaller(PluginProviderDefinition definition, IPluginLoaderInstaller provider)` | — | — |
| `IDisposable RegisterLoginProvider(PluginProviderDefinition definition, IPluginLoginProvider provider)` | — | — |

## PluginResourceFile

```csharp
public sealed record PluginResourceFile(string Token, string Id, string Name, string FileName, ImmutableArray<string> GameVersions, ImmutableArray<string> Loaders, long? SizeBytes, string? Sha1);
```

## PluginResourceFileQuery

```csharp
public sealed record PluginResourceFileQuery(string SourceId, PluginResourceKind Kind, string ProjectId, string? GameVersion = null, string? Loader = null);
```

## PluginResourceInstallRequest

```csharp
public sealed record PluginResourceInstallRequest(string Token, string InstanceId, bool ReplaceExisting = false);
```

## PluginResourceKind

```csharp
public enum PluginResourceKind
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Mod` | — | — |
| `ResourcePack` | — | — |
| `Shader` | — | — |

## PluginResourceNotFoundException

```csharp
public sealed class PluginResourceNotFoundException : PluginOperationException
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `PluginResourceNotFoundException() : base("ResourceNotFound", "实例或业务资源已不存在。")` | — | — |

## PluginResourceProject

```csharp
public sealed record PluginResourceProject(string SourceId, string Id, PluginResourceKind Kind, string Title, string Description, string Author);
```

## PluginResourceQuery

```csharp
public sealed record PluginResourceQuery(string SourceId, PluginResourceKind Kind, string Query = "", string? GameVersion = null, string? Loader = null, int Offset = 0, int Limit = 20);
```

## PluginResourceSearchResult

```csharp
public sealed record PluginResourceSearchResult(ImmutableArray<PluginResourceProject> Items, int Total, int Offset, int Limit);
```

## PluginResourceService

```csharp
public sealed class PluginResourceService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<ImmutableArray<PluginResourceSource>> GetSourcesAsync(CancellationToken ct = default)` | — | — |
| `Task<PluginResourceSearchResult> SearchAsync(PluginResourceQuery query, CancellationToken ct = default)` | — | — |
| `Task<ImmutableArray<PluginResourceFile>> GetFilesAsync(PluginResourceFileQuery query, CancellationToken ct = default)` | — | — |
| `Task<PluginDownloadAcceptance> InstallAsync(PluginResourceInstallRequest request, CancellationToken ct = default)` | — | — |
| `Task<ImmutableArray<PluginGameVersion>> GetGameVersionsAsync(CancellationToken ct = default)` | — | — |
| `Task<PluginDownloadAcceptance> InstallGameVersionAsync(string folderId, string versionId, CancellationToken ct = default)` | — | — |

## PluginResourceSource

```csharp
public sealed record PluginResourceSource(string Id, string Name, bool IsPlugin, ImmutableArray<PluginResourceKind> Kinds);
```

## PluginRuntimeService

```csharp
public sealed class PluginRuntimeService : IDisposable
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `IDisposable RegisterLaunchMiddleware(string id, PluginLaunchMiddleware middleware, int order = 0)` | — | — |
| `IDisposable RegisterAvatarRenderer(string id, PluginAvatarRenderer renderer, int order = 0)` | — | — |
| `IDisposable RegisterSkinModelRenderer(string id, PluginSkinModelRendererFactory factory, int order = 0)` | — | 替换皮肤模型的整个渲染后端；可返回 WPF、D3DImage 或原生窗口宿主。 |
| `void Dispose()` | — | — |

## PluginSettingsChanged

```csharp
public sealed record PluginSettingsChanged(ImmutableArray<string> ChangedKeys) : PluginNotification;
```

## PluginSettingsPatch

```csharp
public sealed record PluginSettingsPatch
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string? UiSkin { get; init; }` | — | — |
| `bool? IsDarkMode { get; init; }` | — | — |
| `string? CustomAccentColor { get; init; }` | — | — |
| `bool? SimplifiedModeEnabled { get; init; }` | — | — |
| `bool? EnablePageAnimations { get; init; }` | — | — |
| `bool? EnableUiAnimations { get; init; }` | — | — |
| `int? UiZoomPercent { get; init; }` | — | — |
| `bool? EnableWindowTransparency { get; init; }` | — | — |
| `int? WindowOpacityPercent { get; init; }` | — | — |
| `bool? EnableGlobalWindowTransparency { get; init; }` | — | — |
| `int? GlobalWindowOpacityPercent { get; init; }` | — | — |
| `int? TextOpacityPercent { get; init; }` | — | — |
| `int? BrightnessPercent { get; init; }` | — | — |
| `int? MinMemoryMb { get; init; }` | — | — |
| `int? MaxMemoryMb { get; init; }` | — | — |
| `bool? IsolateVersionsByDefault { get; init; }` | — | — |
| `string? BuiltInDownloadSource { get; init; }` | — | — |
| `double? CallbackTimeoutSeconds { get; init; }` | — | — |

## PluginSettingsSnapshot

```csharp
public sealed record PluginSettingsSnapshot(long Revision, string LauncherLanguage, string UiSkin, bool IsDarkMode, bool SimplifiedModeEnabled, bool EnablePageAnimations, bool EnableUiAnimations, int UiZoomPercent, int MinMemoryMb, int MaxMemoryMb, bool IsolateVersionsByDefault, string BuiltInDownloadSource);
```

## PluginSkinModelRenderRequest

```csharp
public sealed record PluginSkinModelRenderRequest(PluginContext Plugin, BitmapSource Skin, bool SlimModel, string Username);
```

## PluginTemplateGenerator

```csharp
public static class PluginTemplateGenerator
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `static void Generate(string targetDir, string pluginId, string displayName)` | — | 在 targetDir（必须是一个空目录或者不存在的目录）下生成模板项目。 pluginId 建议形如 "yourname.plugin-name"，会被用作类名、Id 默认值和项目名。 |

## PluginUiContext

```csharp
public sealed class PluginUiContext
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `PluginContext Plugin { get; }` | — | — |
| `string Target { get; }` | — | — |
| `CancellationToken Stopping { get; }` | — | — |

## PluginUiService

```csharp
public sealed partial class PluginUiService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `IDisposable RegisterNavigationPage(PluginNavigationPageDefinition definition)` | — | — |
| `IDisposable RegisterInstanceAction(PluginInstanceActionDefinition definition)` | — | — |
| `IDisposable RegisterSetting(PluginOptionDefinition definition)` | — | — |
| `IDisposable RegisterBackground(PluginBackgroundDefinition definition)` | — | — |
| `Task<PluginBackgroundAsset?> ImportBackgroundImageAsync(CancellationToken ct = default)` | — | — |
| `CancellationToken Stopping { get; }` | — | — |
| `Task RunOnUiAsync(Action action)` | — | — |
| `IDisposable RegisterDialog(PluginDialogDefinition definition)` | — | — |
| `IDisposable RegisterButton(PluginButtonDefinition definition)` | — | — |
| `IDisposable RegisterOption(PluginOptionDefinition definition)` | — | — |
| `IDisposable RegisterPanel(PluginPanelDefinition definition)` | — | — |
| `IDisposable SubscribeDialogs(Action<PluginDialogEvent> observer)` | — | — |
| `string GetDialogTarget(string id)` | — | — |
| `Task<bool?> ShowDialogAsync(string id, object? parameter = null)` | — | — |
| `Task<bool?> ShowMessageAsync(string title, string message)` | — | — |
| `Task<bool> ConfirmAsync(string title, string message)` | — | — |
| `void Dispose()` | — | — |

## PluginUiTargets

```csharp
public static class PluginUiTargets
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `const string PluginSettings = "plugin.settings"` | — | — |
| `const string PluginManager = "dialog:PluginManagerDialog"` | — | — |
| `const string LoaderChoice = "dialog:LoaderChoiceDialog"` | — | — |
| `const string MultiLoaderInstall = "dialog:MultiLoaderInstallWindow"` | — | — |
| `const string CreateServer = "dialog:CreateServerWindow"` | — | — |
| `const string ExperimentalFeatures = "dialog:ExperimentalFeaturesWindow"` | — | — |
| `static string ForDialog<T>() where T : FrameworkElement, IOverlayDialog` | — | — |

## PluginUpgradeResult

```csharp
public sealed record PluginUpgradeResult(string Package, string Report, string Fingerprint);
```

## PluginVersionEntry

```csharp
public sealed record PluginVersionEntry(string Id, string Type, string Url, string ReleaseTime);
```

## PluginVersionManifest

```csharp
public sealed record PluginVersionManifest(string LatestRelease, string LatestSnapshot, ImmutableArray<PluginVersionEntry> Versions);
```

## PluginWindowAction

```csharp
public enum PluginWindowAction
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Activate` | — | — |
| `Minimize` | — | — |
| `Maximize` | — | — |
| `Restore` | — | — |

## PluginWindowPatch

```csharp
public sealed record PluginWindowPatch
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string? Title { get; init; }` | — | — |
| `double? Width { get; init; }` | — | — |
| `double? Height { get; init; }` | — | — |
| `bool? Topmost { get; init; }` | — | — |
| `bool? SidebarCollapsed { get; init; }` | — | — |

## PluginWindowSnapshot

```csharp
public sealed record PluginWindowSnapshot(string Title, string State, double Width, double Height, bool Topmost, bool SidebarCollapsed);
```

## PluginWorldService

```csharp
public sealed class PluginWorldService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<IReadOnlyList<WorldArchive>> GetSnapshotAsync(CancellationToken ct = default)` | — | — |
| `IReadOnlyList<WorldEditField> GetEditSchema()` | — | — |
| `Task<WorldArchive> GetAsync(string worldPath, CancellationToken ct = default)` | — | — |
| `Task<string> UpdateAsync(string worldPath, WorldEdit edit, CancellationToken ct = default)` | — | — |
| `Task<string> GenerateCopyAsync(string worldPath, string destinationPath, WorldEdit edit, CancellationToken ct = default)` | — | — |

## ProjectEntry

```csharp
public sealed record ProjectEntry(string Path, long Bytes, DateTime ModifiedUtc);
```

## ProjectFiles

```csharp
public static class ProjectFiles
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `const long MaxFileBytes = 16 * 1024 * 1024` | — | — |
| `static string Resolve(string root, string relative, bool allowRoot = false)` | — | — |
| `static IEnumerable<ProjectEntry> Enumerate(string root, string pattern = "*", int limit = 20000, CancellationToken ct = default, Func<string, bool>? allowed = null)` | — | — |
| `static byte[] ReadBytes(string path)` | — | — |
| `static string Hash(byte[] bytes)` | — | — |
| `static string HashFile(string path)` | — | — |
| `static (string Text, Encoding Encoding, bool Bom) Decode(byte[] bytes)` | — | — |
| `static byte[] EncodeLike(string text, byte[]? original)` | — | — |
| `static ProjectTextPage ReadPage(string root, string path, int startLine = 1, int lineCount = 200, CancellationToken ct = default)` | — | — |
| `static ProjectSearchResult Search(string root, string query, string pattern = "*", bool regex = false, bool caseSensitive = false, int maxResults = 200, CancellationToken ct = default, Func<string, bool>? allowed = null)` | — | — |
| `static string Patch(string original, string oldText, string newText)` | — | — |
| `static void AtomicWrite(string path, byte[] bytes)` | — | — |

## ProjectMatch

```csharp
public sealed record ProjectMatch(string Path, int Line, int Column, string Text);
```

## ProjectSearchResult

```csharp
public sealed record ProjectSearchResult(IReadOnlyList<ProjectMatch> Matches, int FilesScanned, bool Truncated, IReadOnlyList<string> Warnings);
```

## ProjectTextPage

```csharp
public sealed record ProjectTextPage(string Path, string Sha256, int StartLine, int? NextLine, string Text);
```

## TaskStore

```csharp
public sealed class TaskStore(string directory)
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `WorkTask Get(string id)` | — | — |
| `IReadOnlyList<WorkTask> List()` | — | — |
| `WorkTask Create(string title, string goal, string projectRoot = "")` | — | — |
| `void Save(WorkTask task)` | — | — |
| `WorkTask Update(string id, Action<WorkTask> edit)` | — | — |
| `WorkTask Checkpoint(string id, string text)` | — | — |

## WorkStep

```csharp
public sealed class WorkStep
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string Id { get; set; }` | — | — |
| `string Title { get; set; }` | — | — |
| `string Status { get; set; }` | — | — |
| `string Evidence { get; set; }` | — | — |

## WorkTask

```csharp
public sealed class WorkTask
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string Id { get; set; }` | — | — |
| `string Title { get; set; }` | — | — |
| `string Goal { get; set; }` | — | — |
| `string ProjectRoot { get; set; }` | — | — |
| `string Mode { get; set; }` | — | — |
| `string Status { get; set; }` | — | — |
| `string NextAction { get; set; }` | — | — |
| `string Notes { get; set; }` | — | — |
| `List<WorkStep> Steps { get; set; }` | — | — |
| `List<string> ChangeIds { get; set; }` | — | — |
| `List<string> Checkpoints { get; set; }` | — | — |
| `DateTime UpdatedUtc { get; set; }` | — | — |
| `long Revision { get; set; }` | — | — |

## EXE 命名管道 API

| method | params 字段 |
| --- | --- |
| `hello` | `token` |
| `folders.list` | — |
| `instances.directories` | `instanceId` |
| `instances.setHidden` | `hidden`, `instanceId` |
| `instances.rename` | `instanceId`, `newName` |
| `instances.setIsolation` | `instanceId`, `isolated` |
| `instances.repair` | `instanceId` |
| `mods.setEnabled` | `enabled`, `fileName`, `instanceId` |
| `java.list` | — |
| `java.select` | `instanceId`, `javaId` |
| `downloads.get` | `downloadId` |
| `downloads.pause` | `downloadId` |
| `downloads.resume` | `downloadId` |
| `downloads.cancel` | `downloadId` |
| `downloads.clearFinished` | — |
| `resources.sources` | — |
| `resources.search` | `query` |
| `resources.files` | `query` |
| `resources.install` | `request` |
| `gameVersions.list` | — |
| `gameVersions.install` | `folderId`, `versionId` |
| `launcher.pages` | — |
| `launcher.navigate` | `pageId` |
| `launcher.snapshot` | — |
| `launcher.selectAccount` | `accountId` |
| `launcher.selectInstance` | `instanceId` |
| `launcher.launch` | — |
| `launcher.cancelLaunch` | — |
| `window.snapshot` | — |
| `window.update` | `patch` |
| `window.setState` | `action` |
| `appearance.snapshot` | — |
| `settings.update` | `patch` |
| `worlds.list` | — |
| `worlds.get` | `path` |
| `worlds.schema` | — |
| `worlds.copy` | `destinationPath`, `edit`, `path` |
| `worlds.update` | `edit`, `path` |
| `preview.registerCard` | `accent`, `description`, `destination`, `id`, `title` |
| `preview.updateCard` | `accent`, `description`, `id`, `title` |
| `preview.setAppearance` | `accent`, `compact`, `title` |
| `permissions.snapshot` | — |
| `accounts.list` | — |
| `accounts.selected` | — |
| `instances.list` | `query` |
| `instances.selected` | — |
| `instances.details` | `instanceId` |
| `mods.list` | `instanceId` |
| `downloads.list` | — |
| `settings.snapshot` | — |
| `environment.snapshot` | — |
| `config.get` | — |
| `config.set` | `key`, `value` |
| `config.remove` | `key` |
| `log` | `message` |
| `network.getText` | `url` |
| `registrations.remove` | `registrationId` |
| `events.subscribe` | `event` |
| `events.interceptLaunch` | `callbackId` |
| `events.interceptDownload` | `callbackId` |
| `events.interceptInstanceCreation` | `callbackId` |
| `messages.peers` | — |
| `messages.send` | `channel`, `payload`, `targetPluginId` |
| `messages.subscribe` | `channel` |
| `ui.showMessage` | `message`, `title` |
| `ui.confirm` | `message`, `title` |
| `ui.dialogTarget` | `id` |
| `ui.showDialog` | `id`, `parameter` |
| `ui.subscribeDialogs` | — |
| `ui.registerDialog` | `content`, `dismissOnBackgroundClick`, `dismissOnEscape`, `id`, `title`, `width` |
| `ui.registerButton` | `callbackId`, `description`, `id`, `order`, `target`, `text` |
| `ui.registerPanel` | `content`, `id`, `order`, `target` |
| `ui.registerBackground` | — |
| `ui.registerInstanceAction` | `callbackId`, `id`, `title` |
| `ui.registerSetting` | — |
| `ui.registerOption` | `choices`, `configKey`, `defaultValue`, `description`, `id`, `kind`, `maximum`, `minimum`, `order`, `target`, `title` |
| `ui.importBackgroundImage` | — |
| `ui.registerPage` | `content`, `id`, `languages`, `showInSimplifiedMode`, `title` |
| `providers.register` | `callbacks`, `definition`, `kind` |
| `workspace.writeVersionJson` | `json` |
| `workspace.downloadFile` | `relativePath`, `sha1`, `url` |
| `workspace.runJavaInstaller` | `arguments`, `relativeJar` |
| `auth.authorize` | `brokerId`, `request` |

| 协议接口 | 类型 / 格式 |
| --- | --- |
| 帧 | `uint32 little-endian length` + UTF-8 JSON；1–1048576 字节 |
| 请求 | `protocol:1, kind:"request", id, method, params` |
| 成功响应 | `protocol:1, kind:"response", id, result` |
| 失败响应 | `protocol:1, kind:"response", id, error:{code,message}` |
| 事件通知 | `protocol:1, kind:"event", subscriptionId, event, payload` |
| `callbacks.invoke` | 宿主 → EXE；`callbackId, payload` |
| `XCL2_PLUGIN_PIPE` | 命名管道名称 |
| `XCL2_PLUGIN_SESSION_TOKEN` | `hello.params.token` |
| `XCL2_PLUGIN_PROTOCOL_VERSION` | 协议版本 |
| `XCL2_PLUGIN_DATA_DIR` | 插件专属数据目录 |

```json
{"protocol":1,"kind":"request","id":"search-1","method":"workbench.projects.searchText","params":{"projectId":"选择目录返回的 Id","query":"class MainWindow","glob":"*.cs","limit":100}}
```

## 命令行 API

| 参数 | 行为 |
| --- | --- |
| `-help` | 显示本帮助并退出 |
| `-r --账户名 <账户名> --实例名称 <实例名>` | 使用指定账户与实例直接启动游戏 |
| `--result-file <路径>` | 将启动操作的实际进程结果写入 JSON |
| `--list-instances / --list-accounts` | 查询实例 / 账户，不输出凭据；可配合 --result-file |
| `-gui <页面名>` | 启动后跳转到对应页面，例如：-gui 设置 / -gui 下载中心 / -gui 百宝箱 |
| `--d --版本 <版本号> --加载器 <加载器>` | 打开下载中心；不填 --版本 时会直接弹出下载页面供手动选择， |
| `-l` | 以访客模式启动本次会话（会要求重新阅读并同意协议） |
| `--skip-update-check` | 本次启动跳过自动检查更新 |
| `--check-update-only` | 只检查一次更新，不进入主界面 |
| `--offline` | 强制以离线模式启动 |
| `--portable` | 强制使用便携模式（配置存在启动器目录） |
| `--no-tray` | 不创建托盘图标 |
| `--minimized` | 启动后直接最小化 |
| `--maximized` | 启动后直接最大化 |
| `--reset-window` | 重置窗口位置/大小为默认值 |
| `--width <px> --height <px>` | 指定启动窗口大小 |
| `--lang <代码>` | 本次强制界面语言，例如 zh-CN / en-US |
| `--theme <light\|dark>` | 本次强制主题 |
| `--zoom <百分比>` | 本次强制界面缩放，例如 125 |
| `--no-animation` | 关闭界面动效 |
| `--safe-mode` | 安全模式启动，跳过公告/AI 悬浮球等增强功能 |
| `--debug-console` | 额外弹出调试控制台窗口 |
| `--debug -yrj` | 本次会话无视日期开启愚人节彩蛋（仍尊重 noyrj 禁用） |
| `--verbose` | 调试控制台输出更详细日志 |
| `--config-path <路径>` | 使用指定配置文件路径 |
| `--instance-dir <路径>` | 使用指定实例（.minecraft）根目录 |
| `--java <路径>` | 启动游戏强制使用指定 Java 路径 |
| `--min-memory <MB> --max-memory <MB>` | 启动游戏强制使用的 JVM 内存参数 |
| `--jvm-args <参数>` | 启动游戏额外追加的 JVM 参数 |
| `--game-args <参数>` | 启动游戏额外追加的游戏参数 |
| `--server <ip:port>` | 配合 -r 使用，启动后自动直连该服务器 |
| `--fullscreen` | 配合 -r 使用，游戏以全屏启动 |
| `--clear-cache` | 启动前先清理下载缓存/临时文件 |
| `--export-logs <路径>` | 启动后自动打包导出日志/崩溃报告到指定路径 |
| `--no-single-instance` | 跳过单实例检测，允许多开 |
| `--no-gpu-accel` | 关闭窗口硬件加速渲染 |

## AI HTTP API

| 接口 | 请求 / 响应字段 |
| --- | --- |
| `GET <BaseUrl>/models` | `Authorization: Bearer …`；响应 `data[].id` |
| `POST <BaseUrl>/chat/completions` | `model, messages, stream, stream_options, temperature, max_tokens, reasoning_effort, tools` |
| 非流式响应 | `choices[].message.content`；可选 `usage.prompt_tokens, completion_tokens, total_tokens` |
| 流式响应 | SSE `data:` JSON，`choices[].delta.content`，可选 reasoning/usage；`[DONE]` |
| `messages[]` | `role: system/user/assistant`, `content: string` |
| `stream_options` | `include_usage: true` |

## AI 文件与任务工具 API

| action | 参数 | 模式 |
| --- | --- | --- |
| `read_log` | `—` | Plan / Build |
| `read_config` | `key` | Plan / Build |
| `apply_setting` | `key, value` | Build |
| `request_directory` | `path, content` | Build |
| `list_workspace` | `path` | Plan / Build |
| `read_workspace_file` | `path` | Plan / Build |
| `write_workspace_file` | `path, content, sha256?` | Build |
| `delete_workspace_file` | `path` | Build |
| `run_command` | `command, shell, timeout_seconds` | Build |
| `registry_read` | `path, key` | Plan / Build |
| `registry_write` | `path, key, value, type` | Build |
| `update_task` | `steps[{id,title,status,evidence}], next_action, notes` | Plan / Build |
| `list_project_files` | `path, glob, limit` | Plan / Build |
| `search_project` | `path, query, glob, regex, case_sensitive, limit` | Plan / Build |
| `read_file_range` | `path, start_line, line_count` | Plan / Build |
| `patch_workspace_file` | `path, sha256, old_text, new_text` | Build |

```text
<<XCL2_ACCESS:{"action":"read_file_range","path":"src/MainWindow.cs","start_line":201,"line_count":200}>>
```

## 工作台 API 数据约束

| 接口 | 参数 / 返回约束 |
| --- | --- |
| `ctx.Projects.ChooseFolderAsync` | 用户选择目录后返回项目 ID；重启或撤销 projects.read 后重新选择 |
| `ctx.Projects.GetFiles / GetFileCount / GetTotalBytes / GetExtensionStats / GetLargestFiles` | 文件清单最多 20000 项；统计针对该有界清单；忽略 .git/node_modules/bin/obj/dist/build/.xcl2 和目录链接 |
| `ProjectFiles.Search` | 最多 20000 文件 / 128 MiB 扫描量；`Truncated` 指示结果或扫描上限；每个正则匹配 100 ms 超时 |
| `ProjectFiles.ReadPage` | 文本文件 ≤16 MiB，1–2000 行，输出 ≤80000 字符；`NextLine` 为续读行号，`Sha256` 为整文件版本 |
| `ctx.Changes.Prepare*` | 只保存预览与完整备份；`ApplyAsync` 显示预览后提交；`RollbackAsync` 校验当前内容并确认回滚 |
| `FileChange.State` | Prepared / Applying / Applied / RollingBack / RolledBack |
| `WorkStep.Status` | pending / in_progress / completed / blocked；最多 100 步，同时最多一个 in_progress |
| `WorkTask.Mode` | Plan / Build；任务元数据，不改变宿主授权或 AI 工作模式 |
| `WorkTask.Status` | paused / running / completed / interrupted / archived |
| `WorkTask.Revision` | 乐观并发版本；旧版本保存会拒绝覆盖 |

```csharp
var project = await ctx.Projects.ChooseFolderAsync(ctx.Stopping);
if (project is not null)
{
    var page = ctx.Projects.ReadRange(project.Id, "src/MainWindow.cs", 1, 120);
    var draft = ctx.Changes.PreparePatch(project.Id, "src/MainWindow.cs",
        page.Sha256, "old unique fragment", "new fragment");
    await ctx.Changes.ApplyAsync(project.Id, draft.Id, ctx.Stopping);
    var task = ctx.Tasks.Create("更新界面", "实现新布局", project.Root);
    ctx.Tasks.AddChange(task.Id, draft.Id);
    ctx.Tasks.Checkpoint(task.Id, "已提交变更，等待编译验证");
}
```
