using System.IO;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 服务器实例列表的持久化。风格与 ConfigService 保持一致：内存里维护一份列表，
/// 每次增删改后立即整体写回 xcl2/servers.json（服务器实例数量级通常很小，几个到十几个，
/// 不需要为了性能做局部更新的复杂化处理）。
/// </summary>
public class ServerInstanceService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string ServersJsonPath { get; }
    public List<ServerInstance> Instances { get; private set; } = new();

    public ServerInstanceService()
    {
        ServersJsonPath = Path.Combine(App.DataDir, "servers.json");
    }

    /// <summary>
    /// 上一次 Load() 失败时的具体异常信息（null = 上次加载成功，或者还没加载过）。
    /// 修复"检测不到以前创建的服务器"：之前 catch 块把任何异常都静默吞掉、回退成空列表，
    /// 用户看到的现象就是"服务器列表突然空了"，且没有任何线索——分不清是真的没有服务器、
    /// 还是 servers.json 读取失败被悄悄丢弃了。这里保留异常信息，调用方（MainWindow）可以据此
    /// 提示用户"配置文件读取失败，已保留备份"，而不是让数据在用户毫无察觉的情况下消失。
    /// </summary>
    public Exception? LastLoadError { get; private set; }

    public void Load()
    {
        LastLoadError = null;

        if (!File.Exists(ServersJsonPath))
        {
            Instances = new List<ServerInstance>();
            return;
        }

        try
        {
            var json = File.ReadAllText(ServersJsonPath);
            Instances = JsonSerializer.Deserialize<List<ServerInstance>>(json) ?? new List<ServerInstance>();
            return;
        }
        catch (Exception ex)
        {
            LastLoadError = ex;
        }

        // 主文件解析失败：尝试上一次 Save() 留下的备份（见 Save() 里 .bak 的写入逻辑），
        // 而不是直接判定"没有服务器"。这一步能救回的场景：上次写入 servers.json 时进程被杀掉/
        // 断电，导致主文件截断成非法 JSON，但 .bak 仍然是上上次成功写入的完整内容。
        var backupPath = ServersJsonPath + ".bak";
        try
        {
            if (File.Exists(backupPath))
            {
                var backupJson = File.ReadAllText(backupPath);
                var restored = JsonSerializer.Deserialize<List<ServerInstance>>(backupJson);
                if (restored != null)
                {
                    Instances = restored;
                    return; // 备份恢复成功：LastLoadError 保留主文件的错误信息，供 UI 提示"已从备份恢复"
                }
            }
        }
        catch
        {
            // 备份也读不出来：彻底放弃，走下面的空列表兜底。LastLoadError 仍然是主文件的原始异常。
        }

        // 主文件和备份都读取失败时才回退成空列表——这是真正无法恢复数据时的最后手段，
        // 调用方应该用 LastLoadError 提示用户"配置文件损坏，已重置服务器列表"，
        // 而不是让用户以为自己"从来没创建过服务器"。
        Instances = new List<ServerInstance>();
    }

    /// <summary>
    /// 保存服务器实例列表。做两件事修复数据丢失/损坏风险：
    /// 1) 写入前先把当前磁盘上的旧文件备份成 .bak（用于 Load() 在主文件损坏时的恢复来源）；
    /// 2) 用"写临时文件 + 原子替换"代替直接 File.WriteAllText 覆盖原文件——直接覆盖在写入
    ///    过程中被中断（进程崩溃/断电/强制结束）会让 servers.json 变成半截的非法 JSON，
    ///    下次启动 Load() 就会解析失败，这正是"检测不到以前创建的服务器"的另一个常见根因。
    /// </summary>
    private void Save()
    {
        Directory.CreateDirectory(App.DataDir);

        if (File.Exists(ServersJsonPath))
        {
            try { File.Copy(ServersJsonPath, ServersJsonPath + ".bak", overwrite: true); }
            catch { /* 备份失败不应该阻塞本次保存，下次 Save() 成功时会补上一份新备份 */ }
        }

        var tempPath = ServersJsonPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(Instances, JsonOpts));
        File.Move(tempPath, ServersJsonPath, overwrite: true); // 同卷内 Move 是原子操作，不会产生半截文件
    }

    public ServerInstance Add(ServerInstance instance)
    {
        Instances.Add(instance);
        Save();
        return instance;
    }

    public void Update(ServerInstance instance)
    {
        var idx = Instances.FindIndex(i => i.Id == instance.Id);
        if (idx >= 0) Instances[idx] = instance;
        Save();
    }

    /// <summary>
    /// 设置默认服务器：把目标实例 IsDefault 置 true，同时把其余所有实例的 IsDefault 置 false，
    /// 保证同一时间最多只有一个"默认"。传 null 表示清除默认（没有任何实例是默认）。
    /// </summary>
    public void SetDefault(string? instanceId)
    {
        foreach (var inst in Instances)
            inst.IsDefault = inst.Id == instanceId;
        Save();
    }

    /// <summary>
    /// 保存自定义图标：把源文件复制进 xcl2/server-icons/{instanceId}{扩展名}，返回复制后的绝对路径。
    /// 用 instanceId 命名（而不是保留原文件名）是为了避免重名覆盖，以及方便后续按实例清理。
    /// 不直接引用用户原文件路径的原因：用户可能之后移动/删除那个原始文件，图标应该独立于它存在。
    /// </summary>
    public string SetIcon(string instanceId, string sourceImagePath)
    {
        if (!File.Exists(sourceImagePath))
            throw new FileNotFoundException("找不到选择的图标文件。", sourceImagePath);

        var iconsDir = Path.Combine(App.DataDir, "server-icons");
        Directory.CreateDirectory(iconsDir);

        // 换图标前先把该实例名下所有旧图标文件清掉（扩展名可能变化，例如从 .png 换成 .jpg），
        // 避免 server-icons 目录里堆积用不到的旧文件。
        foreach (var old in Directory.GetFiles(iconsDir, $"{instanceId}.*"))
        {
            try { File.Delete(old); } catch { /* 忽略清理失败，不影响本次设置新图标 */ }
        }

        var ext = Path.GetExtension(sourceImagePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var destPath = Path.Combine(iconsDir, $"{instanceId}{ext}");
        File.Copy(sourceImagePath, destPath, overwrite: true);
        return destPath;
    }

    /// <summary>清除自定义图标：删除磁盘文件（若存在），调用方负责同时把 instance.IconPath 置 null 并 Update。</summary>
    public void ClearIcon(string? iconPath)
    {
        if (string.IsNullOrEmpty(iconPath)) return;
        try { if (File.Exists(iconPath)) File.Delete(iconPath); } catch { /* 忽略删除失败，不阻塞清除操作 */ }
    }

    /// <summary>
    /// 从列表移除一个实例。deleteFiles=true 时同时删除磁盘上的服务端目录——
    /// 这是破坏性操作，调用方（UI 层）必须在调用前自行完成用户确认，这个方法本身
    /// 不做任何二次确认或备份，专注做"删除"这一件事，保护性交互留给上层实现
    /// （对应清单里"清除服务器数据"那一项，那是独立于这里的"移除实例记录"的更完整流程）。
    /// </summary>
    public void Remove(string instanceId, bool deleteFiles)
    {
        var instance = Instances.FirstOrDefault(i => i.Id == instanceId);
        if (instance == null) return;

        Instances.Remove(instance);
        Save();
        ClearIcon(instance.IconPath);

        if (deleteFiles && Directory.Exists(instance.Directory))
        {
            try { Directory.Delete(instance.Directory, recursive: true); }
            catch (Exception ex)
            {
                throw new IOException($"实例记录已移除，但删除文件夹失败：{ex.Message}\n目录：{instance.Directory}", ex);
            }
        }
    }
}
