# XCL2 编译成单文件 exe 指南

当前 `XCL2.App.csproj` 是**框架依赖发布**（不含运行时、不是单文件），这份指南教你怎么改成
单文件发布，含两种口味：**不含 .NET 运行时**（体积小，用户电脑要自己装好 .NET 8 Desktop Runtime）
和**内含 .NET 运行时**（体积大几十 MB，用户电脑什么都不用装，双击就能跑）。

两种方式都不需要改 `.csproj` 文件本身——直接在 `dotnet publish` 命令行上加参数就行，
`.csproj` 里已有的这些配置（`RollForward`、`ApplicationIcon` 等）不会跟单文件发布冲突。

---

## 方式一：不含 .NET 运行时（体积小，推荐给会正常联网/装环境的用户）

用户电脑必须已经装了 **.NET 8 Desktop Runtime**（装 Visual Studio 时通常顺带装了；纯用户机器
大概率没装，需要引导他们去 <https://dotnet.microsoft.com/download/dotnet/8.0> 下载
"Desktop Runtime" 那个安装包，不是 SDK）。

在项目根目录（也就是 `XCL2.App.csproj` 所在的那层文件夹）打开终端，运行：

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

参数说明：
- `-c Release`：编译发布版（不是 Debug）。
- `-r win-x64`：目标平台 64 位 Windows。绝大多数电脑用这个；如果要支持老式 32 位系统，
  把 `win-x64` 换成 `win-x86`（但这个项目用了 WebView2，32 位支持没 64 位稳，不建议）。
- `--self-contained false`：**不**把 .NET 运行时打进 exe，这是"不含 .NET"的关键开关。
- `-p:PublishSingleFile=true`：合并成单个 exe 文件（而不是一堆 dll 散落一地）。
- `-p:IncludeNativeLibrariesForSelfExtract=true`：把少数没法真正合并进单文件的原生 dll
  （比如 WebView2 相关的）也塞进这个 exe，运行时自解压到临时目录，避免 exe 旁边还要
  另外带几个 dll 文件。

**产出目录：**

```
XCL2.App\bin\Release\net8.0-windows\win-x64\publish\
```

这个文件夹里的 **`XCL2.exe`** 就是最终产物，体积大概几 MB 到十几 MB（具体看代码量）。
分发时把这一个 `XCL2.exe` 文件发给用户就够了。

---

## 方式二：内含 .NET 运行时（体积大，用户电脑不用装任何东西）

命令几乎一样，只改一个参数：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

唯一区别：`--self-contained false` 换成 `--self-contained true`——把整个 .NET 8 运行时
一起打进 exe 里。

**产出目录：**

```
XCL2.App\bin\Release\net8.0-windows\win-x64\publish\
```

同一个目录（跟方式一一样，只是里面的 `XCL2.exe` 内容不同），`XCL2.exe` 体积会明显更大，
一般 60~150 MB 左右（取决于用没用裁剪，见下面"可选"部分）。用户电脑上完全不需要装
.NET 运行时，双击就能跑。

---

## 可选：裁剪体积（仅对"内含运行时"版本有意义）

如果方式二的体积你觉得太大，可以加一个裁剪参数，把用不到的 .NET 库砍掉：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=true
```

**注意**：WPF 项目用 `PublishTrimmed` 有一定风险——裁剪器可能会误删反射用到的类型，
导致运行时报"找不到类型"之类的错，尤其是这个项目里 `System.Text.Json`（反序列化用了不少
POCO 类）和 WebView2 这类依赖反射/动态加载的库，裁剪后建议**把主要功能都点一遍测试**
（登录、下载、启动游戏、开服）确认没有裁掉不该裁的东西，出问题概率不算低，不追求极致
体积的话可以跳过这一步。

---

## 两个坑，提前说明

1. **`bin` 目录下会有旧文件残留**：如果你之前普通编译过（F5 调试、或者没加参数的
   `dotnet build`），`bin\Release\net8.0-windows\` 下可能已经有一堆散落的 dll。
   `publish` 只会往 `win-x64\publish\` 这个子目录写新文件，不会污染上一层，但如果想要
   干净的产出，建议先删掉整个 `bin` 文件夹再执行 `dotnet publish`：

   ```powershell
   Remove-Item -Recurse -Force bin, obj
   dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
   ```

2. **`Resources\Terracotta\terracotta-0.4.2-windows-x86_64.exe` 是内嵌资源**（打进程序集
   内部的 `EmbeddedResource`），不是松散文件，两种发布方式都不需要单独处理它，会自动一起
   打进最终的 `XCL2.exe` 里。

---

## 命令速查表

| 场景 | 命令末尾追加的参数 |
|---|---|
| 不含 .NET，单文件 | `--self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` |
| 含 .NET，单文件 | `--self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` |
| 含 .NET，单文件 + 裁剪体积 | 在上一行基础上加 `-p:PublishTrimmed=true`（有风险，需测试） |

产出目录固定都是：

```
XCL2.App\bin\Release\net8.0-windows\win-x64\publish\XCL2.exe
```
