using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using KyjScada.Infrastructure.Export;

namespace KyjScada.Infrastructure.Tests;

/// <summary>
/// xlsx 写出器的结构验证。
///
/// 为什么这类"格式生成"必须测:
///  xlsx 是 zip 里的一堆 XML,写错一个关系 ID,Excel 的报错是"文件已损坏",
///  用户看到的就是"报表导出坏了",而代码看起来完全正常。
///  所以这里逐条读回:包结构、工作表数、表头文字、数字是否真的是数字。
/// </summary>
public static class XlsxWriterTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("Excel 导出:三个工作表结构与数值类型正确", () =>
        {
            var dir = Path.Combine(WorkDir(), "kyj_xlsx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "报表.xlsx");

            try
            {
                var detail = new XlsxSheet("明细");
                detail.TitleLines.Add("空压机站 明细数据");
                detail.Columns.Add(new XlsxColumn("时间", 20));
                detail.Columns.Add(new XlsxColumn("变量", 22));
                detail.Columns.Add(new XlsxColumn("平均", 12, true));
                detail.Rows.Add(new object?[] { "2026-03-10 17:33:57", "LQT_Level", 5.5 });
                detail.Rows.Add(new object?[] { "2026-03-10 17:33:58", "LQT_Level", null });

                var summary = new XlsxSheet("汇总");
                summary.Columns.Add(new XlsxColumn("变量", 22));
                summary.Columns.Add(new XlsxColumn("点数", 10, true));
                summary.Rows.Add(new object?[] { "LQT_Level", 5223 });

                XlsxWriter.Write(path, detail, summary);

                // 留一份样本在 _work 里,便于人工用 Excel/WPS 真打开验证
                File.Copy(path, Path.Combine(WorkDir(), "excel_导出样本.xlsx"), true);

                // 1) zip 能被打开,且必备部件齐全
                using var zip = ZipFile.OpenRead(path);

                var names = zip.Entries.Select(e => e.FullName).ToList();

                foreach (var required in new[]
                         {
                             "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml",
                             "xl/_rels/workbook.xml.rels", "xl/styles.xml",
                             "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml"
                         })
                {
                    TestHelpers.AssertTrue(names.Contains(required), "缺少部件 " + required);
                }

                // 2) workbook 里确实是两张表,名字对得上
                var workbook = Load(zip, "xl/workbook.xml");
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

                var sheetNames = workbook.Root!.Element(ns + "sheets")!
                    .Elements(ns + "sheet")
                    .Select(e => e.Attribute("name")!.Value)
                    .ToList();

                TestHelpers.AssertEq(2, sheetNames.Count, "工作表数");
                TestHelpers.AssertStr("明细", sheetNames[0], "第一张表名");
                TestHelpers.AssertStr("汇总", sheetNames[1], "第二张表名");

                // 3) 明细表:标题行 + 表头 + 数值单元格类型
                var sheet1 = Load(zip, "xl/worksheets/sheet1.xml");
                var rows = sheet1.Root!.Element(ns + "sheetData")!.Elements(ns + "row").ToList();

                TestHelpers.AssertEq(4, rows.Count, "明细行数(标题+表头+2 数据行)");

                var headerTexts = rows[1].Elements(ns + "c")
                    .Select(c => c.Element(ns + "is")?.Element(ns + "t")?.Value ?? "")
                    .ToList();

                TestHelpers.AssertEq(3, headerTexts.Count, "表头列数");
                TestHelpers.AssertStr("时间", headerTexts[0], "第一列表头");
                TestHelpers.AssertStr("平均", headerTexts[2], "第三列表头");

                var dataCells = rows[2].Elements(ns + "c").ToList();
                var avgCell = dataCells[2];

                TestHelpers.AssertTrue(avgCell.Element(ns + "v") is not null, "平均列应写为数值 <v>");
                TestHelpers.AssertStr("5.5", avgCell.Element(ns + "v")!.Value, "平均数值");

                // 4) null 值必须写成空格子,而不是 0 —— 0 会被当成真实测量值
                var nullCells = rows[3].Elements(ns + "c").ToList();
                var third = nullCells[2];

                TestHelpers.AssertTrue(third.Element(ns + "v") is null, "空值不应写成数字");
                TestHelpers.AssertTrue(third.Element(ns + "is") is null, "空值不应写成文本");

                // 5) 汇总表第二个单元格是数字
                var sheet2 = Load(zip, "xl/worksheets/sheet2.xml");
                var sumRows = sheet2.Root!.Element(ns + "sheetData")!.Elements(ns + "row").ToList();
                var countCell = sumRows[1].Elements(ns + "c").ElementAt(1);

                TestHelpers.AssertStr("5223", countCell.Element(ns + "v")!.Value, "汇总点数");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        });

        run("Excel 导出:非法表名与控制字符不破坏文件", () =>
        {
            var dir = Path.Combine(WorkDir(), "kyj_xlsx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "脏值.xlsx");

            try
            {
                var sheet = new XlsxSheet("报表/汇总[1]");   // 非法字符 + 超长名

                sheet.Columns.Add(new XlsxColumn("备注", 30));
                sheet.Rows.Add(new object?[] { "含\u0001控制字符与<尖括号>&符号" });

                XlsxWriter.Write(path, sheet);

                using var zip = ZipFile.OpenRead(path);

                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

                var workbook = Load(zip, "xl/workbook.xml");
                var name = workbook.Root!.Element(ns + "sheets")!
                    .Elements(ns + "sheet").Single().Attribute("name")!.Value;

                TestHelpers.AssertTrue(!name.Contains('/') && !name.Contains('[') && !name.Contains(']'),
                    "表名里的非法字符应被剔除,实际=" + name);
                TestHelpers.AssertTrue(name.Length <= 31, "表名应不超过 31 字符");

                // XML 必须能解析(控制字符被丢掉,不会让 Excel 说文件损坏)
                // Descendants("t") 第一个是表头"备注",数据文本在最后一个
                var sheetXml = Load(zip, "xl/worksheets/sheet1.xml");
                var text = sheetXml.Descendants(ns + "t").Last().Value;

                TestHelpers.AssertTrue(text.Contains("尖括号"), "文本应保留");
                TestHelpers.AssertTrue(!text.Contains('\u0001'), "控制字符应被剔除");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        });
    }

    private static XDocument Load(ZipArchive zip, string entry)
    {
        using var stream = zip.GetEntry(entry)!.Open();

        return XDocument.Load(stream);
    }

    /// <summary>测试工作目录。用程序目录下的 _work,不用系统 TEMP(现场机器上 TEMP 常被策略限制)。</summary>
    private static string WorkDir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "_work");

        Directory.CreateDirectory(dir);

        return dir;
    }
}