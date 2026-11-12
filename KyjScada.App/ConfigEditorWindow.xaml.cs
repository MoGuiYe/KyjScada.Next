using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KyjScada.Domain.Config;
using KyjScada.Infrastructure.Config;

namespace KyjScada.App;

/// <summary>
/// 配置编辑器。
///
/// 设计取舍:
///  1. **独立窗口**而不是再加一个页签 —— 改配置是低频、需要专注的操作,
///     和监控画面挤在一起容易误点;而且改完要重启采集才生效,天然是"另开一屏"。
///  2. **属性面板动态生成**:字段按节点类型不同而不同,写死 20 个控件反而更乱。
///  3. **保存前强制校验**:配置错了不会报错,只会静默读错值,必须在保存时拦。
/// </summary>
public partial class ConfigEditorWindow : Window
{
    /// <summary>配置目录(同目录下的全部 *.json 都可编辑)。</summary>
    private readonly string _dir;

    /// <summary>当前正在编辑的文件(可切换)。</summary>
    private string _currentPath;

    private List<ProjectConfig> _projects = new();

    /// <summary>当前选中的节点。</summary>
    private ConfigNode? _selected;

    /// <summary>选中节点对应的 TreeViewItem(改名后同步标题用)。</summary>
    private TreeViewItem? _selectedItem;

    private bool _dirty;

    /// <summary>切换文件时抑制 FileBox 的 SelectionChanged 递归。</summary>
    private bool _switchingFile;

    public ConfigEditorWindow(string settingsPath)
    {
        InitializeComponent();

        _dir = Path.GetDirectoryName(settingsPath) ?? AppContext.BaseDirectory;
        _currentPath = settingsPath;

        InitFileList();
        Load();

        // 右上角 X 直接关窗口时也要拦一下。
        // 内嵌的属性面板是"边打字边写进对象"的,关掉窗口不会自动落盘;
        // 原来只有"切换文件"有确认,于是调完一整屏阈值、顺手点 X,
        // 改的东西全没了而且没有任何提示。
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_dirty) return;

        var r = MessageBox.Show (this, "有未保存的修改,关闭后会丢失。仍要关闭?",
            "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (r != MessageBoxResult.Yes) e.Cancel = true;
    }

    /// <summary>
    /// 列出配置目录下所有 *.json。现场是按协议分文件的 ——
    /// ModbusTCP / ModbusRTU / ModbusRTUOverTCP,以后还会有 OPC、西门子、三菱等,
    /// 所以编辑器必须能切换,不能写死一个文件名。
    /// </summary>
    private void InitFileList()
    {
        _switchingFile = true;

        FileBox.Items.Clear();

        try
        {
            var files = Directory.GetFiles(_dir, "*.json")
                .Where(f => !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .Where(f => f is not null)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var f in files) FileBox.Items.Add(f);

            var current = Path.GetFileName(_currentPath);

            FileBox.SelectedItem = files.Contains(current) ? current : (files.Count > 0 ? files[0] : null);
        }
        catch
        {
            // 目录读不了就留空,后续 Load 会报具体错误
        }

        _switchingFile = false;
    }

