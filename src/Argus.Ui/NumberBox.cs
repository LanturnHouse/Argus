using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Argus.Ui;

/// <summary>
/// 숫자 직접 입력 칸: 입력 상자 오른쪽에 위·아래 화살표가 있어 정해진 단위(<see cref="Step"/>)로 올리고 내린다.
/// 정수 항목은 Decimals=0 · Step=1, 소수 항목은 Decimals=1 · Step=0.1 처럼 쓴다. Shift 를 누른 채 누르면 10배 단위로 움직인다.
/// 범위를 벗어난 입력은 가까운 끝값으로 맞추고, 숫자가 아닌 입력은 이전 값으로 되돌린다.
/// </summary>
[TemplatePart(Name = "PART_Text", Type = typeof(TextBox))]
[TemplatePart(Name = "PART_Up", Type = typeof(RepeatButton))]
[TemplatePart(Name = "PART_Down", Type = typeof(RepeatButton))]
public class NumberBox : Control
{
    static NumberBox() => DefaultStyleKeyProperty.OverrideMetadata(typeof(NumberBox), new FrameworkPropertyMetadata(typeof(NumberBox)));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(NumberBox),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((NumberBox)d).OnValueChanged(), (d, v) => ((NumberBox)d).Coerce((double)v)));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(NumberBox), new PropertyMetadata(0.0, (d, _) => d.CoerceValue(ValueProperty)));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(NumberBox), new PropertyMetadata(100.0, (d, _) => d.CoerceValue(ValueProperty)));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(NumberBox), new PropertyMetadata(1.0));
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(NumberBox), new PropertyMetadata(0, (d, _) => ((NumberBox)d).SyncText()));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(NumberBox), new PropertyMetadata(""));
    public static readonly DependencyProperty TextWidthProperty = DependencyProperty.Register(nameof(TextWidth), typeof(double), typeof(NumberBox), new PropertyMetadata(72.0));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
    /// <summary>입력 칸 오른쪽에 붙는 단위 글자 (예: "초", "%").</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double TextWidth { get => (double)GetValue(TextWidthProperty); set => SetValue(TextWidthProperty, value); }

    /// <summary>값이 바뀌었을 때 (직접 입력, 화살표, 코드에서 설정 모두).</summary>
    public event EventHandler? ValueChanged;

    private TextBox? _text;

    private double Coerce(double v)
    {
        var max = Math.Max(Minimum, Maximum);
        return Math.Round(Math.Clamp(v, Minimum, max), Math.Clamp(Decimals, 0, 6));
    }

    private void OnValueChanged() { SyncText(); ValueChanged?.Invoke(this, EventArgs.Empty); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_text != null) { _text.LostKeyboardFocus -= Text_LostFocus; _text.PreviewKeyDown -= Text_KeyDown; _text.PreviewMouseWheel -= Text_Wheel; }
        _text = GetTemplateChild("PART_Text") as TextBox;
        if (_text != null) { _text.LostKeyboardFocus += Text_LostFocus; _text.PreviewKeyDown += Text_KeyDown; _text.PreviewMouseWheel += Text_Wheel; }
        if (GetTemplateChild("PART_Up") is RepeatButton up) up.Click += (_, _) => Nudge(+1);
        if (GetTemplateChild("PART_Down") is RepeatButton down) down.Click += (_, _) => Nudge(-1);
        SyncText();
    }

    private string Format(double v) => v.ToString("F" + Math.Clamp(Decimals, 0, 6), CultureInfo.InvariantCulture);

    private void SyncText() { if (_text != null) _text.Text = Format(Value); }

    /// <summary>위(+1)·아래(-1) 한 단계. Shift 를 누르고 있으면 10 단계.</summary>
    private void Nudge(int direction)
    {
        CommitText();
        var factor = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        Value = Value + direction * Step * factor;
    }

    /// <summary>입력 칸의 글자를 값으로 확정한다. 숫자가 아니면 이전 값으로 되돌린다.</summary>
    private void CommitText()
    {
        if (_text == null) return;
        var s = _text.Text.Trim().Replace(',', '.');
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) Value = v;
        SyncText();
    }

    private void Text_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitText();

    private void Text_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: CommitText(); _text!.SelectAll(); e.Handled = true; break;
            case Key.Escape: SyncText(); e.Handled = true; break;
            case Key.Up: Nudge(+1); e.Handled = true; break;
            case Key.Down: Nudge(-1); e.Handled = true; break;
        }
    }

    /// <summary>입력 칸에 포커스가 있을 때만 마우스 휠로 한 단계씩 (페이지 스크롤과 겹치지 않게).</summary>
    private void Text_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (_text is not { IsKeyboardFocused: true }) return;
        Nudge(e.Delta > 0 ? +1 : -1);
        e.Handled = true;
    }
}
