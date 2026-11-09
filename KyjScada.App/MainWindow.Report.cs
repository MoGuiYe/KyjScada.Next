using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using KyjScada.Domain.History;
using KyjScada.Infrastructure.Export;
using KyjScada.Runtime;

namespace KyjScada.App;

/// <summary>报表一行:一个变量在一段时间桶里的统计。</summary>
public sealed class ReportRow
{
    public string TimeText { get; set; } = "";
    public string VarName { get; set; } = "";

    /// <summary>变量中文描述(报表里只写 LQB1_Fre_Set 无法交班、无法核对)。</summary>
    public string Comments { get; set; } = "";

    public string Unit { get; set; } = "";
    public string MinText { get; set; } = "";
    public string MaxText { get; set; } = "";
    public string AvgText { get; set; } = "";
    public string CountText { get; set; } = "";
}

public partial class MainWindow
{
    private readonly List<ReportRow> _reportRows = new();

    /// <summary>
    /// 报表查询/导出进行中。查询要读几十万行、导出要拼几万个单元格,
    /// 期间必须挡住第二次点击:重入会让两遍查询互相覆盖结果,导出也会写出半截文件。
    /// </summary>
    private bool _reportBusy;

    /// <summary>
    /// 初始化报表页:变量列表只列**归档变量**(没归档的查了也是空),
    /// 时间默认今天零点到现在。
    /// </summary>
    private void InitReport()
    {
        ReportVarList.Items.Clear();

        if (_device?.GroupList is not null)
        {
            foreach (var g in _device.GroupList)
            {
                if (g.VarList is null) continue;

                foreach (var v in g.VarList)
                {
                    if (string.IsNullOrEmpty(v.VarName)) continue;
                    if (!v.ArchiveEnable) continue;

                    ReportVarList.Items.Add(v.VarName);
                }
            }
        }

        var now = DateTime.Now;

        ReportFromBox.Text = now.Date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        ReportToBox.Text = now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        if (ReportGrainBox.SelectedIndex < 0) ReportGrainBox.SelectedIndex = 0;

        ReportSelectedText.Text = "已选 0 个";
        ReportFooterText.Text = "选择变量和时间范围后点「查询」。报表按时间桶聚合,取每桶的最小/最大/平均。";
    }