    private void OnFileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingFile) return;

        var name = FileBox.SelectedItem as string;

        if (string.IsNullOrEmpty(name)) return;

        var next = Path.Combine(_dir, name);

        if (string.Equals(next, _currentPath, StringComparison.OrdinalIgnoreCase)) return;

        if (_dirty)
        {
            var r = MessageBox.Show(this, "当前文件有未保存的修改,切换会丢弃它们。继续?",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (r != MessageBoxResult.Yes)
            {
                // 改回原来的选择
                _switchingFile = true;
                FileBox.SelectedItem = Path.GetFileName(_currentPath);
                _switchingFile = false;
                return;
            }
        }

        _currentPath = next;
        Load();
    }

    /// <summary>配置树节点。Model 是真正的配置对象,Kind 决定属性面板长什么样。</summary>
    private sealed class ConfigNode
    {
        public required object Model;

        /// <summary>"project" / "device" / "group" / "var"</summary>
        public required string Kind;

        /// <summary>父节点对象(删除时要回父集合里摘掉)。</summary>
        public object? Parent;

        /// <summary>
        /// 变量节点需要知道所属设备(用于"新增组"时定位)。
        /// 变量 → 组 → 设备 隔了两层,Parent 只指到组,所以单独存一层。
        /// </summary>
        public object? Grand;
    }

    private void Load()
    {
        try
        {
            _projects = SettingsJsonCodec.LoadFile(_currentPath);

            _dirty = false;
            DirtyText.Text = "";

            BuildTree();
        }
        catch (Exception ex)
        {
            ProblemText.Text = "读取配置失败: " + ex.Message;
        }
    }

    // ==================== 树 ====================

    private void BuildTree()
    {
        ConfigTree.Items.Clear();

        foreach (var project in _projects)
        {
            var pItem = new TreeViewItem
            {
                Header = "项目: " + project.ProjectName,
                IsExpanded = true,
                Tag = new ConfigNode { Model = project, Kind = "project" }
            };

            foreach (var kv in project.DevicesByProtocol)
            {
                foreach (var device in kv.Value)
                {
                    var dItem = new TreeViewItem
                    {
                        Header = "设备: " + device.DeviceName + "  (" + kv.Key + ")",
                        IsExpanded = true,
                        Tag = new ConfigNode { Model = device, Kind = "device", Parent = project }
                    };

                    foreach (var group in device.GroupList)
                    {
                        var gItem = new TreeViewItem
                        {
                            Header = "组: " + group.GroupName
                                + "  [" + group.Start + " × " + group.Length + "]",
                            IsExpanded = true,
                            Tag = new ConfigNode { Model = group, Kind = "group", Parent = device }
                        };

                        foreach (var v in group.VarList)
                        {
                            gItem.Items.Add(new TreeViewItem
                            {
                                Header = v.VarName + "  (" + v.VarType + " @ " + v.Start + ")",
                                Tag = new ConfigNode
                                {
                                    Model = v,
                                    Kind = "var",
                                    Parent = group,
                                    Grand = device
                                }
                            });
                        }

                        dItem.Items.Add(gItem);
                    }

                    pItem.Items.Add(dItem);
                }
            }

            ConfigTree.Items.Add(pItem);
        }

        NodeTitle.Text = "请在左侧选择节点";
        PropPanel.Children.Clear();

        _selected = null;
        _selectedItem = null;
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var item = e.NewValue as TreeViewItem;

        if (item?.Tag is not ConfigNode node)
        {
            _selected = null;
            _selectedItem = null;
            return;
        }

        _selected = node;
        _selectedItem = item;

        BuildProps(node);
    }

    // ==================== 属性面板 ====================

    private void BuildProps(ConfigNode node)
    {
        PropPanel.Children.Clear();

        switch (node.Kind)
        {
            case "project":
                NodeTitle.Text = "项目属性";
                BuildProjectProps((ProjectConfig)node.Model);
                break;

            case "device":
                NodeTitle.Text = "设备属性";
                BuildDeviceProps((DeviceConfig)node.Model);
                break;

            case "group":
                NodeTitle.Text = "通讯组属性";
                BuildGroupProps((GroupConfig)node.Model);
                break;

            case "var":
                NodeTitle.Text = "变量属性";
                BuildVarProps((VariableConfig)node.Model);
                break;
        }
    }

    private void BuildProjectProps(ProjectConfig p)
    {
        AddText("项目名称", p.ProjectName, v => { p.ProjectName = v; MarkDirty(); });

        AddLabel("设备数: " + p.DevicesByProtocol.Values.Sum(x => x.Count)
            + "    变量数: " + p.AllVariables().Count());
    }

    private void BuildDeviceProps(DeviceConfig d)
    {
        AddText("设备名称", d.DeviceName, v => { d.DeviceName = v; MarkDirty(); });
        AddLabel("协议: " + d.ProtocolKey);

        if (d.Connection.Kind == ConnectionKind.Tcp)
        {
            AddText("IP 地址", d.Connection.IpAddress ?? "", v => { d.Connection.IpAddress = v; MarkDirty(); });
            AddInt("端口", d.Connection.Port, v => { d.Connection.Port = v; MarkDirty(); });
        }
        else
        {
            AddText("串口", d.Connection.PortName ?? "", v => { d.Connection.PortName = v; MarkDirty(); });
            AddInt("波特率", d.Connection.BaudRate, v => { d.Connection.BaudRate = v; MarkDirty(); });
        }

        AddCombo("字节序", new[] { "ABCD(大端)", "BADC(字节交换)", "CDAB(字交换)", "DCBA(小端)" },
            (int)d.WordOrder, v => { d.WordOrder = (WordOrder)v; MarkDirty(); });

        AddLabel("组数: " + d.GroupList.Count
            + "    变量数: " + d.GroupList.Sum(g => g.VarList.Count));

        AddLabel("提示:字节序配错会让所有 32/64 位数据读成乱值,但 16 位类型不受影响。");
    }

    private void BuildGroupProps(GroupConfig g)
    {
        AddText("组名称", g.GroupName, v => { g.GroupName = v; MarkDirty(); });
        AddText("起始地址", g.Start ?? "", v => { g.Start = v; MarkDirty(); });
        AddInt("长度(寄存器)", g.Length, v => { g.Length = v; MarkDirty(); });
        AddCheck("启用", g.IsActive, v => { g.IsActive = v; MarkDirty(); });

        AddCheck("允许下发写入", g.IsWritable, v => { g.IsWritable = v; MarkDirty(); },
            "勾选后本组的变量才会出现在「参数设置」和「急停/点动」页。只读的状态组不要勾,"
            + "否则界面上会出现能按但写不进去的按钮。");

        AddLabel("变量数: " + g.VarList.Count + "    可写: " + (g.IsWritable ? "是" : "否"));

        if (g.Length > 0 && g.VarList.Count > 0)
        {
            AddLabel("提示:变量的组内偏移 + 占用寄存器数不能超过组长度,否则读不到。");
        }
    }

    private void BuildVarProps(VariableConfig v)
    {
        AddText("变量名", v.VarName, x => { v.VarName = x; MarkDirty(); });
        AddCombo("数据类型", ConfigValidator.KnownTypes, FindTypeIndex(v.VarType),
            i => { v.VarType = ConfigValidator.KnownTypes[i]; MarkDirty(); });

        AddText("组内偏移 / 地址", v.Start ?? "", x => { v.Start = x; MarkDirty(); });
        AddText("完整地址", v.VarAddress ?? "", x => { v.VarAddress = x; MarkDirty(); });
        AddText("单位", v.Unit ?? "", x => { v.Unit = x; MarkDirty(); });
        AddText("备注", v.Comments ?? "", x => { v.Comments = x; MarkDirty(); });

        AddFloat("比例系数 Scale", v.Scale, x => { v.Scale = x; MarkDirty(); },
            "必须为 1,绝不能是 0 —— 配 0 会让读数恒为 0 且不报错");
        AddFloat("偏移量 Offset", v.Offset, x => { v.Offset = x; MarkDirty(); });

        AddCheck("归档到历史库", v.ArchiveEnable, x => { v.ArchiveEnable = x; MarkDirty(); });

        AddCheck("启用上限报警", v.HighAlarmEnable, x => { v.HighAlarmEnable = x; MarkDirty(); });
        AddFloat("上限值", v.HighAlarmValue, x => { v.HighAlarmValue = x; MarkDirty(); });
        AddText("上限报警描述", v.HighAlarmNote ?? "", x => { v.HighAlarmNote = x; MarkDirty(); });

        AddCheck("启用下限报警", v.LowAlarmEnable, x => { v.LowAlarmEnable = x; MarkDirty(); });
        AddFloat("下限值", v.LowAlarmValue, x => { v.LowAlarmValue = x; MarkDirty(); });
        AddText("下限报警描述", v.LowAlarmNote ?? "", x => { v.LowAlarmNote = x; MarkDirty(); });

        AddFloat("报警死区", v.AlarmDeadband, x => { v.AlarmDeadband = x; MarkDirty(); },
            "0 = 不启用。液位这类在阈值附近波动的量建议配死区,否则会反复触发/消除");

        // 写入上下限:留空 = 不配置(只做差异确认)。
        // 绝不能拿报警阈值代替它 —— 频率给定 10Hz 低于报警低限 20Hz 是正常操作。
        AddNullableDouble("写入下限", v.WriteLowLimit, x => { v.WriteLowLimit = x; MarkDirty(); },
            "留空=不限制。写入值低于它是越界,需要确认;远程写入会被直接拒绝");
        AddNullableDouble("写入上限", v.WriteHighLimit, x => { v.WriteHighLimit = x; MarkDirty(); },
            "留空=不限制。写入值高于它是越界,需要确认;远程写入会被直接拒绝");
    }

    /// <summary>
    /// 可空数值输入框(double?)。**留空表示"未配置"**,而不是 0 ——
    /// 0 是合法限值,两者必须区分,所以不能用普通数值框。
    /// </summary>
    private void AddNullableDouble(string label, double? value, Action<double?> setter, string? hint = null)
    {
        var row = MakeRow(label);

        var box = new TextBox
        {
            Text = value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "",
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x25)),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x45, 0x47, 0x5A)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 3, 4, 3)
        };

        System.Windows.Automation.AutomationProperties.SetName(box, label);

        box.TextChanged += (_, _) =>
        {
            var text = box.Text.Trim();

            // 留空 = 取消配置
            if (text.Length == 0)
            {
                setter(null);

                return;
            }

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return;

            // NaN/Infinity 会被 TryParse 认成合法输入,但会让越界比较恒为 false ——
            // 等于"配了保护其实没有",这里直接不接受
            if (double.IsNaN(d) || double.IsInfinity(d)) return;

            setter(d);
        };

        row.Children.Add(box);

        PropPanel.Children.Add(row);

        if (hint is not null) AddLabel(hint);
    }

    private static int FindTypeIndex(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return 0;

        for (int i = 0; i < ConfigValidator.KnownTypes.Length; i++)
        {
            if (string.Equals(ConfigValidator.KnownTypes[i], type.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    // ---- 控件工厂 ----

    private void AddLabel(string text)
    {
        PropPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x6C, 0x70, 0x86)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8)
        });
    }

    private void AddText(string label, string value, Action<string> setter)
    {
        var row = MakeRow(label);

        var box = new TextBox
        {
            Text = value,
            Width = 380,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x25)),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x45, 0x47, 0x5A)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 3, 4, 3)
        };

        box.TextChanged += (_, _) => setter(box.Text);

        row.Children.Add(box);

        PropPanel.Children.Add(row);
    }

    private void AddInt(string label, int value, Action<int> setter)
    {
        var row = MakeRow(label);

        var box = new TextBox
        {
            Text = value.ToString(CultureInfo.InvariantCulture),
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x25)),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x45, 0x47, 0x5A)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 3, 4, 3)
        };

        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                setter(n);
            }
        };

        row.Children.Add(box);

        PropPanel.Children.Add(row);
    }

    private void AddFloat(string label, float value, Action<float> setter, string? hint = null)
    {
        var row = MakeRow(label);

        var box = new TextBox
        {
            Text = value.ToString(CultureInfo.InvariantCulture),
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x25)),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x45, 0x47, 0x5A)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 3, 4, 3)
        };

        // 用 InvariantCulture:配置里的小数点是句点,换机器不会变成逗号
        box.TextChanged += (_, _) =>
        {
            if (!float.TryParse (box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return;

            // "NaN"/"Infinity" 也是 float.TryParse 能认的合法输入。
            // 放进去以后,报警比较恒为 false、Scale 乘出来全是 NaN,
            // 归档库里会写进一堆洗不掉的脏值 —— 这里直接不接受。
            if (float.IsNaN (f) || float.IsInfinity (f)) return;

            setter (f);
        };

        row.Children.Add(box);

        PropPanel.Children.Add(row);

        if (hint is not null) AddLabel(hint);
    }

    private void AddCheck(string label, bool value, Action<bool> setter, string? hint = null)
    {
        var row = MakeRow(label);

        var box = new CheckBox
        {
            IsChecked = value,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        // 标签在左侧独立列,不给复选框起名的话读屏/UIA 只能看到一个无名勾选框
        System.Windows.Automation.AutomationProperties.SetName (box, label);

        box.Checked += (_, _) => setter(true);
        box.Unchecked += (_, _) => setter(false);

        row.Children.Add(box);

        PropPanel.Children.Add(row);

        if (hint is not null) AddLabel(hint);
    }

    private void AddCombo(string label, string[] items, int selected, Action<int> setter)
    {
        var row = MakeRow(label);

        var box = new ComboBox
        {
            Width = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
            SelectedIndex = selected
        };

        foreach (var it in items) box.Items.Add(it);

        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0) setter(box.SelectedIndex);
        };

        row.Children.Add(box);

        PropPanel.Children.Add(row);
    }

    private static StackPanel MakeRow(string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 150,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xC6, 0xCD, 0xE0)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });

        return row;
    }

    private void MarkDirty()
    {
        if (_dirty) return;

        _dirty = true;
        DirtyText.Text = "● 有未保存的修改";

        // 节点标题可能因改名而过期,重建当前节点的标题
        if (_selectedItem?.Tag is ConfigNode node)
        {
            _selectedItem.Header = HeaderOf(node);
        }
    }

    private static string HeaderOf(ConfigNode node)
    {
        switch (node.Model)
        {
            case ProjectConfig p: return "项目: " + p.ProjectName;

            case DeviceConfig d: return "设备: " + d.DeviceName + "  (" + d.ProtocolKey + ")";

            case GroupConfig g: return "组: " + g.GroupName + "  [" + g.Start + " × " + g.Length + "]";

            case VariableConfig v: return v.VarName + "  (" + v.VarType + " @ " + v.Start + ")";

            default: return "";
        }
    }

    // ==================== 增删 ====================

    private void OnAddGroup(object sender, RoutedEventArgs e)
    {
        var device = ResolveDevice(_selected);

        if (device is null)
        {
            ProblemText.Text = "请先选择一个设备(或在某个组/变量上按,会自动找所属设备)";
            return;
        }

        device.GroupList.Add(new GroupConfig
        {
            GroupName = "新组" + (device.GroupList.Count + 1),
            Start = "1.400001",
            Length = 10,
            IsActive = true
        });

        MarkDirty();
        RebuildKeepNothing();
    }

    private void OnAddVariable(object sender, RoutedEventArgs e)
    {
        var group = ResolveGroup(_selected);

        if (group is null)
        {
            ProblemText.Text = "请先选择一个通讯组(或在组内变量上按)";
            return;
        }

        // 新变量默认放在组内最后一个变量之后,避免一加就越界
        var offset = 0;

        if (group.VarList.Count > 0)
        {
            var last = group.VarList[^1];
            if (int.TryParse((last.Start ?? "0").Split('.').Last(), out var n)) offset = n + 2;
        }

        group.VarList.Add(new VariableConfig
        {
            VarName = "新变量" + (group.VarList.Count + 1),
            VarType = "Float",
            Start = "1." + (400001 + offset + 200).ToString(CultureInfo.InvariantCulture),
            VarAddress = "1." + (400001 + offset + 200).ToString(CultureInfo.InvariantCulture),
            Scale = 1f
        });

        MarkDirty();
        RebuildKeepNothing();
    }

    private void OnDeleteNode(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            ProblemText.Text = "请先选择要删除的节点";
            return;
        }

        switch (_selected.Model)
        {
            case GroupConfig g when _selected.Parent is DeviceConfig d:
                d.GroupList.Remove(g);
                break;

            case VariableConfig v when _selected.Parent is GroupConfig gg:
                gg.VarList.Remove(v);
                break;

            case DeviceConfig dev when _selected.Parent is ProjectConfig p:
                foreach (var list in p.DevicesByProtocol.Values) list.Remove(dev);
                break;

            default:
                ProblemText.Text = "项目节点不能删除";
                return;
        }

        MarkDirty();
        RebuildKeepNothing();
    }

    private void RebuildKeepNothing()
    {
        BuildTree();
    }

    private static DeviceConfig? ResolveDevice(ConfigNode? node)
    {
        switch (node?.Model)
        {
            case DeviceConfig d: return d;
            case GroupConfig: return node.Parent as DeviceConfig;
            case VariableConfig: return node.Grand as DeviceConfig;
            default: return null;
        }
    }

    private static GroupConfig? ResolveGroup(ConfigNode? node)
    {
        switch (node?.Model)
        {
            case GroupConfig g: return g;
            case VariableConfig: return node.Parent as GroupConfig;
            default: return null;
        }
    }

    // ==================== 复制 / 粘贴 ====================

