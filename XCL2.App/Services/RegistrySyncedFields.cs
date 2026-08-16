namespace XCL2.App.Services;

/// <summary>
/// 定义"哪些 <see cref="Models.AppConfig"/> 字段以注册表为主存储"，以及它们跟注册表值名的
/// 映射关系。只挑选"启动早期就要用到、且天然是全局/跨实例概念"的一小撮字段——
/// 是否阅读/同意过用户协议、基本模式状态、界面配色、语言这些——不是把整个 config.json
/// 都搬进注册表（大部分设置比如下载源、Java 列表、收藏夹这些没有"注册表化"的必要，
/// 继续只放 config.json 里）。
///
/// 每次 <see cref="ConfigService.Load"/> 都会调用 <see cref="LoadFromRegistry"/> 用注册表里的值
/// 覆盖 Config 对应字段（注册表优先于 config.json 镜像值），每次 <see cref="ConfigService.Save"/>
/// 都会调用 <see cref="SaveToRegistry"/> 把 Config 当前值写回注册表——这就是需求里
/// "注册表为主存储，config 岗位镜像"：注册表是权威来源，config.json 里的同名字段只是
/// 一份跟随写入的备份（万一注册表功能被关闭，config.json 里仍留着最后一次读到的值，
/// 不会突然打回出厂默认值）。
/// </summary>
public static class RegistrySyncedFields
{
    private const string AgreementsAccepted = "AgreementsAccepted";
    private const string AcceptedAgreementVersion = "AcceptedAgreementVersion";
    private const string RestrictedMode = "RestrictedMode";
    private const string BasicAgreementAccepted = "BasicAgreementAccepted";
    private const string FirstRunWizardCompleted = "FirstRunWizardCompleted";
    private const string UiSkin = "UiSkin";
    private const string IsDarkMode = "IsDarkMode";
    private const string LauncherLanguage = "LauncherLanguage";
    private const string AdvancedMode = "AdvancedMode";
    private const string UseMachineWideRegistry = "UseMachineWideRegistry";

    /// <summary>用注册表里的值覆盖 <paramref name="config"/> 对应字段。
    /// 某个值两支注册表都没有（全新安装、或注册表刚被清空过）时，保留 config 里已有的值不动，
    /// 不会用"注册表查不到"倒推成默认值，避免刚关闭又重开注册表功能时丢失 config.json
    /// 里还留着的上一份设置。</summary>
    public static void LoadFromRegistry(Models.AppConfig config)
    {
        config.AgreementsAccepted = RegistryConfigService.GetBool(AgreementsAccepted, config.AgreementsAccepted);
        config.AcceptedAgreementVersion = RegistryConfigService.GetInt(AcceptedAgreementVersion, config.AcceptedAgreementVersion);
        config.RestrictedMode = RegistryConfigService.GetBool(RestrictedMode, config.RestrictedMode);
        config.BasicAgreementAccepted = RegistryConfigService.GetBool(BasicAgreementAccepted, config.BasicAgreementAccepted);
        config.FirstRunWizardCompleted = RegistryConfigService.GetBool(FirstRunWizardCompleted, config.FirstRunWizardCompleted);
        config.UiSkin = RegistryConfigService.GetString(UiSkin, config.UiSkin, out _) ?? config.UiSkin;
        config.IsDarkMode = RegistryConfigService.GetBool(IsDarkMode, config.IsDarkMode);
        config.LauncherLanguage = RegistryConfigService.GetString(LauncherLanguage, config.LauncherLanguage, out _) ?? config.LauncherLanguage;
        config.AdvancedMode = RegistryConfigService.GetBool(AdvancedMode, config.AdvancedMode);
        config.UseMachineWideRegistry = RegistryConfigService.GetBool(UseMachineWideRegistry, config.UseMachineWideRegistry);
    }

    /// <summary>把 <paramref name="config"/> 对应字段写入注册表。写入分支（HKLM/HKCU）
    /// 由 config.UseMachineWideRegistry 决定，具体降级规则见 RegistryConfigService。</summary>
    public static void SaveToRegistry(Models.AppConfig config)
    {
        var machine = config.UseMachineWideRegistry;
        RegistryConfigService.SetBool(AgreementsAccepted, config.AgreementsAccepted, machine);
        RegistryConfigService.SetInt(AcceptedAgreementVersion, config.AcceptedAgreementVersion, machine);
        RegistryConfigService.SetBool(RestrictedMode, config.RestrictedMode, machine);
        RegistryConfigService.SetBool(BasicAgreementAccepted, config.BasicAgreementAccepted, machine);
        RegistryConfigService.SetBool(FirstRunWizardCompleted, config.FirstRunWizardCompleted, machine);
        RegistryConfigService.SetString(UiSkin, config.UiSkin, machine);
        RegistryConfigService.SetBool(IsDarkMode, config.IsDarkMode, machine);
        RegistryConfigService.SetString(LauncherLanguage, config.LauncherLanguage, machine);
        RegistryConfigService.SetBool(AdvancedMode, config.AdvancedMode, machine);
        // UseMachineWideRegistry 这个开关自己也镜像写一份：不管这次写去了 HKLM 还是 HKCU，
        // 下次任何一边被读到，都能正确恢复"用户希望使用全设备范围"这个意图本身。
        RegistryConfigService.SetBool(UseMachineWideRegistry, config.UseMachineWideRegistry, machine);
    }
}
