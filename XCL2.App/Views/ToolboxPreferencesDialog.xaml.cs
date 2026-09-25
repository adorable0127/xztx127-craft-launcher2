using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;

namespace XCL2.App.Views;

public partial class ToolboxPreferencesDialog : OverlayDialogControl
{
    private readonly AppConfig _cfg;
    private readonly List<(int Id, string Name)> _ordered;
    public ToolboxPreferencesDialog(AppConfig cfg)
    {
        _cfg = cfg;
        InitializeComponent();
        // 同工具箱 TabItem 顺序；只存 ID，不存翻译后的标题。
        string[] names = { "成就图片", "皮肤头像", "文件下载", "加载器下载", "系统工具", "计算器", "桌面便签", "电脑清理", "服务器测速", "坐标换算", "颜色代码编辑器", "系统内存监视", "离线UUID生成器", "JVM参数生成器", "资源包校验", "快捷键速查", "随机种子", "种子结构查询", "经验等级计算", "坐标距离", "指令生成器", "Base64编解码", "游戏时间换算", "堆叠格子", "RGB颜色转换", "红石延时", "随机玩家名", "MOTD长度检查", "时间戳", "服务器地址解析", "药水时长", "颜色代码速查", "附魔书架计算", "数据包互转", "Web2", "下载正版账户皮肤" };
        var indices = (cfg.ToolboxPinnedTabOrder ?? new List<int>()).Where(i => i >= 0 && i < names.Length).Distinct().ToList();
        indices.AddRange(Enumerable.Range(0, names.Length).Except(indices));
        _ordered = indices.Select(i => (i, names[i])).ToList();
        VisibleCountCombo.SelectedIndex = Math.Clamp(cfg.ToolboxVisibleCount, 1, 8) - 1;
        Refresh();
    }
    private void Refresh(int index = 0)
    {
        ToolOrderList.Items.Clear();
        foreach (var tool in _ordered) ToolOrderList.Items.Add($"{ToolboxPage.ToolboxIcon(tool.Id)} {tool.Name}");
        ToolOrderList.SelectedIndex = Math.Clamp(index, 0, _ordered.Count - 1);
    }
    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int delta)
    {
        var i = ToolOrderList.SelectedIndex;
        if (i < 0 || i + delta < 0 || i + delta >= _ordered.Count) return;
        (_ordered[i], _ordered[i + delta]) = (_ordered[i + delta], _ordered[i]);
        Refresh(i + delta);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWith(false);
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _cfg.ToolboxVisibleCount = VisibleCountCombo.SelectedIndex + 1;
        _cfg.ToolboxPinnedTabOrder = _ordered.Select(x => x.Id).ToList();
        CloseWith(true);
    }
}
