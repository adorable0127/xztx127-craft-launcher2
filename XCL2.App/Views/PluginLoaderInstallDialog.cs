using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

internal sealed class PluginLoaderInstallDialog : OverlayDialogControl
{
    private readonly MainWindow _owner;
    private readonly string _providerId;
    private readonly string _minecraftVersion;
    private readonly ComboBox _builds = new() { DisplayMemberPath = nameof(PluginLoaderBuild.Name), MinWidth = 320 };
    private readonly TextBox _name = new() { Margin = new Thickness(0, 10, 0, 10) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CancellationTokenSource _stop = new();
    internal PluginLoaderInstallDialog(MainWindow owner, string providerId, string minecraftVersion)
    {
        _owner = owner; _providerId = providerId; _minecraftVersion = minecraftVersion; Width = 540;
        _name.Text = minecraftVersion + "-plugin-" + DateTime.Now.ToString("HHmmss");
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = Loc.T("Str_Plugin_LoaderInstall", "插件加载器安装"), FontSize = 20 });
        root.Children.Add(_builds); root.Children.Add(_name);
        var install = new Button { Content = Loc.T("Str_Plugin_Install", "安装"), Margin = new Thickness(0, 8, 0, 8) };
        install.Click += async (_, _) =>
        {
            if (_builds.SelectedItem is not PluginLoaderBuild build) return; install.IsEnabled = false;
            try { await InstallAsync(build, _name.Text.Trim()); _status.Text = Loc.T("Str_Cs_Installation_Complete", "安装完成"); CloseWith(true); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _status.Text = ex is PluginOperationException operation ? operation.SafeMessage : Loc.T("Str_Plugin_LoaderFailed", "插件安装失败，可以重新选择内置加载器。临时工作区不会加入实例列表。"); }
            finally { install.IsEnabled = true; }
        };
        root.Children.Add(install); root.Children.Add(_status); Content = root;
        RequestClose += (_, _) => _stop.Cancel(); Loaded += async (_, _) => await LoadBuildsAsync();
    }
    private async Task LoadBuildsAsync()
    {
        try
        {
            var entry = PluginProviderRegistry.Get(_providerId, "loader");
            var builds = await PluginProviderRegistry.InvokeAsync(entry, token => ((IPluginLoaderInstaller)entry.Provider).GetBuildsAsync(_minecraftVersion, token), _stop.Token);
            _builds.ItemsSource = builds.IsDefault ? Array.Empty<PluginLoaderBuild>() : builds.Where(b => b != null).Select(b => b with
                { Id = PluginProviderRegistry.Safe(b.Id), Name = PluginProviderRegistry.Safe(b.Name), Version = PluginProviderRegistry.Safe(b.Version) }).ToArray();
            _builds.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = ex is PluginOperationException operation ? operation.SafeMessage : Loc.T("Str_Plugin_LoaderListFailed", "加载器构建列表读取失败。"); }
    }
    private async Task InstallAsync(PluginLoaderBuild build, string name)
    {
        if (!PluginProviderRegistry.ValidFileName(name)) throw new PluginOperationException("InvalidName", "实例名称无效。");
        var root = _owner.ConfigService.Config.SelectedFolderPath;
        if (string.IsNullOrWhiteSpace(root)) throw new PluginOperationException("MissingFolder", "请先选择游戏文件夹。");
        var target = Path.Combine(root, "versions", name);
        if (Directory.Exists(target)) throw new PluginOperationException("InstanceExists", "该实例名称已存在。");
        await PluginInterceptionRegistry.ValidateInstanceAsync(root, name, "plugin-loader", _stop.Token);
        var entry = PluginProviderRegistry.Get(_providerId, "loader");
        entry.Context.Permissions.Demand(PluginPermissionNames.InstanceFilesWrite);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, entry.Context.Stopping);
        using var vanilla = DownloadService.CreateFromConfig(_owner.ConfigService.Config);
        var manifest = await vanilla.GetVersionManifestAsync(stop.Token);
        var game = manifest.Versions.FirstOrDefault(v => v.Id == _minecraftVersion) ?? throw new PluginResourceNotFoundException();
        await vanilla.InstallVersionAsync(root, game, null, stop.Token);
        var workspace = new PluginInstallWorkspace(entry.Context, name, stop.Token);
        try
        {
            await PluginProviderRegistry.InvokeAsync(entry, async token =>
            {
                await ((IPluginLoaderInstaller)entry.Provider).InstallAsync(workspace, new(_minecraftVersion, build.Id, name), token);
                return true;
            }, stop.Token, TimeSpan.FromMinutes(15));
            workspace.Close();
            var versionPath = Path.Combine(workspace.Root, "versions", name, name + ".json");
            if (!File.Exists(versionPath)) throw new PluginOperationException("MissingVersionJson", "安装器没有提供匹配名称的版本 JSON。");
            var profile = JsonSerializer.Deserialize<VersionDetail>(await File.ReadAllTextAsync(versionPath, stop.Token)) ?? throw new PluginOperationException("InvalidVersionJson", "加载器 JSON 无效。");
            if (profile.Id != name || (!string.IsNullOrEmpty(profile.InheritsFrom) && profile.InheritsFrom != _minecraftVersion))
                throw new PluginOperationException("InvalidVersionJson", "安装器的实例 Id / 父版本不匹配。");
            foreach (var file in Directory.EnumerateFileSystemEntries(workspace.Root, "*", SearchOption.AllDirectories))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new PluginOperationException("InvalidPath", "安装器输出含链接。");
            foreach (var library in profile.Libraries)
            {
                var paths = new[] { library.GetMavenPath(), library.Downloads?.Artifact?.Path }.Concat(library.Downloads?.Classifiers?.Values.Select(a => a.Path) ?? Enumerable.Empty<string?>());
                foreach (var relative in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
                    if (Path.IsPathRooted(relative!) || relative!.Contains(':') || relative.Split('/', '\\').Contains("..")) throw new PluginOperationException("InvalidLibraryPath", "安装器输出了无效库路径。");
            }
            entry.Context.Permissions.Demand(PluginPermissionNames.LoaderProvider); entry.Context.Permissions.Demand(PluginPermissionNames.InstanceFilesWrite);
            var source = Path.Combine(workspace.Root, "versions", name);
            await Task.Run(() => CopyTree(source, target), stop.Token);
            try { await vanilla.DownloadLibrariesOnlyAsync(root, profile, null, stop.Token); }
            catch { Directory.Delete(target, true); throw; }
            _owner.NotifyInstanceImported(root, name); PluginHostBridge.Current?.NotifyInstanceCreated(root, name);
        }
        finally { workspace.Close(); try { Directory.Delete(workspace.Root, true); } catch { } }
    }
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), true);
    }
}
