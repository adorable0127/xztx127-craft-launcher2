"""Source-contract checks; does not replace a Windows build or hardware touch tests."""
import json,re,xml.etree.ElementTree as ET
from pathlib import Path
from tree_sitter import Language,Parser
import tree_sitter_c_sharp
ROOT=Path(__file__).resolve().parents[1];APP=ROOT
parser=Parser(Language(tree_sitter_c_sharp.language()))
checks=[]
def check(name,condition):
 checks.append({'name':name,'passed':bool(condition)})
 if not condition:raise AssertionError(name)
check('Project files are directly in the package root',
      all((ROOT/path).exists() for path in
          ('XCL2.App.csproj','App.xaml','Services','Views','Resources','Tests','tools')))
check('No nested copy of the application project',not (ROOT/'XCL2.App').exists())
check('Root build resolves to one project',
      {path.name for path in ROOT.glob('*.*proj')} == {'XCL2.App.csproj'}
      and (ROOT/'tools/XCL2.proj').is_file())
project=ET.parse(ROOT/'XCL2.App.csproj')
excludes={part.strip().replace('\\','/').rstrip('/')
          for element in project.iter('DefaultItemExcludes')
          for part in (element.text or '').split(';')}
for directory in ('WinUI.Preview','Tests','tools','artifacts','dist'):
 check('Main project excludes auxiliary sources: '+directory,
       directory+'/**' in excludes or directory+'/**/*' in excludes)
files=list((APP/'Services/Workbench').glob('*.cs'))+list(APP.glob('Services/*Workbench*.cs'))+list(APP.glob('Views/*Workbench*.cs'))+[APP/'WinUI.Preview/MainWindow.cs']
files += [APP/'Services'/name for name in ('AiAssistantService.cs','AiFileAccessService.cs','AiFileAccessService.Activities.cs','AiWorkModePolicy.cs','TouchVersionPolicy.cs','TouchModeVersionPolicy.cs','TouchInputInjector.cs','LegacyTouchInputBridge.cs','LegacyTouchOptions.cs','LauncherService.cs')]
files += [APP/'Views/TouchOverlayWindow.xaml.cs',APP/'Views/AiAssistantPanel.xaml.cs',APP/'Views/AiAssistantSettingsPanel.MultiApi.cs',APP/'Views/AiAssistantSettingsPanel.xaml.cs']
files += [APP/'Services/PreviewFluentTheme.cs',APP/'Views/MainWindow.WinUiPreview.cs',APP/'Views/HomePage.Preview.cs',APP/'Views/HomePage.xaml.cs']
files += [APP/'Services'/name for name in ('AiControlProtocol.cs','AiProviderJson.cs','AiStreamingCompletion.cs','AiToolActionBudget.cs','AiQuestionProtocol.cs','AiSubAgentProtocol.cs','AiShellPolicy.cs','ConfigService.cs','SettingsVisibilityService.cs')]
files += [APP/'Models/AppConfig.cs',APP/'Models/AiAssistantModels.cs']
files += [APP/'Views'/name for name in ('FeatureSearchBox.xaml.cs','MainWindow.FeatureSearch.cs','SettingsPage.FeatureSearch.cs','SettingsPage.xaml.cs','AiWebSearchConfirmation.cs','AiAssistantSettingsPanel.WebSearch.cs')]
files += [APP/'Tests/AiHttpRegression/Program.cs',APP/'Tests/AiCommandRegression/Program.cs']
files += [APP/'Services/Plugins'/name for name in ('PluginWorkbenchEndpoints.cs','PluginProjectService.cs','PluginChangeService.cs','PluginTaskService.cs','PluginQueryService.cs','PluginContext.cs','PluginMetadata.cs','PluginPermissionService.cs','PluginPipeSession.cs')]
for path in files:
 tree=parser.parse(path.read_bytes());check('C# syntax: '+str(path.relative_to(ROOT)),not tree.root_node.has_error)
