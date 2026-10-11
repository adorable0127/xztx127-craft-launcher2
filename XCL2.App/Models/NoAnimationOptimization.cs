namespace XCL2.App.Models;

/// <summary>完全无动画时的资源取舍，不改变用户保存的各类动画偏好。</summary>
public enum NoAnimationOptimization
{
    Memory = 0,
    Cpu = 1,
    Balanced = 2
}
