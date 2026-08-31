using System;
using System.Collections.Generic;
using System.Linq;

namespace XCL2.App.Services;

/// <summary>
/// 省 Token 模式的一部分：一些问法固定、答案就是"一个链接/一个专有名词"的极简单问题，
/// 命中就直接本地答，不请求 API——这类问题模型答对也不会比查表更好，纯粹是白花 token。
/// 只在 AiAssistantConfig.EffectiveTokenSaverMode 为 true 时才会被
/// <see cref="AiAssistantService.SendMessageAsync"/> 拿来匹配，不影响没开这个模式的用户。
///
/// 匹配策略：每个条目登记若干"触发词"，用户消息里包含任意一个触发词、且消息本身足够短
/// （不超过 MaxTriggerMessageLength）才算命中——这个长度限制是为了避免"我朋友让我把 MC 官网
/// 分享给他，另外我这边启动器提示 xxx 错误，帮我看看"这种长消息被误判成简单问题。
/// </summary>
public static class AiQuickAnswers
{
    private const int MaxTriggerMessageLength = 20;

    private sealed record Entry(string[] Triggers, string Answer);

    // 链接来源：作者本人在设置对话里提供的原始列表。
    // “作者捐助链接” 这一项作者还没给具体 URL，先不登记，避免瞎编一个假链接。
    private static readonly List<Entry> Entries = new()
    {
        new(new[] { "mc官网", "minecraft官网", "我的世界官网" }, "Minecraft 官网：https://minecraft.net"),
        new(new[] { "捐助", "赞助", "爱发电", "支持作者" }, "作者爱发电（捐助/赞助）：https://ifdian.net/a/xztx127"),
        new(new[] { "mc百科", "mc百科官网", "mcmod" }, "MC 百科（MCMOD）：https://mcmod.cn"),
        new(new[] { "mc中文wiki", "mc中文百科", "minecraft wiki", "mc wiki" }, "Minecraft 中文 Wiki：https://zh.minecraft.wiki"),
        new(new[] { "下载地址", "启动器下载", "xcl2下载", "在哪下载" }, "XCL2 启动器下载地址：https://xztx127.dpdns.org/download"),
        new(new[] { "作者mc服务器", "作者的服务器", "作者服务器地址" }, "作者的 Minecraft 服务器：xztx127mc.dpdns.org"),
        new(new[] { "作者mc服务器官网", "服务器官网" }, "作者 Minecraft 服务器官网：https://www.xztx127mc.dpdns.org"),
        new(new[] { "作者b站", "up主b站", "作者哔哩哔哩" }, "作者 B 站：https://space.bilibili.com/3546834976902089"),
        new(new[] { "github", "开源地址", "源码地址" }, "开源仓库：https://github.com/adorable0127/xztx127-craft-launcher2"),
        new(new[] { "其他发布页面", "别的发布渠道", "还有哪里能下载" }, "其他发布页面：https://xztx127.dpdns.org"),
    };

    /// <summary>命中就返回答案文本；没命中返回 null（调用方应继续走正常的 API 请求流程）。</summary>
    public static string? TryAnswer(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return null;
        if (userText.Length > MaxTriggerMessageLength) return null;

        var normalized = userText.Trim().ToLowerInvariant();
        foreach (var entry in Entries)
        {
            if (entry.Triggers.Any(t => normalized.Contains(t.ToLowerInvariant())))
                return entry.Answer;
        }
        return null;
    }
}
