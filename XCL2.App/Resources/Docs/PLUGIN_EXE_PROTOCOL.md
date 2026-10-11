# XCL2 EXE 插件协议（Protocol 1）

本协议用于需要独立进程隔离的插件。EXE 不加载到 XCL2 进程内，但带 `plugin.json`、完成用户授权后，可以通过当前会话的命名管道访问受权限控制的宿主能力。

## 1. 启动环境

宿主使用 `UseShellExecute=false` 启动插件，不附加命令行参数，并设置：

- `XCL2_PLUGIN_DATA_DIR`：该插件实例专属数据目录。
- `XCL2_PLUGIN_PIPE`：本次启动专属命名管道名称。
- `XCL2_PLUGIN_SESSION_TOKEN`：256-bit 随机会话令牌。
- `XCL2_PLUGIN_PROTOCOL_VERSION`：当前为 `1`。

管道只允许当前 Windows 用户连接；握手后宿主还会核对实际客户端 PID 是否等于它刚启动的 EXE PID。令牌只对本次进程有效，重载插件后旧令牌和旧管道立即失效。

没有 `plugin.json` 的旧 EXE 仍可按兼容模式作为独立进程运行，但不会获得新增业务权限，因此不能把“进程已启动”理解成“已拥有完整插件 API”。

## 2. 帧格式与握手

每帧为：`4 字节 little-endian 长度` + `UTF-8 JSON`。单帧上限 1 MiB，JSON 最大深度 32。

首帧必须为请求：

```json
{"protocol":1,"kind":"request","id":"hello-1","method":"hello","params":{"token":"<XCL2_PLUGIN_SESSION_TOKEN>"}}
```

成功响应包含 `apiVersion` 和本次实际 `pluginId`。后续消息支持 `request`、`response`、`event` 三类；插件发出的并发宿主请求最多 8 个。

宿主需要调用 EXE 回调时，会向插件发送 `method = "callbacks.invoke"` 的 request，`params` 包含 `callbackId` 和 `payload`。插件必须用相同 `id` 返回 response。

## 3. 宿主请求方法

以下方法均受 `plugin.json` 权限和运行期撤权控制。注册类方法返回 `registrationId`，可用 `registrations.remove` 提前注销；插件停止/重载时宿主也会自动清理。

### 读取与配置

- `permissions.snapshot`
- `accounts.list` / `accounts.selected`
- `instances.list` / `instances.selected` / `instances.details`
- `mods.list`
- `downloads.list`
- `settings.snapshot`
- `environment.snapshot`
- `config.get` / `config.set` / `config.remove`
- `log`
- `network.getText`

### 事件、干预与消息

- `events.subscribe`：按通知类型名订阅；事件帧中的 `subscriptionId` 等于返回的注册 ID。
- `events.interceptLaunch`
- `events.interceptDownload`
- `events.interceptInstanceCreation`
- `messages.peers`
- `messages.send`
- `messages.subscribe`：每次订阅有独立 `subscriptionId`，不会把多个通道混成固定 ID。

### UI

- `ui.showMessage` / `ui.confirm`
- `ui.dialogTarget`
- `ui.registerDialog` / `ui.showDialog` / `ui.subscribeDialogs`
- `ui.registerButton`
- `ui.registerOption`
- `ui.registerPanel`
- `ui.registerSetting`
- `ui.registerPage`
- `ui.registerInstanceAction`
- `ui.registerBackground` / `ui.importBackgroundImage`

EXE 的 Dialog、Panel、Page 使用声明式 `content` 数组，不传 WPF 对象。当前节点类型：

```json
[
  {"type":"text","text":"说明文字"},
  {"type":"button","text":"执行","callbackId":"callback-1"}
]
```

`ui.registerButton` 本身使用 `callbackId`；回调 payload 包含 `target`。`ui.subscribeDialogs` 产生 `PluginDialogEvent` 事件，能观察由 Overlay 宿主打开/关闭的弹窗。

### Provider 与安装工作区

- `providers.register`：`download` / `mod` / `loader` / `login`
- `workspace.writeVersionJson`
- `workspace.downloadFile`
- `workspace.runJavaInstaller`
- `auth.authorize`

Provider 回调同样使用 `callbacks.invoke`。Loader 工作区和登录 broker 的 ID 只在对应回调期间有效，不能缓存到下一次操作。

## 4. 不能跨进程等价映射的能力

以下 DLL API 依赖真实 WPF/渲染对象或同步进程内委托，因此 Protocol 1 不伪造等价对象：

- `PluginComponentService.Register` 的任意 `FrameworkElement` 工厂；
- `RegisterAvatarRenderer` / `RegisterSkinModelRenderer` 的进程内渲染对象；
- 旧版 `RegisterLaunchMiddleware` 的同步 `next()` 内核替换。

EXE 对启动修改应使用 `events.interceptLaunch`；UI 应使用声明式 Page/Dialog/Panel/Button/Option/Setting。需要任意 WPF 控件树或原生实时渲染后端时使用 DLL 插件。

## 5. 生命周期与错误

重新扫描、禁用、卸载或启动器退出时，宿主会撤销注册、关闭会话并结束仍由宿主管理的 EXE 进程树。EXE 自己退出或管道断开后，插件管理页会显示断开状态；重新加载可重新建立会话。

错误 response 使用结构化 `error.code` / `error.message`。权限撤销、回调超时、无效资源、未知方法等都不应通过猜测宿主内部文件来绕过；插件应回退到自身可用功能或提示用户重新授权。

