using System.Collections.Generic;
using System.Globalization;
using SimuLean.Serialization;

namespace SimuLean.Testing
{
    public static partial class ReferenceModels
    {
        /// <summary>
        /// Two sequencing stations (families A and B) fill carriers of 9 slots, a lot of 9 orders each, sharing one operator
        /// (request order <c>|oldestItemTime</c>: the station whose lot holds the oldest order first). Orders every 120 per
        /// family (B offset by 60; every 5th B vehicle needs no piece → a gap). Pieces are pulled from one list by reference;
        /// family A has 20 pieces at 0 and 100 more only at 3600 (a stock-out window). Times: prepare 30, pick 10, place 5,
        /// gap 3, close 20. Hand check (family A, no gaps): lot k is ready at 1080k − 120; a carrier takes
        /// 30 + 9 × 15 + 20 = 185 → lots 1, 2 at 960–1145, 2040–2225; lot 3 starts at 3120, places 2 pieces by 3180, waits
        /// for stock until 3600 (one stock-out of 420, the operator held), places the other 7 by 3705 and closes at 3725 —
        /// so B's lot 3 (ready 3180) waits for the operator until 3725; 9 carriers per family by 10 000.
        /// </summary>
        public static SimulationConfig SequencingStationLot()
        {
            var c = New(1031, 10000);
            var ordersA = new List<(double, string, (string, string)[])>();
            var ordersB = new List<(double, string, (string, string)[])>();
            for (int i = 0; i * 120 < 10000; i++)
            {
                double ta = i * 120, tb = i * 120 + 60;
                ordersA.Add((ta, "Order", new[] { ("vehicle", (i + 1).ToString(CultureInfo.InvariantCulture)), ("callOffTime", Num(ta)), ("reference", "A") }));
                ordersB.Add((tb, "Order", new[] { ("vehicle", (i + 1).ToString(CultureInfo.InvariantCulture)), ("callOffTime", Num(tb)),
                                                   ("reference", i % 5 == 4 ? "" : "B") }));
            }
            var pieces = new List<(double, string, (string, string)[])>();
            for (int i = 0; i < 20; i++) pieces.Add((0, "Part", new[] { ("sku", "A") }));
            for (int i = 0; i < 200; i++) pieces.Add((0, "Part", new[] { ("sku", "B") }));
            for (int i = 0; i < 100; i++) pieces.Add((3600, "Part", new[] { ("sku", "A") }));
            var wagons = new List<(double, string, (string, string)[])>();
            for (int i = 0; i < 12; i++) wagons.Add((0, "Wagon", new (string, string)[0]));

            Add(c, SchedRows("ordersA", ordersA));
            Add(c, SchedRows("ordersB", ordersB));
            Add(c, SchedRows("pieces", pieces));
            Add(c, SchedRows("wagonsA", wagons));
            Add(c, SchedRows("wagonsB", wagons));
            Add(c, Queue("stock", 1000));
            Add(c, Queue("emptyA", 100));
            Add(c, Queue("emptyB", 100));
            foreach (string id in new[] { "seqA", "seqB" })
            {
                var st = El(id, SequencingStationBuilder.TypeName);
                st.Settings = new SequencingStationSettings
                {
                    CarrierCapacity = 9, LotSize = 9, PrepareTime = SamplerSettings.Constant(30), PickTime = SamplerSettings.Constant(10), PlaceTime = SamplerSettings.Constant(5),
                    GapTime = SamplerSettings.Constant(3), CloseTime = SamplerSettings.Constant(20),
                };
                Add(c, st);
            }
            Add(c, Sink("fullCarriers"));
            Add(c, Sink("servedOrders"));
            Connect(c, "wagonsA", "emptyA"); Connect(c, "emptyA", "seqA");
            Connect(c, "wagonsB", "emptyB"); Connect(c, "emptyB", "seqB");
            Connect(c, "seqA", "fullCarriers"); Connect(c, "seqB", "fullCarriers");
            Connect(c, "pieces", "stock");
            c.Lists.Add(new ListConfig { Id = "stockList", Name = "stockList" });
            Push(c, "stock", "stockList");
            foreach (var (orders, st) in new[] { ("ordersA", "seqA"), ("ordersB", "seqB") })
            {
                var pi = new ConnectionConfig(orders, st, "PortInput"); pi.Port = SequencingStation.PortOrders; c.Connections.Add(pi);
                var po = new ConnectionConfig(st, "servedOrders", "PortOutput"); po.Port = SequencingStation.PortOrders; c.Connections.Add(po);
                var pull = new ConnectionConfig("stockList", st, "PullFromList");
                pull.Query = "";
                pull.Port = SequencingStation.PortPieces;
                c.Connections.Add(pull);
            }
            Team(c, "operator", "|oldestItemTime", "", ("Op", new string[0]));
            ProcessBy(c, "seqA", "operator", "EndOfProcess", "");
            ProcessBy(c, "seqB", "operator", "EndOfProcess", "");
            return c;
        }

        private static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>Schedule source with one row per arrival and any label columns (missing = empty).</summary>
        private static ElementConfig SchedRows(string id, List<(double t, string name, (string col, string value)[] labels)> rows)
        {
            var table = new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string>(), ["Name"] = new List<string>(), ["Q"] = new List<string>(),
            };
            var cols = new List<string>();
            foreach (var r in rows) foreach (var l in r.labels) if (!table.ContainsKey(l.col)) { table[l.col] = new List<string>(); cols.Add(l.col); }
            foreach (var r in rows)
            {
                table["Time"].Add(Num(r.t)); table["Name"].Add(r.name); table["Q"].Add("1");
                foreach (string col in cols)
                {
                    string v = "";
                    foreach (var l in r.labels) if (l.col == col) v = l.value;
                    table[col].Add(v);
                }
            }
            var e = El(id, "Source");
            e.Set("mode", "Schedule");
            e.Set("arrivalsTable", table);
            return e;
        }
    }
}
