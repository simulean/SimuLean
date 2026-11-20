using System.Collections.Generic;
using SimuLean.Serialization;

namespace SimuLean.Testing
{
    // Reference models of the line-supply elements: PacedLine, CallOffPoint
    // (the line-side unpacker's, line_side_unpacker, and the Separator's, separator_split / combiner_separator, are in ReferenceModels.cs).
    public static partial class ReferenceModels
    {
        /// <summary>
        /// Vehicles every 2 → buffer → paced line (5 positions, takt 2) → sink, with two stations: S1 at position 1 fed by
        /// parts every 2.2 (slower than the takt: periodic line stops) and S2 at position 3 fed every 1.9 through a queue;
        /// a break of the line at 100 for 20 (timetable downtime).
        /// </summary>
        public static SimulationConfig PacedLineStarvation()
        {
            var c = New(1102, 400);
            Add(c, ConstSource("cars", 2.0));
            Add(c, Queue("buf", 100));
            var line = El("line", "PacedLine");
            line.Settings = new PacedLineSettings
            {
                Positions = 5, Takt = SamplerSettings.Constant(2),
                Stations = new List<LineStationSettings>
                {
                    new LineStationSettings { Name = "S1", Position = 1, PortCapacity = 2 },
                    new LineStationSettings { Name = "S2", Position = 3, PortCapacity = 2 },
                },
            };
            Add(c, line);
            Add(c, Sink("sink"));
            Add(c, ConstSource("parts1", 2.2));
            Add(c, ConstSource("parts2", 1.9));
            Add(c, Queue("q2", 5));
            Connect(c, "cars", "buf"); Connect(c, "buf", "line"); Connect(c, "line", "sink");
            PortConnect(c, "parts1", "line", "PortInput", "S1");
            Connect(c, "parts2", "q2");
            PortConnect(c, "q2", "line", "PortInput", "S2");
            var brk = new DowntimeConfig("break", "Timetable", "line");
            brk.Set("rows", new List<DowntimeIntervalSettings> { DowntimeIntervalSettings.Of("", 100, 20, "SCHEDULED_DOWN", StopMode.Immediate, reason: "break") });
            c.Downtimes.Add(brk);
            return c;
        }

        /// <summary>
        /// Two-part JIS model: vehicles (exponential, mean 2.2) → call-off point (family Door, references R1 / R2 / R3) →
        /// vehicle buffer → paced line (4 positions, takt 2) with station Door at position 3 (matches <c>vehicle</c>);
        /// the orders are kitted by a server (1.5 each, in sequence) and go to the station port. The requisition time is
        /// the time in the buffer + 3 takts, so it varies with the buffer.
        /// </summary>
        public static SimulationConfig CallOffBufferLine()
        {
            var c = New(1103, 600);
            Add(c, Source("cars", 2.2));
            var cp = El("cp", "CallOffPoint");
            cp.Settings = new CallOffPointSettings
            {
                Families = new List<CallOffFamilySettings>
                {
                    new CallOffFamilySettings { Name = "Door", OrderType = "DoorOrder", ReferenceTables = new List<ReferenceTableSettings> { ReferenceTableSettings.Parse("R1:0.5|R2:0.3|R3:0.2") } },
                },
            };
            Add(c, cp);
            Add(c, Queue("buf", 200));
            var line = El("line", "PacedLine");
            line.Settings = new PacedLineSettings
            {
                Positions = 4, Takt = SamplerSettings.Constant(2),
                Stations = new List<LineStationSettings> { new LineStationSettings { Name = "Door", Position = 3, MatchLabel = "vehicle", PortCapacity = 3 } },
            };
            Add(c, line);
            Add(c, Sink("sink"));
            Add(c, Queue("orders", 200));
            Add(c, Server("kit", 1, "Constant", ("constantValue", 1.5)));
            Connect(c, "cars", "cp"); Connect(c, "cp", "buf"); Connect(c, "buf", "line"); Connect(c, "line", "sink");
            PortConnect(c, "cp", "orders", "PortOutput", "Door");
            Connect(c, "orders", "kit");
            PortConnect(c, "kit", "line", "PortInput", "Door");
            return c;
        }

        private static ElementConfig ConstSource(string id, double gap)
        {
            var e = El(id, "Source");
            e.Set("mode", "InterArrival");
            e.Set("interArrivalDistribution.strategyType", "Constant");
            e.Set("interArrivalDistribution.constantValue", gap);
            return e;
        }

        private static void PortConnect(SimulationConfig c, string from, string to, string type, string port)
        {
            var cc = new ConnectionConfig(from, to, type);
            cc.Port = port;
            c.Connections.Add(cc);
        }
    }
}
