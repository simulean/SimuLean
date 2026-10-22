using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SimuLean.Data
{
    /// <summary>
    /// Reads a text table into the column-oriented form the elements use (column name → values, one per row; the
    /// first line is the header): <c>.csv</c> (comma-separated, double-quoted fields may hold commas, quotes as
    /// <c>""</c> and line breaks) and <c>.data</c> (whitespace-separated). Excel workbooks are read in the Unity
    /// layer (the engine has no Excel dependency) and handed over as the same table.
    /// </summary>
    public static class TableReader
    {
        /// <summary>Reads <paramref name="path"/> by its extension (<c>.csv</c> or <c>.data</c>).</summary>
        public static Dictionary<string, List<string>> Read(string path)
        {
            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            switch (ext)
            {
                case "csv": return Columns(ParseCsv(File.ReadAllText(path)));
                case "data": return Columns(ParseWhitespace(File.ReadAllText(path)));
                default: throw new NotSupportedException($"Table file '{path}': type '.{ext}' is not read by the engine (use .csv or .data; Excel is read in Unity).");
            }
        }

        /// <summary>Rows → columns. Short rows leave their missing cells empty; a duplicated header fails.</summary>
        public static Dictionary<string, List<string>> Columns(List<List<string>> rows)
        {
            var table = new Dictionary<string, List<string>>();
            if (rows.Count == 0) return table;
            List<string> header = rows[0];
            var columns = new List<List<string>>();
            for (int c = 0; c < header.Count; c++)
            {
                string name = header[c].Trim();
                if (name.Length == 0) name = "Column" + c;
                if (table.ContainsKey(name)) throw new FormatException($"Table: column '{name}' appears twice in the header.");
                var col = new List<string>();
                table[name] = col;
                columns.Add(col);
            }
            for (int r = 1; r < rows.Count; r++)
            {
                List<string> row = rows[r];
                if (row.Count == 1 && row[0].Trim().Length == 0) continue;   // blank line
                for (int c = 0; c < columns.Count; c++)
                    columns[c].Add(c < row.Count ? row[c].Trim() : "");
            }
            return table;
        }

        /// <summary>CSV rows (RFC 4180 quoting). The separator is ',' unless the header line has more ';' (Excel in a
        /// decimal-comma locale) or only tabs (such a file became one column named "a;b;c").</summary>
        public static List<List<string>> ParseCsv(string text) => ParseCsv(text, DetectSeparator(text));

        /// <summary>The separator of a CSV text, from its first line (outside quotes): ',' / ';' / tab.</summary>
        public static char DetectSeparator(string text)
        {
            int comma = 0, semicolon = 0, tab = 0;
            bool quoted = false;
            foreach (char ch in text ?? "")
            {
                if (ch == '"') quoted = !quoted;
                else if (quoted) continue;
                else if (ch == '\n') break;
                else if (ch == ',') comma++;
                else if (ch == ';') semicolon++;
                else if (ch == '\t') tab++;
            }
            if (semicolon > comma) return ';';
            if (comma == 0 && tab > 0) return '\t';
            return ',';
        }

        /// <summary>CSV rows with the given separator.</summary>
        public static List<List<string>> ParseCsv(string text, char separator)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false, any = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                any = true;
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else quoted = false;
                    }
                    else cell.Append(ch);
                    continue;
                }
                if (ch == separator) { row.Add(cell.ToString()); cell.Clear(); continue; }
                switch (ch)
                {
                    case '"': quoted = true; break;
                    case '\r': break;
                    case '\n':
                        row.Add(cell.ToString()); cell.Clear();
                        rows.Add(row); row = new List<string>(); any = false;
                        break;
                    default: cell.Append(ch); break;
                }
            }
            if (quoted) throw new FormatException("CSV: a quoted field is not closed.");
            if (any) { row.Add(cell.ToString()); rows.Add(row); }
            return rows;
        }

        /// <summary>Whitespace-separated rows (spaces or tabs, runs count as one separator).</summary>
        public static List<List<string>> ParseWhitespace(string text)
        {
            var rows = new List<List<string>>();
            foreach (string line in text.Split('\n'))
            {
                string l = line.TrimEnd('\r');
                if (l.Trim().Length == 0) continue;
                rows.Add(new List<string>(l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)));
            }
            return rows;
        }
    }
}
