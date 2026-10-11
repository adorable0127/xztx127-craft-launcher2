"""Generate only API contracts from source. Requires tree-sitter and tree-sitter-c-sharp."""
from pathlib import Path
import json, re
from tree_sitter import Language, Parser
import tree_sitter_c_sharp
ROOT = Path(__file__).resolve().parents[1]
parser=Parser(Language(tree_sitter_c_sharp.language()))
def clean(value):return re.sub(r'\s+', ' ', value).strip().replace('|', '\\|').replace('`','\\`')
def code(value):return '`'+clean(value)+'`'
def text(data,node):return data[node.start_byte:node.end_byte].decode('utf-8-sig')
def mods(data,node):return [text(data,c) for c in node.children if c.type=='modifier']
types=[];endpoints=[]
paths=list((ROOT/'Services/Plugins').glob('*.cs'))+list((ROOT/'Services/Workbench').glob('*.cs'))
paths += [ROOT/'Services/EmbeddedDocumentationService.cs',ROOT/'Services/CommandLineService.cs',ROOT/'Services/LauncherCommandResult.cs']
for path in sorted(paths):
 data=path.read_bytes();tree=parser.parse(data)
 def visit(node, public_parent=True, prefix=''):
  if node.type in ('class_declaration','interface_declaration','struct_declaration','record_declaration','enum_declaration'):
   if 'public' not in mods(data,node) or not public_parent:return
   name=text(data,node.child_by_field_name('name'));full=prefix+name
   body=node.child_by_field_name('body'); members=[]
   declaration=clean(data[node.start_byte:(body.start_byte if body else node.end_byte)].decode())
   if body:
    for child in body.named_children:
     if child.type in ('class_declaration','interface_declaration','struct_declaration','record_declaration','enum_declaration'):
      visit(child,True,full+'.');continue
     if child.type=='enum_member_declaration':members.append((text(data,child),'',''));continue
     if child.type not in ('method_declaration','property_declaration','event_field_declaration','field_declaration','constructor_declaration','delegate_declaration','indexer_declaration'):continue
     if 'public' not in mods(data,child) and node.type!='interface_declaration':continue
     raw=text(data,child)
     route_match=re.search(r'WorkbenchEndpoint\("([^"]+)"\)',raw)
     route=route_match.group(1) if route_match else ''
     cut=child.end_byte
     for c in child.children:
      if c.type in ('block','arrow_expression_clause','accessor_list','equals_value_clause'):cut=min(cut,c.start_byte)
     start=child.start_byte
     for c in child.children:
      if c.type=='attribute_list':start=max(start,c.end_byte)
     signature=data[start:cut].decode().strip().rstrip(';').strip()
     signature=re.sub(r'^(public\s+)?(async\s+)?','',signature)
     # Field initializers are kept for public constants, not implementation-only backing members.
     if child.type=='property_declaration':
      access=next((c for c in child.children if c.type=='accessor_list'),None)
      if access:signature+=' { '+ '; '.join(re.match(r'(?:private\s+|protected\s+|internal\s+)*(?:get|set|init)',text(data,c)).group(0) for c in access.named_children if c.type=='accessor_declaration')+'; }'
      else:signature+=' { get; }'
     previous=child.prev_named_sibling;summary=[]
     while previous is not None and previous.type=='comment' and text(data,previous).startswith('///'):
      summary.insert(0,text(data,previous));previous=previous.prev_named_sibling
     desc=clean(re.sub('<[^>]+>','',re.sub(r'///\s?', '', '\n'.join(summary))))[:300]
     members.append((signature,route,desc))
     if route:endpoints.append({'route':route,'type':full,'signature':signature})
   types.append((full,declaration,members,path.relative_to(ROOT).as_posix()))
   return
  for child in node.named_children:visit(child,public_parent,prefix)
 visit(tree.root_node)
assert len(endpoints)==109 and len({e['route'] for e in endpoints})==109
out=['# XCL2 API 2.3','']
# New service contracts first for discoverability, then all pre-existing public plugin members/DTOs.
priority={'PluginContext':0,'IPlugin':1,'PluginProjectService':2,'PluginTaskService':3,'PluginChangeService':4,'PluginQueryService':5}
def permission(route):
 if route.startswith('workbench.projects'):return 'projects.read' + (' + projects.write' if route.endswith('openDataProject') else '')
 if route.startswith('workbench.changes'):return 'projects.read + projects.write'
 if route.startswith('workbench.tasks'):return 'tasks.manage'
 if route.startswith('workbench.query'):return route.split('.')[2]+'.read'
 return '—'
