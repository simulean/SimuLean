using System.Collections.Generic;
using UnityEngine;
using SimuLean.Resources;

namespace SimuLean.Unity
{
    /// <summary>
    /// View of a milk run's trains, read-only: it schedules nothing and moves no item in the model.
    /// <list type="bullet">
    /// <item><b>Tractor</b>: a spatial team's member view (its prefab, moved by <see cref="UnityVehicleAnimator"/> along the
    /// leg) is used as it is; an abstract team's train gets its own tractor (<see cref="UnityMilkRun.tractorPrefab"/>, else a
    /// box), moved straight between the stops' scene positions over the leg's time (<see cref="TravelLeg.Fraction"/>).</item>
    /// <item><b>Trailers</b>: the carriers on board (<see cref="TaskExecutor.Carried"/>, in loading order — their item
    /// visuals) are coupled behind the tractor: each is placed where the tractor was a given distance earlier along its own
    /// path (a breadcrumb trail of the tractor's positions), so the train follows curves. With hitch nodes the couplings
    /// line up: the tractor's <c>Hitch_Rear</c>, each trailer's <c>Hitch_Front</c> / <c>Hitch_Rear</c>; without them the
    /// trailer's length is its bounds' depth (or <see cref="UnityMilkRun.defaultTrailerLength"/>) and
    /// <see cref="UnityMilkRun.trailerGap"/> separates them. A carrier is dropped at its stop by the destination's view
    /// (it leaves <c>Carried</c>) and empties are hitched on when loaded.</item>
    /// </list>
    /// Runs in <c>LateUpdate</c>, after the member views (which ride carried items on their load socket in <c>Update</c>).
    /// </summary>
    public class MilkRunTrainView : MonoBehaviour
    {
        private MilkRun milkRun;
        private UnityMilkRun owner;
        private readonly Dictionary<MilkRunTrain, Train> trains = new Dictionary<MilkRunTrain, Train>();
        private readonly Dictionary<GameObject, (Transform front, Transform rear, float length)> hitches =
            new Dictionary<GameObject, (Transform, Transform, float)>();

        private sealed class Train
        {
            public Transform tractor;
            public bool own;
            public float rearOffset;          // distance from the tractor's pivot back to its coupling
            public readonly List<Vector3> trail = new List<Vector3>();   // tractor positions, newest last
            // On Board: the slot of each carrier (kept while it is on board) and the indexing offset along the track
            public readonly Dictionary<Item, int> slotOf = new Dictionary<Item, int>();
            public float offset, stepFrom, stepTarget, stepShare, legFrom, legTo;
            public object lastStep, lastLeg;
            // the heading of each carrier relative to the vehicle, kept from the moment it was loaded
            public readonly Dictionary<Item, Quaternion> heading = new Dictionary<Item, Quaternion>();
        }

        public void Bind(MilkRun milkRun, UnityMilkRun owner)
        {
            Clear();
            this.milkRun = milkRun;
            this.owner = owner;
        }

        /// <summary>Removes the tractors this view created (the next run binds again).</summary>
        public void Clear()
        {
            foreach (var t in trains.Values) if (t.own && t.tractor != null) Destroy(t.tractor.gameObject);
            trains.Clear();
            hitches.Clear();
            slotsOf.Clear();
            loadFrom.Clear();
            milkRun = null;
        }

        private void LateUpdate()
        {
            if (milkRun == null || owner == null || UnitySimClock.Instance == null) return;
            double now = UnitySimClock.Instance.clock.GetSimulationTime();
            foreach (var mt in milkRun.TrainStates)
            {
                var ex = mt.Executor;
                if (ex == null) continue;
                if (!trains.TryGetValue(mt, out var tv) || tv.tractor == null) trains[mt] = tv = MakeTrain(mt, ex);
                if (tv.own) MoveOwnTractor(tv, mt, ex, now);
                if (owner.carrierView == MilkRunCarrierView.OnBoard) { PlaceOnBoard(tv, mt, ex, now); continue; }
                Record(tv);
                PlaceTrailers(tv, ex);
            }
        }

        // ── tractor ──────────────────────────────────────────────────────────

