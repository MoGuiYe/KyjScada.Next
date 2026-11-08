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

    /// <summary>
    /// 是否启动内嵌 MCP 服务(关掉时端口保持不变,下次打开不用重填)。
    /// **默认关闭**:MCP 带写设备的能力,没在设置里明确打开之前不应该自己监听端口。
    /// 需要外部 AI 客户端读写时,在设置窗口勾选并保存,或把 McpEnabled=1 写进 app_settings.ini。
    /// </summary>
    public bool McpEnabled { get; set; } = false;

    /// <summary>
    /// 读取设置时遇到"认不出但也不致命"的值(例如写错的关键字),记在这里。
    /// 界面启动后可以提示一句,免得现场改了 ini 却不知道根本没生效。
    /// </summary>
    public List<string> Warnings { get; } = new ();

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

    /// <summary>宽松解析布尔开关:1/0、true/false、on/off、yes/no(忽略大小写与首尾空白)。</summary>
    private static bool TryParseFlag (string value, out bool result)
    {
        switch (value.Trim ().ToLowerInvariant ())
        {
            case "1": case "true": case "on": case "yes": case "启用":
                result = true;
                return true;
            case "0": case "false": case "off": case "no": case "禁用": case "":
                result = false;
                return true;
            default:
                result = false;
                return false;
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
                        // 现场写法五花八门:off/no/0/False/禁用、on/yes/1/True/启用 都见过。
                        // 早先只认 "0"/"false"/"1"/"true",写 off 的人以为关掉了,
                        // 实际仍按默认值走 —— 现在默认是关,但"写 on 却没生效"同样会让人困惑。
                        if (TryParseFlag (value, out var enabled))
                        {
                            s.McpEnabled = enabled;
                        }
                        else
                        {
                            s.Warnings.Add ("McpEnabled 的值 \"" + value
                                + "\" 无法识别,按关闭处理。可用 1/0、true/false、on/off、yes/no。");

                            s.McpEnabled = false;
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

    /// <summary>
    /// 保存设置。返回 null 表示成功,否则是失败原因。
    /// 以前这里把异常吞掉,设置窗口照样提示"已保存" —— 磁盘满/无权限时用户以为生效了。
    /// </summary>
    public string? Save ()
    {
        // 先写临时文件再替换:直接覆盖写一半掉电,会把设置文件留成空文件,
        // 下次启动就全回落默认值(归档周期、保留期一起丢)。
        var tmp = FilePath + ".tmp";

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

            File.WriteAllText (tmp, sb.ToString (), new UTF8Encoding (false));

            File.Move (tmp, FilePath, overwrite: true);

            return null;
        }
        catch (Exception ex)
        {
            // 失败的 .tmp 要清掉:rtu-settings.ini 旁边长期躺着一个同内容的 .tmp
            // 既让人以为"设置没保存成功"反复点,也可能被下次启动误读成正式配置。
            try
            {
                if (File.Exists (tmp)) File.Delete (tmp);
            }
            catch
            {
                // 清理失败不改变返回给调用方的错误
            }

            return ex.Message;
        }
    }
}