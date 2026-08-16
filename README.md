# XCL2

## 运行环境要求

本程序基于 .NET 8 (net8.0-windows) 构建，属于**依赖框架发布(framework-dependent)**，
运行前需要电脑上已经安装 **.NET 8 桌面运行时(.NET Desktop Runtime 8)**。

> 你需要安装 .NET8 运行时才可以继续使用本程序。

- 下载地址：https://dotnet.microsoft.com/download/dotnet/8.0 （选择 "Desktop Runtime" 对应你的系统架构，x64 或 arm64）
- 如果电脑上没装 .NET 8 运行时，双击本程序的 .exe 时**根本不会进入程序本身的任何逻辑**，
  会直接弹出 Windows 系统自带的"缺少运行时"提示框（不是本程序自己弹的窗口），
  按提示跳转到微软官网下载安装即可，装完不需要重启电脑，重新打开程序就行。

### 内嵌登录额外需要 WebView2 运行时

「设置」/「登录」页面里的**内嵌登录**(免复制验证码、直接在程序内输入账号密码)功能
额外依赖 **WebView2 Runtime**：

- 大多数 Windows 10 1803+ / Windows 11 电脑上，系统自带的 Edge 浏览器已经预装了这个运行时，无需额外安装。
- 如果点击「内嵌登录」时提示"未检测到 WebView2 运行时"，可以按提示前往
  https://developer.microsoft.com/microsoft-edge/webview2/ 下载安装，或者改用
  「浏览器登录」按钮（效果一样，只是登录过程发生在系统默认浏览器里，不需要 WebView2）。
- WebView2 组件只有在真正点击「内嵌登录」时才会被加载，不会拖慢程序启动或占用主界面的资源。

## 已知限制

- 启动 1.16 及更早版本的 Minecraft 时，程序会自动匹配到 Java 8（Mojang 官方对这些
  版本的最低 Java 版本要求），无需手动指定；如果本机没有 Java 8，会提示自动下载便携版。
