# 轨道加载图标

- `orbit-loading.gif`：透明背景、128 × 128、60 帧、20 ms/帧、1.2 秒循环的蓝紫轨道图标。
- `orbit-loading.png`：第一帧透明 PNG，适合文档或静态加载展示。
- `orbit-loading-contact-sheet.png`：实际解码 GIF 的六个时刻，分别在浅色和深色背景展示。
- `generate_loading.py`：从矢量几何生成以上资源的可复现脚本，仅生成资源时需要 Pillow。

启动器内使用同设计的 `Views/Controls/LoadingIndicator.cs` 原生 WPF 图标：被冻结的共享几何只创建一次，旋转交给 WPF 合成器，不重复解码 GIF，也没有逐帧 UI 定时器或布局变更。原生图标可在高 DPI 下保持清晰，半透明线条也不受 GIF 仅支持一位透明度的限制。

加载速度和强度遵守统一 `AnimationKind.Loading` 设置；默认 75% 时一圈 1.2 秒，100% 时一圈 0.9 秒。完全无动画模式或关闭加载动画时仍显示静态图标及“正在加载”。隐藏、卸载或窗口最小化后会移除动画时钟，重新显示时从暂停角度继续。

主页面、二级页面的完整占位可调用：

```csharp
UIElement placeholder = LoadingPlaceholderFactory.Create();
```

已有页面的局部加载区可直接使用：

```xml
xmlns:controls="clr-namespace:XCL2.App.Views.Controls"

<controls:LoadingIndicator Width="48" Height="48" />
```

上面的图标控件不包含文字，局部加载区需要自行保留“正在加载”的 `TextBlock`。
