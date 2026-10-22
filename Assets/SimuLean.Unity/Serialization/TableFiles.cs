using System.Collections.Generic;
using System.Data;
using System.IO;
using ExcelDataReader;
using SimuLean.Data;

namespace SimuLean.Unity
{
    /// <summary>
    /// Reads a table file for the Unity layer (e.g. a schedule source's arrivals, loaded in the inspector): Excel
    /// workbooks (<c>.xlsx</c> / <c>.xls</c>, a sheet by name or the first) here, text tables (<c>.csv</c>, <c>.data</c>)
    /// through the engine's <see cref="TableReader"/>. The engine itself has no Excel dependency.
    /// </summary>
    public static class TableFiles
    {
        public static Dictionary<string, List<string>> Read(string path, string sheetName = null)
        {
            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return ext == "xlsx" || ext == "xls" ? ReadExcel(path, sheetName) : TableReader.Read(path);
        }

        private static Dictionary<string, List<string>> ReadExcel(string path, string sheetName)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                DataSet data = reader.AsDataSet();
                DataTable sheet = !string.IsNullOrEmpty(sheetName) && data.Tables[sheetName] != null ? data.Tables[sheetName] : data.Tables[0];
                var rows = new List<List<string>>();
                if (sheet != null)
                    foreach (DataRow r in sheet.Rows)
                    {
                        var row = new List<string>();
                        for (int c = 0; c < sheet.Columns.Count; c++) row.Add(r[c]?.ToString() ?? "");
                        rows.Add(row);
                    }
                return TableReader.Columns(rows);
            }
        }
    }
}
