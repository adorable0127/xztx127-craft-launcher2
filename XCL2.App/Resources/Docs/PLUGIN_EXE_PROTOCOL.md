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
