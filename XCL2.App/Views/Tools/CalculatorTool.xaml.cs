using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace XCL2.App.Views.Tools;

/// <summary>
/// 百宝箱「计算器」工具：经典四则运算计算器，纯本地计算、不联网。
///
/// 实现口径：
/// - 即时运算模型（不是表达式解析）：输入数字 → 按运算符（有挂起的运算就先算完）→
///   继续输下一个数 → 按 = 出结果。跟 Windows 自带计算器/手机计算器的标准交互一致。
/// - 键盘直接可用：数字/小数点/四则运算符/Enter/Backspace/Esc/% 都能按，
///   不需要点按钮（焦点在控件内任意位置即可，用 PreviewKeyDown 隧道事件统一接）。
/// </summary>
public partial class CalculatorTool : UserControl
{
    // ===== 计算状态 =====
    private readonly StringBuilder _entry = new("0");   // 当前正在输入的数字（字符串形式，避免连续追加浮点误差）
    private double _acc;                                 // 已累计的数值（挂起运算符的左操作数）
    private string? _op;                                 // 挂起的运算符：+ − × ÷
    private bool _fresh;                                 // 新数字开始输入（刚按过运算符/=，下一个数字键应从头起）

    public CalculatorTool()
    {
        InitializeComponent();
        RefreshDisplay();
    }

    private void CalculatorTool_Loaded(object sender, RoutedEventArgs e)
    {
        Keyboard.Focus(this); // 让键盘输入立即可用
    }

    private void DisplayBox_PreviewMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // ==================== 键盘输入 ====================

