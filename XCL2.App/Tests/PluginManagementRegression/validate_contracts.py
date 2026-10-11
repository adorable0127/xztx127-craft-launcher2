"""Verify 2.2 DLL/pipe parity and policy wiring without claiming runtime compilation."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[2]
plugins = root / 'Services/Plugins'
pipe = (plugins / 'PluginPipeSession.cs').read_text()
management = (plugins / 'PluginManagementService.cs').read_text()
resources = (plugins / 'PluginResourceService.cs').read_text()
bridge = (plugins / 'PluginHostBridge.Management.cs').read_text()
permissions = (plugins / 'PluginMetadata.cs').read_text()
availability = (plugins / 'PluginPermissionService.cs').read_text()
prompts = (root / 'Views/PluginPermissionsDialog.cs').read_text()
methods = {
'folders.list': 'Management.GetFoldersAsync', 'instances.directories': 'Management.GetInstanceDirectoriesAsync',
'instances.setHidden': 'Management.SetInstanceHiddenAsync', 'instances.rename': 'Management.RenameInstanceAsync',
'instances.setIsolation': 'Management.SetInstanceIsolationAsync', 'instances.repair': 'Management.RepairInstanceAsync',
'mods.setEnabled': 'Management.SetModEnabledAsync', 'java.list': 'Management.GetJavaRuntimesAsync',
'java.select': 'Management.SelectJavaAsync', 'downloads.get': 'Management.GetDownloadAsync',
'downloads.pause': 'Management.PauseDownloadAsync', 'downloads.resume': 'Management.ResumeDownloadAsync',
'downloads.cancel': 'Management.CancelDownloadAsync', 'downloads.clearFinished': 'Management.ClearFinishedDownloadsAsync',
'resources.sources': 'Resources.GetSourcesAsync', 'resources.search': 'Resources.SearchAsync',
'resources.files': 'Resources.GetFilesAsync', 'resources.install': 'Resources.InstallAsync',
'gameVersions.list': 'Resources.GetGameVersionsAsync', 'gameVersions.install': 'Resources.InstallGameVersionAsync'}
for route, call in methods.items():
    line = next(line for line in pipe.splitlines() if f'case "{route}":' in line)
    assert pipe.count(f'case "{route}":') == 1, route
    assert f'_context.{call}(' in line and 'ConfigureAwait(false)' in line, route
    assert re.search(r'\b' + call.split('.')[1] + r'\(', management + resources), call
for field, permission in {'DownloadsControl':'downloads.control','InstancesWrite':'instances.write','ModsWrite':'mods.write',
    'ResourcesRead':'resources.read','ResourcesInstall':'resources.install','JavaRead':'java.read','JavaSelect':'java.select'}.items():
    assert f'{field} = "{permission}"' in permissions
    assert f'PluginPermissionNames.{field}' in availability
    assert f'"{permission}" =>' in prompts
api_version = re.search(r'Version Version = new\((\d+),\s*(\d+)\)', permissions)
assert api_version and tuple(map(int, api_version.groups())) >= (2, 2), "API 2.2 management contracts require API version 2.2 or later"
assert 'scope.Check();' in management and 'context.AuthorizationChanged += OnAuthorizationChanged' in management
assert resources.count('PluginPermissionNames.InstancesWrite, PluginPermissionNames.ResourcesInstall') >= 2
assert 'PluginPermissionNames.InstancesWrite, PluginPermissionNames.ResourcesInstall' in management
assert 'lock (_cacheGate)' in resources and 'ReferenceEquals(cached.ProviderContext, entry.Context)' in resources
assert '!string.IsNullOrWhiteSpace(query.Loader) && !MatchesLoader(query.Loader, loaders)' in resources
assert 'item.CanCancel, item.IsRunning' in bridge and 'if (!item.CanCancel)' in bridge
assert '!i.IsRunning' in bridge and 'active != null && active != item' in bridge
assert 'VersionResourcePackIsolationOverrides' in bridge
assert 'DestinationDirectory(host, latest, cached.Kind).Equals(targetDir' in resources
assert 'request.ReplaceExisting' in resources and 'SHA1.HashDataAsync' in resources
assert 'new PluginDownloadAcceptance(item.Id, "Queued")' in management
assert resources.count('new PluginDownloadAcceptance(item.Id, "Queued")') == 2
print(f'Passed: {len(methods)} DLL/EXE route pairs, 7 explicit permissions, queue lifecycle, resource isolation/token/compatibility guards.')
