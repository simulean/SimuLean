using System;
using System.Collections.Generic;
using SimuLean.Collections;
using SimuLean.Resources;
using UnityEngine;

namespace SimuLean.Unity
{
    // TeamTravel and PhysicsEstimate are in Serialization/Extractors/ExtractionRules.cs (with the travel they map to);
    // the team's config is built by UnityResourceTeamExtractor

    /// <summary>A member of a resource team (an operator, a vehicle…).</summary>
    [Serializable]
    public class ResourceMemberSpec
    {
        [Tooltip("Member name (empty = team name + number).")]
        public string name = "";
        [Tooltip("The member's properties (skills, attributes) used by the requirements and the order rules, 'name = expression' " +
                 "evaluated when the member is built (e.g. skill = 'welding', level = 3); text in quotes.")]
        [SimuLean.Unity.SimuLeanExpression("memberProperty", Records = true)]
        public List<string> properties = new List<string>();
        [Tooltip("Its own load / unload times (transport), used unless the element overrides them — seconds with the team's Physical units, else model time units.")]
        [Min(0)] public float loadTime;
        [Min(0)] public float unloadTime;
        [Tooltip("Where its view appears: its own place — e.g. two robots of one team in two cells. Empty = the team's " +
                 "transform. Members moving on the network go to their home node.")]
        public Transform spawnPoint;
    }

    /// <summary>
    /// A resource team in the scene its members and the dispatcher that hands
    /// them to the elements asking for them — for their process or to carry their items (each element's
    /// <c>Resources</c> section). Priority rule = the request order and the resource order (list orders; empty = FIFO
    /// if available). Driven by <see cref="UnitySimClock"/>: built before the elements initialise, started after them.
    /// Its inspector shows each member's state, utilisation and tasks in Play mode. With <b>Network</b> travel the
    /// members are spatial (vehicles, operators walking): they start at the home node, travel on the scene's
    /// <see cref="UnityTransportNetwork"/> at the team speed, and get a GameObject each (prefab or a box) animated
    /// by <see cref="UnityVehicleAnimator"/> — or, for a prefab with a gantry, <see cref="CraneResourceView"/>, and with a
    /// 6-axis arm, <see cref="RobotArmView"/>.
    /// </summary>
    [SimuLeanTool(SimuLeanToolCategories.Resources, "Resource Team", Order = 45,
        Description = "Operators (and later vehicles) assigned to stations for their process or to carry their items, by a priority rule")]
    [AddComponentMenu("SimuLean/Resource Team")]
    [SelectionBase]   // its members' edit-mode preview selects the team
    public class UnityResourceTeam : MonoBehaviour
    {
        [Tooltip("Name of the team in the model (empty = the GameObject name).")]
        public string teamName = "";

        [Tooltip("The members of the team (operators, vehicles, cranes) with their skills and load / unload times.")]
        public List<ResourceMemberSpec> members = new List<ResourceMemberSpec>();

        [Tooltip("Which request is served first (order by a request field: priority, age, count…). Empty = first come, first served: " +
                 "the jobs' Priority orders the queue only with an order by it (priority desc); preemption uses it always.")]
        public ListQuerySpec requestOrder = new ListQuerySpec();

        [Tooltip("Named request orders (name = query text, e.g. Urgent = |priority desc). Request Order Name may name one instead " +
                 "of the Request Order, which makes the order a choice for the Experimenter / optimiser.")]
        public List<NamedOrder> namedOrders = new List<NamedOrder>();
        [Tooltip("Optional: the named order used as the request order (overrides Request Order), or rule:Type - a dispatch rule " +
                 "(a class implementing IDispatchRule in Assets/SimuLean.UserLogic) that scores each pending request.")]
        public string requestOrderName = "";
        [Tooltip("The dispatch rule's parameters (its [RuleParameter] fields) and their values.")]
        public List<RuleParameterValue> ruleParameters = new List<RuleParameterValue>();