entries={'PluginProjectService':'ctx.Projects','PluginTaskService':'ctx.Tasks','PluginChangeService':'ctx.Changes','PluginQueryService':'ctx.Queries'}
merged={}
for name,decl,members,path in types:
 if name in merged:merged[name][2].extend(members)
 else:merged[name]=[name,decl,members,path]
for name,decl,members,path in sorted(merged.values(),key=lambda x:(priority.get(x[0],10),x[0])):
 out += ['## '+entries.get(name,name),'', '```csharp',decl,'```','']
 if members:
  out += ['| API 签名 | EXE method | 权限 / 接口说明 |','| --- | --- | --- |']
  for sig,route,desc in members:
   detail=(permission(route) if route else desc) or '—'
   out.append('| '+code(sig)+' | '+(code(route) if route else '—')+' | '+clean(detail)+' |')
  out+=['']
# Existing explicit EXE routes, extracted from the real switch body.
pipe=(ROOT/'Services/Plugins/PluginPipeSession.cs').read_text();pipe=pipe[pipe.index('private async Task<object?> DispatchAsync'):pipe.index('private PluginInstallWorkspace Workspace')]
cases=list(re.finditer(r'case "([^"]+)":',pipe))
out+=['## EXE 命名管道 API','', '| method | params 字段 |','| --- | --- |','| `hello` | `token` |']
for i,m in enumerate(cases):
 body=pipe[m.end():cases[i+1].start() if i+1<len(cases) else pipe.index('default:',m.end()) if 'default:' in pipe[m.end():] else len(pipe)]
 keys=sorted(set(re.findall(r'(?:String\(args,\s*|args\.(?:GetProperty|TryGetProperty)\()"([^"]+)"',body)))
 out.append('| '+code(m.group(1))+' | '+(', '.join(code(k) for k in keys) or '—')+' |')
out += ['','| 协议接口 | 类型 / 格式 |','| --- | --- |',
 '| 帧 | `uint32 little-endian length` + UTF-8 JSON；1–1048576 字节 |',
 '| 请求 | `protocol:1, kind:"request", id, method, params` |',
 '| 成功响应 | `protocol:1, kind:"response", id, result` |',
 '| 失败响应 | `protocol:1, kind:"response", id, error:{code,message}` |',
 '| 事件通知 | `protocol:1, kind:"event", subscriptionId, event, payload` |',
 '| `callbacks.invoke` | 宿主 → EXE；`callbackId, payload` |',
 '| `XCL2_PLUGIN_PIPE` | 命名管道名称 |',
 '| `XCL2_PLUGIN_SESSION_TOKEN` | `hello.params.token` |',
 '| `XCL2_PLUGIN_PROTOCOL_VERSION` | 协议版本 |',
 '| `XCL2_PLUGIN_DATA_DIR` | 插件专属数据目录 |','',
 '```json','{"protocol":1,"kind":"request","id":"search-1","method":"workbench.projects.searchText","params":{"projectId":"选择目录返回的 Id","query":"class MainWindow","glob":"*.cs","limit":100}}','```','']
# Current CLI HelpText is the canonical list; exclude explanatory/migration prose.
cli=(ROOT/'Services/CommandLineService.cs').read_text();section=cli[cli.index('public const string HelpText'):];section=section[:section.index('示例：')]
out += ['## 命令行 API','','| 参数 | 行为 |','| --- | --- |']
for line in re.findall(r'"  ([^"\n]+)\\n"',section):
 parts=re.split(r'\s{2,}',line.strip(),maxsplit=1)
 if len(parts)==2:out.append('| '+code(parts[0])+' | '+clean(parts[1])+' |')
# AI APIs: source names, actual implemented streaming modes, no stale usage guide.
out += ['','## AI HTTP API','','| 接口 | 请求 / 响应字段 |','| --- | --- |',
 '| `GET <BaseUrl>/models` | `Authorization: Bearer …`；响应 `data[].id` |',
 '| `POST <BaseUrl>/chat/completions` | `model, messages, stream, stream_options, temperature, max_tokens, reasoning_effort, tools` |',
 '| 非流式响应 | `choices[].message.content`；可选 `usage.prompt_tokens, completion_tokens, total_tokens` |',
 '| 流式响应 | SSE `data:` JSON，`choices[].delta.content`，可选 reasoning/usage；`[DONE]` |',
 '| `messages[]` | `role: system/user/assistant`, `content: string` |',
 '| `stream_options` | `include_usage: true` |','',
 '## AI 文件与任务工具 API','','| action | 参数 | 模式 |','| --- | --- | --- |']