/// <summary>剪贴板:存的是**克隆**,源节点之后被改/删都不会影响粘贴结果。</summary>
private readonly List<VariableConfig> _clipVars = new();
private GroupConfig? _clipGroup;

private void OnEditorKeyDown(object sender, KeyEventArgs e)
{
    // 焦点在文本输入框里时**不要**拦 Ctrl+C/V。
    // 这里处理的是"复制/粘贴变量或组",抢走按键的后果是属性面板的
    // TextBox 里选不中文字、也粘不进内容(现场改备注/单位/说明时最常碰到)。
    // 让它走系统默认的复制粘贴。
    if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;

    if (Keyboard.Modifiers != ModifierKeys.Control) return;

    if (e.Key == Key.C)
    {
        CopySelected();
        e.Handled = true;
    }
    else if (e.Key == Key.V)
    {
        PasteIntoSelection();
        e.Handled = true;
    }
}

private void OnCopyNode(object sender, RoutedEventArgs e) => CopySelected();

private void OnPasteNode(object sender, RoutedEventArgs e) => PasteIntoSelection();

private void CopySelected()
{
    if (_selected is null)
    {
        ProblemText.Text = "请先选择要复制的变量或组";
        return;
    }

    _clipVars.Clear();
    _clipGroup = null;

    switch (_selected.Model)
    {
        case VariableConfig v:
            _clipVars.Add(v.Clone());
            ProblemText.Text = "已复制变量:" + v.VarName;
            break;

        case GroupConfig g:
            _clipGroup = g.Clone();
            ProblemText.Text = "已复制组:" + g.GroupName + "(" + g.VarList.Count + " 个变量)";
            break;

        default:
            ProblemText.Text = "只能复制变量或通讯组";
            break;
    }
}

