namespace XCL2.App.Services;

/// <summary>
/// 定义“哪些 <see cref="Models.AppConfig"/> 字段额外同步一份到注册表”，以及它们跟注册表值名的
/// 映射关系。AppData JSON 已经是主存储；这里只保留兼容/灾难恢复镜像。只挑选
/// “启动早期就要用到、且天然是全局/跨实例概念”的一小撮字段——
/// 是否阅读/同意过用户协议、基本模式状态、界面配色、语言这些——不是把整个 config.json
/// 都搬进注册表（大部分设置比如下载源、Java 列表、收藏夹这些没有"注册表化"的必要，
/// 继续只放 config.json 里）。
///
/// 正常 <see cref="ConfigService.Load"/> 只读取 AppData/JSON，不允许注册表反向覆盖；只有所有 JSON
/// 副本都不可用时，才调用 <see cref="LoadFromRegistry"/> 做灾难恢复。每次 <see cref="ConfigService.Save"/>
/// 仍会调用 <see cref="SaveToRegistry"/> 写兼容镜像，因此老版本/人工排障仍能读取这些值。
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
    private const string CustomBackgroundImagePath = "CustomBackgroundImagePath";
    private const string CustomBackgroundFrostPercent = "CustomBackgroundFrostPercent";
    private const string EnableWindowTransparency = "EnableWindowTransparency";
    private const string WindowOpacityPercent = "WindowOpacityPercent";

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
        // 需求："背景，磨砂都，透明度保存在注册表中"——切换背景后这几项也要经注册表镜像持久化，
        // 不只是留在 config.json 里。字符串/DWORD 都可能因为极端配置文件损坏而拿到异常值，
        // 交给 AppConfig 各自属性 setter/后续使用处的 Clamp 兜底，这里只管原样读回。
        config.CustomBackgroundImagePath = RegistryConfigService.GetString(CustomBackgroundImagePath, config.CustomBackgroundImagePath, out _) ?? config.CustomBackgroundImagePath;
        config.CustomBackgroundFrostPercent = RegistryConfigService.GetInt(CustomBackgroundFrostPercent, config.CustomBackgroundFrostPercent);
        config.EnableWindowTransparency = RegistryConfigService.GetBool(EnableWindowTransparency, config.EnableWindowTransparency);
        config.WindowOpacityPercent = RegistryConfigService.GetInt(WindowOpacityPercent, config.WindowOpacityPercent);
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
        // 背景图片路径/磨砂度/窗口透明度开关与百分比：见 LoadFromRegistry 同名字段注释。
        // 路径可能为 null（用户清除了自定义背景），SetString 要求非空字符串，这里退化写空串；
        // 读回时空串会被 LoadFromRegistry 当成"没有自定义背景路径"，跟 null 语义等价。
        RegistryConfigService.SetString(CustomBackgroundImagePath, config.CustomBackgroundImagePath ?? string.Empty, machine);
        RegistryConfigService.SetInt(CustomBackgroundFrostPercent, config.CustomBackgroundFrostPercent, machine);
        RegistryConfigService.SetBool(EnableWindowTransparency, config.EnableWindowTransparency, machine);
        RegistryConfigService.SetInt(WindowOpacityPercent, config.WindowOpacityPercent, machine);
    }
}
