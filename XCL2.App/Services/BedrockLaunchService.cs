using System.Diagnostics;

namespace XCL2.App.Services;

/// <summary>
/// 基岩版（Bedrock Edition）"轻量跳转"支持：只做"检测系统是否已安装 Minecraft for Windows，
/// 已安装就唤起它"这一件事，不下载、不管理版本、不接管任何 mod/Add-On——这些跟基岩版的引擎
/// （C++ 原生 + UWP/GDK 打包）、分发方式（Microsoft Store）、内容生态（Add-Ons，跟 Java 版
/// Forge/Fabric/NeoForge mod 完全不是一套体系）都跟这个启动器现有的 Java 版整套逻辑
/// （JavaService/LauncherService/ModrinthService 等）不兼容，这个类刻意保持"只跳转"这么小的范围。
///
/// 唤起方式：Windows 提供 shell:AppsFolder\&lt;PackageFamilyName&gt;!&lt;AppId&gt; 这个通用的
/// "按已注册的应用清单唤起"协议，只要这个应用包已经在系统里注册（不管它内部实际是旧版 UWP
/// 打包还是新版 GDK 打包，这一层协议不关心），就能用同一种方式唤起，不需要知道它具体安装
/// 在哪个磁盘路径、也不需要碰任何 Store 私有 API 或做任何逆向——这是官方支持、任何第三方
/// 程序都可以合法调用的唤起方式（跟"开始菜单"点击这个应用图标是同一条路径）。
///
/// 包信息来源：Minecraft for Windows（正式版）固定使用 PackageFamilyName
/// "Microsoft.MinecraftUWP_8wekyb3d8bbwe"，这是 Mojang/Microsoft 从最早的 UWP 版本沿用至今
/// 的包标识，即使内部安装目录/打包格式后续改成了 GDK（游戏文件挪到了
/// "&lt;安装盘&gt;\XboxGames\Minecraft for Windows\"），这个 PackageFamilyName 本身在系统里
/// 注册应用清单时保持不变，唤起协议依然有效。Preview 版是另一个独立包
/// （Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe），本类默认只处理正式版，不猜测/不兼顾
/// Preview，避免装了 Preview 没装正式版的用户被"检测到已安装"误导。
/// </summary>
/// <summary>
/// 关于"授权状态分流"的技术边界（写在最前面，避免后来者想当然地去做一个实际做不到的事）：
///
/// 本类唯一能合法查询的是"这个包有没有在系统里注册"（Get-AppxPackage），这跟"这个包
/// 当前有没有有效的购买/Trial 授权"是两回事——后者（正版 / 官方 Trial-Demo / 无授权）
/// 由 Windows Store 的许可证服务（clipsvc）维护，官方公开 API 里唯一能查询许可证状态的
/// 是 <c>Windows.Services.Store.StoreContext.GetAppLicenseAsync()</c>，而这个 API 只能查询
/// "调用方自己所在包" 的许可证——也就是说，只有 Minecraft for Windows 自己（它是被 MSIX/
/// UWP 打包、有自己包身份的应用）才能合法查到"我自己是正版/Trial/没授权"，一个独立的、
/// 未打包的第三方 Win32 启动器（本项目）没有任何官方支持的方式替它读这个状态——能读到的
/// 途径只剩 clipsvc 的本地许可证存储（未公开格式、需要逆向/绕过其访问控制），这正是
/// 用户明确要求不做的"不得伪造许可证、绕过 DRM"的范畴，所以本类不做，也不应该有人后续
/// 加上去。
///
/// 因此这里实际能做、且完全合规的"分流"是：把三态判断的活交给 Minecraft for Windows
/// 自己——它启动时会自己读取自己的许可证状态，该正常进正式版就进正式版、该进 Trial 就进
/// Trial、没有任何授权就自己弹登录/购买/试用引导，这本来就是官方客户端自带的行为，
/// 跟用户在开始菜单里点这个应用图标是完全一样的路径。本启动器负责的只是：
///   1) 已安装 → 唤起（Launch），让 Minecraft 自己决定进哪个状态；
///   2) 未安装 / 无法确认已装 → 引导去 Microsoft Store 商品页（OpenStorePage），
///      登录账户、领取官方 Trial、购买正版这三件事都是在那个官方页面里完成的，
///      本启动器不代为伪造、不代为绕过，只做"带路"。
/// </summary>
public static class BedrockLaunchService
{
    /// <summary>Minecraft for Windows（正式版）固定的 PackageFamilyName，Windows 应用商店包的
    /// 稳定标识符，不随版本号变化。</summary>
    public const string PackageFamilyName = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";

