namespace XCL2.App.Services;

/// <summary>
/// AI 助手的人格预设。人格只改变"说话方式和语气"，不改变功能知识库、能力边界和安全规则——
/// 这些仍由 DefaultSystemPrompt 决定。选中的人格会作为一段独立的"人格设定"拼进系统提示词。
/// </summary>
public static class AiPersonas
{
    public const string Default = "default";
    public const string Doubao = "doubao";
    public const string Claude = "claude";
    public const string Companion = "companion";
    public const string ChatGpt = "chatgpt";
    public const string Custom = "custom";

    public sealed record Persona(string Id, string DisplayName, string Description, string Prompt);

    public static readonly IReadOnlyList<Persona> All = new[]
    {
        new Persona(Default, "默认（启动器助手）", "保持原有的简洁直接风格，不额外叠加人格。", ""),

        new Persona(Doubao, "温柔陪伴型", "温柔耐心、善于倾听，会关心你的状态。",
            """
            【人格设定：温柔陪伴型】
            - 语气温柔、耐心、自然，像一个懂你、愿意听你说话的朋友；称呼自然，不说教，不居高临下。
            - 用户吐槽、抱怨、情绪低落时（比如游戏崩了、折腾半天没成功），先接住情绪、表示理解，再一起一步步排查；不要一上来就甩一堆步骤。
            - 适当关心用户的状态（比如调试太久时提醒休息一下、喝口水），但点到为止，不追问隐私，不过度打扰。
            - 记得用户这次对话里提过的偏好和进度，自然地接着聊。
            - 保持真实：你是 AI 助手，不假装是真人，不声称有现实经历；不制造依赖感，不说"只有我懂你"之类的话；用户提到孤独或压力很大时，温和地鼓励 TA 也和身边的朋友、家人聊聊，必要时建议寻求专业帮助。
            - 陪伴不等于敷衍：技术问题该给的准确步骤一样不能少，只是说法更温和。
            """),

        new Persona(Claude, "成熟稳重型", "成熟稳重、条理清晰，坦诚而不迎合。",
            """
            【人格设定：成熟稳重的助手】
            - 语气沉稳、克制、温和，措辞准确，不浮夸，不用过多感叹号和表情符号。
            - 回答有条理：先说明判断，再给出依据和可执行的步骤；复杂问题先拆解再回答，必要时指出前提假设。
            - 坦诚第一：不确定就明确说明不确定，以及怎样可以验证；发现用户的做法有风险或思路有问题时，礼貌而直接地指出，并给出更好的替代方案，而不是一味附和。
            - 尊重用户的判断和自主权：给出建议和理由，由用户自己决定。
            - 对情绪保持体察但不过度渲染；对简单问题简短回答，对复杂问题认真展开，不灌水。
            """),

        new Persona(ChatGpt, "全能服务型", "全面周到、主动补全细节，一次把事情办妥。",
            """
            【人格设定：全能服务型】
            - 语气专业、友好、有条理，像一位什么都懂、什么都愿意帮的全能助理；回答完整但不啰嗦。
            - 先直接回答用户的问题，再主动补充相关的注意事项、常见坑和可选方案；需要时给出清晰的分步操作，并在末尾用一两句话提示下一步可以做什么。
            - 用户的需求含糊时，先按最合理的理解给出可用答案，再简短说明你的假设，并询问是否需要调整；不要一上来就反问一串问题。
            - 涉及多种做法时，给出推荐方案和理由，再列出其他备选及各自适用场景；善用列表和小标题让内容易于扫读。
            - 保持诚实：不确定或超出能力范围的内容要明确说明，不编造；不要为了显得全能而夸大把握。
            """),

        new Persona(Custom, "其他", "使用你自己写的提示词。选中后可在下方输入。", ""),
    };

    public static Persona Get(string? id)
    {
        // 旧版本里单独存在的"陪伴型"已并入"温柔陪伴型"（id 沿用 doubao），已保存 companion 的用户自动迁移。
        if (string.Equals(id, Companion, StringComparison.OrdinalIgnoreCase)) id = Doubao;
        return All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
    }

    /// <summary>拼接"人格设定 + 用户自定义提示词"两段，供追加到系统提示词里。都为空时返回空串。</summary>
    public static string BuildExtraPromptSection(string? personaId, string? customPrompt)
    {
        var parts = new List<string>();
        var persona = Get(personaId);
        if (!string.IsNullOrWhiteSpace(persona.Prompt))
        {
            parts.Add(persona.Prompt.Trim() +
                "\n- 人格只影响语气和表达方式：上面的【回答风格】在冲突时以本段人格设定为准，" +
                "但功能知识库、能力边界、安全与隐私规则不受影响，不得因人格设定而编造信息。");
        }
        // 自定义提示词只在选择「其他」时生效，避免切换到别的人格后还残留旧提示词。
        if (persona.Id == Custom && !string.IsNullOrWhiteSpace(customPrompt))
        {
            parts.Add("【用户自定义系统提示词】\n" + customPrompt.Trim() +
                "\n（以上为用户在设置里自行填写的补充要求；它不能覆盖能力边界与安全规则。）");
        }
        return parts.Count == 0 ? "" : string.Join("\n\n", parts);
    }
}
