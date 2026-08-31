using System;
using System.Windows;
using System.Windows.Controls;

namespace XCL2.App.Views;

/// <summary>
/// 打开 AI 助手时的使用条款弹窗。只在 AiAssistantConfig.AcceptedTermsVersion 小于
/// <see cref="CurrentVersion"/> 时才需要弹出（见 AiAssistantConfig.AcceptedTermsVersion 注释）。
/// 用户点"同意"，调用方负责把 AcceptedTermsVersion 写成 CurrentVersion 并保存配置；
/// 点"不同意"就不进入 AI 助手页面，配置不变，下次打开还会再问。
/// </summary>
public partial class AiTermsDialog : UserControl, IOverlayDialog
{
    /// <summary>条款版本号。内容有实质性修改（比如新增一类免责事项）才加 1；
    /// 纯文字润色不需要动，不然会让已经同意过的老用户莫名其妙又被弹一次。</summary>
    public const int CurrentVersion = 1;

    public event EventHandler<bool?>? RequestClose;

    public AiTermsDialog()
    {
        InitializeComponent();
        LegalText.Text = AiTermsText.Legal;
        PlainText.Text = AiTermsText.PlainLanguage;
    }

    private void Agree_Click(object sender, RoutedEventArgs e) => RequestClose?.Invoke(this, true);

    private void Decline_Click(object sender, RoutedEventArgs e) => RequestClose?.Invoke(this, false);
}

/// <summary>条款文案单独放一个类，方便以后改文案不用去翻 XAML。</summary>
public static class AiTermsText
{
    public const string Legal = """
一、内容性质。AI 助手在本启动器内输出的全部对话内容，均由生成式人工智能模型自动生成，
不代表启动器作者本人的观点、承诺或事实陈述，亦不构成任何形式的专业建议（包括但不限于法律、
医疗、财务建议）。AI 输出可能存在错误、遗漏或与实际情况不符的情形，请自行判断并核实后再采取
相应行动，因依赖 AI 输出内容造成的后果由使用者自行承担。

二、服务提供方。本 AI 助手接入的模型由第三方服务商提供，并非由启动器作者本人训练、拥有或在本地
部署；启动器作者不对第三方服务商的模型能力、输出内容、服务可用性、数据处理方式作任何保证。第三方
服务商可能依据其自身政策记录、处理你发送的对话内容，具体以该服务商的相关条款为准。

三、费用。若你选择在设置中使用自己的 API Key 接入第三方服务，由此产生的调用费用由你自行向对应
服务商承担，与启动器作者无关；启动器内置的默认接口目前免费提供，但不保证长期免费或持续可用。

四、条款变更。本条款可能随功能调整而更新，更新后会在你下次打开 AI 助手时重新展示。继续使用即视为
同意更新后的条款；不同意的，可以随时在设置中关闭 AI 助手功能。
""";

    public const string PlainLanguage = """
说人话版（跟上面正文表达的是同一个意思，方便直接看这个）：

· AI 说的话都是 AI 自己现编的，不是作者说的，也不是官方结论——AI 也会说错、说漏，重要的事自己再核实一下。
· 这个 AI 不是作者自己训的模型、也没部署在作者自己的电脑/服务器上，是接第三方的服务；你的聊天内容
  会经过那家第三方，具体他们怎么处理数据，得看他们自己的规定，作者管不着也不背书。
· 如果你在设置里填了自己的 API Key，那这个 Key 产生的调用费用是你自己出的，跟作者没关系；
  启动器自带的默认接口现在是免费的，但不保证永远免费/永远能用。
· 以后这份条款要是有实质性改动，会再弹一次给你看；不想用 AI 助手的话，设置里随时可以关掉。
""";
}