    /// <summary>Minecraft for Windows 在 Microsoft Store 里的商品 ID，固定不变。用于"未安装 /
    /// 无有效授权"时引导用户去官方页面登录账户、领取官方 Trial 或购买正版——这三件事全部由
    /// Store/Minecraft 官方页面完成，本类只负责跳转，不代为处理账户或许可证。</summary>
    public const string StoreProductId = "9NBLGGH2JHXJ";

    /// <summary>
    /// 基岩版官方系统要求：Windows 10 及以上（分发渠道 Microsoft Store 本身也要求 Win10+，
    /// Win7/Win8.1 没有 Store，且基岩版依赖的 UWP 应用包模型在 Win7/8 上不存在，
    /// 不是"技术上能不能凑合跑"的问题，是系统层面压根没有这套机制）。
    /// 用 Environment.OSVersion.Version.Major 判断：Win10/11 内核版本号都是 10.x，
    /// Win7 是 6.1，Win8/8.1 是 6.2/6.3——所以 Major >= 10 就是 Win10 及以上。
    /// </summary>
    public static bool IsOsSupported => Environment.OSVersion.Version.Major >= 10;

    /// <summary>系统不支持时给用户看的说明文案，UI 层直接复用，避免各处措辞不一致。</summary>
    public const string UnsupportedOsMessage =
        "基岩版（Minecraft for Windows）官方只支持 Windows 10 及以上系统。\n" +
        "当前系统版本更低，无法安装/运行基岩版，也没有 Microsoft Store 可用——这是游戏本身的" +
        "官方限制，不是本启动器能绕过的问题。\n\n" +
        "如果想在这台电脑上玩 Minecraft，建议使用 Java 版（对系统版本要求更宽松）。";