        private Train MakeTrain(MilkRunTrain mt, TaskExecutor ex)
        {
            var tv = new Train();
            // a spatial team shows its members (UnityResourceTeam names each view after its member)
            var team = owner.transportBy != null ? owner.transportBy.team : null;
            Transform member = team != null ? team.transform.Find(ex.Name) : null;
            if (member != null) tv.tractor = member;
            else
            {
                GameObject go;
                if (owner.tractorPrefab != null) go = Instantiate(owner.tractorPrefab);
                else
                {
                    go = new GameObject();
                    var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    box.transform.SetParent(go.transform, false);
                    box.transform.localPosition = new Vector3(0f, 0.4f, 0f);
                    box.transform.localScale = new Vector3(0.9f, 0.8f, 1.4f);
                    var col = box.GetComponent<Collider>();
                    if (col != null) Destroy(col);
                }
                go.name = owner.name + " " + ex.Name;
                go.transform.SetParent(transform, worldPositionStays: true);
                if (owner.TryStopPosition(owner.depotName, out Vector3 p)) go.transform.position = p;
                tv.tractor = go.transform;
                tv.own = true;
            }
            var rear = FindDeep(tv.tractor, "Hitch_Rear");
            if (rear != null) tv.rearOffset = Mathf.Max(0f, -tv.tractor.InverseTransformPoint(rear.position).z);
            else
            {
                Vector3 size = ItemVisuals.SizeOf(tv.tractor.gameObject);
                tv.rearOffset = size.z > 0 ? size.z * 0.5f : 0.7f;
            }
            return tv;
        }

        private void MoveOwnTractor(Train tv, MilkRunTrain mt, TaskExecutor ex, double now)
        {
            Vector3 pos = tv.tractor.position;
            var leg = ex.CurrentLeg;
            if (leg != null && mt.LegFrom != null && mt.LegTo != null
                && owner.TryStopPosition(mt.LegFrom.Name, out Vector3 a) && owner.TryStopPosition(mt.LegTo.Name, out Vector3 b))
            {
                var path = owner.LegPath(mt.LegFrom.Name, mt.LegTo.Name);   // through the leg's via points, else straight
                float u = (float)leg.Fraction(now);
                pos = path != null ? path.PointAt(u * path.Length) : Vector3.Lerp(a, b, u);
            }
            else if ((mt.At ?? mt.Position) is MilkRunStop at && owner.TryStopPosition(at.Name, out Vector3 s))
                pos = s;
            Vector3 d = pos - tv.tractor.position; d.y = 0;
            if (d.sqrMagnitude > 1e-6f) tv.tractor.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
            tv.tractor.position = pos;
        }

        // ── carriers on board (a shuttle's deck) ─────────────────────────────

        private readonly Dictionary<Transform, List<Transform>> slotsOf = new Dictionary<Transform, List<Transform>>();
        private readonly Dictionary<Item, Vector3> loadFrom = new Dictionary<Item, Vector3>();
        private readonly List<Item> onBoard = new List<Item>();

        // Slot i: the vehicle's node CargoSlot_i, else a row along the local X of its Deck / LoadSocket node (or its top).
        private Vector3 SlotPosition(Train tv, int i, out Quaternion rotation)
        {
            if (!slotsOf.TryGetValue(tv.tractor, out var slots) || slots.Exists(x => x == null || !x.gameObject.activeInHierarchy))
            {
                slots = new List<Transform>();
                for (int k = 0; ; k++)
                {
                    var s = FindDeep(tv.tractor, "CargoSlot_" + k);
                    if (s == null) break;
                    slots.Add(s);
                }
                if (slots.Count == 0) slots.AddRange(LaneSlots(tv.tractor));
                Transform deck = FindDeep(tv.tractor, "Deck") ?? FindDeep(tv.tractor, "LoadSocket");
                if (slots.Count == 0) slots.Add(deck != null ? deck : tv.tractor);
                slotsOf[tv.tractor] = slots;
            }
            if (i < slots.Count && (slots.Count > 1 || slots[0].name.StartsWith("CargoSlot_") || slots[0].name.StartsWith("Slot_")))
            {
                rotation = slots[i].rotation;
                return slots[i].position;
            }
            Transform basis = slots.Count > 0 && i >= slots.Count ? slots[slots.Count - 1] : slots[0];
            rotation = basis.rotation;
            if (basis.name.StartsWith("CargoSlot_") || basis.name.StartsWith("Slot_"))   // more carriers than slots: continue the row past the last slot
                return basis.position + basis.right * owner.deckSpacing * (i - slots.Count + 1);
            int n = Mathf.Max(1, CarriedCount);
            return basis.position + basis.right * owner.deckSpacing * (i - (n - 1) * 0.5f);
        }

        private int CarriedCount;

