using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>Windows 当前用户 DPAPI；公开账户 JSON 只保留非凭据信息。不提供插件读取接口。</summary>
internal static class AccountSecretStore
{
    private sealed record Secrets(string? Refresh, string? Access, string? Client);
    [StructLayout(LayoutKind.Sequential)] private struct Blob { internal int Length; internal IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static string Vault(string accountsPath) => Path.Combine(Path.GetDirectoryName(accountsPath)!, "account-secrets.dpapi");
    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var memory = Marshal.AllocHGlobal(bytes.Length); var output = default(Blob);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length); var input = new Blob { Length = bytes.Length, Data = memory };
            var ok = encrypt ? CryptProtectData(ref input, "XCL2 account credentials", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "账户安全存储不可用。");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero) { for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(memory, i, 0);
            Marshal.FreeHGlobal(memory);
        }
    }
    internal static bool Restore(string accountsPath, IEnumerable<Account> accounts)
    {
        var list = accounts.ToArray();
        var legacy = list.Any(a => a.MsRefreshToken != null || a.MinecraftAccessToken != null || a.AuthServerClientToken != null);
        if (legacy) return true; // 先让 Save 完成加密，成功之后才清除旧明文。
        var path = Vault(accountsPath); if (!File.Exists(path)) return false;
        byte[]? json = null;
        try
        {
            json = Protect(File.ReadAllBytes(path), false);
            var saved = JsonSerializer.Deserialize<Dictionary<string, Secrets>>(json) ?? new();
            foreach (var account in list)
                if (saved.TryGetValue(account.Id, out var secret))
                { account.MsRefreshToken = secret.Refresh; account.MinecraftAccessToken = secret.Access; account.AuthServerClientToken = secret.Client; }
        }
        catch (Exception ex) { LauncherLogService.AppendLine("[Accounts] CredentialRestoreFailed: " + ex.GetType().Name + "; 请重新登录受影响的账户。"); }
        finally { if (json != null) CryptographicOperations.ZeroMemory(json); }
        return false;
    }
    internal static void Save(string accountsPath, IEnumerable<Account> accounts, JsonSerializerOptions options)
    {
        var list = accounts.ToArray();
        var secrets = list.GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => { var a = g.Last(); return new Secrets(a.MsRefreshToken, a.MinecraftAccessToken, a.AuthServerClientToken); }, StringComparer.Ordinal);
        var plain = JsonSerializer.SerializeToUtf8Bytes(secrets);
        try { AtomicWrite(Vault(accountsPath), Protect(plain, true)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var safe = JsonSerializer.SerializeToNode(list, options)!.AsArray();
        foreach (var node in safe)
        {
            var item = node!.AsObject(); item["MsRefreshToken"] = null; item["MinecraftAccessToken"] = null; item["AuthServerClientToken"] = null;
        }
        AtomicWrite(accountsPath, System.Text.Encoding.UTF8.GetBytes(safe.ToJsonString(options)));
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