        [Tooltip("Which free member gets a request (order by a member field: idleTime, utilization, tasks, distance, a property…). Empty = longest idle first.")]
        public ListQuerySpec resourceOrder = new ListQuerySpec();

        [Header("Travel")]
        [Tooltip("Straight (default): the members go in straight lines between the elements at the team speed, no obstacles (as " +
                 "a mobile resource); each element without a location gets a point in front of it. Abstract: no " +
                 "position, travel takes no time (or the element's defined travel time); the members stand at the team. " +
                 "Network: on the transport network. Grid: around obstacles (walk grid, A*).")]
        public TeamTravel travel = TeamTravel.Straight;
        [Tooltip("Network travel: the network (empty = the scene's).")]
        public UnityTransportNetwork network;
        [Tooltip("Network travel under traffic control: one vehicle per track (the network's capacity), vehicles wait at nodes, deadlocks resolved and counted.")]
        public bool traffic;
        [Tooltip("Matrix travel: the distance matrix.")]
        public UnityTravelMatrix matrix;
        [Tooltip("Grid travel: the walkable area (free movement around obstacles, paths by A*).")]
        public UnityWalkGrid walkGrid;
        [Tooltip("Unity Physics travel: the model estimating distances (uses this team's walk grid / matrix / network).")]
        public PhysicsEstimate physicsEstimate = PhysicsEstimate.Grid;
        [Tooltip("Unity Physics travel: the measured times, fitted (Travel Calibration tool) — required for headless runs.")]
        public TravelCalibrationAsset calibration;
        [Tooltip("Axes travel: max speed per axis (Unity x, y = vertical, z); 0 = the axis does not move.")]
        public Vector3 axisSpeed = new Vector3(1f, 0.5f, 1f);
        [Tooltip("Axes travel: acceleration per axis (0 = instant).")]
        public Vector3 axisAccel = Vector3.zero;
        [Tooltip("Axes travel: axes move one after the other (else together: the slowest axis decides).")]
        public bool axesSequential;
        [Tooltip("Spatial travel: node where the members start (Straight travel: empty = the team's position).")]
        [SimuLeanHelp("A Transport Node of the scene's network. Straight travel uses it too (the members start there and, with " +
                      "returnHome, go back there); Straight without one: the team's own position; Network without one: the network's first node.")]
        public UnityTransportNode homeNode;
        [Tooltip("Spatial travel: after each task a member walks back to the home node and is free only there (a waiting point). " +
                 "Off: a free member waits where its last task ended.")]
        public bool returnHome;
        [Tooltip("A member's breakdown or shift end (an Immediate stop) during work that has a preemption mode: Hand Over = the " +
                 "work goes back to the queue and any member that meets the requirements takes it (default); Wait For Same = it " +
                 "waits, paused, for that member. Work without a preemption mode keeps its member through the stop.")]
        public SimuLean.Resources.ResourceStopPolicy jobOnMemberStop = SimuLean.Resources.ResourceStopPolicy.HandOver;
        [Tooltip("Spatial travel: speed — distance per second with Physical units, else distance per model time unit.")]
        [Min(0.0001f)] public float speed = 1f;
        [Tooltip("Units of the speeds, accelerations and the members' load / unload times. Model: per model time unit (the clock's " +
                 "Time Unit). Physical: per second (m/s, s), converted with the clock's Time Unit at build — e.g. 1.2 m/s in a 1 h model.")]
        public SimuLean.Serialization.ResourceUnits units = SimuLean.Serialization.ResourceUnits.Model;

        /// <summary>The speed in distance per model time unit (the clock's unit applied to physical units).</summary>
        public float SimSpeed
        {
            get
            {
                double unit = UnitySimClock.Instance != null && UnitySimClock.Instance.TimeUnitSeconds > 0 ? UnitySimClock.Instance.TimeUnitSeconds : 1.0;
                return units == SimuLean.Serialization.ResourceUnits.Physical ? (float)(speed * unit) : speed;
            }
        }

