using System.Collections.Generic;
using SimuLean.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The user example of the warehouse / line-supply library: the scene
    /// <c>Assets/Scenes/LibraryExamples/Warehouse_ToLine.unity</c>, the reference model <c>warehouse_to_line</c>
    /// (<see cref="SimuLean.Testing.ReferenceModels.WarehouseToLine"/>) as a scene. Car bodies wait in a buffer for an
    /// assembly that fits one part A and one part B; a truck-fed rack publishes its pallets to the list <c>Stock</c>, a
    /// forklift brings the pallets the pick faces pull, the pieces are packed four into an empty tote (a combiner per
    /// SKU; the pieces travel inside it) and a tugger train takes the
    /// totes to the line-side unpackers and brings the empty totes back. Rebuilt by <see cref="RebuildScene"/>
    /// (<i>Tools ▸ SimuLean ▸ Examples</i>) or in batch mode with
    /// <c>-executeMethod SimuLean.Unity.Editor.WarehouseToLineExample.RebuildScene</c>; <see cref="Build"/> builds the model
    /// under a root object of any scene (the EditMode tests use it in a temporary scene).
    /// </summary>
    public static class WarehouseToLineExample
    {
        public const string ScenePath = "Assets/Scenes/LibraryExamples/Warehouse_ToLine.unity";

        // the rack's layout (the reference model's lanes are at the same places)
        public const float RackX = 18.6f, RackZ = 1.5f, BayWidth = 2.8f, SlotWidth = 0.9f, LevelHeight = 1.5f;

        [MenuItem("Tools/SimuLean/Developer/Rebuild Warehouse to Line Scene", priority = 200)]
        public static void RebuildScene()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var clock = SimuLeanSceneSetup.SetUpScene(scene);
            if (clock != null) clock.MaxTime = 28800;
            Build(scene);
            ExampleFloor.Add(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        /// <summary>Builds the model under a new root object <c>Model</c> of <paramref name="scene"/>; returns the root.</summary>
        public static GameObject Build(Scene scene)
        {
            var root = new GameObject("Model");
            SceneManager.MoveGameObjectToScene(root, scene);
            T Make<T>(string name, Vector3 at, Transform parent = null) where T : Component
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent != null ? parent : root.transform);
                go.transform.position = at;
                return go.AddComponent<T>();
            }
            RandomProcessConfig Constant(float v) => new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = v };
            GameObject Model(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(path);
            void Look(Component c, string label, string path) => LibraryPresets.AddModel(c.gameObject, (label, path));

            // the warehouse floor: dock — rack — pick faces (forklift network)
            var net = Make<UnityTransportNetwork>("Network", Vector3.zero);
            var dock = Make<UnityTransportNode>("dock", new Vector3(0, 0, 0), net.transform);
            var rack1 = Make<UnityTransportNode>("rack1", new Vector3(20, 0, 0), net.transform);
            var rack2 = Make<UnityTransportNode>("rack2", new Vector3(22.8f, 0, 0), net.transform);
            var faces = Make<UnityTransportNode>("faces", new Vector3(40, 0, 0), net.transform);
            net.nodes = new List<UnityTransportNode> { dock, rack1, rack2, faces };
            net.edges = new List<UnityTransportNetwork.EdgeDefinition>
            {
                new UnityTransportNetwork.EdgeDefinition { nodeA = dock, nodeB = rack1 },
                new UnityTransportNetwork.EdgeDefinition { nodeA = rack1, nodeB = rack2 },
                new UnityTransportNetwork.EdgeDefinition { nodeA = rack2, nodeB = faces },
            };

            var forklifts = Make<UnityResourceTeam>("Forklifts", new Vector3(0, 0, -3));
            forklifts.AddMembers(1);
            forklifts.members[0].loadTime = 1; forklifts.members[0].unloadTime = 1;
            forklifts.travel = TeamTravel.Network;
            forklifts.homeNode = dock;
            forklifts.speed = 1.5f;
            forklifts.memberPrefab = Model(LibraryPresets.Members + "Forklift.prefab");
            var tuggers = Make<UnityResourceTeam>("Tuggers", new Vector3(48, 0, -6));
            tuggers.travel = TeamTravel.Abstract;   // the milk run draws the tow tractor with its trailers (legs are timed)
            tuggers.AddMembers(1);
            tuggers.members[0].loadTime = 1; tuggers.members[0].unloadTime = 1;

            var stock = Make<UnitySimList>("Stock", new Vector3(10, 0, -6));
            stock.listName = "Stock";

            // trucks and the rack
            var trucks = Make<UnityBaseStockReplenisher>("Trucks", new Vector3(-6, 0, 0));
            trucks.skus = new List<ReplenishmentSkuRow>
            {
                new ReplenishmentSkuRow { sku = "A", piecesPerLoad = 40, initialLoads = 4 },
                new ReplenishmentSkuRow { sku = "B", piecesPerLoad = 40, initialLoads = 4 },
            };
            trucks.truckloadSize = 4;
            trucks.useLeadTime = true;
            trucks.leadTime = Constant(3600);
            Look(trucks, "Semi-trailer", "TruckTrailer/SemiTrailer.fbx");
            trucks.loadPrefab = Model(LibraryPresets.Models + "WarehouseLoadUnits/PalletLoad_Cartons.fbx");   // full pallets in the rack
            // a pallet rack along the aisle: 2 bays x 3 positions x 2 levels, single-deep (12 pallets); the forklift
            // stops in front of each bay (its node), its centre on the aisle
            var rack = Make<UnityStorageRack>("Rack", new Vector3(RackX, 0, RackZ));
            rack.style = StorageRackStyle.ConventionalRack;
            rack.bays = 2; rack.slotsPerBay = 3; rack.levels = 2; rack.depth = 1;
            rack.bayWidth = BayWidth; rack.slotWidth = SlotWidth; rack.levelHeight = LevelHeight; rack.slotDepth = 1.2f;
            rack.Rebuild();
            for (int i = 0; i < rack.lanes.Count; i++)
                rack.lanes[i].node = i < rack.lanes.Count / 2 ? rack1 : rack2;   // lanes in bay order
            rack.location = dock;
            rack.transportBy = new TransportResourceUse { team = forklifts };
            rack.sendTo = SElement.SendMode.List;
            rack.pushToList = stock;
            trucks.nextElements = new List<SElement> { rack };

            // the line
            var bodies = Make<UnitySource>("Bodies", new Vector3(70, 0, -10));
            bodies.mode = SourceMode.InterArrival;
            bodies.interArrivalDistribution = Constant(60);
            bodies.itemPrefab = Model(LibraryPresets.Models + "CarBody/CarBody.fbx");
            var buffer = Make<UnityQueue>("BodyBuffer", new Vector3(70, 0, -5));
            buffer.capacity = 5;
            var assembly = Make<UnityCombiner>("Assembly", new Vector3(70, 0, 0));
            assembly.requirements = new[] { 1, 1 };
            assembly.delayStrategy = Constant(50);
            var cars = Make<UnitySink>("Cars", new Vector3(70, 0, 6));
            Look(bodies, "Source station", "SourceStation/SourceStation.fbx");
            Look(buffer, "Buffer position", "BufferPosition/BufferPosition.fbx");
            Look(assembly, "Assembly table", "AssemblyWorkstation/AssemblyWorkstation.fbx");
            Look(cars, "Sink station", "SinkStation/SinkStation.fbx");
            bodies.nextElements = new List<SElement> { buffer };
            buffer.nextElements = new List<SElement> { assembly };
            assembly.nextElements = new List<SElement> { cars };

            var emptyPallets = Make<UnitySink>("EmptyPallets", new Vector3(40, 0, 9));
            var totesBack = Make<UnitySink>("TotesBack", new Vector3(48, 0, 9));
            Look(emptyPallets, "Sink station", "SinkStation/SinkStation.fbx");
            Look(totesBack, "Sink station", "SinkStation/SinkStation.fbx");

            // per part: pick face → packing → depot; line-side unpacker and empty totes at the line side
            var depots = new Dictionary<string, UnityQueue>();
            var lines = new Dictionary<string, UnityLineSideUnpacker>();
            var empties = new List<SElement>();
            float side = -3f;
            foreach (string sku in new[] { "A", "B" })
            {
                var face = Make<UnityPickFace>("Face" + sku, new Vector3(40, 0, side));
                face.sku = sku;
                face.positions = 2;
                face.reorderPoint = 1;
                face.reorderQuantity = 1;
                face.piecesPerLoad = 40;
                face.location = faces;
                face.receiveFrom = SElement.ReceiveMode.List;
                face.pullFromList = stock;
                face.pullQuery = new ListQuerySpec { conditions = new List<ListCondition> { new ListCondition("sku", ListOp.Equal, sku) } };
                face.emptiesDestination = emptyPallets;
                face.loadPrefab = Model(LibraryPresets.Items + "EuroPallet.prefab");
                face.piecePrefab = Model(LibraryPresets.Models + "WarehouseLoadUnits/CardboardCarton_300x200x200.fbx");

                // packing: an empty tote (main input) takes four pieces of the face, which travel inside it (batch mode)
                var totes = Make<UnitySource>("Totes" + sku, new Vector3(44, 0, side * 2));
                totes.mode = SourceMode.InterArrival;
                totes.interArrivalDistribution = Constant(1);   // always one waiting: the source holds it while packing
                totes.itemPrefab = Model(LibraryPresets.Items + "Tote.prefab");
                var pack = Make<UnityCombiner>("Pack" + sku, new Vector3(44, 0, side));
                pack.requirements = new[] { 4 };
                pack.batchMode = true;
                pack.delayStrategy = Constant(20);
                pack.inputOrder = new List<SElement> { totes, face };
                var depot = Make<UnityQueue>("Depot" + sku, new Vector3(48, 0, side));
                depot.capacity = 2;
                totes.nextElements = new List<SElement> { pack };
                face.nextElements = new List<SElement> { pack };
                pack.nextElements = new List<SElement> { depot };

                var line = Make<UnityLineSideUnpacker>("Line" + sku, new Vector3(64, 0, side));
                line.capacity = 2;
                var empty = Make<UnityQueue>("Empty" + sku, new Vector3(60, 0, side));
                empty.capacity = 4;
                line.emptiesDestination = empty;
                line.nextElements = new List<SElement> { assembly };
                Look(face, "Buffer position", "BufferPosition/BufferPosition.fbx");
                Look(totes, "Source station", "SourceStation/SourceStation.fbx");
                Look(pack, "Workstation", "WorkstationProcessor/WorkstationProcessor.fbx");
                Look(depot, "Buffer position", "BufferPosition/BufferPosition.fbx");
                Look(line, "Buffer box", "BufferBox/BufferBox.fbx");
                Look(empty, "Buffer position", "BufferPosition/BufferPosition.fbx");

                depots[sku] = depot; lines[sku] = line; empties.Add(empty);
                side = 3f;
            }
            assembly.inputOrder = new List<SElement> { buffer, lines["A"], lines["B"] };

            // the tugger train: depot → line side and back
            var train = Make<UnityMilkRun>("TuggerTrain", new Vector3(52, 0, -6));
            train.transportBy = new TransportResourceUse { team = tuggers, overrideLoadTimes = true, loadTime = Constant(10), unloadTime = Constant(10) };
            train.overrideEmptiesTimes = true;
            train.emptiesLoadTime = Constant(5);
            train.emptiesUnloadTime = Constant(5);
            train.capacity = 4;
            train.emptiesDestination = totesBack;
            train.stops.Add(new UnityMilkRun.StopSpec { name = "LineSide", emptiesOrigins = empties });
            train.flows.Add(new UnityMilkRun.FlowSpec { name = "PartsA", origin = depots["A"], stop = "LineSide", destination = lines["A"], maxPerTour = 2 });
            train.flows.Add(new UnityMilkRun.FlowSpec { name = "PartsB", origin = depots["B"], stop = "LineSide", destination = lines["B"], maxPerTour = 2 });
            train.tourTypes.Add(new UnityMilkRun.TourTypeSpec { name = "Supply", flows = new List<string> { "PartsA", "PartsB" }, stops = new List<string> { "LineSide" } });
            train.pattern = new List<string> { "Supply" };
            train.legs.Add(new UnityMilkRun.LegSpec { from = "Depot", to = "LineSide", time = Constant(60) });
            train.symmetricLegs = true;
            train.tractorPrefab = Model(LibraryPresets.Models + "TuggerTrain/TowTractor.fbx");
            return root;
        }
    }
}
