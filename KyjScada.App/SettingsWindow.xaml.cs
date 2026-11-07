using System.Globalization;
using System.Windows;

namespace KyjScada.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        // 复制一份再编辑:取消时不影响正在运行的参数
        _settings = new AppSettings
        {
            ArchiveIntervalSeconds = settings.ArchiveIntervalSeconds,
            RetentionDays = settings.RetentionDays,
            WriteMinIntervalMs = settings.WriteMinIntervalMs,
            ShiftHours = settings.ShiftHours,
            ShiftStartHour = settings.ShiftStartHour,
            DefaultAlarmDeadband = settings.DefaultAlarmDeadband,
            McpEnabled = settings.McpEnabled,
            McpPort = settings.McpPort
        };

        IntervalBox.Text = _settings.ArchiveIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        RetentionBox.Text = _settings.RetentionDays.ToString(CultureInfo.InvariantCulture);
        WriteIntervalBox.Text = _settings.WriteMinIntervalMs.ToString(CultureInfo.InvariantCulture);
        ShiftHoursBox.Text = _settings.ShiftHours.ToString(CultureInfo.InvariantCulture);
        ShiftStartBox.Text = _settings.ShiftStartHour.ToString(CultureInfo.InvariantCulture);
        DeadbandBox.Text = _settings.DefaultAlarmDeadband.ToString(CultureInfo.InvariantCulture);

        McpEnabledBox.IsChecked = _settings.McpEnabled;
        McpPortBox.Text = _settings.McpPort.ToString(CultureInfo.InvariantCulture);
        McpPortBox.IsEnabled = _settings.McpEnabled;

        RefreshMcpStatus();
    }

    /// <summary>把运行中的真实状态显示在窗口里(端口/端点/是否含写通道),而不是显示输入框里的期望值。</summary>
    private void RefreshMcpStatus()
    {
        var endpoint = McpHost.Endpoint;

        if (!string.IsNullOrEmpty(endpoint))
        {
            McpStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            McpStatusText.Text = "运行中 " + endpoint;
            CopyEndpointBtn.IsEnabled = true;
        }
        else
        {
            McpStatusText.Foreground = System.Windows.Media.Brushes.SandyBrown;
            McpStatusText.Text = McpHost.Status;
            CopyEndpointBtn.IsEnabled = false;
        }

        McpHintText.Text =
            "共 9 个工具:7 个只读(变量清单/最新值/批量值/历史时序/统计/报警日志/设备概况)"
            + " + 2 个写入(写变量/查写通道)。写入必须先登录有「参数设置」权限的账号,"
            + "并在「参数设置」页勾选「允许写入」,否则工具会明确拒绝而不是假装成功。"
            + "保存后若端口或开关有改动,关闭本窗口即自动重启服务。";
    }

    private void OnMcpEnabledChanged(object sender, RoutedEventArgs e)
    {
        // ctor 里 InitializeComponent 期间也会触发一次,此时控件还没全部就绪
        if (McpPortBox is null) return;

        McpPortBox.IsEnabled = McpEnabledBox.IsChecked == true;
    }

    private void OnCopyEndpoint(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(McpHost.Endpoint))
        {
            ResultText.Text = "服务未启动,没有可复制的端点。";
            return;
        }

        try
        {
            Clipboard.SetText(McpHost.Endpoint);
            ResultText.Text = "端点已复制:" + McpHost.Endpoint;
        }
        catch (Exception ex)
        {
            ResultText.Text = "复制失败(剪贴板被占用?):" + ex.Message;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IntervalBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval)
            || interval < 1)
        {
            ResultText.Text = "归档周期必须是 >= 1 的整数(秒)";
            return;
        }

        if (!int.TryParse(RetentionBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
        {
            ResultText.Text = "保留天数必须是整数(0 表示永久保留)";
            return;
        }

        if (!int.TryParse(WriteIntervalBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var writeMs)
            || writeMs < 0)
        {
            ResultText.Text = "写入间隔必须是 >= 0 的整数(毫秒)";
            return;
        }

        if (!int.TryParse(ShiftHoursBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shiftHours)
            || shiftHours < 1 || shiftHours > 24)
        {
            ResultText.Text = "班次小时数必须是 1~24 的整数";
            return;
        }

        if (!int.TryParse(ShiftStartBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shiftStart)
            || shiftStart < 0 || shiftStart > 23)
        {
            ResultText.Text = "早班起点必须是 0~23 的整数";
            return;
        }

        // MCP 端口:0 在老版本里表示"关闭",这里保留兼容;启用时必须落在可用端口段
        if (!int.TryParse(McpPortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mcpPort)
            || mcpPort < 0 || mcpPort > 65535)
        {
            ResultText.Text = "MCP 端口必须是 0~65535 的整数(0 = 不启动)";
            return;
        }

        var mcpEnabled = McpEnabledBox.IsChecked == true;

        if (mcpEnabled && (mcpPort < 1024 || mcpPort > 65535))
        {
            ResultText.Text = "启用 MCP 时端口必须在 1024~65535 之间(1024 以下是系统保留端口)";
            return;
        }

        _settings.ArchiveIntervalSeconds = interval;

        if (!double.TryParse(DeadbandBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var deadband)
            || deadband < 0)
        {
            ResultText.Text = "默认死区必须是 >= 0 的数值";
            return;
        }

        _settings.ShiftHours = shiftHours;
        _settings.DefaultAlarmDeadband = deadband;
        _settings.ShiftStartHour = shiftStart;
        _settings.RetentionDays = days;
        _settings.WriteMinIntervalMs = writeMs;
        _settings.McpEnabled = mcpEnabled;
        _settings.McpPort = mcpPort;

        // 端口/开关是否真的变了,决定要不要提示"自动重启"(主窗口在窗口关闭后比对)
        var changed = mcpEnabled != McpHost.Running
                      || (mcpEnabled && !string.Equals(
                          McpHost.Endpoint,
                          "http://127.0.0.1:" + mcpPort + "/mcp",
                          StringComparison.OrdinalIgnoreCase));

        try
        {
            var saveError = _settings.Save ();

            if (saveError is not null)
            {
                // 保存真的失败了就说失败,不能继续往下写"已保存"
                ResultText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                ResultText.Text = "保存失败: " + saveError;

                return;
            }

            if (changed)
            {
                // 服务要关窗口后才真正重启,这里如实显示"待生效",不要让人以为已经跑起来了
                McpStatusText.Foreground = System.Windows.Media.Brushes.SandyBrown;
                McpStatusText.Text = mcpEnabled
                    ? "待重启生效 → http://127.0.0.1:" + mcpPort + "/mcp"
                    : "待重启生效 → 关闭 MCP";

                CopyEndpointBtn.IsEnabled = false;

                ResultText.Text = "已保存。关闭本窗口后 MCP 服务会自动按新设置重启;归档周期/保留期需重启程序生效。";
            }
            else
            {
                ResultText.Text = "已保存。归档周期/保留期需重启程序生效;写入间隔下次建立写入闸门时生效。";
            }
        }
        catch (Exception ex)
        {
            ResultText.Text = "保存失败: " + ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}