        /// <summary>A transfer car's sockets <c>Slot_&lt;lane&gt;_&lt;depth&gt;</c> (<see cref="UnityTransferCar"/>), depth first,
        /// then lane (active ones only).</summary>
        public static List<Transform> LaneSlots(Transform root)
        {
            var found = new List<(int d, int l, Transform t)>();
            void Walk(Transform t)
            {
                if (!t.gameObject.activeInHierarchy) return;
                var parts = t.name.Split('_');
                if (parts.Length == 3 && parts[0] == "Slot" && int.TryParse(parts[1], out int l) && int.TryParse(parts[2], out int d))
                    found.Add((d, l, t));
                foreach (Transform c in t) Walk(c);
            }
            Walk(root);
            found.Sort((a, b) => a.d != b.d ? a.d.CompareTo(b.d) : a.l.CompareTo(b.l));
            return found.ConvertAll(x => x.t);
        }

        // ── lane indexing (a transfer car aligns the lane it serves with the stop's transfer point) ──

        /// <summary>The current indexing shift of a train's vehicle along the track (views, screenshots).</summary>
        public float OffsetOf(MilkRunTrain mt) => mt != null && trains.TryGetValue(mt, out var tv) ? tv.offset : 0f;

        /// <summary>The shift along the track (<paramref name="trackAxis"/>) that brings a slot at rest
        /// (<paramref name="slotAtRest"/>) in line with <paramref name="transferPoint"/>: the transfer is then perpendicular
        /// to the track.</summary>
        public static float LaneOffset(Vector3 slotAtRest, Vector3 transferPoint, Vector3 trackAxis)
        {
            trackAxis.y = 0;
            if (trackAxis.sqrMagnitude < 1e-8f) return 0f;
            return Vector3.Dot(transferPoint - slotAtRest, trackAxis.normalized);
        }

        /// <summary>Progress of the indexing move at fraction <paramref name="u"/> of a load / unload step (eased, done at
        /// <paramref name="share"/>).</summary>
        public static float IndexProgress(float u, float share) => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(u / Mathf.Max(1e-3f, share)));

        /// <summary>Share of a load / unload step used to index (<paramref name="share"/>), none when the served lane is
        /// already aligned (within 5 mm): the leg ended there, so the transfer starts at once.</summary>
        public static float StepShare(float fromOffset, float targetOffset, float share) => Mathf.Abs(targetOffset - fromOffset) < 0.005f ? 0f : share;

