using System;

namespace XCL2.App.Services;

/// <summary>限制工具请求数量，不授予文件、Shell 或系统权限。0 不限次数。</summary>
internal sealed class AiToolActionBudget
{
    internal int Limit { get; }
    private int _started;

    internal AiToolActionBudget(int configuredLimit) => Limit = Math.Max(0, configuredLimit);

    internal bool TryStart()
    {
        // Unlimited turns do not increment a counter; there is no overflow-induced cutoff.
        if (Limit == 0) return true;
        if (_started >= Limit) return false;
        _started++;
        return true;
    }

    internal static string DescribeLimit(int configuredLimit) => configuredLimit <= 0
        ? "不限次数（用户可随时停止；仍受既有权限和超时约束）"
        : configuredLimit.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 次";
}