        [Header("Visualization")]
        [Tooltip("GameObject of each member. Empty = a box.")]
        public GameObject memberPrefab;
        [Tooltip("Gantries already in the scene driven by the members, in order (e.g. a gantry stacker's own gantry). " +
                 "Members without one get the member prefab; a prefab with a gantry (GantryRig / Head) is fitted to the team's points.")]
        public List<GantryRig> craneRigs = new List<GantryRig>();
        public Vector3 carriedItemOffset = new Vector3(0f, 0.75f, 0f);
        [Tooltip("Show the members in Play mode (spatial teams move; abstract ones stand at the team or their spawn point).")]
        public bool showMembers = true;

        private readonly List<GameObject> memberViews = new List<GameObject>();
        private SimuLean.Transport.TransportNetwork builtNetwork;

        public UnityTransportNetwork Network => network != null ? network : UnityCompat.FindAny<UnityTransportNetwork>();

        public string TeamName => string.IsNullOrEmpty(teamName) ? gameObject.name : teamName;

        /// <summary>The dispatcher (null before the run initialises).</summary>
        public ResourceDispatcher Dispatcher { get; private set; }

        /// <summary>The members built for the current run.</summary>
        public IReadOnlyList<TaskExecutor> Executors => executors;
        private readonly List<TaskExecutor> executors = new List<TaskExecutor>();

        /// <summary>Builds the dispatcher and its members (before the elements initialise).</summary>
        /// <summary>
        /// Binds the team the model factory built for this run (scene runs build the whole model from the scene's config,
        /// as headless runs do): its dispatcher and members, and the model's points / network for the member views.
        /// </summary>
        public void Bind(ResourceDispatcher dispatcher, SimuLean.Transport.TransportNetwork points)
        {
            Dispatcher = dispatcher;
            executors.Clear();
            if (dispatcher != null) executors.AddRange(dispatcher.Members);
            builtNetwork = travel != TeamTravel.Abstract ? points : null;
            if (travel == TeamTravel.UnityPhysics)
            {
                var sim = UnitySimClock.Instance;
                if (sim != null && sim.RunAsFastAsPossible)
                {
                    Debug.LogWarning($"[SimuLean] Resource team '{TeamName}' moves with Unity physics: maximum speed is turned off for this run.");
                    sim.RunAsFastAsPossible = false;
                }
            }
        }

        /// <summary>The run started: one view per spatial member.</summary>
        public void OnRunStarted() => SpawnMemberViews();

        /// <summary>
        /// The run is cleared (Restart / Stop): the item views its members carry and the member views go, as the
        /// elements' (they stayed in the scene, the member views still pointing at the old model).
        /// </summary>
        public void ClearViews()
        {
            foreach (var ex in executors)
                foreach (var item in ex.Carried)
                    if (item?.vItem is GameObject carried && carried != null) Destroy(carried);
            foreach (var go in memberViews) if (go != null) Destroy(go);
            memberViews.Clear();
        }

        // One animated GameObject per spatial member (views only: skipped headless).
        /// <summary>Where member <paramref name="member"/>'s view appears: its spawn point, else the team's transform.</summary>
        public Transform SpawnPointOf(int member)
            => member >= 0 && member < members.Count && members[member] != null && members[member].spawnPoint != null
                ? members[member].spawnPoint : transform;