actions=[('read_log','—','Plan / Build'),('read_config','key','Plan / Build'),('apply_setting','key, value','Build'),('request_directory','path, content','Build'),('list_workspace','path','Plan / Build'),('read_workspace_file','path','Plan / Build'),('write_workspace_file','path, content, sha256?','Build'),('delete_workspace_file','path','Build'),('run_command','command, shell, timeout_seconds','Build'),('registry_read','path, key','Plan / Build'),('registry_write','path, key, value, type','Build'),('update_task','steps[{id,title,status,evidence}], next_action, notes','Plan / Build'),('list_project_files','path, glob, limit','Plan / Build'),('search_project','path, query, glob, regex, case_sensitive, limit','Plan / Build'),('read_file_range','path, start_line, line_count','Plan / Build'),('patch_workspace_file','path, sha256, old_text, new_text','Build')]
for a,b,c in actions:out.append('| '+code(a)+' | '+code(b)+' | '+c+' |')
out+=['','```text','<<XCL2_ACCESS:{"action":"read_file_range","path":"src/MainWindow.cs","start_line":201,"line_count":200}>>','```','',
 '## 工作台 API 数据约束','','| 接口 | 参数 / 返回约束 |','| --- | --- |',
 '| `ctx.Projects.ChooseFolderAsync` | 用户选择目录后返回项目 ID；重启或撤销 projects.read 后重新选择 |',
 '| `ctx.Projects.GetFiles / GetFileCount / GetTotalBytes / GetExtensionStats / GetLargestFiles` | 文件清单最多 20000 项；统计针对该有界清单；忽略 .git/node_modules/bin/obj/dist/build/.xcl2 和目录链接 |',
 '| `ProjectFiles.Search` | 最多 20000 文件 / 128 MiB 扫描量；`Truncated` 指示结果或扫描上限；每个正则匹配 100 ms 超时 |',
 '| `ProjectFiles.ReadPage` | 文本文件 ≤16 MiB，1–2000 行，输出 ≤80000 字符；`NextLine` 为续读行号，`Sha256` 为整文件版本 |',
 '| `ctx.Changes.Prepare*` | 只保存预览与完整备份；`ApplyAsync` 显示预览后提交；`RollbackAsync` 校验当前内容并确认回滚 |',
 '| `FileChange.State` | Prepared / Applying / Applied / RollingBack / RolledBack |',
 '| `WorkStep.Status` | pending / in_progress / completed / blocked；最多 100 步，同时最多一个 in_progress |',
 '| `WorkTask.Mode` | Plan / Build；任务元数据，不改变宿主授权或 AI 工作模式 |',
 '| `WorkTask.Status` | paused / running / completed / interrupted / archived |',
 '| `WorkTask.Revision` | 乐观并发版本；旧版本保存会拒绝覆盖 |','',
 '```csharp','var project = await ctx.Projects.ChooseFolderAsync(ctx.Stopping);','if (project is not null)','{','    var page = ctx.Projects.ReadRange(project.Id, "src/MainWindow.cs", 1, 120);','    var draft = ctx.Changes.PreparePatch(project.Id, "src/MainWindow.cs",','        page.Sha256, "old unique fragment", "new fragment");','    await ctx.Changes.ApplyAsync(project.Id, draft.Id, ctx.Stopping);','    var task = ctx.Tasks.Create("更新界面", "实现新布局", project.Root);','    ctx.Tasks.AddChange(task.Id, draft.Id);','    ctx.Tasks.Checkpoint(task.Id, "已提交变更，等待编译验证");','}','```','']
content='\n'.join(out)
for output in [ROOT/'API开放文件.md', ROOT/'Resources/Docs/API_REFERENCE.md']:
 output.write_text(content,encoding='utf-8')
(ROOT/'Tests/workbench-api-contracts.json').write_text(json.dumps(endpoints,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'new_api_count':len(endpoints),'public_types':len(types),'existing_exe_routes':len(cases),'document_bytes':len(content.encode())}))
