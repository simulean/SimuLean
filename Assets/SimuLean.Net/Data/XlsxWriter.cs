using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SimuLean.Data
{
    /// <summary>
    /// Minimal .xlsx (OOXML) writer using only System.IO.Compression (engine, no external library): string and
    /// numeric cells, bold headers, several sheets. Used by the result exports (<c>SimuLean.Results.ResultExport</c>).
    /// </summary>
    public class XlsxWriter
    {
        private readonly List<XlsxSheet> sheets = new List<XlsxSheet>();
        // Shared-string table (strings are stored once and referenced by index)
        private readonly List<string> sharedStrings = new List<string>();
        private readonly Dictionary<string, int> sharedStringIndex = new Dictionary<string, int>();

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Adds a sheet; the name is made valid for Excel (no <c>[]:*?/\</c>, at most 31 characters, unique).</summary>
        public XlsxSheet AddSheet(string name)
        {
            var sheet = new XlsxSheet(UniqueName(name));
            sheets.Add(sheet);
            return sheet;
        }

        /// <summary>Writes the workbook to <paramref name="filePath"/>.</summary>
        public void Save(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Pre-build shared-string table so indices are stable when BuildSheet runs
            sharedStrings.Clear();
            sharedStringIndex.Clear();
            foreach (var sheet in sheets)
                foreach (var row in sheet.Rows)
                    foreach (var cell in row)
                        if (cell.IsString && cell.StringValue != null)
                            GetOrAddString(cell.StringValue);

            using var fs  = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: Encoding.UTF8);

            WriteEntry(zip, "[Content_Types].xml",        BuildContentTypes());
            WriteEntry(zip, "_rels/.rels",                BuildRootRels());
            WriteEntry(zip, "xl/workbook.xml",            BuildWorkbook());
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", BuildWorkbookRels());
            WriteEntry(zip, "xl/styles.xml",              BuildStyles());
            WriteEntry(zip, "xl/sharedStrings.xml",       BuildSharedStrings());

            for (int i = 0; i < sheets.Count; i++)
                WriteEntry(zip, $"xl/worksheets/sheet{i + 1}.xml", BuildSheet(sheets[i]));
        }

        private string UniqueName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in string.IsNullOrWhiteSpace(name) ? "Sheet" : name) sb.Append("[]:*?/\\".IndexOf(c) >= 0 ? '_' : c);
            string baseName = sb.ToString().Trim('\'');
            if (baseName.Length == 0) baseName = "Sheet";
            if (baseName.Length > 31) baseName = baseName.Substring(0, 31);
            string candidate = baseName;
            for (int k = 2; sheets.Exists(x => string.Equals(x.Name, candidate, StringComparison.OrdinalIgnoreCase)); k++)
            {
                string suffix = " (" + k + ")";
                candidate = (baseName.Length + suffix.Length > 31 ? baseName.Substring(0, 31 - suffix.Length) : baseName) + suffix;
            }
            return candidate;
        }

        // ── OOXML builders ────────────────────────────────────────────────────

        private string BuildContentTypes()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            sb.Append("<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>");
            for (int i = 0; i < sheets.Count; i++)
                sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string BuildRootRels() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        private string BuildWorkbook()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<sheets>");
            for (int i = 0; i < sheets.Count; i++)
                sb.Append($"<sheet name=\"{XmlEsc(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            sb.Append("</sheets></workbook>");
            return sb.ToString();
        }

        private string BuildWorkbookRels()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 0; i < sheets.Count; i++)
                sb.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
            int next = sheets.Count + 1;
            sb.Append($"<Relationship Id=\"rId{next}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append($"<Relationship Id=\"rId{next + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string BuildStyles() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\">" +
              "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
              "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
            "</fonts>" +
            "<fills count=\"2\">" +
              "<fill><patternFill patternType=\"none\"/></fill>" +
              "<fill><patternFill patternType=\"gray125\"/></fill>" +
            "</fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"3\">" +
              "<xf numFmtId=\"0\" fontId=\"0\" xfId=\"0\"/>" +  // style 0: normal
              "<xf numFmtId=\"0\" fontId=\"1\" xfId=\"0\"/>" +  // style 1: bold
              "<xf numFmtId=\"4\" fontId=\"0\" xfId=\"0\"/>" +  // style 2: 2 decimal places
            "</cellXfs>" +
            "</styleSheet>";

        private string BuildSharedStrings()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append($"<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"{sharedStrings.Count}\" uniqueCount=\"{sharedStrings.Count}\">");
            foreach (var s in sharedStrings)
                sb.Append($"<si><t xml:space=\"preserve\">{XmlEsc(s)}</t></si>");
            sb.Append("</sst>");
            return sb.ToString();
        }

        private string BuildSheet(XlsxSheet sheet)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<sheetData>");

            for (int rowIdx = 0; rowIdx < sheet.Rows.Count; rowIdx++)
            {
                var row = sheet.Rows[rowIdx];
                int rowNum = rowIdx + 1;
                sb.Append($"<row r=\"{rowNum}\">");

                for (int colIdx = 0; colIdx < row.Count; colIdx++)
                {
                    var cell   = row[colIdx];
                    string ref_ = $"{ColLetter(colIdx)}{rowNum}";
                    string sAttr = cell.StyleIndex > 0 ? $" s=\"{cell.StyleIndex}\"" : "";

                    if (cell.IsString)
                    {
                        int ssIdx = GetOrAddString(cell.StringValue ?? "");
                        sb.Append($"<c r=\"{ref_}\" t=\"s\"{sAttr}><v>{ssIdx}</v></c>");
                    }
                    else
                    {
                        sb.Append($"<c r=\"{ref_}\"{sAttr}><v>{cell.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                    }
                }

                sb.Append("</row>");
            }

            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private int GetOrAddString(string s)
        {
            if (!sharedStringIndex.TryGetValue(s, out int idx))
            {
                idx = sharedStrings.Count;
                sharedStrings.Add(s);
                sharedStringIndex[s] = idx;
            }
            return idx;
        }

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
        }

        private static string ColLetter(int colIndex)
        {
            // Convert 0-based index to Excel column letter(s): 0→A, 25→Z, 26→AA …
            var result = "";
            for (int n = colIndex + 1; n > 0; n = (n - 1) / 26)
                result = (char)('A' + (n - 1) % 26) + result;
            return result;
        }

        private static string XmlEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;");
        }
    }

    // ── Sheet / Cell data models ───────────────────────────────────────────────

    public class XlsxSheet
    {
        public string Name { get; }
        public List<List<XlsxCell>> Rows { get; } = new List<List<XlsxCell>>();

        public XlsxSheet(string name) => Name = name;

        /// <summary>Add a row of mixed string/number values (strings auto-detected).</summary>
        public void AddRow(params object[] values)
        {
            var row = new List<XlsxCell>();
            foreach (var v in values)
            {
                if (v == null || v is string)
                    row.Add(new XlsxCell((string)v));
                else if (v is bool b)
                    row.Add(new XlsxCell(b ? "true" : "false"));
                else
                {
                    double d = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
                    // a cell cannot hold NaN / infinity: leave it empty
                    row.Add(double.IsNaN(d) || double.IsInfinity(d) ? new XlsxCell("") : new XlsxCell(d));
                }
            }
            Rows.Add(row);
        }

        /// <summary>Add a bold header row.</summary>
        public void AddHeaderRow(params string[] headers)
        {
            var row = new List<XlsxCell>();
            foreach (var h in headers)
                row.Add(new XlsxCell(h, styleIndex: 1));
            Rows.Add(row);
        }

        public void AddEmptyRow() => Rows.Add(new List<XlsxCell>());

        /// <summary>A table: its columns as a bold header, then its rows.</summary>
        public void AddTable(ResultTable table)
        {
            AddHeaderRow(new List<string>(table.Columns).ToArray());
            foreach (var r in table.Rows) AddRow(r);
        }
    }

    public class XlsxCell
    {
        public bool   IsString    { get; }
        public string StringValue { get; }
        public double NumberValue { get; }
        public int    StyleIndex  { get; }

        public XlsxCell(string value, int styleIndex = 0)
        {
            IsString    = true;
            StringValue = value;
            StyleIndex  = styleIndex;
        }

        public XlsxCell(double value, int styleIndex = 0)
        {
            IsString    = false;
            NumberValue = value;
            StyleIndex  = styleIndex;
        }
    }
}
