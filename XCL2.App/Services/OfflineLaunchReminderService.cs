using System.Diagnostics;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

public static class OfflineLaunchReminderService
{
    private const string RegistryPath = @"SOFTWARE\XCL2";
    private const string SuppressValueName = "jzlzde";
    private const string DonateUrl = "https://ifdian.net/a/xztx127";
    private const string BuyUrl = "https://www.xbox.com/zh-CN/games/store/minecraft-java-bedrock-edition-for-pc/9NXP44L49SHJ";

    public static void OnOfflineLaunch(AppConfig config)
    {
        if (IsSuppressed() || config.DonationAcknowledged) return;
        config.OfflineLaunchCount++;
        var count = config.OfflineLaunchCount;

        if (count >= 25 && (count == 25 || (count - 25) % 50 == 0))
        {
            if (MessageBoxDialog.ShowConfirm("你正在使用离线账户启动。若已拥有正版可忽略；支持正版可以获得官方联机和账户服务。是否前往 Xbox 购买 Minecraft？", "支持正版"))
                OpenUrl(BuyUrl);
            return;
        }

        if (count > 25 && (count - 25) % 10 == 0)
        {
            if (MessageBoxDialog.ShowConfirm("本项目一直保持开源。愿意捐助一次支持维护吗？捐助后将不再提示。", "支持 XCL2"))
            {
                config.DonationAcknowledged = true;
                OpenUrl(DonateUrl);
            }
        }
    }

    private static bool IsSuppressed()
    {
        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(RegistryPath, false);
            using var user = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            return Convert.ToInt32(machine?.GetValue(SuppressValueName, user?.GetValue(SuppressValueName, 0)) ?? 0) == 1;
        }
        catch { return false; }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