for path in (APP/'Views/AiAssistantPanel.xaml',APP/'Views/MainWindow.xaml',APP/'Views/HomePage.xaml',APP/'Views/FeatureSearchBox.xaml',APP/'Views/SettingsPage.xaml',APP/'Views/AiAssistantSettingsPanel.xaml',APP/'Resources/Themes/PreviewFluent.xaml',APP/'WinUI.Preview/XCL2.WinUI.Preview.csproj',ROOT/'Tests/WorkbenchRegression/WorkbenchRegression.csproj',ROOT/'Tests/AiHttpRegression/AiHttpRegression.csproj',ROOT/'Tests/AiCommandRegression/AiCommandRegression.csproj'):
 ET.parse(path);check('XML: '+str(path.relative_to(ROOT)),True)
manifest=json.loads((ROOT/'Tests/workbench-api-contracts.json').read_text());routes=[m['route'] for m in manifest]
check('109 distinct implemented API endpoints',len(routes)==109 and len(set(routes))==109)
doc=(ROOT/'API开放文件.md').read_text()
check('All new API routes documented',all('`'+route+'`' in doc for route in routes))
check('Embedded docs match deliverable',doc==(APP/'Resources/Docs/API_REFERENCE.md').read_text())
context=(APP/'Services/Plugins/PluginContext.cs').read_text()
for service in ('Projects','Tasks','Changes','Queries'):check('Context exposes '+service,(' '+service+' { get; }') in context)
check('EXE dispatch bound', 'PluginWorkbenchEndpoints.InvokeAsync(_context, method, args, ct)' in (APP/'Services/Plugins/PluginPipeSession.cs').read_text())
meta=(APP/'Services/Plugins/PluginMetadata.cs').read_text();permission=(APP/'Services/Plugins/PluginPermissionService.cs').read_text()
for name in ('ProjectsRead','ProjectsWrite','TasksManage'):check('Permission registered: '+name,meta.count(name)>=2 and name in permission)
policy=(APP/'Services/AiWorkModePolicy.cs').read_text()
allow=policy[policy.index('public static bool Allows'):policy.index('public const string DeniedMessage')]
for tool in ('update_task','search_project','read_file_range','list_project_files'):check('Plan allows read/metadata: '+tool,'"'+tool+'"' in allow)
check('Plan does not allow patch', '"patch_workspace_file"' not in allow)
activities=(APP/'Services/AiFileAccessService.Activities.cs').read_text()
check('Preview precedes apply',activities.index('bool accepted = review')<activities.index('Changes.Apply(change.Id'))
check('Full byte backup and conflict check','before.bin' in (APP/'Services/Workbench/ChangeJournal.cs').read_text() and 'change.AfterHash' in (APP/'Services/Workbench/ChangeJournal.cs').read_text())
check('Recursive reads keep private-path guard','!IsLauncherPrivatePath(candidate)' in activities)
check('Task marker parser and durable checkpoint', 'UpdateTaskFromReply' in (APP/'Services/AiAssistantService.cs').read_text() and 'RecordToolCheckpoint(session' in (APP/'Services/AiAssistantService.cs').read_text())
ui=(APP/'WinUI.Preview/MainWindow.cs').read_text()
for feature in ('_navigation','_search','_loaderFilter','_sort','LayoutInstanceCards','RenderInstances'):check('Native preview: '+feature,feature in ui)
report={'checks':checks,'passed':len(checks),'windows_build':'NOT_RUN: CoreCLR initialization failed in this environment (0x8007000E); Windows SDK/UI unavailable','runtime_regression':'NOT_RUN: run Tests/WorkbenchRegression with .NET 8','touch_hardware':'NOT_RUN: requires Windows touch hardware and actual Minecraft clients'}
(ROOT/'Tests/workbench-static-validation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n')
print(json.dumps({'static_checks_passed':len(checks),'runtime_tests':'not run'}))
