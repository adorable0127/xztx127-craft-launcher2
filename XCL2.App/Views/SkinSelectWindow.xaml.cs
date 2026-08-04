using System.Windows;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 离线账户的皮肤选择窗口：史蒂夫/艾利克斯内置骨架二选一，或上传自定义 PNG。
/// 交接文档 Round14 #1：史蒂夫/艾利克斯不需要下载任何文件，只是标记账户的
/// SkinType；自定义皮肤才需要真正拷贝文件（通过 SkinService.SaveCustomSkin），
/// 并要求用户在这一步就勾选清楚是否为纤细手臂(Alex)骨架。
/// </summary>
public partial class SkinSelectWindow : OverlayDialogControl
{
    private readonly Account _account;
    private readonly SkinService _skinService;
    private string? _pendingSourcePngPath;

    public SkinSelectWindow(Account account, SkinService skinService)
    {
        _account = account;
        _skinService = skinService;
        InitializeComponent();

        switch (account.SkinType)
        {
            case OfflineSkinType.Alex:
                AlexRadio.IsChecked = true;
                break;
            case OfflineSkinType.Custom:
                CustomRadio.IsChecked = true;
                break;
            default:
                SteveRadio.IsChecked = true;
                break;
        }

        SlimArmCheck.IsChecked = account.CustomSkinSlim;
        if (account.SkinType == OfflineSkinType.Custom && !string.IsNullOrEmpty(account.CustomSkinPath))
            SkinFileNameText.Text = System.IO.Path.GetFileName(account.CustomSkinPath);
    }

    private void SkinTypeRadio_Checked(object sender, RoutedEventArgs e)
    {
        // 控件在 InitializeComponent 完成前不会触发这个事件，但为保险起见还是判空。
        if (CustomPanelBorder == null) return;
        CustomPanelBorder.Visibility = CustomRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseSkin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PNG 图片|*.png|所有文件|*.*",
            Title = "选择皮肤图片(PNG)"
        };
        if (dialog.ShowDialog() != true) return;

        _pendingSourcePngPath = dialog.FileName;
        SkinFileNameText.Text = System.IO.Path.GetFileName(dialog.FileName);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        if (SteveRadio.IsChecked == true)
        {
            _skinService.RemoveCustomSkin(_account.Id);
            _account.SkinType = OfflineSkinType.Steve;
            _account.CustomSkinPath = null;
            _account.CustomSkinSlim = false;
        }
        else if (AlexRadio.IsChecked == true)
        {
            _skinService.RemoveCustomSkin(_account.Id);
            _account.SkinType = OfflineSkinType.Alex;
            _account.CustomSkinPath = null;
            _account.CustomSkinSlim = false;
        }
        else // Custom
        {
            // 允许"没有重新选择文件、但之前已经有自定义皮肤"的情况直接保存（比如只是切换纤细手臂勾选）。
            if (_pendingSourcePngPath == null && string.IsNullOrEmpty(_account.CustomSkinPath))
            {
                ShowError("请先选择一张 PNG 皮肤图片。");
                return;
            }

            if (_pendingSourcePngPath != null)
            {
                try
                {
                    _account.CustomSkinPath = _skinService.SaveCustomSkin(_account.Id, _pendingSourcePngPath);
                }
                catch (Exception ex)
                {
                    ShowError($"保存皮肤文件失败：{ex.Message}");
                    return;
                }
            }

            _account.SkinType = OfflineSkinType.Custom;
            _account.CustomSkinSlim = SlimArmCheck.IsChecked == true;
        }

        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(false);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
