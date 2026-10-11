using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

internal sealed class PluginModSourcesDialog : OverlayDialogControl
{
    private readonly MainWindow _owner;
    private readonly DownloadCenterPage _fallback;
    private readonly ComboBox _sources = new() { Width = 260, DisplayMemberPath = nameof(PluginProviderInfo.Name) };
    private readonly TextBox _query = new() { MinWidth = 200, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ListBox _projects = new() { DisplayMemberPath = nameof(PluginModProject.Title), MinHeight = 100, MaxHeight = 200 };
    private readonly ComboBox _files = new() { DisplayMemberPath = nameof(PluginModFile.Name), MinWidth = 350 };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CancellationTokenSource _stop = new();
    private string? _instanceId;
    private string? _version;
    private string? _loader;
    private long _selection;
    internal PluginModSourcesDialog(MainWindow owner, DownloadCenterPage fallback)
    {
        _owner = owner; _fallback = fallback; Width = 740;
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = Loc.T("Str_Plugin_ModSources", "插件 Mod 来源"), FontSize = 20, Margin = new Thickness(0, 0, 0, 12) });
        var top = new WrapPanel(); var search = new Button { Content = Loc.T("Str_Common_Search", "搜索"), Margin = new Thickness(8, 0, 0, 0) };
        top.Children.Add(_sources); top.Children.Add(_query); top.Children.Add(search); root.Children.Add(top);
        root.Children.Add(_projects); root.Children.Add(_details); root.Children.Add(_files);
        var download = new Button { Content = Loc.T("Str_Plugin_InstallSelectedMod", "安装到当前实例"), Margin = new Thickness(0, 12, 0, 8), HorizontalAlignment = HorizontalAlignment.Right };
        root.Children.Add(download); root.Children.Add(_status); Content = root;
        var back = new Button { Content = Loc.T("Str_Plugin_BackToBuiltInSources", "返回内置来源"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        root.Children.Add(back);
        back.Click += (_, _) => CloseWith(false);
        _sources.ItemsSource = PluginProviderRegistry.Snapshot("mod"); _sources.SelectedIndex = 0;
        RequestClose += (_, _) => { Interlocked.Increment(ref _selection); _stop.Cancel(); };
        _sources.SelectionChanged += (_, _) =>
        {
            Interlocked.Increment(ref _selection);
            ResetResults();
            _status.Text = "";
        };
        search.Click += async (_, _) => await SearchAsync();
        _projects.SelectionChanged += async (_, _) => await LoadDetailsAsync();
        download.Click += (_, _) => InstallSelected();
        if (_sources.Items.Count == 0) _status.Text = Loc.T("Str_Plugin_NoModSources", "没有已授权的插件 Mod 来源，请先启用插件并授权 providers.mod 与 network。");
    }
    private void ResetResults()
    {
        _projects.ItemsSource = null;
        _files.ItemsSource = null;
        _details.Text = "";
        _instanceId = null;
        _version = null;
        _loader = null;
    }
    private async Task SearchAsync()
    {
        if (_sources.SelectedItem is not PluginProviderInfo info) return;
        ResetResults();
        var generation = Interlocked.Increment(ref _selection);
        try
        {
            var host = PluginHostBridge.Current ?? throw new PluginHostUnavailableException();
            var instances = await host.GetInstancesAsync(new(), _stop.Token);
            var instance = instances.Items.FirstOrDefault(i => i.IsSelected);
            var entry = PluginProviderRegistry.Get(info.Id, "mod");
            var query = new PluginModQuery(_query.Text.Trim(), instance?.MinecraftVersion, instance?.LoaderId);
            var result = await PluginProviderRegistry.InvokeAsync(entry,
                token => ((IPluginModProvider)entry.Provider).SearchAsync(query, token), _stop.Token);
            if (generation != Interlocked.Read(ref _selection)) return;
            if (result.Items.IsDefault || result.Items.Length > 1000) throw new PluginOperationException("InvalidResults", "插件搜索结果无效。");
            _instanceId = instance?.Id; _version = instance?.MinecraftVersion; _loader = instance?.LoaderId;
            _projects.ItemsSource = result.Items.Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id)).Select(p => p with
                { Id = host.Safe(p.Id), Title = host.Safe(p.Title), Description = host.Safe(p.Description), Author = host.Safe(p.Author) }).ToArray();
            _status.Text = Loc.F("Str_Plugin_ModResultCount", "返回 {0} 条结果；当前实例：{1}", result.Items.Length, instance?.Name ?? Loc.T("Str_Plugin_NoSelectedInstance", "未选择"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation != Interlocked.Read(ref _selection)) return;
            _status.Text = ex is PluginOperationException operation ? operation.SafeMessage : Loc.T("Str_Plugin_SearchFailed", "插件搜索失败，已转到内置来源。");
            _fallback.SelectModCategoryAndSearch(_query.Text.Trim());
        }
    }
    private async Task LoadDetailsAsync()
    {
        var generation = Interlocked.Increment(ref _selection);
        _files.ItemsSource = null;
        _details.Text = "";
        if (_sources.SelectedItem is not PluginProviderInfo info || _projects.SelectedItem is not PluginModProject project) return;
        try
        {
            var entry = PluginProviderRegistry.Get(info.Id, "mod"); var provider = (IPluginModProvider)entry.Provider;
            var detail = await PluginProviderRegistry.InvokeAsync(entry, async token => await provider.GetDetailsAsync(project.Id, token) ?? project, _stop.Token);
            var files = await PluginProviderRegistry.InvokeAsync(entry, token => provider.GetFilesAsync(project.Id, _version, _loader, token), _stop.Token);
            if (generation != Interlocked.Read(ref _selection)) return;
            _details.Text = PluginProviderRegistry.Safe(detail?.Description ?? project.Description);
            if (files.IsDefault || files.Length > 2000) throw new PluginOperationException("InvalidFiles", "插件文件列表无效。");
            _files.ItemsSource = files.Where(f => f != null && PluginProviderRegistry.ValidFileName(f.FileName) && PluginProviderRegistry.ValidUrl(f.Url))
                .Select(f => f with { Name = PluginProviderRegistry.Safe(f.Name), GameVersion = PluginProviderRegistry.Safe(f.GameVersion), Loader = PluginProviderRegistry.Safe(f.Loader) })
                .OrderByDescending(f => f.GameVersion, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Loader, StringComparer.OrdinalIgnoreCase).ToArray();
            _files.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation != Interlocked.Read(ref _selection)) return;
            _status.Text = ex is PluginOperationException operation ? operation.SafeMessage : Loc.T("Str_Plugin_DetailsFailed", "插件详情读取失败，可以切回内置来源。");
        }
    }
    private void InstallSelected()
    {
        if (_sources.SelectedItem is not PluginProviderInfo source || _files.SelectedItem is not PluginModFile file || _instanceId == null)
        { _status.Text = Loc.T("Str_Plugin_SelectModTarget", "请选择文件并先在启动器中选中一个实例。"); return; }
        var instanceId = _instanceId;
        DownloadQueueService.Instance.StartNew(Loc.F("Str_Plugin_ModDownloadName", "下载 {0}", file.Name), async (item, ct) =>
        {
            var entry = PluginProviderRegistry.Get(source.Id, "mod"); var revision = Interlocked.Read(ref entry.Context.AuthorizationRevision);
            var host = PluginHostBridge.Current ?? throw new PluginHostUnavailableException();
            var detail = await host.GetInstanceDetailsAsync(instanceId, ct) ?? throw new PluginResourceNotFoundException();
            var staging = Path.Combine(entry.Context.DataDirectory, "downloads", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
            try
            {
                using var service = new GenericFileDownloadService();
                var downloaded = await service.DownloadAsync(file.Url, staging, file.FileName, null, item.CreateProgress(), ct);
                if (!string.IsNullOrEmpty(file.Sha1))
                {
                    await using var stream = File.OpenRead(downloaded); var digest = Convert.ToHexString(await SHA1.HashDataAsync(stream, ct));
                    if (!digest.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase)) throw new PluginOperationException("HashMismatch", "Mod 文件校验失败。");
                }
                entry.Context.Permissions.Demand(PluginPermissionNames.ModProvider); entry.Context.Permissions.Demand(PluginPermissionNames.Network);
                if (revision != Interlocked.Read(ref entry.Context.AuthorizationRevision)) throw new PluginOperationException("AuthorizationChanged", "插件授权已变更，请重新安装。");
                if (!file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new PluginOperationException("InvalidModFile", "Mod 来源只允许安装 JAR 文件。");
                foreach (var plan in ModInstallService.PrepareBatch(new[] { downloaded }, Path.Combine(detail.GameDirectory, "mods"))) ModInstallService.Install(plan);
            }
            finally { try { Directory.Delete(staging, true); } catch { } }
        });
        _status.Text = Loc.T("Str_Plugin_ModQueued", "已加入下载队列。");
    }
}
