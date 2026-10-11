using System.Text.Json.Serialization;

namespace XCL2.App.Models;

/// <summary>单类动画的用户偏好；强度 0 等同于关闭，100 为默认效果，与播放速度无关。</summary>
public sealed class AnimationOption
{
    private double _intensityPercent = 100;

    public bool Enabled { get; set; } = true;

    // 人工编辑过的配置可能含非有限值，恢复默认值以免把 NaN 传给动画或滑块。
    [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    public double IntensityPercent
    {
        get => _intensityPercent;
        set => _intensityPercent = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 100;
    }
}