        private void SpawnMemberViews()
        {
            var preview = transform.Find("MembersPreview");   // the editor's preview of the members gives way to the run's
            if (preview != null) Destroy(preview.gameObject);
            foreach (var go in memberViews) if (go != null) Destroy(go);
            memberViews.Clear();
            if (!showMembers) return;
            if (builtNetwork == null) { SpawnStandingViews(); return; }
            for (int i = 0; i < executors.Count; i++)
            {
                var ex = executors[i];
                GantryRig rig = craneRigs != null && i < craneRigs.Count ? craneRigs[i] : null;
                if (rig != null)
                {
                    // A gantry already in the scene (e.g. a gantry stacker's): the member drives it.
                    var sceneView = rig.GetComponent<CraneResourceView>();
                    Bind(sceneView != null ? sceneView : rig.gameObject.AddComponent<CraneResourceView>(), ex, rig);
                    continue;
                }
                GameObject go;
                // its own spawn point, else the team's transform
                Transform spawn = SpawnPointOf(i);
                if (memberPrefab != null) go = Instantiate(memberPrefab, spawn.position, spawn.rotation);
                else
                {
                    // a box standing on the floor (the member's pivot is its ground point)
                    go = new GameObject();
                    go.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
                    var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    box.transform.SetParent(go.transform, false);
                    box.transform.localPosition = new Vector3(0f, 0.25f, 0f);
                    box.transform.localScale = new Vector3(0.8f, 0.5f, 1.2f);
                }
                go.name = ex.Name;
                go.transform.SetParent(transform, worldPositionStays: true);
                memberViews.Add(go);
                // before the views find the joints; on a child for members that push a carrier (a porter with a bed)
                var look = go.GetComponentInChildren<OperatorAppearance>(true);
                if (look != null) look.ForMember(i);

                if (travel == TeamTravel.UnityPhysics)
                {
                    var member = go.GetComponent<PhysicsTravelMember>();
                    if (member == null) member = go.AddComponent<PhysicsTravelMember>();
                    member.carriedOffset = carriedItemOffset;
                    member.Bind(ex, builtNetwork, SimSpeed);
                    continue;
                }
                // an AS/RS stacker crane (a carriage with forks on a mast, no forklift mast): runs along its aisle
                if (StackerCraneView.IsStackerCrane(go.transform))
                {
                    var stacker = go.GetComponent<StackerCraneView>();
                    (stacker != null ? stacker : go.AddComponent<StackerCraneView>()).Bind(ex, builtNetwork);
                    continue;
                }
                ICraneRig crane = CraneRigOf(go);
                if (crane != null)
                {
                    crane.FitAround(ReachPoints(), 1f);   // a crane covers the points it works at
                    var view = go.GetComponent<CraneResourceView>();
                    Bind(view != null ? view : go.AddComponent<CraneResourceView>(), ex, crane);
                    continue;
                }
                // A level-luffing crane (nodes Slew / Jib): its hook follows the member's axes; travel, slew and luff
                // are solved.
                var luffing = go.GetComponent<LevelLuffingCraneView>();
                if (luffing == null && HasChild(go.transform, "Slew") && HasChild(go.transform, "Jib"))
                    luffing = go.AddComponent<LevelLuffingCraneView>();
                if (luffing != null)
                {
                    luffing.Bind(ex, builtNetwork);
                    continue;
                }
                // A prefab with a 6-axis arm: its tool follows the member's axes, the joints are solved.
                var arm = go.GetComponentInChildren<RobotArmRig>();
                if (arm == null && HasChild(go.transform, "A1") && HasChild(go.transform, "A6")) arm = go.AddComponent<RobotArmRig>();
                if (arm != null)
                {
                    var armView = go.GetComponent<RobotArmView>();
                    (armView != null ? armView : go.AddComponent<RobotArmView>()).Bind(ex, builtNetwork, arm);
                    continue;
                }
                var animator = go.GetComponent<UnityVehicleAnimator>();
                if (animator == null) animator = go.AddComponent<UnityVehicleAnimator>();
                animator.Bind(ex, builtNetwork, carriedItemOffset);
            }
        }

        private void Bind(CraneResourceView view, TaskExecutor ex, ICraneRig rig) => view.Bind(ex, builtNetwork, rig);

        // An abstract team has no positions: each member stands at its spawn point (members sharing the team's transform
        // side by side along its right), so a team is always seen. View only: nothing follows the engine.
        private void SpawnStandingViews()
        {
            if (memberPrefab == null) return;
            int shared = 0;
            for (int i = 0; i < executors.Count; i++)
            {
                Transform spawn = SpawnPointOf(i);
                Vector3 at = spawn.position;
                if (spawn == transform) at += transform.right * (1.2f * shared++);
                var go = Instantiate(memberPrefab, at, spawn.rotation);
                go.name = executors[i].Name;
                go.transform.SetParent(transform, worldPositionStays: true);
                memberViews.Add(go);
                var look = go.GetComponentInChildren<OperatorAppearance>(true);
                if (look != null) look.ForMember(i);
            }
        }