private void PasteIntoSelection()
{
    if (_clipVars.Count == 0 && _clipGroup is null)
    {
        ProblemText.Text = "剪贴板是空的,先按 Ctrl+C 复制";
        return;
    }

    var group = ResolveGroup(_selected);

    // 复制的整组可以直接粘到设备下:新建一个组
    if (_clipGroup is not null && group is null)
    {
        var device = ResolveDevice(_selected);

        if (device is null)
        {
            ProblemText.Text = "请选择一个设备或通讯组作为粘贴位置";
            return;
        }

        var fresh = _clipGroup.Clone();
        fresh.GroupName = UniqueGroupName(device, fresh.GroupName);

        device.GroupList.Add(fresh);

        MarkDirty();
        RebuildKeepNothing();

        ProblemText.Text = "已粘贴组:" + fresh.GroupName + "(" + fresh.VarList.Count + " 个变量,地址与原组相同,请核对)";

        return;
    }

    if (group is null)
    {
        ProblemText.Text = "请选择一个通讯组作为粘贴位置";
        return;
    }

    var source = _clipVars.Count > 0 ? _clipVars : _clipGroup?.VarList ?? new List<VariableConfig>();

    if (source.Count == 0)
    {
        ProblemText.Text = "剪贴板里没有变量";
        return;
    }

    // 地址必须错开:直接照抄地址会让两个变量抢同一个寄存器,
    // 采上来两列数一样,现场极难发现。按变量占用宽度往后推。
    var cursor = LastOffset(group);
    var added = 0;

    foreach (var v in source)
    {
        var copy = v.Clone();

        copy.VarName = UniqueVarName(group, copy.VarName);

        var width = RegWidth(copy.VarType);

        cursor += width;

        copy.Start = ShiftAddress(copy.Start, cursor);
        copy.VarAddress = ShiftAddress(copy.VarAddress, cursor);

        group.VarList.Add(copy);

        added++;
    }

    MarkDirty();
    RebuildKeepNothing();

    ProblemText.Text = "已粘贴 " + added + " 个变量到组 " + group.GroupName + "(地址已自动错开)";
}