    private void OnReportVarSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ReportSelectedText.Text = "已选 " + ReportVarList.SelectedItems.Count + " 个";
    }

    private void OnReportSelectAll(object sender, RoutedEventArgs e)
    {
        ReportVarList.SelectAll();
        ReportSelectedText.Text = "已选 " + ReportVarList.SelectedItems.Count + " 个";
    }

    private void OnReportClearVars(object sender, RoutedEventArgs e)
    {
        ReportVarList.UnselectAll();
        ReportSelectedText.Text = "已选 0 个";
    }

    /// <summary>时间桶长度(秒)。按班用设置里的班次小时数,不写死 8 小时。</summary>
    private double ReportIntervalSeconds()
    {
        return ReportGrainBox.SelectedIndex switch
        {
            1 => 86400,
            2 => Math.Max(1, _appSettings.ShiftHours) * 3600.0,
            3 => 30 * 86400.0,
            _ => 3600
        };
    }

    private string GrainName(int index)
    {
        return index switch
        {
            1 => "按天",
            2 => "按班(" + _appSettings.ShiftHours + "h,早班 " + _appSettings.ShiftStartHour + ":00 起)",
            3 => "按月(30天)",
            _ => "按小时"
        };
    }

    /// <summary>
    /// 把起点对齐到班次边界。
    /// 班报必须按整班统计,否则第一班是"半天",和后续班次不可比。
    /// </summary>
    private DateTime AlignToShiftStart(DateTime t)
    {
        var hours = Math.Max(1, Math.Min(24, _appSettings.ShiftHours));
        var startHour = Math.Max(0, Math.Min(23, _appSettings.ShiftStartHour));

        // 以"今天 startHour 点"为基准,按 hours 向下取整
        var anchor = t.Date.AddHours(startHour);

        if (t < anchor) anchor = anchor.AddDays(-1);

        var diffHours = (t - anchor).TotalHours;
        var bucket = Math.Floor(diffHours / hours);

        return anchor.AddHours(bucket * hours);
    }

    private async void OnRunReport(object sender, RoutedEventArgs e)
    {
        if (_reportBusy) return;

        if (_historyRepo is null)
        {
            ReportFooterText.Text = "历史库不可用";
            return;
        }

        if (!HasPermission(KyjScada.Domain.Users.PermissionBit.CanViewReport))
        {
            ReportFooterText.Text = "没有「报表查看」权限,请用有权限的账号登录";
            return;
        }

        if (!TryParseReportTime(ReportFromBox.Text, out var from)
            || !TryParseReportTime(ReportToBox.Text, out var to))
        {
            ReportFooterText.Text = "时间格式应为 yyyy-MM-dd HH:mm";
            return;
        }

        if (to <= from)
        {
            ReportFooterText.Text = "结束时间必须晚于开始时间";
            return;
        }

        var vars = ReportVarList.SelectedItems.OfType<string>().ToList();

        if (vars.Count == 0)
        {
            ReportFooterText.Text = "请至少选择一个变量";
            return;
        }

        var interval = ReportIntervalSeconds();
        var grains = ReportGrainBox.SelectedIndex;

        // 班报对齐到整班起点,否则首班只是"半天",和后续班次不可比
        if (grains == 2) from = AlignToShiftStart(from);

        // 单位只在 UI 线程取一次:FindVarConfig 走的是界面侧的组态缓存,
        // 不能拿进后台线程用(那边的集合不保证线程安全)。
        var units = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);

        foreach (var v in vars) units[v] = FindVarConfig (v)?.Unit ?? "";

        _reportBusy = true;
        SetReportBusy (true);

        try
        {
            ReportFooterText.Text = "正在查询,请稍候…";

            var fromL = from;
            var toL = to;

            // 查库搬进后台:一个变量能取 20 万行,十几个变量在 UI 线程上跑,
            // 窗口会整段白屏(连"查询"都点不动),操作员会以为程序卡死。
            var rows = await Task.Run (() => BuildReportRows (vars, units, fromL, toL, interval, grains));

            _reportRows.Clear();
            _reportRows.AddRange(rows);

            ReportGrid.ItemsSource = null;
            ReportGrid.ItemsSource = _reportRows;

            ReportInfoText.Text = vars.Count + " 变量 · " + _reportRows.Count + " 行 · "
                + GrainName(grains);

            ReportFooterText.Text = _reportRows.Count == 0
                ? "该时间范围内没有归档数据"
                : "共 " + _reportRows.Count + " 行," + from.ToString("MM-dd HH:mm")
                  + " ~ " + to.ToString("MM-dd HH:mm");
        }
        catch (Exception ex)
        {
            ReportFooterText.Text = "查询失败: " + ex.Message;
        }
        finally
        {
            _reportBusy = false;
            SetReportBusy (false);
        }
    }

    /// <summary>
    /// 后台线程里跑报表查询:查库 + 聚合 + 拼行,全程不碰界面元素。
    /// 返回的行集合由调用方在 UI 线程上替换进 _reportRows。
    /// </summary>
    private List<ReportRow> BuildReportRows (
        List<string> vars,
        Dictionary<string, string> units,
        DateTime from,
        DateTime to,
        double interval,
        int grains)
    {
        var rows = new List<ReportRow> ();

        foreach (var varName in vars)
        {
            var raw = _historyRepo!.Query (varName, from, to, 200000);

            // 复用趋势聚合器:它已经把每个桶的最小/最大/平均算好了
            var buckets = TrendAggregator.Aggregate (raw, from, to, interval, TrendAggregate.Average);

            units.TryGetValue (varName, out var unit);

            foreach (var b in buckets)
            {
                if (b.IsEmpty) continue;

                rows.Add (new ReportRow
                {
                    TimeText = grains == 1 || grains == 3
                        ? b.Time.ToString ("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : b.Time.ToString ("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    VarName = varName,
                    Comments = CommentsOf (varName),
                    Unit = unit ?? "",
                    MinText = b.Min.ToString ("G6", CultureInfo.InvariantCulture),
                    MaxText = b.Max.ToString ("G6", CultureInfo.InvariantCulture),
                    AvgText = b.Value.ToString ("G6", CultureInfo.InvariantCulture),
                    CountText = b.Count.ToString (CultureInfo.InvariantCulture)
                });
            }
        }

        return rows;
    }

    /// <summary>查询/导出期间禁用三个按钮,并给出可见提示。</summary>
    private void SetReportBusy (bool busy)
    {
        RunReportButton.IsEnabled = !busy;
        ExportReportButton.IsEnabled = !busy;
        ExportReportExcelButton.IsEnabled = !busy;
    }

    private static bool TryParseReportTime(string? text, out DateTime value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(text)) return false;

        return DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out value);
    }

    /// <summary>
    /// 导出报表 CSV。带 BOM 让中文 Excel 直接打开不乱码;字段含逗号时加引号。
    /// </summary>
    private void OnExportReport(object sender, RoutedEventArgs e)
    {
        if (_reportBusy) return;

        if (!HasPermission(KyjScada.Domain.Users.PermissionBit.CanViewReport))
        {
            ReportFooterText.Text = "没有「报表查看」权限";
            return;
        }

        if (_reportRows.Count == 0)
        {
            ReportFooterText.Text = "没有可导出的报表数据,请先查询";
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出报表",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = "报表_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();

            sb.Append("时间,变量,单位,最小,最大,平均,点数\n");

            foreach (var r in _reportRows)
            {
                sb.Append(Csv(r.TimeText)).Append(',')
                  .Append(Csv(r.VarName)).Append(',')
                  .Append(Csv(r.Unit)).Append(',')
                  .Append(r.MinText).Append(',')
                  .Append(r.MaxText).Append(',')
                  .Append(r.AvgText).Append(',')
                  .Append(r.CountText).Append('\n');
            }

            var tmp = dlg.FileName + ".tmp";

            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));

            // 原子替换(同卷原子,失败不丢原文件),收尾细节见 AtomicFileWrite
            AtomicFileWrite.Replace(tmp, dlg.FileName);

            ReportFooterText.Text = "已导出 " + _reportRows.Count + " 行 → " + dlg.FileName;
        }
        catch (Exception ex)
        {
            ReportFooterText.Text = "导出失败: " + ex.Message;
        }
    }

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        return s.IndexOfAny(new[] { ',', '"', '\n' }) < 0
            ? s
            : "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    // ==================== Excel 导出 ====================

    /// <summary>
    /// 导出 .xlsx。按模板出表:
    ///   0 标准(三表)= 明细 + 汇总 + 班次
    ///   1 仅明细     = 给需要自己透视的人
    ///   2 仅汇总     = 一页纸交给领导
    ///
    /// 为什么自己算汇总而不是让 Excel 公式算:
    ///  导出的表常被转发、被另存、被上传到别的系统,公式一断链就是一堆 #REF!。
    ///  数字写死比公式可靠。
    /// </summary>
    private async void OnExportReportExcel(object sender, RoutedEventArgs e)
    {
        if (_reportBusy) return;

        if (!HasPermission(KyjScada.Domain.Users.PermissionBit.CanViewReport))
        {
            ReportFooterText.Text = "没有「报表查看」权限";

            return;
        }

        if (_reportRows.Count == 0)
        {
            ReportFooterText.Text = "没有可导出的报表数据,请先查询";

            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出 Excel 报表",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            FileName = "报表_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".xlsx"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var mode = ReportTemplateBox?.SelectedIndex ?? 0;
            var sheets = new List<XlsxSheet>();

            // 三个工作表在 UI 线程拼:Build*Sheet 和 AddTitleLines 会读界面上的
            // 时间框与模板选择,拿进后台线程就是跨线程访问 UI。
            if (mode != 2) sheets.Add (BuildDetailSheet ());
            if (mode != 1) sheets.Add (BuildSummarySheet ());
            if (mode == 0) sheets.Add (BuildShiftSheet ());

            if (sheets.Count == 0) sheets.Add (BuildDetailSheet ());

            var path = dlg.FileName;
            var arr = sheets.ToArray ();

            _reportBusy = true;
            SetReportBusy (true);
            ReportFooterText.Text = "正在生成 Excel,请稍候…";

            // 写盘搬后台:几万个单元格要逐个生成 XML 再压缩成 zip,
            // 期间界面完全无响应,连"导出完成"的提示都要等它结束才刷得出来。
            await Task.Run (() => XlsxWriter.Write (path, arr));

            ReportFooterText.Text = "已导出 " + _reportRows.Count + " 行 · " + sheets.Count
                + " 个工作表 → " + dlg.FileName;
        }
        catch (Exception ex)
        {
            ReportFooterText.Text = "导出失败: " + ex.Message;
        }
        finally
        {
            _reportBusy = false;
            SetReportBusy (false);
        }
    }

    private XlsxSheet BuildDetailSheet ()
    {
        var sheet = new XlsxSheet ("明细");

        AddTitleLines (sheet, "明细数据");

        sheet.Columns.Add (new XlsxColumn ("时间", 20));
        sheet.Columns.Add (new XlsxColumn ("变量", 22));
        sheet.Columns.Add (new XlsxColumn ("单位", 8));
        sheet.Columns.Add (new XlsxColumn ("最小", 12, true));
        sheet.Columns.Add (new XlsxColumn ("最大", 12, true));
        sheet.Columns.Add (new XlsxColumn ("平均", 12, true));
        sheet.Columns.Add (new XlsxColumn ("点数", 8, true));

        foreach (var r in _reportRows)
        {
            sheet.Rows.Add (new object?[]
            {
                r.TimeText, r.VarName, r.Unit,
                Num (r.MinText), Num (r.MaxText), Num (r.AvgText),
                int.TryParse (r.CountText, out var n) ? n : 0
            });
        }

        return sheet;
    }

    private XlsxSheet BuildSummarySheet ()
    {
        var sheet = new XlsxSheet ("汇总");

        AddTitleLines (sheet, "按变量汇总");

        sheet.Columns.Add (new XlsxColumn ("变量", 22));
        sheet.Columns.Add (new XlsxColumn ("单位", 8));
        sheet.Columns.Add (new XlsxColumn ("点数", 10, true));
        sheet.Columns.Add (new XlsxColumn ("最小", 12, true));
        sheet.Columns.Add (new XlsxColumn ("最大", 12, true));
        sheet.Columns.Add (new XlsxColumn ("平均", 12, true));

        // 同一变量可能跨多个时间桶,汇总必须重新合并,不能取第一个桶
        foreach (var g in _reportRows
                     .GroupBy (r => new { r.VarName, r.Unit })
                     .OrderBy (g => g.Key.VarName, StringComparer.Ordinal))
        {
            var values = g.ToList ();
            var mins = values.Select (r => Num (r.MinText)).Where (v => v is not null).Select (v => v!.Value).ToList ();
            var maxs = values.Select (r => Num (r.MaxText)).Where (v => v is not null).Select (v => v!.Value).ToList ();
            var avgs = values.Select (r => Num (r.AvgText)).Where (v => v is not null).Select (v => v!.Value).ToList ();
            var counts = values.Sum (r => int.TryParse (r.CountText, out var n) ? n : 0);

            sheet.Rows.Add (new object?[]
            {
                g.Key.VarName,
                g.Key.Unit,
                counts,
                mins.Count > 0 ? mins.Min () : null,
                maxs.Count > 0 ? maxs.Max () : null,
                avgs.Count > 0 ? avgs.Average () : null
            });
        }

        return sheet;
    }

    private XlsxSheet BuildShiftSheet ()
    {
        var sheet = new XlsxSheet ("班次");

        AddTitleLines (sheet, "按班次汇总(班长 " + _appSettings.ShiftHours + " 小时,"
            + _appSettings.ShiftStartHour.ToString (CultureInfo.InvariantCulture) + " 点起班)");

        sheet.Columns.Add (new XlsxColumn ("班次", 24));
        sheet.Columns.Add (new XlsxColumn ("变量", 22));
        sheet.Columns.Add (new XlsxColumn ("单位", 8));
        sheet.Columns.Add (new XlsxColumn ("点数", 10, true));
        sheet.Columns.Add (new XlsxColumn ("最小", 12, true));
        sheet.Columns.Add (new XlsxColumn ("最大", 12, true));
        sheet.Columns.Add (new XlsxColumn ("平均", 12, true));

        var buckets = new List<(string Shift, ReportRow Row)> ();

        foreach (var r in _reportRows)
        {
            if (!DateTime.TryParse (r.TimeText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            {
                continue;
            }

            buckets.Add ((ShiftLabel (t), r));
        }

        foreach (var g in buckets
                     .GroupBy (b => new { b.Shift, b.Row.VarName, b.Row.Unit })
                     .OrderBy (g => g.Key.Shift, StringComparer.Ordinal)
                     .ThenBy (g => g.Key.VarName, StringComparer.Ordinal))
        {
            var values = g.Select (b => b.Row).ToList ();
            var mins = values.Select (r => Num (r.MinText)).Where (v => v is not null).Select (v => v!.Value).ToList ();
            var maxs = values.Select (r => Num (r.MaxText)).Where (v => v is not null).Select (v => v!.Value).ToList ();
            var avgs = values.Select (r => Num (r.AvgText)).Where (v => v is not null).Select (v => v!.Value).ToList ();

            sheet.Rows.Add (new object?[]
            {
                g.Key.Shift,
                g.Key.VarName,
                g.Key.Unit,
                values.Sum (r => int.TryParse (r.CountText, out var n) ? n : 0),
                mins.Count > 0 ? mins.Min () : null,
                maxs.Count > 0 ? maxs.Max () : null,
                avgs.Count > 0 ? avgs.Average () : null
            });
        }

        return sheet;
    }

    /// <summary>班次标签:「03-10 第1班 08:00~16:00」,跨天也看得懂。</summary>
    private string ShiftLabel (DateTime t)
    {
        var start = AlignToShiftStart (t);
        var hours = Math.Max (1, Math.Min (24, _appSettings.ShiftHours));
        var index = Math.Max (0, (int)Math.Floor ((t - AlignToShiftStart (t.Date.AddHours (_appSettings.ShiftStartHour))).TotalHours / hours));
        var end = start.AddHours (hours);

        return start.ToString ("MM-dd", CultureInfo.InvariantCulture)
               + " 第" + (index + 1) + "班 "
               + start.ToString ("HH:mm", CultureInfo.InvariantCulture) + "~"
               + end.ToString ("HH:mm", CultureInfo.InvariantCulture);
    }

    private void AddTitleLines (XlsxSheet sheet, string what)
    {
        var device = _device?.DeviceName ?? "";

        sheet.TitleLines.Add ((device.Length > 0 ? device + " " : "") + what);
        sheet.TitleLines.Add ("范围 " + (ReportFromBox?.Text ?? "") + " ~ " + (ReportToBox?.Text ?? "")
            + "  ·  " + ReportGrainBox?.Text + "  ·  导出于 "
            + DateTime.Now.ToString ("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
    }

    private static double? Num (string? text)
    {
        if (string.IsNullOrWhiteSpace (text)) return null;

        return double.TryParse (text.Trim (), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }
}