        /// <summary>Progress of the straight transfer at fraction <paramref name="u"/> (after the indexing share).</summary>
        public static float TransferProgress(float u, float share) => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((u - share) / Mathf.Max(1e-3f, 1f - share)));

        private readonly Dictionary<Element, Transform> elementViews = new Dictionary<Element, Transform>();

        /// <summary>Where carriers transfer at an element: its child <c>TransferPoint</c>, else its own position.</summary>
        public static Transform TransferPointOf(Transform element) => element == null ? null : FindDeep(element, "TransferPoint") ?? element;

        private Transform ViewOf(Element e)
        {
            if (e == null) return null;
            if (elementViews.TryGetValue(e, out var t) && t != null) return t;
            foreach (var se in UnityCompat.FindAll<SElement>())
                if (se != null && se.GetElement() == e) { t = se.transform; break; }
            elementViews[e] = t;
            return t;
        }

        private int FreeSlot(Train tv, int count)
        {
            for (int k = 0; ; k++)
            {
                bool used = false;
                foreach (var v in tv.slotOf.Values) if (v == k) { used = true; break; }
                if (!used || k >= count + 8) return k;
            }
        }

        private void PlaceOnBoard(Train tv, MilkRunTrain mt, TaskExecutor ex, double now)
        {
            onBoard.Clear();
            onBoard.AddRange(ex.Carried);
            var step = ex.CurrentStep;
            bool loadStep = step != null && step.Kind == SimuLean.Resources.StepKind.Load;
            bool unloadStep = step != null && step.Kind == SimuLean.Resources.StepKind.Unload;
            Item served = loadStep || unloadStep ? step.Item : null;
            CarriedCount = onBoard.Count + (loadStep ? 1 : 0);

            // stable slots: a carrier keeps its slot (and its heading) while on board
            gone.Clear();
            foreach (var kv in tv.slotOf) if (!onBoard.Contains(kv.Key) && kv.Key != served) gone.Add(kv.Key);
            foreach (var g in gone) { tv.slotOf.Remove(g); tv.heading.Remove(g); }
            if (served != null && !tv.slotOf.ContainsKey(served)) tv.slotOf[served] = FreeSlot(tv, CarriedCount);
            foreach (var it in onBoard) if (!tv.slotOf.ContainsKey(it)) tv.slotOf[it] = FreeSlot(tv, CarriedCount);

            double a = ex.StepStartTime, b = ex.StepEndTime;
            float u = double.IsNaN(a) || double.IsNaN(b) || b <= a ? 1f : Mathf.Clamp01((float)((now - a) / (b - a)));
            Vector3 axis = tv.tractor.forward; axis.y = 0;
            axis = axis.sqrMagnitude > 1e-8f ? axis.normalized : Vector3.forward;
            Vector3 basePos = tv.tractor.position;   // the vehicle on its node / leg (no shift yet this frame)

            // the element the served carrier comes from / goes to, and its transfer point
            Transform tp = null;
            if (served != null)
                foreach (var c in mt.Cargo)
                    if (c.Item == served) { tp = TransferPointOf(ViewOf(loadStep ? c.Origin : c.Destination)); break; }

            bool aligning = owner.alignLanes && served != null && tp != null;
            if (!aligning) tv.stepShare = 0f;
            if (aligning)
            {
                // at a stop: the leg already ended with the first served lane aligned; a further lane indexes from there
                if (!ReferenceEquals(step, tv.lastStep))
                {
                    tv.lastStep = step;
                    tv.stepFrom = tv.offset;
                    Vector3 rest = SlotPosition(tv, tv.slotOf[served], out _);   // the vehicle at its node (no shift)
                    tv.stepTarget = LaneOffset(rest, tp.position, axis);
                    tv.stepShare = StepShare(tv.stepFrom, tv.stepTarget, owner.indexShare);   // aligned: transfer at once
                }
                tv.offset = Mathf.Lerp(tv.stepFrom, tv.stepTarget, IndexProgress(u, tv.stepShare));
            }
            else if (ex.CurrentLeg != null)
            {
                // a travel leg: the visual end of the leg is retargeted so the lane served first at the next stop arrives in
                // line with its transfer point; the shift follows the vehicle's progress along the leg, so the vehicle never
                // reverses and does not stop-and-shift on arrival (the engine's leg time is unchanged)
                var leg = ex.CurrentLeg;
                if (!ReferenceEquals(leg, tv.lastLeg))
                {
                    tv.lastLeg = leg;
                    tv.legFrom = tv.offset;
                    tv.legTo = 0f;
                    if (owner.alignLanes && mt.LegTo != null && owner.TryStopPosition(mt.LegTo.Name, out Vector3 end)
                        && PredictFirstService(tv, mt, mt.LegTo, out int slot, out Transform first))
                    {
                        Vector3 restAtEnd = SlotPosition(tv, slot, out _) - basePos + end;
                        tv.legTo = LaneOffset(restAtEnd, first.position, axis);
                    }
                }
                float p = float.NaN;
                if (mt.LegFrom != null && mt.LegTo != null && owner.TryStopPosition(mt.LegFrom.Name, out Vector3 la)
                    && owner.TryStopPosition(mt.LegTo.Name, out Vector3 lb))
                    p = LegProgress(basePos, la, lb);
                if (float.IsNaN(p)) p = (float)leg.Fraction(now);
                tv.offset = LegOffset(tv.legFrom, tv.legTo, p);
            }
            if (Mathf.Abs(tv.offset) > 1e-5f) tv.tractor.position = basePos + axis * tv.offset;

            // carriers on board at their slots, with the heading they were loaded with (the served one transfers)
            foreach (var it in onBoard)
            {
                if (it == served) continue;
                Vector3 slot = SlotPosition(tv, tv.slotOf[it], out Quaternion r);
                Place(it, slot, HeadingOf(tv, it, r));
            }
            if (served != null && served.vItem is GameObject go && go != null)
            {
                Vector3 slot = SlotPosition(tv, tv.slotOf[served], out Quaternion slotRot);
                float pivot = go.transform.position.y - Bottom(go);
                float t = TransferProgress(u, tv.stepShare);
                if (loadStep)
                {
                    if (t <= 0f || !loadFrom.ContainsKey(served) || !tv.heading.ContainsKey(served))
                    {
                        // until the transfer starts the carrier is its origin's (it may still roll to the head): where it
                        // waits and its heading are taken when it starts to move
                        loadFrom[served] = go.transform.position - Vector3.up * pivot;
                        tv.heading[served] = Quaternion.Inverse(tv.tractor.rotation) * go.transform.rotation;
                    }
                    if (t > 0f)
                    {
                        TransferPose(loadFrom[served], slot, tv.tractor.rotation * tv.heading[served], t, out Vector3 bottom, out Quaternion rot);
                        go.transform.SetPositionAndRotation(bottom + Vector3.up * pivot, rot);
                    }
                }
                else
                {
                    // unload: straight from the slot onto the transfer point (its height when it is a TransferPoint node)
                    Vector3 to = tp == null ? slot : (tp.name == "TransferPoint" ? tp.position : new Vector3(tp.position.x, slot.y, tp.position.z));
                    TransferPose(slot, to, HeadingOf(tv, served, slotRot), t, out Vector3 bottom, out Quaternion rot);
                    go.transform.SetPositionAndRotation(bottom + Vector3.up * pivot, rot);
                }
            }
            if (loadFrom.Count > 8) { var keep = new List<Item>(loadFrom.Keys); foreach (var k in keep) if (k != served) loadFrom.Remove(k); }
        }

        private readonly List<Item> gone = new List<Item>();

        // a carrier's heading on board: the one it was loaded with (relative to the vehicle), else its slot's
        private static Quaternion HeadingOf(Train tv, Item it, Quaternion slotRotation)
            => tv.heading.TryGetValue(it, out var h) ? tv.tractor.rotation * h : slotRotation;

        /// <summary>The first carrier the train will serve at <paramref name="stop"/>, as the engine serves a stop: the
        /// carriers due there are unloaded first (loading order), then it loads (a carrier reserved there, else from the
        /// stop's first pickup origin). Its slot and the transfer point; false when nothing is foreseen.</summary>
        private bool PredictFirstService(Train tv, MilkRunTrain mt, MilkRunStop stop, out int slot, out Transform tp)
        {
            slot = 0; tp = null;
            foreach (var c in mt.Cargo)
                if (c.Loaded && !c.Delivered && c.Flow != null && c.Flow.Stop == stop && tv.slotOf.TryGetValue(c.Item, out slot))
                {
                    tp = TransferPointOf(ViewOf(c.Destination));
                    return tp != null;
                }
            Element origin = null;
            foreach (var c in mt.Cargo) if (!c.Loaded && c.CollectedAt == stop) { origin = c.Origin; break; }
            if (origin == null) foreach (var o in stop.Pickups) if (o != null) { origin = o; break; }
            if (origin == null) return false;
            tp = TransferPointOf(ViewOf(origin));
            slot = FreeSlot(tv, CarriedCount + 1);
            return tp != null;
        }

        /// <summary>Progress (0..1) of a vehicle at <paramref name="position"/> along the leg from <paramref name="from"/> to
        /// <paramref name="to"/> (on the floor plane); NaN for a leg with no length.</summary>
        public static float LegProgress(Vector3 position, Vector3 from, Vector3 to)
        {
            Vector3 d = to - from; d.y = 0;
            if (d.sqrMagnitude < 1e-4f) return float.NaN;
            Vector3 r = position - from; r.y = 0;
            return Mathf.Clamp01(Vector3.Dot(r, d) / d.sqrMagnitude);
        }

        /// <summary>The shift along the track at <paramref name="progress"/> of a leg that leaves with
        /// <paramref name="fromOffset"/> and must arrive with <paramref name="toOffset"/>: proportional to the progress, so the
        /// leg's start and end are moved and its speed profile is kept (the vehicle never reverses within a leg).</summary>
        public static float LegOffset(float fromOffset, float toOffset, float progress) => Mathf.Lerp(fromOffset, toOffset, Mathf.Clamp01(progress));

        /// <summary>A carrier transferring straight between a vehicle's slot and an element: its bottom on the segment
        /// <paramref name="fromBottom"/> → <paramref name="toBottom"/> at progress <paramref name="t"/>, its heading kept.</summary>
        public static void TransferPose(Vector3 fromBottom, Vector3 toBottom, Quaternion heading, float t, out Vector3 bottom, out Quaternion rotation)
        {
            bottom = Vector3.Lerp(fromBottom, toBottom, Mathf.Clamp01(t));
            rotation = heading;
        }

        private static void Place(Item item, Vector3 slot, Quaternion rotation)
        {
            if (!(item?.vItem is GameObject go) || go == null) return;
            go.transform.rotation = rotation;
            float pivot = go.transform.position.y - Bottom(go);
            go.transform.position = slot + Vector3.up * pivot;
        }

        // ── trail and trailers ───────────────────────────────────────────────

        private const float TrailStep = 0.05f;

        private void Record(Train tv)
        {
            Vector3 p = tv.tractor.position;
            if (tv.trail.Count == 0 || (p - tv.trail[tv.trail.Count - 1]).sqrMagnitude > TrailStep * TrailStep) tv.trail.Add(p);
            // keep only what the longest train needs (plus a margin)
            float keep = tv.rearOffset + 4f + 12f * (owner.defaultTrailerLength + owner.trailerGap) + 10f;
            float len = 0;
            for (int i = tv.trail.Count - 1; i > 0; i--)
            {
                len += Vector3.Distance(tv.trail[i], tv.trail[i - 1]);
                if (len > keep) { tv.trail.RemoveRange(0, i - 1); break; }
            }
        }

        private void PlaceTrailers(Train tv, TaskExecutor ex)
        {
            float d = tv.rearOffset;
            Vector3 back = -tv.tractor.forward;
            foreach (Item item in ex.Carried)
            {
                if (!(item?.vItem is GameObject go) || go == null) continue;
                var h = HitchesOf(go);
                Vector3 front = PointBehind(tv.trail, d, back);
                Vector3 rear = PointBehind(tv.trail, d + h.length, back);
                Vector3 dir = front - rear; dir.y = 0;
                Quaternion rot = dir.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(dir.normalized, Vector3.up) : tv.tractor.rotation;
                go.transform.rotation = rot;
                if (h.front != null)
                {
                    // the trailer's front hitch on the coupling point (pivot kept at its height above the floor)
                    Vector3 offset = go.transform.position - h.front.position;
                    go.transform.position = new Vector3(front.x, go.transform.position.y, front.z) + new Vector3(offset.x, 0, offset.z);
                    go.transform.position = new Vector3(go.transform.position.x, front.y + (go.transform.position.y - Bottom(go)), go.transform.position.z);
                }
                else
                {
                    Vector3 mid = (front + rear) * 0.5f;
                    float pivotAboveBottom = go.transform.position.y - Bottom(go);
                    go.transform.position = new Vector3(mid.x, mid.y + pivotAboveBottom, mid.z);
                }
                d += h.length + (h.front != null && h.rear != null ? 0f : owner.trailerGap);   // hitched: rear hitch = next front hitch
            }
        }

        private (Transform front, Transform rear, float length) HitchesOf(GameObject go)
        {
            if (hitches.TryGetValue(go, out var h)) return h;
            Transform f = FindDeep(go.transform, "Hitch_Front"), r = FindDeep(go.transform, "Hitch_Rear");
            float length;
            if (f != null && r != null) length = Mathf.Max(0.1f, Vector3.Distance(f.position, r.position));
            else
            {
                Vector3 size = ItemVisuals.SizeOf(go);
                length = size.z > 0.05f ? Mathf.Max(size.x, size.z) : owner.defaultTrailerLength;
            }
            hitches[go] = h = (f, r, length);
            return h;
        }

        /// <summary>The point <paramref name="distance"/> back along <paramref name="trail"/> (positions, newest last), measured
        /// from its newest point; beyond its oldest point it continues straight along <paramref name="fallbackBack"/>.</summary>
        public static Vector3 PointBehind(IReadOnlyList<Vector3> trail, float distance, Vector3 fallbackBack)
        {
            if (trail == null || trail.Count == 0) return fallbackBack * distance;
            float left = distance;
            for (int i = trail.Count - 1; i > 0; i--)
            {
                float seg = Vector3.Distance(trail[i], trail[i - 1]);
                if (left <= seg) return seg > 0 ? Vector3.Lerp(trail[i], trail[i - 1], left / seg) : trail[i];
                left -= seg;
            }
            Vector3 dir = fallbackBack;
            if (trail.Count >= 2)
            {
                Vector3 last = trail[0] - trail[1];
                if (last.sqrMagnitude > 1e-8f) dir = last.normalized;
            }
            dir.y = 0;
            if (dir.sqrMagnitude < 1e-8f) dir = Vector3.back;
            return trail[0] + dir.normalized * left;
        }

        private static float Bottom(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return go.transform.position.y;
            float min = float.MaxValue;
            foreach (var r in rs) min = Mathf.Min(min, r.bounds.min.y);
            return min;
        }

        private static Transform FindDeep(Transform root, string n)
        {
            if (root == null) return null;
            if (root.name == n) return root;
            foreach (Transform c in root) { var r = FindDeep(c, n); if (r != null) return r; }
            return null;
        }
    }
}
