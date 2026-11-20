using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Downtime;
using SimuLean.Storage;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Headless build of <see cref="PickFace"/> (<see cref="PickFaceSettings"/>) and <see cref="BaseStockReplenisher"/>
    /// (<see cref="ReplenisherSettings"/>); typed settings. The size of the loads' / pieces' 3D model
    /// (<c>modelWidth</c> / <c>modelHeight</c> / <c>modelLength</c> / <c>modelLoadFloor</c>, the pieces' with the prefix
    /// <c>piece</c>, metres, as a source's) is written by the scene's extractor from the load / piece prefab and given to
    /// the items where their labels set no size (<see cref="Item.SetSizeWhereMissing"/>).
    /// </summary>
    public static class ReplenishmentBuilders
    {
        /// <summary>Prefix of the pieces' model size settings (<c>pieceModelWidth</c>…); the loads' have none.</summary>
        public const string PieceSizePrefix = "piece";

        /// <summary>The path of one side of a model size: <c>modelWidth</c>, or <c>pieceModelWidth</c> with a prefix
        /// (<paramref name="side"/> 0 = width, 1 = height, 2 = length, 3 = load floor: a walled container's inside floor).</summary>
        public static string ModelSizeKey(string prefix, int side)
        {
            string name = side == 0 ? "Width" : side == 1 ? "Height" : side == 2 ? "Length" : "LoadFloor";
            return string.IsNullOrEmpty(prefix) ? "model" + name : prefix + "Model" + name;
        }

        /// <summary>Writes a model size (width, height, length[, load floor]; null or all 0 = nothing written).</summary>
        public static void EncodeModelSize(ElementConfig c, string prefix, double[] size)
        {
            if (size == null || size.Length < 3 || (size[0] <= 0 && size[1] <= 0 && size[2] <= 0)) return;
            for (int i = 0; i < 3; i++) c.Set(ModelSizeKey(prefix, i), Math.Max(0, size[i]));
            if (size.Length > 3 && size[3] > 0) c.Set(ModelSizeKey(prefix, 3), size[3]);
        }

        /// <summary>The model size of an element (its settings at <see cref="ModelSizeKey"/>), or null when it has none.</summary>
        public static double[] DecodeModelSize(ElementConfig c, string prefix)
        {
            double floor = Math.Max(0, c.Get(ModelSizeKey(prefix, 3), 0.0));
            var size = new double[floor > 0 ? 4 : 3];
            for (int i = 0; i < 3; i++) size[i] = Math.Max(0, c.Get(ModelSizeKey(prefix, i), 0.0));
            if (floor > 0) size[3] = floor;
            return size[0] > 0 || size[1] > 0 || size[2] > 0 ? size : null;
        }

        // ── pick face ────────────────────────────────────────────────────────

        public static PickFace BuildPickFace(ElementConfig c, SimClock clock, IVElement vElement)
        {
            var s = c.SettingsAs<PickFaceSettings>();
            if (string.IsNullOrEmpty(s.Sku)) throw new FormatException($"Pick face '{c.Name}': no sku.");
            return new PickFace(c.Name, clock, s.Sku, Math.Max(1, s.Positions), vElement)
            {
                ReorderPoint = Math.Max(0, s.ReorderPoint),
                ReorderQuantity = Math.Max(1, s.ReorderQuantity),
                PiecesPerLoad = Math.Max(0, s.PiecesPerLoad),
                StartFull = s.StartFull,
                LoadType = s.LoadType ?? "",
                PieceType = s.PieceType ?? "",
                LoadModelSize = DecodeModelSize(c, ""),
                PieceModelSize = DecodeModelSize(c, PieceSizePrefix),
            };
        }

        // ── replenisher ──────────────────────────────────────────────────────

        /// <summary>
        /// SKU rows from a table (e.g. a CSV / Excel file read by the Unity layer): columns <c>sku</c>,
        /// <c>piecesPerLoad</c>, <c>initialLoads</c> (case-insensitive; the last two optional: 1 and 0). A count that is not
        /// an integer fails, naming it.
        /// </summary>
        public static List<ReplenisherSkuSettings> SkusFromTable(Dictionary<string, List<string>> table, string owner)
        {
            var result = new List<ReplenisherSkuSettings>();
            if (table == null) return result;
            List<string> Col(string name)
            {
                foreach (var kv in table) if (string.Equals(kv.Key?.Trim(), name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                return null;
            }
            int Count(List<string> col, int i, int def, string what)
            {
                string text = col != null && i < col.Count ? (col[i] ?? "").Trim() : "";
                if (text.Length == 0) return def;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
                throw new FormatException($"Replenisher '{owner}': SKU table row {i + 1}: {what} '{text}' is not an integer.");
            }
            var sku = Col("sku") ?? throw new FormatException($"Replenisher '{owner}': the SKU table has no 'sku' column.");
            var pieces = Col("piecesPerLoad"); var initial = Col("initialLoads");
            for (int i = 0; i < sku.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(sku[i])) continue;
                result.Add(new ReplenisherSkuSettings
                {
                    Sku = sku[i].Trim(), PiecesPerLoad = Count(pieces, i, 1, "piecesPerLoad"), InitialLoads = Count(initial, i, 0, "initialLoads"),
                });
            }
            return result;
        }

        public static BaseStockReplenisher BuildReplenisher(ElementConfig c, SimClock clock, IVElement vElement)
        {
            var s = c.SettingsAs<ReplenisherSettings>();
            var r = new BaseStockReplenisher(c.Name, clock, vElement)
            {
                TruckloadSize = Math.Max(1, s.TruckloadSize),
                LoadType = s.LoadType ?? "",
                PieceType = s.PieceType ?? "",
                LoadModelSize = DecodeModelSize(c, ""),
            };
            foreach (var spec in s.Skus ?? new List<ReplenisherSkuSettings>())
            {
                if (spec == null || string.IsNullOrWhiteSpace(spec.Sku)) continue;
                r.AddSku(spec.Sku.Trim(), spec.PiecesPerLoad, spec.InitialLoads);
            }
            if (s.LeadTime != null) r.LeadTime = ElementBuildHelpers.SamplerOf(s.LeadTime, clock, c.Name, "leadTime", "leadTime");
            if (!string.IsNullOrWhiteSpace(s.UnloadingWindows)) r.UnloadingWindows = WeeklyShiftPattern.Parse(s.UnloadingWindows);
            return r;
        }

        /// <summary>After the links: the storages it observes and where the initial stock goes (by element name).</summary>
        public static void WireReplenisher(BaseStockReplenisher r, ElementConfig c, IReadOnlyDictionary<string, Element> created)
        {
            var s = c.SettingsAs<ReplenisherSettings>();
            Element ByName(string name)
            {
                foreach (var e in created.Values) if (e.Name == name) return e;
                if (created.TryGetValue(name, out Element byId)) return byId;
                throw new FormatException($"Replenisher '{c.Name}': element '{name}' is not in the model.");
            }
            bool any = false;
            foreach (string name in s.Observe ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!(ByName(name.Trim()) is StorageSystem st)) throw new FormatException($"Replenisher '{c.Name}': '{name}' is not a storage.");
                r.Observe(st); any = true;
            }
            if (!any)   // default: the storages it delivers to
            {
                IEnumerable<Element> dests = r.GetOutput() is GeneralLink g ? g.Outputs
                    : r.GetOutput() is SimuLean.Resources.ResourceTransportLink t ? t.Destinations : (IEnumerable<Element>)new Element[0];
                foreach (var d in dests) if (d is StorageSystem st) r.Observe(st);
            }
            if (!string.IsNullOrWhiteSpace(s.InitialStockTarget)) r.InitialStockTarget = ByName(s.InitialStockTarget.Trim());
        }
    }

    /// <summary>Headless build of <c>UnityPickFace</c> → <see cref="PickFace"/> (codec in <see cref="ReplenishmentBuilders"/>).
    /// Port <c>empties</c> (connection <c>PortOutput</c>) is where the emptied unit loads leave.</summary>
    internal sealed class PickFaceHandler : ElementHandlerBase
    {
        public override string TypeName => "PickFace";

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => ReplenishmentBuilders.BuildPickFace(config, clock, vElement);
    }

    /// <summary>Headless build of <c>UnityBaseStockReplenisher</c> → <see cref="BaseStockReplenisher"/> (codec in
    /// <see cref="ReplenishmentBuilders"/>); <see cref="Wire"/> resolves the observed storages and the initial-stock target.</summary>
    internal sealed class BaseStockReplenisherHandler : ElementHandlerBase
    {
        public override string TypeName => "BaseStockReplenisher";

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => ReplenishmentBuilders.BuildReplenisher(config, clock, vElement);

        public override void Wire(Element element, ElementConfig config, IReadOnlyDictionary<string, Element> created)
        {
            if (element is BaseStockReplenisher r) ReplenishmentBuilders.WireReplenisher(r, config, created);
        }
    }
}
