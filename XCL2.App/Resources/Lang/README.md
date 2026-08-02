# XCL2 启动器界面多语言资源说明（写给继续扩展这部分的开发者/AI）

## 这是什么，不是什么

这套资源字典管的是**启动器 UI 本身**的文案（首页、按钮、弹窗标题这些）。
它跟 `AppConfig.GameLanguage`（游戏内 Minecraft 客户端显示什么语言，`zh_cn`/`en_us`
这种 options.txt 格式）**完全是两回事**，不要混用、不要把这两个配置项合并。

- 启动器 UI 语言 = `AppConfig.LauncherLanguage`，本目录下的资源字典负责渲染。
- 游戏内语言 = `AppConfig.GameLanguage`，跟 Minecraft 客户端本体有关，在别的地方处理。

## 当前进度（重要：文案覆盖范围还很不完整）

这一批只搭好了框架 + 语言选择入口 + 首页/顶部这几个"少数关键界面"的文案，
**其余几十个页面/弹窗（版本选择、下载中心、Mod管理、服务端管理等等）的文案
目前仍是硬编码中文，没有资源化**。切换到非中文语言时，这些还没资源化的地方
会继续显示中文——这是已知的、有意为之的当前状态，不是 bug。

后续要扩展覆盖范围时，请按下面"如何新增一个可翻译文案"的步骤，逐个页面补，
不需要、也不建议一次性全项目大改（改动量和回归风险都太大）。

## 支持的语言列表

见 `Services/LocalizationService.cs` 里的 `SupportedLanguages`。目前包括：
简体中文(zh-Hans，默认)、繁体中文(zh-Hant)、英语-美国(en-US)、德语(de-DE)、
法语(fr-FR)、意大利语(it-IT)、瑞典语(sv-SE)、日语(ja-JP)、韩语(ko-KR)、
粤语(yue-Hant，用繁体字书写，这是目前 Windows/主流系统对粤语书面语的通行处理方式)。

语言选择弹窗（`LanguageSelectWindow`）里每种语言都用"该语言自己的名字"显示
（比如英语显示 "English (United States)" 而不是"英语"），这是国际惯例，
不要改成用当前 UI 语言去翻译语言名本身。

## 每个语言一份资源字典文件

`Lang.zh-Hans.xaml`、`Lang.en-US.xaml` ... 每个文件是一个独立的
`ResourceDictionary`，key 相同（比如 `Str_Home_Welcome`），value 是对应语言的文案。

**所有语言文件的 key 集合必须完全一致**——`LocalizationService` 启动时会做一次
校验（Debug 模式下），发现某语言缺了某个 key 会在输出窗口打印警告，缺失的 key
在运行时会 fallback 到简体中文（不会崩溃，也不会显示英文/空字符串）。

## 命名约定

`Str_<所在区域>_<用途>`，例如：
- `Str_Home_Welcome` = 首页欢迎语
- `Str_Home_Toggle_DarkMode` / `Str_Home_Toggle_LightMode`
- `Str_Lang_DialogTitle` = 语言选择弹窗标题

## 如何新增一个可翻译文案（给以后扩展别的页面用）

1. 在 `Lang.zh-Hans.xaml` 里加一条 `<system:String x:Key="Str_XXX_YYY">中文文案</system:String>`。
2. 在**其余每一个** `Lang.*.xaml` 文件里加同一个 key 的对应语言翻译
   （暂时没有真实译文的，可以先写英文或者留 TODO 注释，但 key 本身不能缺）。
3. XAML 里把原来的 `Text="中文文案"` 改成 `Text="{DynamicResource Str_XXX_YYY}"`。
   用 `DynamicResource`（不是 `StaticResource`），否则运行时切换语言不会生效。
4. 如果是 code-behind 里拼接的字符串（不是 XAML 里的静态文本），改用
   `LocalizationService.GetString("Str_XXX_YYY")` 取值，同样是运行时查询、
   会随语言切换自动变化（调用方如果自己缓存了这个字符串，需要在
   `LocalizationService.LanguageChanged` 事件里重新取一次）。

## 切换语言时界面为什么会立即刷新

跟 `ThemeService`（换肤）是同一套刷新机制：切换语言时把资源字典整个替换掉
（`Application.Current.Resources.MergedDictionaries` 里换一份），然后调用
`ThemeService` 里现成的窗口刷新逻辑，强制当前所有已打开窗口重新从资源字典取值。
详见 `LocalizationService.Apply` 的注释。
