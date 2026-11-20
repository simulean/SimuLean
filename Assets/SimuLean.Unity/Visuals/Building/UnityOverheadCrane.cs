using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Single-girder overhead travelling crane from the OverheadCrane kit:
    /// two runway beams with rails and end stops (tiling, any length), the bridge (two
    /// end trucks that keep their aspect ratio + a tiling box girder of any span with the
    /// SimuLean logo segment in the middle) and the trolley/hoist with ropes, hook block
    /// and pendant.
    ///
    /// <para><b>Frame.</b> Origin at floor level in the middle of the runway system; the
    /// runways run along local Z (length <see cref="runwayLength"/>) at x = ±span/2 with the
    /// rail tops at <see cref="railHeight"/>. The bridge travels along Z
    /// (<see cref="bridgePosition"/>), the trolley along X (<see cref="trolleyPosition"/>),
    /// the hook down (<see cref="hookDrop"/>, rope length in m). To sit the crane on
    /// <see cref="UnitySteelColumn"/> brackets, put the column axes 0.5 m outside each rail
    /// and use the same rail height.</para>
    ///
    /// <para><b>Driving it.</b> Set the pose fields (edit or play mode) for an instant pose,
    /// or call <see cref="MoveTo"/> / <see cref="MoveHookTo"/> to travel at the configured
    /// speeds; <see cref="Arrived"/> fires when all three axes reach their targets. Attach
    /// the carried load to <see cref="HookSocket"/>. The end-truck wheels and the hoist
    /// drum turn with the motion.</para>
    ///
    /// <para><b>As a crane resource</b> (a hall crane team's member prefab): it is an <see cref="ICraneRig"/> —
    /// <see cref="CraneResourceView"/> poses it from the engine (trolley = plan x, bridge = plan z, the hook socket = the
    /// cup) and the team fits its span and runway around the points it works at (<see cref="FitAround"/>).</para>
    /// </summary>
    public class UnityOverheadCrane : ModularAssetBehaviour, ICraneRig
    {
        public const float GirderTop = 1.35f;          // rail top to girder top
        public const float GirderOverhang = 0.20f;     // girder beyond each rail axis
        public const float TrolleyLevel = 0.55f;       // rail top to the girder running flange
        public const float TrolleyEndClearance = 0.55f;
        public const float WheelRadius = 0.16f, DrumRadius = 0.16f;
        const float PendantGripHeight = 1.46f;         // floor to the cable end (grip 1.1 m + pendant)
        const float MinDrop = 0.25f, HookFloorClearance = 0.15f;

        [Serializable]
        public class Pieces
        {
            public GameObject Runway_Segment, Runway_EndStop, Girder_Segment, Girder_Logo, Girder_End, EndTruck, Trolley;
        }

        [Header("Crane size (m)")]
        [Tooltip("Rail axis to rail axis.")]
        [Min(3f)] public float span = 16f;

        [Tooltip("Runway length along local Z, including the end stops.")]
        [Min(4f)] public float runwayLength = 24f;

        [Tooltip("Floor to the rail tops.")]
        [Min(2f)] public float railHeight = 6f;

        [Tooltip("Build the runway beams (turn off if they are modelled separately).")]
        public bool buildRunways = true;

        [Tooltip("Stand the runway beams on steel columns of the building kit (at most 8 m apart, the bracket under " +
                 "each rail). Turn off when a hall's own columns carry them.")]
        public bool buildColumns = true;

        /// <summary>The longest bay between two runway columns (m).</summary>
        public const float MaxColumnSpacing = 8f;
        /// <summary>The columns' cap above the rail top (m), as the kit's column of a 6 m rail.</summary>
        public const float ColumnAboveRail = 1.6f;

        [Header("Pose")]
        [Tooltip("Bridge position along the runway (local Z, m from the centre).")]
        public float bridgePosition;

        [Tooltip("Trolley position along the girder (local X, m from the centre).")]
        public float trolleyPosition;

        [Tooltip("Hoist rope length (m).")]
        public float hookDrop = 2f;

        [Header("Motion (m/s)")]
        [Min(0.01f)] public float bridgeSpeed = 0.8f;
        [Min(0.01f)] public float trolleySpeed = 0.5f;
        [Min(0.01f)] public float hoistSpeed = 0.25f;

        [Header("Kit pieces (auto-filled from Art/Models/OverheadCrane)")]
        public Pieces pieces = new Pieces();

        /// <summary>Raised when a <see cref="MoveTo"/> / <see cref="MoveHookTo"/> completes.</summary>
        public event Action<UnityOverheadCrane> Arrived;

        [SerializeField, HideInInspector] Transform bridge, trolley, drum, ropes, hookBlock, hookSocket, pendantCable, pendant;
        [SerializeField, HideInInspector] Transform[] wheels = new Transform[0];
        [SerializeField, HideInInspector] float[] wheelSigns = new float[0];
        [SerializeField, HideInInspector] Quaternion[] wheelRest = new Quaternion[0];
        [SerializeField, HideInInspector] Quaternion drumRest = Quaternion.identity;
        [SerializeField, HideInInspector] Vector3 hookRest, pendantRest, ropesRest = Vector3.one, cableRest = Vector3.one;
        [SerializeField, HideInInspector] float bridgeLimit;

        float wheelAngle, lastBridge = float.NaN;
        float targetBridge, targetTrolley, targetDrop;
        bool driving;

        public Transform Bridge => bridge;
        public Transform Trolley => trolley;
        /// <summary>Attach point for the carried load (child of the hook block).</summary>
        public Transform HookSocket => hookSocket;
        public bool IsMoving => driving;

        public float BridgeLimit => Mathf.Max(0f, bridgeLimit);
        public float TrolleyLimit => Mathf.Max(0f, span * 0.5f - TrolleyEndClearance);
        public float MaxDrop => Mathf.Max(MinDrop, DropForSocketHeight(HookFloorClearance));

        public override string GetSummary() =>
            $"span {span:0.##} m  |  runway {runwayLength:0.##} m  |  rail at {railHeight:0.##} m  |  " +
            $"travel ±{BridgeLimit:0.##} m, trolley ±{TrolleyLimit:0.##} m, hook ≤ {MaxDrop:0.##} m";

        protected override void ResolvePieces() => ModularKit.ResolvePieces(pieces, ModularKit.CraneKit);

        // ------------------------------------------------------------------ build

        // The runways' columns: the building kit's HEB300 column (as UnitySteelColumn builds it) with its bracket under each
        // rail, the column axis BracketReach outside it, evenly spaced inside the end stops at most MaxColumnSpacing apart.
        List<GameObject> BuildColumns(Transform root)
        {
            var all = new List<GameObject>();
            var cp = new UnitySteelColumn.Pieces();
            ModularKit.ResolvePieces(cp, ModularKit.BuildingKit);
            if (cp.Column_Base == null || cp.Column_Shaft == null) return all;
            float height = railHeight + ColumnAboveRail;
            float shaft = Mathf.Max(0.1f, height - UnitySteelColumn.TopHeight - UnitySteelColumn.BaseHeight);
            int segments = Mathf.Max(1, Mathf.RoundToInt(shaft));
            float k = shaft / segments;
            float usable = Mathf.Max(0f, runwayLength - 1f);
            int bays = Mathf.Max(1, Mathf.CeilToInt(usable / MaxColumnSpacing));
            foreach (int sx in new[] { 1, -1 })
                for (int i = 0; i <= bays; i++)
                {
                    var col = new GameObject($"Column_{(sx > 0 ? "R" : "L")}{i}").transform;
                    col.SetParent(root, false);
                    col.localPosition = new Vector3(sx * (span * 0.5f + UnitySteelColumn.BracketReach), 0f,
                                                    -usable * 0.5f + usable * i / bays);
                    col.localRotation = Quaternion.Euler(0f, sx > 0 ? -90f : 90f, 0f);   // the bracket (+Z) under the rail
                    all.Add(ModularKit.Place(cp.Column_Base, col, Vector3.zero));
                    for (int j = 0; j < segments; j++)
                        all.Add(ModularKit.Place(cp.Column_Shaft, col, new Vector3(0f, UnitySteelColumn.BaseHeight + j * k, 0f),
                                                 Quaternion.identity, new Vector3(1f, k, 1f)));
                    all.Add(ModularKit.Place(cp.Column_Top, col, new Vector3(0f, height - UnitySteelColumn.TopHeight, 0f)));
                    all.Add(ModularKit.Place(cp.Column_Bracket, col, new Vector3(0f, railHeight - UnitySteelColumn.BracketBelowRail, 0f)));
                }
            all.RemoveAll(g => g == null);
            return all;
        }

        [ContextMenu("Rebuild Crane")]
        public override void Rebuild()
        {
            var root = BeginBuild();
            var p = pieces;

            float capLen = p.Runway_EndStop ? ModularKit.LocalBounds(p.Runway_EndStop).size.z : 0f;
            var tb = ModularKit.LocalBounds(p.EndTruck);
            float truckHalf = Mathf.Max(Mathf.Abs(tb.min.z), Mathf.Abs(tb.max.z));
            bridgeLimit = runwayLength * 0.5f - capLen - truckHalf;

            if (buildRunways && p.Runway_Segment != null)
            {
                var runs = new List<GameObject>();
                foreach (int sx in new[] { 1, -1 })
                {
                    var run = new GameObject(sx > 0 ? "Runway_R" : "Runway_L").transform;
                    run.SetParent(root, false);
                    run.localPosition = new Vector3(sx * span * 0.5f, railHeight, -runwayLength * 0.5f);
                    runs.AddRange(ModularKit.Strip(run, Vector3.zero, runwayLength, p.Runway_Segment, p.Runway_EndStop));
                }
                if (buildColumns) runs.AddRange(BuildColumns(root));
                if (combine) ModularKit.Combine(root, runs, chunkLength, 2);
            }

            bridge = new GameObject("Bridge").transform;
            bridge.SetParent(root, false);
            var wl = new List<Transform>();
            var ws = new List<float>();
            foreach (int sx in new[] { 1, -1 })
            {
                var truck = ModularKit.Place(p.EndTruck, bridge, new Vector3(sx * span * 0.5f, 0f, 0f),
                                             Quaternion.Euler(0f, sx > 0 ? 0f : 180f, 0f), Vector3.one);
                if (truck == null) continue;
                truck.name = sx > 0 ? "EndTruck_R" : "EndTruck_L";
                foreach (var w in new[] { "Wheel_A", "Wheel_B" })
                {
                    var t = ModularKit.FindDeep(truck.transform, w);
                    if (t == null) continue;
                    wl.Add(t);
                    ws.Add(sx);            // the left truck is turned 180°: its wheels spin the other way
                }
            }
            wheels = wl.ToArray();
            wheelSigns = ws.ToArray();
            wheelRest = Array.ConvertAll(wheels, w => w.localRotation);

            float gl = span + 2f * GirderOverhang;
            var girder = new GameObject("Girder").transform;
            girder.SetParent(bridge, false);
            girder.localPosition = new Vector3(-gl * 0.5f, GirderTop, 0f);
            girder.localRotation = Quaternion.Euler(0f, 90f, 0f);      // strip +Z along the span (+X)
            if (p.Girder_Segment != null)
            {
                var g = ModularKit.Strip(girder, Vector3.zero, gl, p.Girder_Segment, p.Girder_End, null, p.Girder_Logo);
                if (combine) ModularKit.Combine(girder, g, 1000f, 2);   // one chunk; it moves with the bridge
            }

            var tr = ModularKit.Place(p.Trolley, bridge, new Vector3(0f, TrolleyLevel, 0f));
            trolley = tr != null ? tr.transform : null;
            drum = ModularKit.FindDeep(trolley, "Drum");
            ropes = ModularKit.FindDeep(trolley, "Ropes");
            hookBlock = ModularKit.FindDeep(trolley, "HookBlock");
            hookSocket = ModularKit.FindDeep(trolley, "HookSocket");
            pendantCable = ModularKit.FindDeep(trolley, "PendantCable");
            pendant = ModularKit.FindDeep(trolley, "Pendant");
            if (drum) drumRest = drum.localRotation;
            if (ropes) ropesRest = ropes.localScale;
            if (hookBlock) hookRest = hookBlock.localPosition;          // pose at 1 m of rope
            if (pendantCable) cableRest = pendantCable.localScale;
            if (pendant) pendantRest = pendant.localPosition;           // pose at 1 m of cable

            lastBridge = float.NaN;
            ApplyPose();
        }

        // ------------------------------------------------------------------ pose & motion

        /// <summary>Applies the pose fields (clamped to the travel limits) to the transforms.</summary>
        public void ApplyPose()
        {
            bridgePosition = Mathf.Clamp(bridgePosition, -BridgeLimit, BridgeLimit);
            trolleyPosition = Mathf.Clamp(trolleyPosition, -TrolleyLimit, TrolleyLimit);
            hookDrop = Mathf.Clamp(hookDrop, MinDrop, MaxDrop);
            if (bridge == null) return;

            if (!float.IsNaN(lastBridge))
                wheelAngle = Mathf.Repeat(wheelAngle + (bridgePosition - lastBridge) / WheelRadius * Mathf.Rad2Deg, 360f);
            lastBridge = bridgePosition;

            bridge.localPosition = new Vector3(0f, railHeight, bridgePosition);
            for (int i = 0; i < wheels.Length; i++)
                if (wheels[i]) wheels[i].localRotation = wheelRest[i] * Quaternion.Euler(wheelSigns[i] * wheelAngle, 0f, 0f);
            if (trolley) trolley.localPosition = new Vector3(trolleyPosition, TrolleyLevel, 0f);
            if (drum) drum.localRotation = drumRest * Quaternion.Euler(hookDrop / DrumRadius * Mathf.Rad2Deg, 0f, 0f);
            if (ropes) ropes.localScale = new Vector3(ropesRest.x, ropesRest.y * hookDrop, ropesRest.z);
            if (hookBlock) hookBlock.localPosition = hookRest + Vector3.down * (hookDrop - 1f);
            if (pendantCable)
            {
                float cable = Mathf.Max(0.2f, railHeight + TrolleyLevel + pendantCable.localPosition.y - PendantGripHeight);
                pendantCable.localScale = new Vector3(cableRest.x, cableRest.y * cable, cableRest.z);
                if (pendant) pendant.localPosition = pendantRest + Vector3.down * (cable - 1f);
            }
        }

        /// <summary>Jumps to a pose (bridge z, trolley x, rope length) without travelling.</summary>
        public void SetPose(float bridgeZ, float trolleyX, float drop)
        {
            bridgePosition = bridgeZ;
            trolleyPosition = trolleyX;
            hookDrop = drop;
            driving = false;
            ApplyPose();
        }

        /// <summary>Travels to a pose at the configured speeds (all axes at once).</summary>
        public void MoveTo(float bridgeZ, float trolleyX, float drop)
        {
            targetBridge = Mathf.Clamp(bridgeZ, -BridgeLimit, BridgeLimit);
            targetTrolley = Mathf.Clamp(trolleyX, -TrolleyLimit, TrolleyLimit);
            targetDrop = Mathf.Clamp(drop, MinDrop, MaxDrop);
            driving = true;
        }

        /// <summary>Travels so that <see cref="HookSocket"/> reaches a world point.</summary>
        public void MoveHookTo(Vector3 worldPoint)
        {
            var local = transform.InverseTransformPoint(worldPoint);
            MoveTo(local.z, local.x, DropForSocketHeight(local.y));
        }

        /// <summary>Rope length that puts the hook socket at crane-local height <paramref name="y"/>.</summary>
        public float DropForSocketHeight(float y)
        {
            float socket = hookSocket ? hookSocket.localPosition.y : 0f;
            return railHeight + TrolleyLevel + hookRest.y + 1f + socket - y;
        }

        void Update()
        {
            if (!driving) return;
            float dt = Time.deltaTime;
            bridgePosition = Mathf.MoveTowards(bridgePosition, targetBridge, bridgeSpeed * dt);
            trolleyPosition = Mathf.MoveTowards(trolleyPosition, targetTrolley, trolleySpeed * dt);
            hookDrop = Mathf.MoveTowards(hookDrop, targetDrop, hoistSpeed * dt);
            ApplyPose();
            if (Mathf.Approximately(bridgePosition, targetBridge) && Mathf.Approximately(trolleyPosition, targetTrolley) &&
                Mathf.Approximately(hookDrop, targetDrop))
            {
                driving = false;
                Arrived?.Invoke(this);
            }
        }

        // ------------------------------------------------------------------ crane resource (ICraneRig)

        bool ICraneRig.HasHead => hookSocket != null;

        /// <summary>The hook socket's height (crane frame y) with the rope at its shortest.</summary>
        public float RestCup => SocketHeightFor(MinDrop);

        Vector2 ICraneRig.Plan => hookSocket != null ? PlanOf(hookSocket.position) : new Vector2(trolleyPosition, bridgePosition);

        Vector3 ICraneRig.CupWorld => hookSocket != null ? hookSocket.position
            : transform.TransformPoint(new Vector3(trolleyPosition, SocketHeightFor(hookDrop), bridgePosition));

        public Vector2 PlanOf(Vector3 world) { var p = transform.InverseTransformPoint(world); return new Vector2(p.x, p.z); }

        public float HeightOf(Vector3 world) => transform.InverseTransformPoint(world).y;

        /// <summary>Jumps to the pose that puts the hook socket at plan (trolley x, bridge z) and height <paramref name="cup"/>.</summary>
        public void SetHead(Vector2 plan, float cup)
        {
            float drop = DropForSocketHeight(cup);
            SetPose(plan.y, plan.x, drop);
            if (hookSocket == null) return;
            // the hook does not hang exactly under the trolley's origin: move the axes by the socket's offset
            Vector3 s = transform.InverseTransformPoint(hookSocket.position);
            float dx = s.x - trolleyPosition, dz = s.z - bridgePosition;
            if (Mathf.Abs(dx) > 1e-4f || Mathf.Abs(dz) > 1e-4f) SetPose(plan.y - dz, plan.x - dx, drop);
        }

        /// <summary>Turns the runway along the longer side of the points, centres the crane on them and sizes the span,
        /// the runway and the rail height so the hook reaches each (with <paramref name="margin"/>), then rebuilds.</summary>
        public void FitAround(IEnumerable<Vector3> worldPoints, float margin = 1f)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue, maxY = float.MinValue;
            foreach (var w in worldPoints)
            {
                minX = Mathf.Min(minX, w.x); maxX = Mathf.Max(maxX, w.x); minZ = Mathf.Min(minZ, w.z); maxZ = Mathf.Max(maxZ, w.z);
                maxY = Mathf.Max(maxY, w.y);
            }
            if (minX > maxX) return;
            bool alongX = maxX - minX > maxZ - minZ;
            transform.rotation = Quaternion.Euler(0f, alongX ? 90f : 0f, 0f);
            float across = alongX ? maxZ - minZ : maxX - minX, along = alongX ? maxX - minX : maxZ - minZ;
            transform.position = new Vector3((minX + maxX) / 2f, transform.position.y, (minZ + maxZ) / 2f);
            span = Mathf.Max(3f, across + 2f * (TrolleyEndClearance + margin));
            railHeight = Mathf.Max(railHeight, maxY - transform.position.y + 4f);
            runwayLength = Mathf.Max(4f, along + 2f * margin);
            Rebuild();
            runwayLength += 2f * Mathf.Max(0f, runwayLength * 0.5f - BridgeLimit);   // room for the end trucks and stops
            ClearColumnRows();
            Rebuild();
        }

        /// <summary>A column's half width (HEB300) plus the free space kept around it (m).</summary>
        const float ColumnBand = 1.15f;   // 0.15 m half flange + 1 m free

        /// <summary>
        /// Moves each runway outwards until its row of columns stands clear of the scene's elements and floor zones (the
        /// footprint of their views) along the runway — a column never stands in a work station or its marked area, the
        /// ones the crane serves or its neighbours —, then re-centres the crane between the two rails. Nothing to do
        /// without columns.
        /// </summary>
        public void ClearColumnRows()
        {
            if (!buildRunways || !buildColumns) return;
            float half = runwayLength * 0.5f;
            var footprints = new List<Rect>();
            // the stations' views and the floor zones that mark work areas (not a team: its members move about)
            var areas = UnityCompat.FindAll<SElement>(false).Where(e => !(e is UnityResourceTeam)).Select(e => e.transform)
                .Concat(UnityCompat.FindAll<UnityFloorZone>(false).Select(z => z.transform));
            foreach (var area in areas)
            {
                if (area.IsChildOf(transform) || transform.IsChildOf(area)) continue;
                var renderers = area.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0) continue;
                var b = renderers[0].bounds;
                foreach (var r in renderers) b.Encapsulate(r.bounds);
                float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                foreach (var corner in new[] { new Vector3(b.min.x, 0f, b.min.z), new Vector3(b.min.x, 0f, b.max.z),
                                               new Vector3(b.max.x, 0f, b.min.z), new Vector3(b.max.x, 0f, b.max.z) })
                {
                    Vector3 l = transform.InverseTransformPoint(corner);
                    x0 = Mathf.Min(x0, l.x); x1 = Mathf.Max(x1, l.x); z0 = Mathf.Min(z0, l.z); z1 = Mathf.Max(z1, l.z);
                }
                if (z1 > -half && z0 < half) footprints.Add(Rect.MinMaxRect(x0, z0, x1, z1));
            }
            if (footprints.Count == 0) return;
            float right = ClearLine(span * 0.5f + UnitySteelColumn.BracketReach, +1, footprints) - UnitySteelColumn.BracketReach;
            float left = ClearLine(-span * 0.5f - UnitySteelColumn.BracketReach, -1, footprints) + UnitySteelColumn.BracketReach;
            transform.position += transform.right * ((right + left) * 0.5f);
            span = right - left;
        }

        // the first column line outward of x (direction dir) whose band crosses no footprint
        static float ClearLine(float x, int dir, List<Rect> footprints)
        {
            for (int pass = 0; pass < 64; pass++)
            {
                bool moved = false;
                foreach (var f in footprints)
                    if (x + ColumnBand > f.xMin && x - ColumnBand < f.xMax)
                    {
                        x = dir > 0 ? f.xMax + ColumnBand : f.xMin - ColumnBand;
                        moved = true;
                    }
                if (!moved) break;
            }
            return x;
        }

        // the inverse of DropForSocketHeight
        private float SocketHeightFor(float drop) => DropForSocketHeight(0f) - drop;

        void OnValidate()
        {
            if (!driving) ApplyPose();
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.6f, 0f, 0.8f);
            float h = railHeight, z = runwayLength * 0.5f, x = span * 0.5f;
            Gizmos.DrawLine(new Vector3(x, h, -z), new Vector3(x, h, z));
            Gizmos.DrawLine(new Vector3(-x, h, -z), new Vector3(-x, h, z));
            Gizmos.DrawWireCube(new Vector3(0f, h * 0.5f, 0f), new Vector3(2f * TrolleyLimit, 0.01f, 2f * BridgeLimit));
        }
    }
}