        /// <summary>The crane rig of a member instance: its own (Goliath, hall crane…), else the library gantry
        /// (<see cref="GantryRig"/>, added) when it has a <c>Bridge</c> and a <c>Head</c>; null when it is not a crane. The
        /// run fits it around <see cref="ReachPoints"/>, and so does the editor's preview of the members.</summary>
        public static ICraneRig CraneRigOf(GameObject member)
        {
            var crane = member.GetComponentInChildren<ICraneRig>();
            if (crane == null && IsLibraryGantry(member.transform)) crane = member.AddComponent<GantryRig>();
            return crane;
        }

        /// <summary>True when the member prefab is a crane (see <see cref="CraneRigOf"/>), without changing it.</summary>
        public static bool IsCraneMember(GameObject prefab) =>
            prefab != null && (prefab.GetComponentInChildren<ICraneRig>(true) != null || IsLibraryGantry(prefab.transform));

        private static bool IsLibraryGantry(Transform root) => HasChild(root, "Head") && HasChild(root, "Bridge");

        private static bool HasChild(Transform root, string n)
        {
            if (root.name == n) return true;
            foreach (Transform c in root) if (HasChild(c, n)) return true;
            return false;
        }

        /// <summary>Where the team works: its home and the locations of the elements that use it (for a stacker in
        /// crane mode, its pick and position nodes).</summary>
        public List<Vector3> ReachPoints()
        {
            var pts = new List<Vector3>();
            if (homeNode != null) pts.Add(homeNode.transform.position);
            foreach (var el in UnityCompat.FindAll<SElement>())
            {
                bool uses = (el.processBy != null && el.processBy.team == this) || (el.transportBy != null && el.transportBy.team == this);
                if (!uses) continue;
                if (el.location != null) pts.Add(el.location.transform.position);
                else if (travel == TeamTravel.Straight) pts.Add(AutoPoint(el));   // the extractor's point in front of it
                if (el is UnityStacker st && st.pickNode != null)
                {
                    pts.Add(st.pickNode.transform.position);
                    foreach (var n in st.positionNodes) if (n != null) pts.Add(n.transform.position);
                }
                foreach (var next in el.nextElements)
                    if (next != null && el.transportBy != null && el.transportBy.team == this)
                    {
                        if (next.location != null) pts.Add(next.location.transform.position);
                        else if (travel == TeamTravel.Straight) pts.Add(AutoPoint(next));
                    }
            }
            if (homeNode == null && travel == TeamTravel.Straight) pts.Add(transform.position);
            return pts;
        }

        /// <summary>Where a Straight team goes at an element without a location: where a person works at it, on its floor
        /// (the point the extractor adds).</summary>
        public static Vector3 AutoPoint(SElement element)
        {
            Vector3 p = StationModel.StaffPosition(element, out _);
            p.y = element.transform.position.y;
            return p;
        }

        /// <summary>True when the members are robot arms (the member prefab has a <see cref="RobotArmRig"/> or the nodes
        /// A1 … A6): they work at a station's work socket, not in front of it.</summary>
        public bool MembersAreArms
            => memberPrefab != null && (memberPrefab.GetComponentInChildren<RobotArmRig>(true) != null
                                        || (HasChild(memberPrefab.transform, "A1") && HasChild(memberPrefab.transform, "A6")));

        /// <summary>Adds <paramref name="n"/> members named <c>Team_k</c> (editor helper).</summary>
        public void AddMembers(int n)
        {
            for (int i = 0; i < n; i++) members.Add(new ResourceMemberSpec { name = $"{TeamName}_{members.Count + 1}" });
        }
    }
}
