using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using KyjScada.Domain.Config;
using KyjScada.Domain.Users;

namespace KyjScada.App;

/// <summary>
/// 急停/点动页。
///
/// 为什么列出"所有可写点"而不是只列 Bool:
///   真实现场配置里 52 个 Bool 全在「状态值」组,而该组默认没有可写标记,
///   只列 Bool 会导致页面上一个点都没有。这里改成列出所有闸门认可的可写点,
///   点动只对 Bool 开放(开/关两态),强制写对任何数值类型都可用。
///
/// 安全设计:
///  1. 急停 = 把清单里所有 Bool 可写点写 0,执行前弹确认并列出将受影响的点数
///  2. 点动 = 选中 Bool 点写 1 / 写 0,需 CanJog
///  3. 强制 = 写指定值并保持,需 CanForce + 确认弹窗
///  4. 所有写入走同一个 CommandGate(串行化/限速/审计),与参数设置页共享
/// </summary>
public partial class MainWindow
{
    // ==================== 状态 ====================

    /// <summary>急停/点动页的一行。</summary>
    private sealed class JogRow : INotifyPropertyChanged
    {
        // 必须是属性,不能是字段:WPF 绑定不认公开字段,
        // 写成字段的结果是表格整片空白且不报任何错(排查过一次,别再踩)。
        public string GroupName { get; set; } = "";
        public string VarName { get; set; } = "";
        public string VarType { get; set; } = "";
        public string Comments { get; set; } = "";
        public bool IsBool { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private string _currentText = "--";

        // 同 ValueRow:有通知才能去掉整表重画(Items.Refresh)
        public string CurrentText
        {
            get => _currentText;
            set
            {
                if (string.Equals(_currentText, value, StringComparison.Ordinal)) return;

                _currentText = value;

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentText)));
            }
        }

        /// <summary>下拉框没走模板时的兜底显示(SetValue/读屏都依赖它)。</summary>
        public override string ToString() => VarName;
    }

    private readonly List<JogRow> _jogRows = new();
    private bool _jogPageBuilt;

    /// <summary>点动按下/松开写的值(整寄存器 Bool:1/0)。</summary>
    private const double JogOnValue = 1.0;
    private const double JogOffValue = 0.0;

    /// <summary>急停写 0(写 0 = 停)。现场语义不同时应改组态而不是改代码。</summary>
    private const double EStopValue = 0.0;

    // ==================== 页面构建 ====================

    /// <summary>构急停/点动页(设备切换、权限变化时重建)。</summary>
    private void BuildJogPage()
    {
        _jogRows.Clear();
        _jogPageBuilt = true;

        if (_device is not null && _gate is not null)
        {
            // 候选集规则在 Runtime/JogPoints(可单测),这里只做视图映射
            foreach (var p in KyjScada.Runtime.JogPoints.Select (_device, _gate.IsWritable))
            {
                _jogRows.Add (new JogRow
                {
                    GroupName = p.GroupName,
                    VarName = p.VarName,
                    VarType = p.VarType,
                    Comments = p.Comments,
                    IsBool = p.IsBool
                });
            }
        }

        JogGrid.ItemsSource = null;
        JogGrid.ItemsSource = _jogRows;

        JogVarBox.ItemsSource = null;
        JogVarBox.DisplayMemberPath = "VarName";
        JogVarBox.ItemsSource = _jogRows;
        if (_jogRows.Count > 0) JogVarBox.SelectedIndex = 0;

        var boolCount = _jogRows.Count(r => r.IsBool);

        JogInfoText.Text = _jogRows.Count == 0
            ? "可写点 0 个。要在本页下发命令,先到「配置编辑器」把命令所在组勾上「允许下发写入」。"
            : "可写点 " + _jogRows.Count + " 个(其中 Bool " + boolCount + " 个)。"
              + "点动只对 Bool 有效,强制写对所有数值类型有效。";

        ApplyJogPermissions();
    }

    /// <summary>按权限开/关本页控件(界面禁用只是提示,事件里还会再校验一次)。</summary>
    private void ApplyJogPermissions()
    {
        var hasJog = HasPermission(PermissionBit.CanJog);
        var hasForce = HasPermission(PermissionBit.CanForce);
        var hasAny = _jogRows.Count > 0;

        if (EstopBtn is not null) EstopBtn.IsEnabled = hasJog && _jogRows.Any(r => r.IsBool);
        if (JogOnBtn is not null) JogOnBtn.IsEnabled = hasJog && hasAny;
        if (JogOffBtn is not null) JogOffBtn.IsEnabled = hasJog && hasAny;
        if (ForceWriteBtn is not null) ForceWriteBtn.IsEnabled = hasForce && hasAny;
        if (ForceValueBox is not null) ForceValueBox.IsEnabled = hasForce && hasAny;
        if (JogVarBox is not null) JogVarBox.IsEnabled = hasAny;

        // 与「参数设置」页共用同一个命令闸门,这里只显示/切换它的状态
        if (JogEnableBox is not null)
        {
            JogEnableBox.IsEnabled = hasJog;
            JogEnableBox.IsChecked = _gate?.Enabled == true;
        }

        if (JogGateText is not null)
        {
            JogGateText.Text = _gate?.Enabled == true ? "闸门已开" : "闸门已关";
        }
    }

    /// <summary>切换命令闸门。需要「点动」权限,否则勾上也立刻取消。</summary>
    private void OnJogEnableChanged(object sender, RoutedEventArgs e)
    {
        if (_gate is null) return;

        var wantOn = JogEnableBox.IsChecked == true;

        if (wantOn && !HasPermission(PermissionBit.CanJog))
        {
            JogEnableBox.IsChecked = false;

            JogResultText.Text = "✘ 没有「点动」权限,不能开启下发";
            return;
        }

        _gate.Enabled = wantOn;

        // 参数设置页的勾选框是同一个闸门,顺手同步,避免两页显示相反状态
        if (WriteEnableBox is not null) WriteEnableBox.IsChecked = wantOn;

        if (WriteInfoText is not null)
        {
            WriteInfoText.Text = wantOn ? "写入已启用" : "写入已关闭(闸门由急停/点动页关闭)";
        }

        JogGateText.Text = wantOn ? "闸门已开" : "闸门已关";
        JogResultText.Text = wantOn
            ? "写入已启用。命令会经串行化/限速后下发。"
            : "写入已关闭,急停/点动/强制都不会下发。";
    }

    // ==================== 事件 ====================

    /// <summary>选中的目标变量(没有则返回 null)。</summary>
    private JogRow? SelectedJogRow()
    {
        var row = JogVarBox.SelectedItem as JogRow;
        if (row is not null) return row;

        // 用户可能只在表格里点了行
        return JogGrid.SelectedItem as JogRow;
    }

    /// <summary>急停:清单里所有 Bool 可写点写 0,执行前确认。</summary>
    private async void OnEstop(object sender, RoutedEventArgs e)
    {
        if (_gate is null)
        {
            JogResultText.Text = "✘ 命令闸门未初始化";
            return;
        }

        if (!HasPermission(PermissionBit.CanJog))
        {
            JogResultText.Text = "✘ 没有「点动」权限,不能操作急停";
            return;
        }

        var targets = _jogRows.Where(r => r.IsBool).ToList();

        if (targets.Count == 0)
        {
            JogResultText.Text = "✘ 本设备没有可写的 Bool 点。急停需要一个可下发的 Bool(通常是把命令组勾上「允许下发写入」)。";
            return;
        }

        var preview = string.Join(", ", targets.Take(6).Select(t => t.VarName));
        if (targets.Count > 6) preview += " 等 " + targets.Count + " 个";

        var answer = MessageBox.Show(
            "确认执行急停?\n\n将把下列 Bool 可写点全部写为 0:\n" + preview +
            "\n\n这是立即下发动作,请确认设备处于允许急停的状态。",
            "急停确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        // 急停要连发 N 个写命令;断线时每个都要等超时,累计能把界面冻好几分钟。
        // 整批丢到后台线程做,界面只负责显示进度/结果。
        JogResultText.Text = "■ 正在执行急停(" + targets.Count + " 个点)…";

        var gate = _gate;
        var names = targets.Select(t => t.VarName).ToList();

        var (ok, fail) = await Task.Run(() =>
        {
            var good = 0;
            var bad = new List<string>();

            foreach (var name in names)
            {
                var r = gate.Write(name, EStopValue, "estop");
                if (r.Success) good++;
                else bad.Add(name + ": " + r.Message);
            }

            return (good, bad);
        }).ConfigureAwait(true);

        JogResultText.Text = "■ 急停已执行:成功 " + ok + "/" + names.Count
            + (fail.Count > 0 ? "\n失败: " + string.Join("; ", fail.Take(3)) : "");
    }

    /// <summary>点动开:选中 Bool 点写 1。</summary>
    private void OnJogOn(object sender, RoutedEventArgs e)
    {
        JogPoint(JogOnValue, "点动开");
    }

    /// <summary>点动关:选中 Bool 点写 0。</summary>
    private void OnJogOff(object sender, RoutedEventArgs e)
    {
        JogPoint(JogOffValue, "点动关");
    }

    /// <summary>点动公共实现:校验权限 + 类型后再下发。</summary>
    private async void JogPoint(double value, string action)
    {
        if (_gate is null) return;

        if (!HasPermission(PermissionBit.CanJog))
        {
            JogResultText.Text = "✘ 没有「点动」权限";
            return;
        }

        var row = SelectedJogRow();
        if (row is null)
        {
            JogResultText.Text = "✘ 请先在上方选择目标变量";
            return;
        }

        if (!row.IsBool)
        {
            JogResultText.Text = "✘ " + row.VarName + " 是 " + row.VarType + " 类型,点动只支持 Bool。数值点请用「强制写入」。";
            return;
        }

        // 同写入页:断线时写一帧要等超时,不能在界面线程上等
        JogResultText.Text = "正在下发「" + action + "」…";

        var gate = _gate;
        var name = row.VarName;

        var r = await Task.Run(() => gate.Write(name, value, "jog")).ConfigureAwait(true);

        JogResultText.Text = (r.Success ? "✔ " : "✘ ") + name + " " + action + ": " + r.Message;
    }

    /// <summary>强制写:CanForce + 确认弹窗。目标 = 选中变量,任何数值类型都可用。</summary>
    private async void OnForceWrite(object sender, RoutedEventArgs e)
    {
        if (_gate is null) return;

        if (!HasPermission(PermissionBit.CanForce))
        {
            JogResultText.Text = "✘ 没有「强制」权限(仅管理员/授权账号)";
            return;
        }

        var row = SelectedJogRow();
        if (row is null)
        {
            JogResultText.Text = "✘ 请先在上方选择目标变量";
            return;
        }

        if (!double.TryParse(ForceValueBox.Text, out var value))
        {
            JogResultText.Text = "强制值不是有效数字: " + ForceValueBox.Text;
            return;
        }

        var answer = MessageBox.Show(
            "确认强制写入?\n\n变量: " + row.VarName + "  (" + row.VarType + ")\n值: " + value +
            "\n\n强制写会保持该值直到下次修改,请确认设备状态允许。",
            "强制写确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        JogResultText.Text = "正在强制写入…";

        var gate = _gate;
        var name = row.VarName;

        var r = await Task.Run(() => gate.Write(name, value, "force")).ConfigureAwait(true);

        JogResultText.Text = (r.Success ? "✔ " : "✘ ") + name + " 强制: " + r.Message;
    }

    /// <summary>刷新当前值(页签可见时)。</summary>
    private void TickJogPage()
    {
        if (MainTabs.SelectedIndex != 6 || _jogRows.Count == 0) return;

        var snap = _bus.GetSnapshot();

        foreach (var row in _jogRows)
        {
            var tv = snap[row.VarName];
            var d = tv?.AsDouble();

            row.CurrentText = d.HasValue
                ? (row.IsBool ? (d.Value != 0 ? "ON" : "OFF") : d.Value.ToString("G6"))
                : "--";
        }

        // 不用 Items.Refresh():JogRow 已实现 INotifyPropertyChanged,只重画变化的单元格
    }
}