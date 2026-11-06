using System.Globalization;
using System.IO;
using System.Text;

namespace KyjScada.App;

/// <summary>
/// 应用级设置(与设备配置分开:这些是"这台机器怎么跑"的参数,不是设备参数)。
///
/// 现场不同客户对归档周期和保留期要求差别很大:
/// 有的要 5 秒一个点做精细分析,有的只留 90 天省磁盘。
/// 写死在代码里等于每个客户都要重新编译。
/// </summary>
public sealed class AppSettings
{
    /// <summary>归档周期(秒)。太小会撑爆磁盘,太大会丢细节。</summary>
    public int ArchiveIntervalSeconds { get; set; } = 10;

    /// <summary>历史保留天数。0 或负数 = 永久保留。</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>全局默认报警死区(变量自己没配时用)。</summary>
    public double DefaultAlarmDeadband { get; set; }

    /// <summary>班次小时数(2/3 班倒时不同厂不一样)。</summary>
    public int ShiftHours { get; set; } = 8;

    /// <summary>早班开始钟点(0~23),用于班次边界对齐。</summary>
    public int ShiftStartHour { get; set; } = 8;

    /// <summary>报警确认等写入操作的最小间隔(毫秒)。</summary>
    public int WriteMinIntervalMs { get; set; } = 800;

    /// <summary>是否启动内嵌 MCP 服务(关掉时端口保持不变,下次打开不用重填)。</summary>
    public bool McpEnabled { get; set; } = true;

    /// <summary>
    /// 内嵌 MCP 服务端口(0 或 McpEnabled=false = 不启动)。
    /// 主程序自己起 MCP,读工具用本进程内的同一份库/组态,写工具直接走主程序的 CommandGate,
    /// 这样外部 AI 客户端就能读到实时快照并在授权下写值,而不必另起一个进程抢通道。
    /// </summary>
    public int McpPort { get; set; } = 18321;

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dir);

            return Path.Combine(dir, "app_settings.ini");
        }
    }

    public static AppSettings Load()
    {
        var s = new AppSettings();

        try
        {
            var path = FilePath;

            if (!File.Exists(path)) return s;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();

                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();

                switch (key.ToLowerInvariant())
                {
                    case "archiveintervalseconds":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) && a > 0)
                        {
                            s.ArchiveIntervalSeconds = a;
                        }
                        break;

                    case "retentiondays":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
                        {
                            s.RetentionDays = r;
                        }
                        break;

                    case "defaultalarmdeadband":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var db) && db >= 0)
                        {
                            s.DefaultAlarmDeadband = db;
                        }
                        break;

                    case "shifthours":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sh) && sh > 0 && sh <= 24)
                        {
                            s.ShiftHours = sh;
                        }
                        break;

                    case "shiftstarthour":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ssh) && ssh >= 0 && ssh <= 23)
                        {
                            s.ShiftStartHour = ssh;
                        }
                        break;

                    case "writeminintervalms":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && w >= 0)
                        {
                            s.WriteMinIntervalMs = w;
                        }
                        break;

                    case "mcpenabled":
                        if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase))
                        {
                            s.McpEnabled = false;
                        }
                        else if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase))
                        {
                            s.McpEnabled = true;
                        }
                        break;

                    case "mcpport":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mp) && mp >= 0 && mp <= 65535)
                        {
                            s.McpPort = mp;
                        }
                        break;
                }
            }
        }
        catch
        {
            // 设置文件坏了就用默认值,不能因此起不来
        }

        return s;
    }

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();

            sb.Append("; KyjScada 应用设置(修改后重启生效)\n");
            sb.Append("ArchiveIntervalSeconds=").Append(ArchiveIntervalSeconds).Append('\n');
            sb.Append("RetentionDays=").Append(RetentionDays).Append('\n');
            sb.Append("DefaultAlarmDeadband=").Append(DefaultAlarmDeadband.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("ShiftHours=").Append(ShiftHours).Append('\n');
            sb.Append("ShiftStartHour=").Append(ShiftStartHour).Append('\n');
            sb.Append("WriteMinIntervalMs=").Append(WriteMinIntervalMs).Append('\n');
            sb.Append("McpPort=").Append(McpPort).Append('\n');
            sb.Append("McpEnabled=").Append(McpEnabled ? "1" : "0").Append('\n');

            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 保存失败只影响下次启动的默认值
        }
    }
}