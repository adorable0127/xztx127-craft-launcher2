using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;
// 注意：这个文件里所有裸写的 Color 都特意指 System.Drawing.Color（WinForms 托盘图标/
// 菜单要的颜色类型），不是 WPF 的 System.Windows.Media.Color——两者同名，csproj 里已经
// 把 UseWindowsForms 附带的全局 System.Drawing using 去掉了，所以这里保留上面这行显式
// using System.Drawing; 就不会跟 WPF 的 Color 打架。唯一用到的 WPF 类型 SolidColorBrush
// 改成写全名，不再整体 using System.Windows.Media，避免它把 Color 也带进来冲突。

namespace XCL2.App.Services;

/// <summary>
/// 系统托盘图标服务：主窗口"最小化到托盘"或"点叉号时的默认行为=最小化到托盘"后，
/// 窗口本身 Hide()，改由这里常驻的 NotifyIcon 维持进程可见性入口。
///
/// 用 System.Windows.Forms.NotifyIcon 而不是纯 Win32 P/Invoke，是因为 .NET 官方支持
/// WPF 项目里混用 WinForms（只需 csproj 加 UseWindowsForms），NotifyIcon 本身封装好了
/// Shell_NotifyIcon 的一整套消息循环，比自己重新拼一遍 P/Invoke 可靠得多。
///
/// 右键菜单没有用 WinForms 原生 ContextMenuStrip 的默认外观（那是浅灰色 Win32 系统菜单，
/// 跟 XCL2 自己的深/浅主题完全对不上），而是在构造时从 Application.Current.Resources 里
/// 读取当前主题的 PanelBrush/TextPrimaryBrush/AccentBrush 等颜色，用 Renderer 重绘菜单
/// 背景/文字/悬停高亮，让托盘菜单尽量跟随启动器当前主题（浅色/深色皮肤切换后，下次
/// 右键弹出菜单时会用最新颜色，因为每次弹出前都会 Refresh 一次配色，不是构造时固定死）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _showItem;
    private readonly WinForms.ToolStripMenuItem _launchItem;
    private readonly WinForms.ToolStripMenuItem _exitItem;

    /// <summary>右键菜单「显示主界面」被点击。</summary>
    public event Action? ShowMainRequested;

    /// <summary>右键菜单「启动正在选定的游戏 / 登录」被点击。</summary>
    public event Action? LaunchSelectedRequested;

    /// <summary>右键菜单「退出」被点击，或双击图标以外的真正退出请求。</summary>
    public event Action? ExitRequested;

    /// <summary>左键单击 / 双击图标本身（约定：单击和双击都视为"回到主界面"，
    /// 跟绝大多数托盘类软件的直觉一致——不用非得记住"要双击才能唤出"）。</summary>
    public event Action? IconActivated;

    public TrayIconService(string launchItemText = "启动正在选定的游戏 / 登录")
    {
        _menu = new WinForms.ContextMenuStrip
        {
            ShowImageMargin = false,
            RenderMode = WinForms.ToolStripRenderMode.Professional
        };

        _showItem = new WinForms.ToolStripMenuItem("显示主界面");
        _launchItem = new WinForms.ToolStripMenuItem(launchItemText);
        var separator = new WinForms.ToolStripSeparator();
        _exitItem = new WinForms.ToolStripMenuItem("退出");

        _showItem.Click += (_, _) => ShowMainRequested?.Invoke();
        _launchItem.Click += (_, _) => LaunchSelectedRequested?.Invoke();
        _exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _menu.Items.Add(_showItem);
        _menu.Items.Add(_launchItem);
        _menu.Items.Add(separator);
        _menu.Items.Add(_exitItem);

        ApplyTheme();

        _icon = new WinForms.NotifyIcon
        {
            Text = "XCL2",
            Visible = false,
            ContextMenuStrip = _menu
        };
        _icon.MouseClick += (_, e) =>
        {
            // 只处理左键：右键交给 ContextMenuStrip 自己弹出，这里不用重复处理。
            if (e.Button == WinForms.MouseButtons.Left)
                IconActivated?.Invoke();
        };
        // 弹出右键菜单前重新上色一次，覆盖"托盘图标在后台常驻期间用户切换了主题"的情况。
        _menu.Opening += (_, _) => ApplyTheme();
    }

    /// <summary>从当前 WPF 主题资源里取色，套到 WinForms 菜单的自绘 Renderer 上。
    /// 找不到对应资源（理论上不会，除非资源字典改了 key）时静默跳过，退回 WinForms 默认灰色，
    /// 不能因为取色失败就让整个托盘菜单崩掉。</summary>
    private void ApplyTheme()
    {
        try
        {
            Color panel = ReadColor("PanelBrush", System.Drawing.Color.FromArgb(255, 255, 255, 255));
            Color text = ReadColor("TextPrimaryBrush", System.Drawing.Color.FromArgb(255, 21, 27, 38));
            Color accent = ReadColor("AccentBrush", System.Drawing.Color.FromArgb(255, 24, 104, 232));
            Color border = ReadColor("BorderBrush2", System.Drawing.Color.FromArgb(255, 214, 226, 240));

            _menu.Renderer = new ThemedMenuRenderer(panel, text, accent, border);
            _menu.BackColor = panel;
            _menu.ForeColor = text;
            foreach (WinForms.ToolStripItem item in _menu.Items)
            {
                item.ForeColor = text;
                item.BackColor = panel;
            }
        }
        catch
        {
            // 取色失败不影响托盘菜单基本功能，忽略即可。
        }
    }

    private static Color ReadColor(string resourceKey, Color fallback)
    {
        if (Application.Current?.Resources[resourceKey] is System.Windows.Media.SolidColorBrush brush)
        {
            var c = brush.Color;
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        return fallback;
    }

    /// <summary>显示托盘图标。图标资源直接复用主程序 exe 自带的图标，避免额外打包一份 .ico
    /// 还要处理路径找不到的问题（GetExecutablePath 在极少数场景，比如通过某些沙箱/单文件
    /// 发布方式启动时可能不可靠，所以做了 try/catch 兜底成系统默认应用图标，不能因为
    /// 图标取不到就让托盘功能整体不可用）。</summary>
    public void Show()
    {
        if (_icon.Icon == null)
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                    _icon.Icon = Icon.ExtractAssociatedIcon(exePath);
            }
            catch { /* 忽略，下面兜底 */ }
            _icon.Icon ??= SystemIcons.Application;
        }
        _icon.Visible = true;
    }

    public void Hide() => _icon.Visible = false;

    /// <summary>更新「启动正在选定的游戏 / 登录」菜单项文字，比如把版本号也带上，
    /// 让用户不用先展开主界面就知道右键菜单点下去会启动哪个版本。</summary>
    public void UpdateLaunchItemText(string text) => _launchItem.Text = text;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    /// <summary>极简版菜单 Renderer：背景用主题 PanelBrush，文字用 TextPrimaryBrush，
    /// 鼠标悬停/选中项用 AccentBrush 高亮（白字），边框用 BorderBrush2。
    /// 不追求完全复刻 XCL2 主界面里那套自定义按钮的圆角/阴影效果——WinForms 原生菜单
    /// 本身没有那么强的自定义能力，这里做到"配色跟随主题"已经是合理的"尽量"程度。</summary>
    private sealed class ThemedMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        private readonly Color _panel;
        private readonly Color _text;
        private readonly Color _accent;
        private readonly Color _border;

        public ThemedMenuRenderer(Color panel, Color text, Color accent, Color border)
            : base(new ThemedColorTable(panel, accent, border))
        {
            _panel = panel;
            _text = text;
            _accent = accent;
            _border = border;
        }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Color.White : _text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(_border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }

    private sealed class ThemedColorTable : WinForms.ProfessionalColorTable
    {
        private readonly Color _panel;
        private readonly Color _accent;
        private readonly Color _border;

        public ThemedColorTable(Color panel, Color accent, Color border)
        {
            _panel = panel;
            _accent = accent;
            _border = border;
        }

        public override Color ToolStripDropDownBackground => _panel;
        public override Color ImageMarginGradientBegin => _panel;
        public override Color ImageMarginGradientMiddle => _panel;
        public override Color ImageMarginGradientEnd => _panel;
        public override Color MenuBorder => _border;
        public override Color MenuItemBorder => _accent;
        public override Color MenuItemSelected => _accent;
        public override Color MenuItemSelectedGradientBegin => _accent;
        public override Color MenuItemSelectedGradientEnd => _accent;
        public override Color MenuItemPressedGradientBegin => _accent;
        public override Color MenuItemPressedGradientEnd => _accent;
        public override Color SeparatorDark => _border;
        public override Color SeparatorLight => _border;
    }
}
