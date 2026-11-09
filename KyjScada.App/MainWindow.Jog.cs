using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using KyjScada.Domain.Config;
using KyjScada.Domain.Users;
using KyjScada.Runtime;

namespace KyjScada.App;

/// <summary>
/// 急停/点动页:只做 Bool 开关量(写 1 / 写 0),没有任何数值输入框。
///
/// 安全设计:
///  1. 急停 = 把清单里所有 Bool 可写点写 0,执行前弹确认并列出将受影响的点数
///  2. 点动 = 选中 Bool 点写 1 / 写 0,需 CanJog
///  3. 所有写入走同一个 CommandGate(串行化/限速/审计),与参数设置页共享
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
            // 只留 Bool:数值点在本页既没有开/关语义,也没有可填的值
            foreach (var p in KyjScada.Runtime.JogPoints.Select (_device, _gate.IsWritable).Where (p => p.IsBool))
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

        JogInfoText.Text = _jogRows.Count == 0
            ? EmptyJogHint ()
            : "可点动的 Bool 点 " + _jogRows.Count + " 个(点动开/关,急停全部写 0)。";

        ApplyJogPermissions();
    }

    /// <summary>
    /// 有命令正在下发(急停/点动/强制写)。这几个动作都是"确认 → 后台 await → 回填结果",
    /// 中间界面完全可点:连点两次会同时下发两批写命令,还会互相覆盖结果文字。
    /// </summary>
    private bool _jogBusy;

    /// <summary>
    /// 正在用代码回填 JogEnableBox.IsChecked。WPF 里**代码赋值也会触发 Checked/Unchecked**,
    /// 不挡住的话每次刷新权限都会走一遍 OnJogEnableChanged,
    /// 把"上一条命令的结果"覆盖成"写入已启用",还会连带改参数设置页的勾选框。
    /// </summary>
    private bool _syncingGate;

    /// <summary>
    /// 一个可点动的 Bool 点都没有时,说清"为什么没有、该去勾哪个组"。
    ///
    /// 只说一句"去配置编辑器勾允许写入"现场根本不知道勾哪儿:
    /// 本设备的 Bool 全在「状态值」组,而该组默认不是可写组,
    /// 用户看到的就是"0 个",以为功能坏了。
    /// </summary>
    private string EmptyJogHint()
    {
        if (_device is null) return "未选择设备,无法列出可点动的 Bool 点。";

        var locked = KyjScada.Runtime.JogPoints.LockedBoolGroups (_device);

        if (locked.Count == 0)
        {
            return "本设备的组里没有 Bool 类型变量:点动/急停只对 Bool 有意义,数值点请到「参数设置」页写入。";
        }

        var names = string.Join ("」「", locked.Take (4));

        return "可点动的 Bool 点 0 个。本设备还有 Bool 点分布在「" + names
            + "」组,这些组没有开放写入 —— 要到本页下发命令,先到「配置编辑器」把对应组勾上「允许下发写入」。"
            + "(寄存器区(4xxxx)的 Bool 即使勾了也写不了,需改用 0xxxx 线圈)";
    }

    /// <summary>按权限开/关本页控件(界面禁用只是提示,事件里还会再校验一次)。</summary>
    private void ApplyJogPermissions()
    {
        var hasJog = HasPermission(PermissionBit.CanJog);
        var hasAny = _jogRows.Count > 0;
        var idle = !_jogBusy;

        if (EstopBtn is not null) EstopBtn.IsEnabled = hasJog && idle && hasAny;
        if (JogOnBtn is not null) JogOnBtn.IsEnabled = hasJog && idle && hasAny;
        if (JogOffBtn is not null) JogOffBtn.IsEnabled = hasJog && idle && hasAny;
        if (JogVarBox is not null) JogVarBox.IsEnabled = hasAny && idle;

        // 与「参数设置」页共用同一个命令闸门,这里只显示/切换它的状态
        if (JogEnableBox is not null)
        {
            JogEnableBox.IsEnabled = hasJog && idle;

            _syncingGate = true;

            try
            {
                JogEnableBox.IsChecked = _gate?.Enabled == true;
            }
            finally
            {
                _syncingGate = false;
            }
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

        // 程序回填勾选状态时不要把用户/上一次命令的结果刷掉
        if (_syncingGate) return;

        if (_jogBusy)
        {
            JogResultText.Text = "✘ 命令正在下发,稍后再切换闸门";
            ApplyJogPermissions();
            return;
        }

        var wantOn = JogEnableBox.IsChecked == true;

        if (wantOn && !HasPermission(PermissionBit.CanJog))
        {
            _syncingGate = true;

            try
            {
                JogEnableBox.IsChecked = false;
            }
            finally
            {
                _syncingGate = false;
            }

            JogResultText.Text = "✘ 没有「点动」权限,不能开启下发";
            return;
        }

        // 闸门只有一个:两页的勾选框、两处提示文字都在 SetGateEnabled 里统一回填
        SetGateEnabled (wantOn);

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

        if (_jogBusy)
        {
            JogResultText.Text = "✘ 上一条命令还在下发,请稍候";
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

        _jogBusy = true;
        ApplyJogPermissions ();

        try
        {
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
        catch (Exception ex)
        {
            // async void 里没有 catch 会直接终止进程,按钮也会永久停在 disabled
            JogResultText.Text = "■ 急停执行异常: " + ex.Message;
        }
        finally
        {
            _jogBusy = false;
            ApplyJogPermissions ();
        }
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

        if (_jogBusy)
        {
            JogResultText.Text = "✘ 上一条命令还在下发,请稍候";
            return;
        }

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
            JogResultText.Text = "✘ " + row.VarName + " 是 " + row.VarType + " 类型,点动只支持 Bool。数值写入请到「参数设置」页。";
            return;
        }

        // 同写入页:断线时写一帧要等超时,不能在界面线程上等
        JogResultText.Text = "正在下发「" + action + "」…";

        var gate = _gate;
        var name = row.VarName;

        _jogBusy = true;
        ApplyJogPermissions ();

        try
        {
            var r = await Task.Run(() => gate.Write(name, value, "jog")).ConfigureAwait(true);

            JogResultText.Text = (r.Success ? "✔ " : "✘ ") + name + " " + action + ": " + r.Message;
        }
        catch (Exception ex)
        {
            JogResultText.Text = "✘ " + name + " 点动异常: " + ex.Message;
        }
        finally
        {
            _jogBusy = false;
            ApplyJogPermissions ();
        }
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
                ? (d.Value != 0 ? "ON" : "OFF")
                : "--";
        }

        // 不用 Items.Refresh():JogRow 已实现 INotifyPropertyChanged,只重画变化的单元格
    }
}