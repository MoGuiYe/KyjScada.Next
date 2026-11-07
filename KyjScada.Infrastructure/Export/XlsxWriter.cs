using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace KyjScada.Infrastructure.Export;

/// <summary>一列:标题、宽度、数字格式。</summary>
public sealed class XlsxColumn
{
    public XlsxColumn(string title, double width = 16, bool numeric = false)
    {
        Title = title;
        Width = width;
        Numeric = numeric;
    }

    public string Title { get; }
    public double Width { get; }

    /// <summary>true = 该列按数字写(right-aligned),否则按文本。</summary>
    public bool Numeric { get; }
}

/// <summary>一个工作表:列定义 + 行数据 + 标题行(可为空)。</summary>
public sealed class XlsxSheet
{
    public XlsxSheet(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>表头上方的标题行(如"XX 空压机站 运行报表"),每行一个字符串。</summary>
    public List<string> TitleLines { get; } = new();

    public List<XlsxColumn> Columns { get; } = new();

    /// <summary>行数据:与 Columns 等长;null 写空格子,string 写文本,其余按数字。</summary>
    public List<object?[]> Rows { get; } = new();

    /// <summary>是否冻结表头、加自动筛选。</summary>
    public bool FreezeHeader { get; set; } = true;
}

/// <summary>
/// 极简 xlsx 写出器 —— 不引入任何第三方库,直接写 OOXML(一个 zip 里放几个 XML)。
///
/// 为什么不引 EPPlus/ClosedXML/NPOI:
///  本系统要发到现场工控机,装机不联网。多一个包就多一份"上线时装不上"的风险,
///  而报表导出只需要"表格 + 数字 + 中文 + 列宽",OOXML 手写两百行就够。
///
/// 取舍:
///  · 文本一律 inlineStr,不建 sharedStrings —— 少一个表,导出更快;
///  · 数字按 double 原样写,Excel 里可直接求和、画图;
///  · 只支持一个默认样式 + 表头加粗底色,够用即可。
/// </summary>
public static class XlsxWriter
{
    public static void Write(string path, params XlsxSheet[] sheets)
    {
        if (sheets.Length == 0) throw new ArgumentException("至少需要一个工作表", nameof(sheets));

        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Add(zip, "[Content_Types].xml", ContentTypes(sheets.Length));
                Add(zip, "_rels/.rels", RootRels());
                Add(zip, "xl/workbook.xml", Workbook(sheets));
                Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Length));
                Add(zip, "xl/styles.xml", Styles());

