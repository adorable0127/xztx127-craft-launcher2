using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XCL2.App.Models;

public enum TouchControlMode { Tap, Hold, Toggle, Command }
public enum TouchControlVisibility { Always, Game, Menu }
public enum TouchControlAnchor { TopLeft, TopRight, BottomLeft, BottomRight, BottomCenter }
public enum TouchMenuAction { None, Toggle, Open, Close }

/// <summary>坐标以客户区 DIP 为单位；右/下锚点的 X/Y 是距右/下边缘的距离。</summary>
public sealed class TouchControlDefinition : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "新按键";
    private string _binding = "G";
    private TouchControlMode _mode = TouchControlMode.Tap;
    public string Binding { get => _binding; set => SetField(ref _binding, value); }
    public TouchControlMode Mode { get => _mode; set => SetField(ref _mode, value); }
    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    public TouchMenuAction MenuAction { get; set; }
    public TouchControlVisibility Visibility { get; set; } = TouchControlVisibility.Game;
    public TouchControlAnchor Anchor { get; set; }
    public double X { get; set; } = 24;
    public double Y { get; set; } = 80;
    public double Width { get; set; } = 62;
    public double Height { get; set; } = 62;
    public int ZIndex { get; set; }
    public bool Enabled { get; set; } = true;
    public TouchControlDefinition Copy()
    {
        var copy = (TouchControlDefinition)MemberwiseClone();
        copy.PropertyChanged = null;
        return copy;
    }
}