    /// <summary>
    /// 检测这个包是否已经安装在当前系统里。用 PowerShell 的 Get-AppxPackage 按
    /// PackageFamilyName 查询——这是 Windows 自带、面向普通用户/脚本开放的标准查询方式，
    /// 不需要管理员权限，也不需要读取受保护的 WindowsApps/XboxGames 目录（那些目录哪怕有
    /// 管理员权限，默认权限设置下也读不到内容，直接用文件是否存在来判断会不可靠）。
    ///
    /// 查询失败（比如 PowerShell 不可用、被组策略禁用等极端情况）时返回 false，调用方应该
    /// 把"未检测到已安装"和"检测本身失败"同等对待——都是"点击后应该提示用户自己去 Store
    /// 安装"，不需要对用户区分这两种内部原因。
    /// </summary>
    public static async Task<bool> IsInstalledAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"(Get-AppxPackage -Name '{PackageFamilyName.Split('_')[0]}').Count\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            return int.TryParse(output.Trim(), out var count) && count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 唤起已安装的 Minecraft for Windows。调用方应该先调用 IsInstalledAsync 确认已安装，
    /// 这里不重复检测——跟项目里其他"前置条件由外层页面负责校验"的约定一致（比如
    /// ExperimentalFeaturesWindow 不重复校验 token 解锁状态）。
    ///
    /// 修复"启动基岩版时会错误地唤起文件夹"：原来这里借道 explorer.exe 去解析
    /// shell:AppsFolder\...!App 这个虚拟路径——问题在于，一旦这个包身份解析失败
    /// （比如系统里实际注册的包，其 PackageFamilyName 跟这里写死的正式版 PFN 不一致，
    /// 侧载/开发者模式注册的包就可能出现这种情况），explorer.exe 并不会像正常的
    /// Win32 API 调用那样抛异常，而是把这个解析不了的参数当成普通路径处理，
    /// 静默地打开一个新的资源管理器窗口（默认目录，比如"此电脑"）——这正是用户看到
    /// "点启动基岩版结果弹出一个文件夹"的原因，而且因为没有异常抛出，catch 块也接不到，
    /// 用户体验上就是"莫名其妙弹了个文件夹，游戏没启动，也没有任何报错"。
    ///
    /// 改成直接把 shell:AppsFolder\...!App 作为 ProcessStartInfo.FileName、
    /// UseShellExecute=true 直接调用（不再经过 explorer.exe 这层转发）：这是
    /// ShellExecuteEx 原生支持的用法，解析失败时会按标准 Win32 规则抛出
    /// Win32Exception（通常是 ERROR_FILE_NOT_FOUND / ERROR_NO_ASSOCIATION），
    /// 调用方 catch 得到，能给用户一个清楚的"唤起失败"提示，而不是无声无息地弹出一个
    /// 完全不相关的文件夹。"!App" 是 Minecraft for Windows 清单里的默认 Application Id。
    /// </summary>
    public static void Launch()
    {
        // Process.Start 的返回值这里不需要长期持有（前面类注释已经说明：这条路径唤起的是
        // 系统另起的独立应用，跟这个返回的 Process 对象没有稳定的对应关系，没法也不该拿它
        // 做后续监控），但仍然要 Dispose——不然每点一次「启动基岩版」就泄漏一个内核句柄。
        //
        // 修复"点击后无论如何都会显示启动失败，哪怕基岩版其实已经正常打开了"：
        // 之前这里用 using 包裹 Process.Start 的返回值，把"启动"和"释放句柄"绑在同一个
        // try 域里。但 shell:AppsFolder 唤起的是 UWP/Store 包，ShellExecuteEx 对这类目标
        // 返回的并不是常规子进程那种可查询的进程句柄，而是一个伪句柄——ShellExecuteEx 本身
        // 早已成功把应用激活起来，但 .NET 在随后构造/释放这个 Process 包装对象时，在不少
        // 系统上会对着这个伪句柄抛出 Win32Exception（"句柄无效"一类），而这个异常发生在
        // 唤起动作成功之后，跟"唤起有没有成功"完全无关。原来的 using 会让这个纯粹是句柄
        // 清理层面的噪音异常，被调用方 catch 块误判成"唤起基岩版失败"——所以不管基岩版
        // 有没有真的打开，只要走到这条路径，几乎必然弹出失败提示。
        //
        // 现在把"启动"（Process.Start 本身）和"释放句柄"（Dispose）拆成两段：真正的
        // 启动失败（包没注册、shell 协议解析不了）仍然发生在 Process.Start 这一步，异常
        // 照常向外抛出、照常能被调用方捕获提示；只有启动已经成功之后、单纯清理伪句柄这一步
        // 才可能出的异常，这里原地吞掉，不让它冒充"启动失败"。
        var proc = Process.Start(new ProcessStartInfo($"shell:AppsFolder\\{PackageFamilyName}!App")
        {
            UseShellExecute = true
        });
        try
        {
            proc?.Dispose();
        }
        catch
        {
            // 见上：唤起本身已经成功，这里只是伪句柄清理噪音，忽略即可。
        }
    }

    /// <summary>运行中的 Minecraft for Windows 进程快照。ExecutablePath 在 UWP/受保护进程上可能读不到，
    /// 这属于正常情况；ProcessId 始终可用于“关闭选中实例”。</summary>
    public sealed record RunningBedrockProcess(int ProcessId, string ProcessName, string WindowTitle, string? ExecutablePath);