                for (var i = 0; i < sheets.Length; i++)
                {
                    Add(zip, "xl/worksheets/sheet" + (i + 1) + ".xml", Sheet(sheets[i]));
                }
            }

            // 先写临时文件再原子替换:导出到一半断电不会留下半截 xlsx
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, null);
                }
                catch
                {
                    // Replace 在部分文件系统/被占用时不可用。先试覆盖式移动(同卷内原子),
                    // 实在不行才"删除后移动" —— 那一步不原子,失败时旧报表就没了,
                    // 所以只作为最后手段。
                    try
                    {
                        File.Move(tmp, path, overwrite: true);
                    }
                    catch
                    {
                        File.Delete(path);
                        File.Move(tmp, path);
                    }
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch
        {
            // 失败时别把 .tmp 留在导出目录里:现场多半会反复点"导出",
            // 每次留一个几十 MB 的临时文件,最后把磁盘塞满。
            TryDelete(tmp);

            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉就算了:不能因为清理临时文件而盖掉原始异常
        }
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);

        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        writer.Write(content);
    }

    private static string ContentTypes(int sheetCount)
    {
        var sb = new StringBuilder();

        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");

        for (var i = 1; i <= sheetCount; i++)
        {
            sb.Append("<Override PartName=\"/xl/worksheets/sheet").Append(i)
              .Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }

        sb.Append("</Types>");

        return sb.ToString();
    }

    private static string RootRels()
    {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
               "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
               "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
               "</Relationships>";
    }

    private static string Workbook(XlsxSheet[] sheets)
    {
        var sb = new StringBuilder();

        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
        sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");

        for (var i = 0; i < sheets.Length; i++)
        {
            sb.Append("<sheet name=\"").Append(EscapeAttr(SafeName(sheets[i].Name, i)))
              .Append("\" sheetId=\"").Append(i + 1).Append("\" r:id=\"rId").Append(i + 1).Append("\"/>");
        }

        sb.Append("</sheets></workbook>");

        return sb.ToString();
    }

    private static string WorkbookRels(int sheetCount)
    {
        var sb = new StringBuilder();

        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");

        for (var i = 1; i <= sheetCount; i++)
        {
            sb.Append("<Relationship Id=\"rId").Append(i)
              .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet")
              .Append(i).Append(".xml\"/>");
        }

        sb.Append("<Relationship Id=\"rId").Append(sheetCount + 1)
          .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");

        sb.Append("</Relationships>");

        return sb.ToString();
    }

    private static string Styles()
    {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
               "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
               "<fonts count=\"3\">" +
               "<font><sz val=\"11\"/><name val=\"宋体\"/></font>" +
               "<font><b/><sz val=\"11\"/><name val=\"宋体\"/></font>" +
               "<font><b/><sz val=\"13\"/><name val=\"宋体\"/></font>" +
               "</fonts>" +
               "<fills count=\"3\">" +
               "<fill><patternFill patternType=\"none\"/></fill>" +
               "<fill><patternFill patternType=\"gray125\"/></fill>" +
               "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDCE6F1\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
               "</fills>" +
               "<borders count=\"2\">" +
               "<border><left/><right/><top/><bottom/><diagonal/></border>" +
               "<border><left style=\"thin\"><color rgb=\"FFB0B0B0\"/></left><right style=\"thin\"><color rgb=\"FFB0B0B0\"/></right>" +
               "<top style=\"thin\"><color rgb=\"FFB0B0B0\"/></top><bottom style=\"thin\"><color rgb=\"FFB0B0B0\"/></bottom><diagonal/></border>" +
               "</borders>" +
               "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
               "<cellXfs count=\"4\">" +
               "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" applyBorder=\"1\"/>" +
               "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\">" +
               "<alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" +
               "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" applyFont=\"1\"/>" +
               "<xf numFmtId=\"2\" fontId=\"0\" fillId=\"0\" borderId=\"1\" applyBorder=\"1\" applyNumberFormat=\"1\"/>" +
               "</cellXfs>" +
               // 必须有 cellStyleXfs + cellStyles(Normal),否则 openpyxl/WPS 会提示"缺少默认样式"
               "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
               "</styleSheet>";
    }

    private static string Sheet(XlsxSheet sheet)
    {
        var sb = new StringBuilder();

        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        if (sheet.FreezeHeader && sheet.TitleLines.Count + 1 > 0)
        {
            var split = sheet.TitleLines.Count + 1;

            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"").Append(split)
              .Append("\" topLeftCell=\"A").Append(split + 1)
              .Append("\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        }

        sb.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");

        sb.Append("<cols>");

        for (var i = 0; i < sheet.Columns.Count; i++)
        {
            sb.Append("<col min=\"").Append(i + 1).Append("\" max=\"").Append(i + 1)
              .Append("\" width=\"").Append(sheet.Columns[i].Width.ToString("0.#", CultureInfo.InvariantCulture))
              .Append("\" customWidth=\"1\"/>");
        }

        sb.Append("</cols><sheetData>");

        var row = 0;

        // 标题行:合并到整行宽度
        foreach (var title in sheet.TitleLines)
        {
            row++;

            sb.Append("<row r=\"").Append(row).Append("\">");
            sb.Append("<c r=\"A").Append(row).Append("\" s=\"2\" t=\"inlineStr\"><is><t>")
              .Append(Escape(title)).Append("</t></is></c>");
            sb.Append("</row>");
        }

        // 表头
        row++;

        sb.Append("<row r=\"").Append(row).Append("\">");

        for (var c = 0; c < sheet.Columns.Count; c++)
        {
            sb.Append("<c r=\"").Append(Cell(c, row)).Append("\" s=\"1\" t=\"inlineStr\"><is><t>")
              .Append(Escape(sheet.Columns[c].Title)).Append("</t></is></c>");
        }

        sb.Append("</row>");

        var headerRow = row;

        foreach (var data in sheet.Rows)
        {
            row++;

            sb.Append("<row r=\"").Append(row).Append("\">");

            for (var c = 0; c < sheet.Columns.Count && c < data.Length; c++)
            {
                var value = data[c];

                var reference = Cell(c, row);

                if (value is null)
                {
                    sb.Append("<c r=\"").Append(reference).Append("\" s=\"0\"/>");

                    continue;
                }

                if (value is double || value is float || value is int || value is long || value is decimal)
                {
                    sb.Append("<c r=\"").Append(reference).Append("\" s=\"3\"><v>")
                      .Append(Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture))
                      .Append("</v></c>");

                    continue;
                }

                sb.Append("<c r=\"").Append(reference).Append("\" s=\"0\" t=\"inlineStr\"><is><t>")
                  .Append(Escape(value.ToString() ?? ""))
                  .Append("</t></is></c>");
            }

            sb.Append("</row>");
        }

        sb.Append("</sheetData>");

        if (sheet.FreezeHeader && sheet.Rows.Count > 0)
        {
            sb.Append("<autoFilter ref=\"A").Append(headerRow).Append(':')
              .Append(ColumnName(sheet.Columns.Count > 0 ? sheet.Columns.Count - 1 : 0))
              .Append(row).Append("\"/>");
        }

        sb.Append("</worksheet>");

        return sb.ToString();
    }

    /// <summary>列号 → 字母(A..Z, AA..)。</summary>
    private static string ColumnName(int index)
    {
        var name = "";
        var n = index;

        do
        {
            name = (char)('A' + n % 26) + name;
            n = n / 26 - 1;
        }
        while (n >= 0);

        return name;
    }

    private static string Cell(int column, int row) => ColumnName(column) + row.ToString(CultureInfo.InvariantCulture);

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;

                // xlsx 不接受 XML 非法控制字符,直接丢掉(老库脏值里真有)
                default:
                    if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') break;

                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    private static string EscapeAttr(string text) => Escape(text).Replace("\"", "&quot;");

    /// <summary>工作表名限制:31 字符、不能含 \ / ? * [ ] :。</summary>
    private static string SafeName(string name, int index)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? "Sheet" + (index + 1) : name.Trim();

        foreach (var ch in "\\/?*[]:")
        {
            clean = clean.Replace(ch.ToString(), "", StringComparison.Ordinal);
        }

        return clean.Length > 31 ? clean[..31] : clean;
    }
}