/// <summary>组内最后一个变量末尾的偏移量;解析不出就返回 0。</summary>
private static int LastOffset(GroupConfig group)
{
    var max = 0;

    foreach (var v in group.VarList)
    {
        var tail = TailNumber(v.Start);

        if (tail > max) max = tail;
    }

    return max;
}

private static int TailNumber(string? address)
{
    if (string.IsNullOrWhiteSpace(address)) return 0;

    var part = address.Trim().Split('.').Last();

    return int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}

/// <summary>把地址末尾的数字整体平移。地址形如 "1.400001" / "40001" / "D100"。</summary>
private static string? ShiftAddress(string? address, int delta)
{
    if (string.IsNullOrWhiteSpace(address)) return address;

    var parts = address.Trim().Split('.');
    var last = parts[^1];

    if (!int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return address;

    parts[^1] = (n + delta).ToString(CultureInfo.InvariantCulture);

    return string.Join(".", parts);
}

/// <summary>变量占几个 16 位寄存器:决定粘贴时地址往后推多少。</summary>
private static int RegWidth(string? type)
{
    return (type ?? "").Trim().ToLowerInvariant() switch
    {
        "bool" or "short" or "ushort" or "int16" or "uint16" => 1,
        "double" or "int64" or "uint64" or "float64" => 4,
        _ => 2 // float / int32 / uint32 等按 2 个寄存器算
    };
}

private static string UniqueVarName(GroupConfig group, string name)
{
    var baseName = string.IsNullOrWhiteSpace(name) ? "变量" : name.Trim();
    var candidate = baseName + "_副本";
    var i = 2;

    while (group.VarList.Any(v => string.Equals(v.VarName, candidate, StringComparison.Ordinal)))
    {
        candidate = baseName + "_副本" + i;
        i++;
    }

    return candidate;
}

private static string UniqueGroupName(DeviceConfig device, string name)
{
    var baseName = string.IsNullOrWhiteSpace(name) ? "新组" : name.Trim();
    var candidate = baseName + "_副本";
    var i = 2;

    while (device.GroupList.Any(g => string.Equals(g.GroupName, candidate, StringComparison.Ordinal)))
    {
        candidate = baseName + "_副本" + i;
        i++;
    }

    return candidate;
}

// ==================== 批量阈值 ====================

/// <summary>
/// 给所选变量(或整组变量)统一设置上下限、使能位与死区。
///
/// 为什么需要:一份 139 个变量的配置,逐个点进去改阈值要点一百多次;
/// 而现场调报警往往就是"整组同一套阈值"。
/// </summary>
private void OnBatchThreshold(object sender, RoutedEventArgs e)
{
    var group = ResolveGroup(_selected);

    if (group is null)
    {
        ProblemText.Text = "请先选择一个通讯组或组内变量";
        return;
    }

    var onlySelected = _selected.Model is VariableConfig ? 1 : 0;

    var targets = _selected.Model is VariableConfig single ? new List<VariableConfig> { single } : group.VarList.ToList();

    if (targets.Count == 0)
    {
        ProblemText.Text = "该组没有变量";
        return;
    }

    var win = new BatchThresholdWindow(group.VarList.Count)
    {
        Owner = this
    };

    if (win.ShowDialog() != true) return;

    var scope = win.AllInGroup ? group.VarList.ToList() : targets;

    var changed = 0;

    foreach (var v in scope)
    {
        if (win.SetHigh) { v.HighAlarmValue = win.HighValue; v.HighAlarmEnable = win.HighEnable; }
        if (win.SetLow) { v.LowAlarmValue = win.LowValue; v.LowAlarmEnable = win.LowEnable; }
        if (win.SetDeadband) v.AlarmDeadband = win.Deadband;

        changed++;
    }

    MarkDirty();

    ProblemText.Text = "已对 " + changed + " 个变量更新阈值"
        + (onlySelected == 1 && !win.AllInGroup ? "(只改了所选变量)" : "(整组)")
        + ",记得保存";

    BuildProps(_selected);
}

// ==================== 导入变量表 ====================

/// <summary>
/// 从 CSV 导入变量表(列与"导出变量表"一致,可直接拿导出的文件改完再导回)。
///
/// 导入到当前所选设备:按 CSV 里的"组"列分流,组不存在就新建。
/// 只覆盖同名的现有变量 —— 不能因为导入把现场已经调好的阈值悄悄抹掉。
/// </summary>
private void OnImportVars(object sender, RoutedEventArgs e)
{
    var device = ResolveDevice(_selected) ?? CurrentProject().AllDevices().FirstOrDefault();

    if (device is null)
    {
        ProblemText.Text = "请先在左侧选择一个设备,再导入变量表";
        return;
    }

    var dlg = new Microsoft.Win32.OpenFileDialog
    {
        Title = "导入变量表(导入到设备:" + device.DeviceName + ")",
        Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*"
    };

    if (dlg.ShowDialog() != true) return;

    List<List<string>> records;

    try
    {
        records = SplitCsvRecords (File.ReadAllText (dlg.FileName, System.Text.Encoding.UTF8));
    }
    catch (Exception ex)
    {
        ProblemText.Text = "读文件失败: " + ex.Message;
        return;
    }

    if (records.Count == 0)
    {
        ProblemText.Text = "文件是空的";
        return;
    }

    var start = records[0].Count > 0 && records[0][0].StartsWith ("设备", StringComparison.Ordinal)
        ? 1
        : 0; // 有表头就跳过

    var added = 0;
    var updated = 0;
    var skipped = 0;

    for (var i = start; i < records.Count; i++)
    {
        var f = records[i];

        if (f.Count == 0 || f.All (x => string.IsNullOrWhiteSpace (x))) continue;

        if (f.Count < 6)
        {
            skipped++;
            continue;
        }

        var groupName = f[2].Trim();
        var varName = f[3].Trim();

        if (varName.Length == 0)
        {
            skipped++;
            continue;
        }

        var group = device.GroupList.FirstOrDefault(g => string.Equals(g.GroupName, groupName, StringComparison.Ordinal));

        if (group is null)
        {
            group = new GroupConfig
            {
                GroupName = groupName.Length > 0 ? groupName : "导入组",
                Start = f[5].Trim(),
                Length = 0,
                IsActive = true
            };

            device.GroupList.Add(group);
        }

        var v = group.VarList.FirstOrDefault(x => string.Equals(x.VarName, varName, StringComparison.Ordinal));

        if (v is null)
        {
            v = new VariableConfig { VarName = varName };
            group.VarList.Add(v);
            added++;
        }
        else
        {
            updated++;
        }

        v.VarType = f[4].Trim().Length > 0 ? f[4].Trim() : v.VarType;
        v.Start = f[5].Trim().Length > 0 ? f[5].Trim() : v.Start;
        v.VarAddress = v.Start;
        if (f.Count > 6 && f[6].Trim().Length > 0) v.Unit = f[6].Trim();
        // 数值列统一走 ParseFinite:CSV 是手工改的,"NaN"/"Infinity"/"1e400" 都可能出现,
        // 直接灌进配置会让 Scale/阈值/死区变成非有限值,后面所有比较与归档全部失效。
        if (f.Count > 7 && ParseFinite (f[7], out var sc)) v.Scale = sc == 0 ? 1f : sc;
        if (f.Count > 8 && ParseFinite (f[8], out var off)) v.Offset = off;
        if (f.Count > 9) v.ArchiveEnable = f[9].Trim() is "是" or "1" or "true" or "True";
        if (f.Count > 10 && ParseFinite (f[10], out var hi)) v.HighAlarmValue = hi;
        if (f.Count > 11) v.HighAlarmEnable = f[11].Trim() is "是" or "1" or "true" or "True";
        if (f.Count > 12 && ParseFinite (f[12], out var lo)) v.LowAlarmValue = lo;
        if (f.Count > 13) v.LowAlarmEnable = f[13].Trim() is "是" or "1" or "true" or "True";
        if (f.Count > 14 && ParseFinite (f[14], out var db)) v.AlarmDeadband = db;
        // 列序必须与导出一致(见下方导出表头 18 列):
        //   15=写入下限 16=写入上限 17=备注
        // 早先这里把 f[15] 当备注 —— 导出→导入一轮,写入下限被写进备注、两个写入限全丢。
        if (f.Count > 15 && f[15].Trim().Length > 0)
        {
            if (ParseFinite (f[15], out var wlo)) v.WriteLowLimit = wlo;
        }
        else v.WriteLowLimit = null;
        if (f.Count > 16 && f[16].Trim().Length > 0)
        {
            if (ParseFinite (f[16], out var whi)) v.WriteHighLimit = whi;
        }
        else v.WriteHighLimit = null;
        if (f.Count > 17 && f[17].Trim().Length > 0) v.Comments = f[17].Trim();
    }

    MarkDirty();
    RebuildKeepNothing();

    ProblemText.Text = "导入完成:新增 " + added + " 个,更新 " + updated + " 个"
        + (skipped > 0 ? ",跳过 " + skipped + " 行(格式不对)" : "")
        + "。变量总地址请用「校验」按钮核对";
}

/// <summary>
/// CSV 数值解析:只接受**有限**浮点。
/// float.TryParse 会把 "NaN"/"Infinity" 当成合法值,"1e400" 也会溢出成 Infinity,
/// 这些值一旦进了配置,缩放、报警比较、归档写入全部失效而且看不出来。
/// </summary>
private static bool ParseFinite(string text, out float value)
{
    value = 0f;

    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return false;

    if (float.IsNaN(f) || float.IsInfinity(f)) return false;

    value = f;

    return true;
}

/// <summary>极简 CSV 拆行:支持双引号包裹与 "" 转义(单行版本,保留给老调用)。</summary>
private static List<string> SplitCsv(string line)
{
    var records = SplitCsvRecords(line);

    return records.Count > 0 ? records[0] : new List<string>();
}

/// <summary>
/// 把整个 CSV 文本拆成「记录 → 字段」两层。必须整文解析,不能一行一条:
/// 引号里的备注可以带换行(Excel 里 Alt+Enter 就能敲出来),导出时也是原样引起来的。
/// 原来用 File.ReadAllLines + 逐行 SplitCsv:一条带换行的备注会被拆成两条,
/// 后半截被当成新变量行解析,于是导入后凭空多出一批地址离谱的变量,
/// 而界面上只报"新增 N 个"。
/// </summary>
private static List<List<string>> SplitCsvRecords(string text)
{
    var records = new List<List<string>>();
    var fields = new List<string>();
    var sb = new System.Text.StringBuilder();
    var quoted = false;

    for (var i = 0; i < text.Length; i++)
    {
        var c = text[i];

        if (quoted)
        {
            if (c == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                // 引号内的换行是**数据**,原样保留(统一成 \n,避免导出/导入来回变形)
                sb.Append('\n');
                i++;
            }
            else sb.Append(c);
        }
        else if (c == '"') quoted = true;
        else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
        else if (c == '\r')
        {
            // 行尾(CRLF 或单个 CR):收一条记录
            fields.Add(sb.ToString());
            sb.Clear();
            records.Add(fields);
            fields = new List<string>();

            if (i + 1 < text.Length && text[i + 1] == '\n') i++;
        }
        else if (c == '\n')
        {
            fields.Add(sb.ToString());
            sb.Clear();
            records.Add(fields);
            fields = new List<string>();
        }
        else sb.Append(c);
    }

    // 收尾:最后一行可能没有换行符
    if (sb.Length > 0 || fields.Count > 0)
    {
        fields.Add(sb.ToString());
        records.Add(fields);
    }

    return records;
}

// ==================== 保存 / 校验 ====================

    /// <summary>
    /// 导出变量表 CSV:设备/组/变量名/类型/地址/单位/缩放/偏移/归档/上下限/死区/备注。
    ///
    /// 用途:现场交接、跨客户复用、给 PLC 工程师核对点位。
    /// 带 BOM 让中文 Excel 直接打开不乱码。
    /// </summary>
    private void OnExportVars(object sender, RoutedEventArgs e)
    {
        var project = CurrentProject();

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出变量表",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = "变量表_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb = new System.Text.StringBuilder();

            // 写入上下限必须一起导出:CSV 是配置的维护通道之一,
            // 漏掉这两列会导致"导出→改→导入"之后写入保护静默丢失(变回未配置)。
            sb.Append("设备,协议,组,变量名,类型,地址,单位,缩放,偏移,归档,上限,上限启用,下限,下限启用,死区,"
                + "写入下限,写入上限,备注\n");

            foreach (var d in project.AllDevices())
            {
                foreach (var g in d.GroupList)
                {
                    if (g.VarList is null) continue;

                    foreach (var v in g.VarList)
                    {
                        sb.Append(Csv(d.DeviceName)).Append(',')
                          .Append(Csv(d.ProtocolKey)).Append(',')
                          .Append(Csv(g.GroupName)).Append(',')
                          .Append(Csv(v.VarName)).Append(',')
                          .Append(Csv(v.VarType)).Append(',')
                          .Append(Csv(v.Start)).Append(',')
                          .Append(Csv(v.Unit)).Append(',')
                          .Append(v.Scale.ToString(CultureInfo.InvariantCulture)).Append(',')
                          .Append(v.Offset.ToString(CultureInfo.InvariantCulture)).Append(',')
                          .Append(v.ArchiveEnable ? "是" : "否").Append(',')
                          .Append(v.HighAlarmValue.ToString(CultureInfo.InvariantCulture)).Append(',')
                          .Append(v.HighAlarmEnable ? "是" : "否").Append(',')
                          .Append(v.LowAlarmValue.ToString(CultureInfo.InvariantCulture)).Append(',')
                          .Append(v.LowAlarmEnable ? "是" : "否").Append(',')
                          .Append(v.AlarmDeadband.ToString(CultureInfo.InvariantCulture)).Append(',')
                          // 留空 = 未配置(与 0 区分开)
                          .Append(v.WriteLowLimit.HasValue
                              ? v.WriteLowLimit.Value.ToString(CultureInfo.InvariantCulture) : "").Append(',')
                          .Append(v.WriteHighLimit.HasValue
                              ? v.WriteHighLimit.Value.ToString(CultureInfo.InvariantCulture) : "").Append(',')
                          .Append(Csv(v.Comments)).Append('\n');
                    }
                }
            }

            var tmp = dlg.FileName + ".tmp";

            File.WriteAllText(tmp, sb.ToString(), new System.Text.UTF8Encoding(true));

            // 原子替换(同卷原子,失败不丢原文件),收尾细节见 AtomicFileWrite
            KyjScada.Infrastructure.Export.AtomicFileWrite.Replace(tmp, dlg.FileName);

            ProblemText.Text = "已导出变量表:" + dlg.FileName;
        }
        catch (Exception ex)
        {
            ProblemText.Text = "导出失败: " + ex.Message;
        }
    }

    private static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        // 触发字符必须和读写两侧一致:导出的解析器按 \n 拆行、并按 \r 处理 CRLF,
        // 漏掉 '\r' 时含裸 CR 的字段不套引号 —— 导出再导入就多出一行,往返不互逆。
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
            ? s
            : "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private void OnValidate(object sender, RoutedEventArgs e)
    {
        var problems = ConfigValidator.Validate(CurrentProject());

        if (problems.Count == 0)
        {
            ProblemText.Text = "校验通过:没有发现问题";
            return;
        }

        ProblemText.Text = "发现问题 " + problems.Count + " 项:\n"
            + string.Join("\n", problems.Select(p => "  · " + p));
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            // 保存前自检:结构性错误直接拦下,可疑项只提示不拦
            var problems = ConfigValidator.Validate(CurrentProject());

            var blocking = problems.Where(x =>
                x.Contains("无法解析") || x.Contains("超出组长") || x.Contains("Scale 为 0")
                || x.Contains("未知数据类型") || x.Contains("长度无效")
                || x.Contains("变量名重复") || x.Contains("设备名称重复")
                || x.Contains("项目名称不能为空")).ToList();

            if (blocking.Count > 0)
            {
                ProblemText.Text = "保存已阻止 —— 先修掉这些结构性问题:\n"
                    + string.Join("\n", blocking.Select(p => "  · " + p));
                return;
            }

            SettingsJsonCodec.SaveFile(_currentPath, _projects);

            _dirty = false;
            DirtyText.Text = "已保存(原文件已备份为 .bak)";

            ProblemText.Text = problems.Count == 0
                ? "保存成功。提示:采集服务需要重新加载配置才生效。"
                : "保存成功,但有 " + problems.Count + " 项可疑(未阻止):\n"
                  + string.Join("\n", problems.Select(p => "  · " + p));
        }
        catch (Exception ex)
        {
            ProblemText.Text = "保存失败: " + ex.Message;
        }
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (_dirty)
        {
            var r = MessageBox.Show(this, "有未保存的修改,重新加载会丢弃它们。继续?",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (r != MessageBoxResult.Yes) return;
        }

        Load();
        ProblemText.Text = "已重新加载";
    }

    private ProjectConfig CurrentProject()
    {
        return _projects.FirstOrDefault() ?? new ProjectConfig();
    }
}