    /// <summary>
    /// 枚举当前用户可见的基岩版客户端进程。这里只匹配 Minecraft.Windows*，避免把 Java 版
    /// java/javaw 或 Minecraft Launcher 一起误关掉。新版/Preview/侧载包最终实际游戏进程都使用
    /// Minecraft.Windows 这一命名族；无法读取 MainModule 路径时仍保留 PID 供用户手动选择关闭。
    /// </summary>
    public static IReadOnlyList<RunningBedrockProcess> GetRunningClientProcesses()
    {
        var result = new List<RunningBedrockProcess>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                var name = proc.ProcessName ?? "";
                if (!name.StartsWith("Minecraft.Windows", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? path = null;
                try { path = proc.MainModule?.FileName; }
                catch { /* Store/UWP 或权限不足时拿不到路径，PID 仍然有效 */ }

                string title;
                try { title = proc.MainWindowTitle ?? ""; }
                catch { title = ""; }

                result.Add(new RunningBedrockProcess(proc.Id, name, title, path));
            }
            catch
            {
                // 进程可能在枚举过程中刚好退出，忽略这一条即可。
            }
            finally
            {
                proc.Dispose();
            }
        }

        return result.OrderBy(p => p.ProcessId).ToList();
    }

    /// <summary>等待基岩版进程出现。用于 shell/UWP 启动路径的“启动成功”确认。</summary>
    public static async Task<RunningBedrockProcess?> WaitForRunningClientAsync(
        TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var process = GetRunningClientProcesses().FirstOrDefault();
            if (process != null) return process;
            await Task.Delay(250, ct);
        }
        return GetRunningClientProcesses().FirstOrDefault();
    }

    /// <summary>
    /// 关闭指定 PID 的基岩版实例。先尝试正常关闭窗口，2 秒内没有退出再强制终止进程树。
    /// 返回 false 表示该 PID 已不存在或关闭失败；失败原因通过 error 返回给 UI。
    /// </summary>
    public static async Task<bool> CloseClientProcessAsync(int processId, CancellationToken ct = default)
    {
        Process proc;
        try { proc = Process.GetProcessById(processId); }
        catch (ArgumentException) { return true; } // 已经退出，等价于关闭完成

        using (proc)
        {
            try
            {
                if (!proc.ProcessName.StartsWith("Minecraft.Windows", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"PID {processId} 不是 Minecraft for Windows 进程，已拒绝关闭。");

                try
                {
                    if (proc.CloseMainWindow())
                    {
                        var normalClose = proc.WaitForExitAsync(ct);
                        var timeout = Task.Delay(TimeSpan.FromSeconds(2), ct);
                        if (await Task.WhenAny(normalClose, timeout) == normalClose)
                            return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    return true; // 等待过程中已经退出
                }

                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync(ct);
                }
                return true;
            }
            catch (InvalidOperationException) when (proc.HasExited)
            {
                return true;
            }
        }
    }

    /// <summary>一键关闭当前检测到的所有基岩版客户端实例。</summary>
    public static async Task<(int ClosedCount, List<string> Failures)> CloseAllClientProcessesAsync(
        CancellationToken ct = default)
    {
        var snapshot = GetRunningClientProcesses();
        var closed = 0;
        var failures = new List<string>();

        foreach (var item in snapshot)
        {
            try
            {
                if (await CloseClientProcessAsync(item.ProcessId, ct)) closed++;
            }
            catch (Exception ex)
            {
                failures.Add($"PID {item.ProcessId}：{ex.Message}");
            }
        }
        return (closed, failures);
    }

    /// <summary>
    /// 跳转到 Minecraft for Windows 在 Microsoft Store 里的商品页——"未安装"或者"没有任何
    /// 有效授权"时该走的路径：登录 Microsoft 账户、领取官方 Trial/Demo、购买正版，这三件事
    /// 都在这个官方页面里完成，本方法只负责带用户过去，不做任何本地许可证判断或修改。
    ///
    /// 优先用 ms-windows-store: 协议直接拉起 Store 客户端里的商品页（体验最好，能直接点
    /// "试用"/"购买"/切换登录账户）；如果这台机器 Store 客户端本身不可用（拉起失败），
    /// 回退到网页版商品页，好歹能让用户看到购买入口。两条路径都是官方地址，没有绕过任何
    /// 东西。
    /// </summary>
    public static void OpenStorePage()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo($"ms-windows-store://pdp/?productid={StoreProductId}")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            using var proc = Process.Start(new ProcessStartInfo($"https://www.microsoft.com/store/productId/{StoreProductId}")
            {
                UseShellExecute = true
            });
        }
    }
}