    private void CalculatorTool_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.D0 or Key.NumPad0: InputDigit('0'); e.Handled = true; break;
            case Key.D1 or Key.NumPad1: InputDigit('1'); e.Handled = true; break;
            case Key.D2 or Key.NumPad2: InputDigit('2'); e.Handled = true; break;
            case Key.D3 or Key.NumPad3: InputDigit('3'); e.Handled = true; break;
            case Key.D4 or Key.NumPad4: InputDigit('4'); e.Handled = true; break;
            case Key.D5 or Key.NumPad5: InputDigit('5'); e.Handled = true; break;
            case Key.D6 or Key.NumPad6: InputDigit('6'); e.Handled = true; break;
            case Key.D7 or Key.NumPad7: InputDigit('7'); e.Handled = true; break;
            case Key.D8 or Key.NumPad8: InputDigit('8'); e.Handled = true; break;
            case Key.D9 or Key.NumPad9: InputDigit('9'); e.Handled = true; break;
            case Key.OemPeriod or Key.Decimal: InputDigit('.'); e.Handled = true; break;
            case Key.Add or Key.OemPlus: InputOp("+"); e.Handled = true; break;
            case Key.Subtract or Key.OemMinus: InputOp("−"); e.Handled = true; break;
            case Key.Multiply: InputOp("×"); e.Handled = true; break;
            case Key.Divide or Key.OemQuestion: InputOp("÷"); e.Handled = true; break;
            case Key.Oem5: InputPercent(); e.Handled = true; break;   // 键盘上的 % 键
            case Key.Enter or Key.Return: InputEquals(); e.Handled = true; break;
            case Key.Back: InputBackspace(); e.Handled = true; break;
            case Key.Escape: InputClear(); e.Handled = true; break;
        }
    }

    // ==================== 按钮 ====================

    private void CalcKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        switch (tag)
        {
            case "C": InputClear(); break;
            case "Back": InputBackspace(); break;
            case "%": InputPercent(); break;
            case "±": InputNegate(); break;
            case "=": InputEquals(); break;
            case "+" or "−" or "×" or "÷": InputOp(tag); break;
            case ".": InputDigit('.'); break;
            default: InputDigit(tag[0]); break;   // 数字
        }
        Keyboard.Focus(this); // 点完按钮焦点还留在控件上，继续支持键盘
    }

    // ==================== 输入动作 ====================

    private void InputDigit(char c)
    {
        if (c is >= '0' and <= '9')
        {
            if (_fresh) { _entry.Clear(); _fresh = false; }
            // 去掉前导 0（"0123" → "123"；单独一个 0 保留）；上限 16 位防溢出显示
            if (_entry.Length == 1 && _entry[0] == '0') _entry.Clear();
            if (_entry.Length >= 16) return;
            _entry.Append(c);
        }
        else if (c == '.')
        {
            if (_fresh) { _entry.Clear().Append('0'); _fresh = false; }
            if (!_entry.ToString().Contains('.')) _entry.Append('.');
        }
        RefreshDisplay();
    }

    private void InputOp(string op)
    {
        if (Error()) return;
        // 已有挂起运算且用户没在输入新数（比如 3 + 4 ×）：先把 3+4 算掉再挂 ×
        if (_op != null && !_fresh) Evaluate();
        if (Error()) return; // 链式运算中途除零：直接停住，不让错误状态继续往下挂运算符
        _op = op;
        _acc = CurrentValue();
        _fresh = true;
        ExprText.Text = $"{FormatValue(_acc)} {op}";
        RefreshDisplay();
    }

    private void InputEquals()
    {
        if (_op == null || Error()) return;
        var rhs = CurrentValue();
        ExprText.Text = $"{FormatValue(_acc)} {_op} {FormatValue(rhs)} =";
        Evaluate();
        _fresh = true; // 结果之后再按数字键，从新数字开始，而不是接在结果后面
    }

    /// <summary>把当前挂起的运算算进 _acc（结果直接显示在主屏）。</summary>
    private void Evaluate()
    {
        var rhs = CurrentValue();
        var result = Apply(_acc, _op!, rhs);
        if (double.IsNaN(result) || double.IsInfinity(result))
        {
            SetError();
            return;
        }
        _acc = result;
        _op = null;
        // 把结果写回当前输入，这样紧接着按 %/± 作用在结果上，而不是旧的操作数
        _entry.Clear();
        _entry.Append(FormatValue(result));
        DisplayBox.Text = FormatValue(result);
    }

    private void InputPercent() => InputDigitFromValue(CurrentValue() / 100.0, _op);

    /// <summary>把某个数值直接作为当前输入（% 用）：沿用现有挂起运算符，避免"3 + 50%"变味。</summary>
    private void InputDigitFromValue(double value, string? keepOp)
    {
        if (Error()) return;
        _fresh = false;
        _entry.Clear();
        _entry.Append(FormatValue(value));
        if (keepOp != null) ExprText.Text = $"{FormatValue(_acc)} {keepOp}";
        RefreshDisplay();
    }

    private void InputNegate()
    {
        if (Error()) return;
        var v = CurrentValue();
        _fresh = false;
        _entry.Clear();
        _entry.Append(FormatValue(-v));
        RefreshDisplay();
    }

    private void InputBackspace()
    {
        if (Error() || _fresh) return;
        if (_entry.Length > 1) _entry.Length--;
        else if (_entry[0] != '0') _entry[0] = '0';
        RefreshDisplay();
    }

    private void InputClear()
    {
        _acc = 0;
        _op = null;
        _fresh = true;
        _entry.Clear().Append('0');
        ExprText.Text = "";
        DisplayBox.Text = "0";
        CalcStatusText.Text = "";
    }

    // ==================== 内部计算 ====================

    private double CurrentValue()
        => double.TryParse(_entry.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double Apply(double a, string op, double b) => op switch
    {
        "+" => a + b,
        "−" => a - b,
        "×" => a * b,
        "÷" => b == 0 ? double.NaN : a / b,
        _ => b,
    };

    private void SetError()
    {
        CalcStatusText.Text = "除数不能为 0（或结果超出范围）";
        DisplayBox.Text = "错误";
        _acc = 0;
        _op = null;
        _fresh = true;
        _entry.Clear().Append('0');
    }

    private bool Error() => DisplayBox.Text == "错误";

    private void RefreshDisplay()
    {
        if (!Error()) DisplayBox.Text = _entry.ToString();
    }

    /// <summary>数值显示格式：整数不带小数点；否则最多 15 位有效数字，去掉多余尾零。</summary>
    internal static string FormatValue(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "错误";
        var s = v.ToString("G15", CultureInfo.InvariantCulture);
        if (s.Contains('E') || s.Contains('e')) return s; // 科学计数法直接显示
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        return s;
    }
}