## API 2.0 新方法（传输协议仍为 protocol = 1）

下列方法沿用现有请求 / 响应和注册句柄生命周期。

| method | params | result |
| --- | --- | --- |
| `preview.registerCard` | `{ "id":"hello", "title":"欢迎", "description":"扩展内容", "destination":"worlds", "accent":"#1BD96A" }` | `{ "registrationId":"..." }` |
| `preview.updateCard` | `{ "id":"hello", "title":"新标题", "description":"新内容", "accent":"#1BD96A" }` | true |
| `preview.setAppearance` | `{ "title":"我的 XCL2", "accent":"#1BD96A", "compact":false }` | `{ "registrationId":"..." }` |
| `worlds.list` | `{}` | WorldArchive 数组 |
| `worlds.update` | `{ "path":"列表返回的存档路径", "edit":{ "name":"新世界", "gameMode":0, "difficulty":2, "allowCommands":false, "hardcore":false } }` | level.dat 备份路径 |

preview 方法需要 `ui.preview`；worlds 方法分别需要 `worlds.read` / `worlds.write`。对应总开关和每插件授权同时生效；存档修改还需要用户逐次确认。预览接口只提交经过校验的文字、颜色与导航目标，不执行任意 XAML 或脚本。


## API 2.1 新方法（protocol 仍为 1）

| method | params | 权限 | 返回 |
| --- | --- | --- | --- |
| launcher.pages | {} | ui.navigation | 页面数组 |
| launcher.navigate | {pageId} | ui.navigation | 导航是否开始 |
| launcher.snapshot | {} | launcher.control | 控制状态快照 |
| launcher.selectAccount | {accountId} | launcher.control | true |
| launcher.selectInstance | {instanceId} | launcher.control | true |
| launcher.launch | {} | launcher.control | {accepted,code} |
| launcher.cancelLaunch | {} | launcher.control | 是否取消了启动准备 |
| window.snapshot | {} | ui.window | 窗口快照 |
| window.update | {patch} | ui.window | 更新后的窗口快照 |
| window.setState | {action} | ui.window | 更新后的窗口快照 |
| appearance.snapshot | {} | settings.read | 外观和可选皮肤 |
| settings.update | {patch} | settings.write | 更新后的外观快照 |
| worlds.get | {path} | worlds.read | WorldArchive |
| worlds.schema | {} | worlds.read | 可编辑字段数组 |
| worlds.copy | {path,destinationPath,edit} | worlds.write | 新副本路径 |

JSON 字段为 camelCase。window.setState 的 action 为 Activate、Minimize、Maximize、Restore。patch 中 null / 缺省字段保留原值，范围和枚举按 API_LAUNCHER_CONTROL.md 校验。worlds.update 自动支持 WorldEdit 新增的可选属性，见 API_WORLDS.md。

用户取消业务确认会返回 `error.code = "Canceled"`，不会让请求永久挂起。已停止的会话会关闭管道。可把宿主回调等待设到最多 3600 秒，但仍必须并行读取宿主消息并响应取消；连接握手仍限制 30 秒。


## API 2.2 新方法（protocol 仍为 1）

| method | params | 权限 | 返回 |
| --- | --- | --- | --- |
| folders.list | {} | instances.read | PluginGameFolder 数组 |
| instances.directories | {instanceId} | instances.read | PluginInstanceDirectories |
| instances.setHidden | {instanceId,hidden} | instances.write | bool：隐藏状态 |
| instances.rename | {instanceId,newName} | instances.write | 改名后的新实例 Id |
| instances.setIsolation | {instanceId,isolated} | instances.write | bool：生效的隔离状态；isolated 可为 null |
| mods.setEnabled | {instanceId,fileName,enabled} | mods.write | PluginMod |
| java.list | {} | java.read | PluginJavaRuntime 数组 |
| java.select | {javaId,instanceId} | java.select | bool；两个 Id 均可为 null |
| downloads.get | {downloadId} | downloads.read | PluginDownloadDetails |
| downloads.pause | {downloadId} | downloads.control | PluginDownloadDetails |
| downloads.resume | {downloadId} | downloads.control | PluginDownloadDetails |
| downloads.cancel | {downloadId} | downloads.control | bool |
| downloads.clearFinished | {} | downloads.control | 已清理条目数 |
| instances.repair | {instanceId} | instances.write + resources.install | PluginDownloadAcceptance |
| resources.sources | {} | resources.read | PluginResourceSource 数组 |
| resources.search | {query} | resources.read | PluginResourceSearchResult |
| resources.files | {query} | resources.read | PluginResourceFile 数组 |
| resources.install | {request} | resources.install | PluginDownloadAcceptance |
| gameVersions.list | {} | resources.read | PluginGameVersion 数组 |
| gameVersions.install | {folderId,versionId} | instances.write + resources.install | PluginDownloadAcceptance |

DTO 字段和完整帧示例见 `API_LAUNCHER_COMPOSITION.md`。下载 ID 是 Guid 字符串。query.kind 使用 `Mod`、`ResourcePack` 或 `Shader`；搜索带 sourceId、query、可选 gameVersion/loader、offset 和 limit，文件查询带 sourceId、kind、projectId、可选 gameVersion/loader。install 的 request 为 token、instanceId、replaceExisting（默认 false）。

不要提交自造路径、下载 URL 或跨会话文件令牌。查询得到的文件 token 仅在当前插件上下文中限时使用。接受排队结果不表示安装完成；沿用 downloads.list/get 与下载通知跟踪任务。取消、撤权和插件停止会中止进行中的资源操作。
