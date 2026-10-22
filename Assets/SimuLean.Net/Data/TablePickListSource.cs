using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimuLean.Data
{
    /// <summary>
    /// Pick lists from a data table of the model: one row per SKU line — <c>skuColumn</c>, <c>qtyColumn</c> and,
    /// for per-order lists, a <c>keyColumn</c> matched against the container's key (its label <c>containerKeyLabel</c>,
    /// numeric labels formatted invariantly, else its type). Indexed once when built (the run's snapshot), never read
    /// during the run (the database is never queried mid-run). Without a key column the whole
    /// table is one list for every container. A container without a list gets null (the next source of the chain).
    /// </summary>
    public sealed class TablePickListSource : IPickListSource
    {
        private readonly Dictionary<string, Dictionary<string, int>> byKey = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> all;
        private readonly string containerKeyLabel;

        public TablePickListSource(ModelTable table, string skuColumn = "sku", string qtyColumn = "qty",
                                   string keyColumn = null, string containerKeyLabel = "order_id")
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            this.containerKeyLabel = containerKeyLabel;
            if (string.IsNullOrEmpty(keyColumn)) table.Require(skuColumn, qtyColumn);
            else table.Require(skuColumn, qtyColumn, keyColumn);
            for (int r = 0; r < table.RowCount; r++)
            {
                string sku = table.Text(r, skuColumn);
                if (string.IsNullOrEmpty(sku) || !ModelTable.TryNumber(table.Text(r, qtyColumn), out double q)) continue;
                int qty = (int)Math.Round(q);
                if (qty <= 0) continue;
                Dictionary<string, int> list;
                if (string.IsNullOrEmpty(keyColumn)) list = all ?? (all = new Dictionary<string, int>(StringComparer.Ordinal));
                else
                {
                    string key = Key(table.Text(r, keyColumn));
                    if (!byKey.TryGetValue(key, out list)) byKey[key] = list = new Dictionary<string, int>(StringComparer.Ordinal);
                }
                list[sku] = list.TryGetValue(sku, out int had) ? had + qty : qty;
            }
            if (string.IsNullOrEmpty(keyColumn) && all == null) all = new Dictionary<string, int>();
        }

        public Dictionary<string, int> GetPickList(Item container)
        {
            if (all != null) return all.Count == 0 ? null : new Dictionary<string, int>(all);
            string key = ContainerKey(container);
            return key != null && byKey.TryGetValue(key, out var list) ? new Dictionary<string, int>(list) : null;
        }

        // A key cell: a number is written as a container label would be ("7", not "7.0").
        private static string Key(string cell)
            => ModelTable.TryNumber(cell, out double v) && v == Math.Floor(v) && Math.Abs(v) < 1e15
                ? ((long)v).ToString(CultureInfo.InvariantCulture)
                : (cell ?? "").Trim();

        private string ContainerKey(Item container)
        {
            if (container == null) return null;
            if (!string.IsNullOrEmpty(containerKeyLabel))
            {
                double? v = container.GetLabelValue(containerKeyLabel);
                if (v.HasValue) return Key(v.Value.ToString("R", CultureInfo.InvariantCulture));
                string text = container.GetLabelText(containerKeyLabel);
                if (!string.IsNullOrEmpty(text)) return Key(text);
            }
            return container.GetTypeName();
        }
    }
}
