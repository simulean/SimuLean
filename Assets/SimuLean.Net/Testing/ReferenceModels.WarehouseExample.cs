using System.Collections.Generic;
using SimuLean.Serialization;

namespace SimuLean.Testing
{
    // The user example of the warehouse / line-supply library: a warehouse that supplies an assembly line. Its
    // scene is Assets/Scenes/LibraryExamples/Warehouse_ToLine.unity (built by WarehouseToLineExample in the editor).
    public static partial class ReferenceModels
    {
        /// <summary>
        /// Warehouse to line, in seconds over one 8-hour shift. Car bodies arrive every 60 s and wait in a buffer; the
        /// assembly fits one part A and one part B to each body (50 s) and the finished cars leave.
        /// <list type="bullet">
        /// <item><b>Warehouse.</b> A base-stock replenisher (4 pallets of 40 pieces per SKU at the start, trucks of 4
        ///   pallets of the consumed mix, lead time 1 h) stores the pallets in a pallet rack (2 bays x 3 positions x 2
        ///   levels, single-deep); the rack
        ///   publishes its stock to the list <c>Stock</c> and a forklift (network dock — rack — faces, 1.5 m/s)
        ///   carries each pulled pallet to the pick faces.</item>
        /// <item><b>Pick faces.</b> One per SKU (two pallets: open + reserve, reorder point 1): they pull their SKU from
        ///   <c>Stock</c> and hand out single pieces; a combiner per SKU packs four of them into an empty tote (its main
        ///   input, a source that always has one waiting; batch mode: the pieces travel inside the tote; 20 s) and sends
        ///   it to the depot queue (2 totes); the empty pallets leave.</item>
        /// <item><b>Milk run.</b> A tugger train (capacity 4 totes, 2 per part) takes the totes from the depot to the
        ///   line (60 s each way, 10 s to load / unload a tote) when any flow is ready, and brings back the empty totes.</item>
        /// <item><b>Line side.</b> An unpacker per part (2 totes) hands the pieces to the assembly one by one; its empty
        ///   totes wait for the train in a queue at the stop.</item>
        /// </list>
        /// </summary>
        public static SimulationConfig WarehouseToLine()
        {
            var c = New(1301, 28800);
            c.Network = Network(("dock", 0), ("rack1", 20), ("rack2", 22.8), ("faces", 40));

            // the line
            var bodies = El("Bodies", "Source");
            bodies.Set("mode", "InterArrival");
            bodies.Set("interArrivalDistribution.strategyType", "Constant");
            bodies.Set("interArrivalDistribution.constantValue", 60.0);
            Add(c, bodies);
            Add(c, Queue("BodyBuffer", 5));
            var asm = El("Assembly", "Combiner");
            asm.Set("requirements", new[] { 1, 1 });
            asm.Set("delayStrategy.strategyType", "Constant");
            asm.Set("delayStrategy.constantValue", 50.0);
            asm.Set("inputOrder", new List<string> { "BodyBuffer", "LineA", "LineB" });
            Add(c, asm);
            Add(c, Sink("Cars"));
            Connect(c, "Bodies", "BodyBuffer"); Connect(c, "BodyBuffer", "Assembly"); Connect(c, "Assembly", "Cars");

            // the warehouse
            var trucks = El("Trucks", "BaseStockReplenisher");
            var rs = new ReplenisherSettings { TruckloadSize = 4, LeadTime = SamplerSettings.Constant(3600) };
            rs.Skus.Add(SkuRow("A", 40, 4)); rs.Skus.Add(SkuRow("B", 40, 4));
            trucks.Settings = rs;
            Add(c, trucks);
            // the scene's pallet rack: 2 bays (one node each) x 3 positions x 2 levels, in its order (bay, position, level)
            var lanes = new List<(string, double, int, string)>();
            for (int b = 0; b < 2; b++)
                for (int s = 0; s < 3; s++)
                    for (int l = 0; l < 2; l++)
                        lanes.Add(($"B{b}_S{s}_L{l}", 18.6 + b * 2.8 + (s + 0.5) * 0.9, 1, b == 0 ? "rack1" : "rack2"));
            var rack = Placed(StorageEl("Rack", SimuLean.Storage.AccessDiscipline.Direct, lanes.ToArray()), "dock");
            rack.Set("transportBy.team", "Forklifts");
            Add(c, rack);
            Fleet(c, "Forklifts", "dock", 1.5, "", 1);
            Connect(c, "Trucks", "Rack");
            c.Lists.Add(new ListConfig { Id = "Stock", Name = "Stock" });
            Push(c, "Rack", "Stock");

            // pick faces, packing, depot, line side
            Add(c, Sink("EmptyPallets"));
            Add(c, Sink("TotesBack"));
            foreach (string sku in new[] { "A", "B" })
            {
                var face = El("Face" + sku, "PickFace");
                face.Settings = new PickFaceSettings { Sku = sku, Positions = 2, ReorderPoint = 1, ReorderQuantity = 1, PiecesPerLoad = 40 };
                Placed(face, "faces");
                Add(c, face);
                Pull(c, "Stock", "Face" + sku, "sku == " + sku);
                var pallets = new ConnectionConfig("Face" + sku, "EmptyPallets", "PortOutput");
                pallets.Port = "empties";
                c.Connections.Add(pallets);

                var totes = El("Totes" + sku, "Source");
                totes.Set("mode", "InterArrival");
                totes.Set("interArrivalDistribution.strategyType", "Constant");
                totes.Set("interArrivalDistribution.constantValue", 1.0);
                Add(c, totes);
                var pack = El("Pack" + sku, "Combiner");
                pack.Set("requirements", new[] { 4 });
                pack.Set("batchMode", true);
                pack.Set("delayStrategy.strategyType", "Constant");
                pack.Set("delayStrategy.constantValue", 20.0);
                pack.Set("inputOrder", new List<string> { "Totes" + sku, "Face" + sku });
                Add(c, pack);
                Add(c, Queue("Depot" + sku, 2));
                Connect(c, "Totes" + sku, "Pack" + sku); Connect(c, "Face" + sku, "Pack" + sku); Connect(c, "Pack" + sku, "Depot" + sku);

                var unpack = El("Line" + sku, "LineSideUnpacker");
                unpack.Settings = new LineSideUnpackerSettings { Capacity = 2 };
                Add(c, unpack);
                Add(c, Queue("Empty" + sku, 4));
                Connect(c, "Line" + sku, "Assembly");
                var empties = new ConnectionConfig("Line" + sku, "Empty" + sku, "PortOutput");
                empties.Port = Separator.PortEmpties;
                c.Connections.Add(empties);
            }

            // the tugger train
            Team(c, "Tuggers", "", "", ("Tugger1", new string[0]));
            var mr = El("TuggerTrain", "MilkRun");
            mr.Set("transportBy.team", "Tuggers");
            mr.Set("transportBy.loadTime", "Constant~10");
            mr.Set("transportBy.unloadTime", "Constant~10");
            mr.Set("emptiesLoadTime", "Constant~5");
            mr.Set("emptiesUnloadTime", "Constant~5");
            mr.Set("capacity", 4);
            mr.Set("emptiesDestination", "TotesBack");
            mr.Set("stops", new List<MilkRunStopSettings> { MilkRunStopSettings.Of("LineSide", "", new[] { "EmptyA", "EmptyB" }) });
            mr.Set("flows", new List<MilkRunFlowSettings>
            {
                MilkRunFlowSettings.Of("PartsA", "DepotA", "LineSide", "LineA", 2),
                MilkRunFlowSettings.Of("PartsB", "DepotB", "LineSide", "LineB", 2),
            });
            mr.Set("tourTypes", new List<MilkRunTourTypeSettings> { MilkRunTourTypeSettings.Of("Supply", new[] { "PartsA", "PartsB" }, new[] { "LineSide" }) });
            mr.Set("pattern", new List<string> { "Supply" });
            mr.Set("legs", new List<MilkRunLegSettings>
            {
                MilkRunLegSettings.Of("Depot", "LineSide", SamplerSettings.Constant(60)), MilkRunLegSettings.Of("LineSide", "Depot", SamplerSettings.Constant(60)),
            });
            Add(c, mr);
            return c;
        }
    }
}
