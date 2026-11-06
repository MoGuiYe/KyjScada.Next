using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KyjScada.App;

/// <summary>
/// 批量阈值对话框:统一设置上/下限、使能位与死区。
///
/// 设计取舍:
///  1. 每项都有独立勾选框 —— 只想改死区时不能顺带把上下限清零。
///  2. 默认按整组生效,勾掉"整组"就只作用于所选变量。
///  3. 深色底上必须显式设前景色,否则输入框文字是黑的、看不见。
/// </summary>
public sealed class BatchThresholdWindow : Window
{
    private readonly TextBox _high;
    private readonly TextBox _low;
    private readonly TextBox _dead;
    private readonly CheckBox _setHigh;
    private readonly CheckBox _setLow;
    private readonly CheckBox _setDead;
    private readonly CheckBox _highOn;
    private readonly CheckBox _lowOn;
    private readonly CheckBox _all;
    private readonly TextBlock _error;

    public bool SetHigh => _setHigh.IsChecked == true;
    public bool SetLow => _setLow.IsChecked == true;
    public bool SetDeadband => _setDead.IsChecked == true;

    public float HighValue { get; private set; }
    public float LowValue { get; private set; }
    public float Deadband { get; private set; }

    public bool HighEnable => _highOn.IsChecked == true;
    public bool LowEnable => _lowOn.IsChecked == true;
    public bool AllInGroup => _all.IsChecked == true;

    public BatchThresholdWindow(int groupVarCount)
    {
        Title = "批量设置报警阈值";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb (0x18, 0x18, 0x25));
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness (16) };

        panel.Children.Add (new TextBlock
        {
            Text = "给所选变量(" + groupVarCount + " 个)统一设置报警阈值",
            Foreground = new SolidColorBrush (Color.FromRgb (0xFF, 0xFF, 0xFF)),
            FontSize = 13,
            Margin = new Thickness (0, 0, 0, 12)
        });

        _setHigh = AddCheckRow (panel, "设置上限", true);
        _high = AddNumberRow (panel, "上限值", "100");
        _highOn = AddCheckRow (panel, "上限报警启用", true, 24);

        _setLow = AddCheckRow (panel, "设置下限", true);
        _low = AddNumberRow (panel, "下限值", "0");
        _lowOn = AddCheckRow (panel, "下限报警启用", true, 24);

        _setDead = AddCheckRow (panel, "设置死区", true);
        _dead = AddNumberRow (panel, "死区", "0");

        panel.Children.Add (new TextBlock
        {
            Text = "死区 0 = 不启用。阈值附近反复穿越会产生大量抖动报警。",
            Foreground = new SolidColorBrush (Color.FromRgb (0xC6, 0xCD, 0xE0)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness (0, 2, 0, 8)
        });

        _all = new CheckBox
        {
            Content = "作用于本组全部变量(不勾则只改所选变量)",
            IsChecked = true,
            Foreground = new SolidColorBrush (Color.FromRgb (0xFF, 0xFF, 0xFF)),
            Margin = new Thickness (0, 4, 0, 12)
        };

        panel.Children.Add (_all);

        _error = new TextBlock
        {
            Foreground = new SolidColorBrush (Color.FromRgb (0xFC, 0xA5, 0xA5)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 18
        };

        panel.Children.Add (_error);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness (0, 8, 0, 0)
        };

        buttons.Children.Add (MakeButton ("确定", 76, OnOk, "#2A3A2A", "#FFFFFF"));
        buttons.Children.Add (MakeButton ("取消", 76, (s, e) => { DialogResult = false; }, "#313244", "#FFFFFF"));

        panel.Children.Add (buttons);

        Content = panel;
    }

    private static CheckBox AddCheckRow (Panel panel, string text, bool value, double top = 0)
    {
        var cb = new CheckBox
        {
            Content = text,
            IsChecked = value,
            Foreground = new SolidColorBrush (Color.FromRgb (0xFF, 0xFF, 0xFF)),
            Margin = new Thickness (0, top, 0, 4)
        };

        panel.Children.Add (cb);

        return cb;
    }

    private static TextBox AddNumberRow (Panel panel, string label, string init)
    {
        var row = new DockPanel { Margin = new Thickness (24, 0, 0, 6) };

        var lb = new TextBlock
        {
            Text = label + ":",
            Width = 70,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush (Color.FromRgb (0xC6, 0xCD, 0xE0)),
            FontSize = 12
        };

        var tb = new TextBox
        {
            Text = init,
            Width = 150,
            Background = new SolidColorBrush (Color.FromRgb (0x11, 0x11, 0x1B)),
            Foreground = new SolidColorBrush (Colors.White),
            BorderBrush = new SolidColorBrush (Color.FromRgb (0x45, 0x47, 0x5A)),
            Padding = new Thickness (4, 2, 4, 2)
        };

        DockPanel.SetDock (lb, Dock.Left);
        row.Children.Add (lb);
        row.Children.Add (tb);

        panel.Children.Add (row);

        return tb;
    }

    private static Button MakeButton (string text, double width, RoutedEventHandler click, string back, string fore)
    {
        var b = new Button
        {
            Content = text,
            Width = width,
            Height = 26,
            Margin = new Thickness (6, 0, 0, 0),
            Background = new SolidColorBrush ((Color)ColorConverter.ConvertFromString (back)),
            Foreground = new SolidColorBrush ((Color)ColorConverter.ConvertFromString (fore)),
            BorderBrush = new SolidColorBrush (Color.FromRgb (0x45, 0x47, 0x5A)),
            BorderThickness = new Thickness (1)
        };

        b.Click += click;

        return b;
    }

    private void OnOk (object sender, RoutedEventArgs e)
    {
        var hi = 0f;
        var lo = 0f;
        var db = 0f;

        if (SetHigh && !TryParse (_high.Text, out hi))
        {
            _error.Text = "上限值不是数字: " + _high.Text;

            return;
        }

        if (SetLow && !TryParse (_low.Text, out lo))
        {
            _error.Text = "下限值不是数字: " + _low.Text;

            return;
        }

        if (SetDeadband && !TryParse (_dead.Text, out db))
        {
            _error.Text = "死区不是数字: " + _dead.Text;

            return;
        }

        if (SetHigh) HighValue = hi;
        if (SetLow) LowValue = lo;
        if (SetDeadband) Deadband = db;

        if (SetHigh && SetLow && LowValue >= HighValue)
        {
            _error.Text = "下限必须小于上限(下限 " + LowValue + ",上限 " + HighValue + ")";

            return;
        }

        if (SetDeadband && Deadband < 0)
        {
            _error.Text = "死区不能是负数";

            return;
        }

        DialogResult = true;
    }

    private static bool TryParse (string? text, out float value)
    {
        return float.TryParse ((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}