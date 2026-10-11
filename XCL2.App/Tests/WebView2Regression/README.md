# WebView2 缺依赖回归

在 Windows、.NET 8 SDK 上从源码根目录运行：

```powershell
dotnet run --project .\Tests\WebView2Regression\WebView2Regression.csproj -c Release
```

测试会在临时目录中复制自身输出，在三个全新子进程中分别保留完整依赖、移除 `Microsoft.Web.WebView2.Wpf.dll`、移除 `Microsoft.Web.WebView2.Core.dll`。每种情形均创建真实种子查询和 Web2 XAML 页面并反复卸载；缺文件时检查内嵌按钮被禁用、清晰说明和一次性气泡，注入浏览器替身确认释放仅执行一次。测试不加载任何网页，不修改启动器安装目录，不要求安装 WebView2 Runtime。

还需进行 Windows 人工验收：

1. `dotnet build .\XCL2.App.csproj -c Release`，确认普通构建通过 WebView2 文件验证。
2. `dotnet publish .\XCL2.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`，确认单文件主程序旁仍有 WebView2 Core/Wpf DLL 和对应架构 Loader，发布验证通过。WinUI 预览仍位于完整的 `WinUI.Preview` 目录。
3. 在完整发布目录中打开种子地图，确认地图可加载；在首次加载过程中迅速切换页面，确认无卸载错误，重新进入后可再次打开。
4. 在复制出的测试发布目录中移走 Wpf DLL，重新启动；仅进入并离开百宝箱不应报错，主动打开地图时应提示重新解压完整发布包，“用浏览器打开”仍有效。恢复文件后重启可再次内嵌打开。
5. 在未安装 WebView2 Runtime 的测试机上保留完整程序文件，提示应明确为 Runtime 不可用，不应误称程序 DLL 丢失。

根因：页面字段和 `Unloaded` 直接引用 SDK 类型时，JIT 可能在执行方法体之前加载失败，方法内 `try/catch` 来不及捕获。修复将 SDK 类型隔离到实现类及禁止内联的工厂方法；页面仅持有自有接口。构建和发布同时校验 SDK 托管 DLL 和 Loader，单文件发布明确旁置这些组件。

发布文件依据：[Microsoft WebView2 distribution — Files to ship with the app](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution#files-to-ship-with-the-app)。

当前交付环境为 Linux，.NET CoreCLR 无法启动，未执行以上 Windows 运行测试；已进行 C# 语法和项目 XML 检查，不能据此声称 Windows 编译或运行通过。
