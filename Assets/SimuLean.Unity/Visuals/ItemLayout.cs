using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Where the items an element holds are drawn (taken out of <see cref="UnityQueue"/> so every holding element
    /// — queues, delays — lays its items out the same way): <see cref="QueueLayoutMode.Grid"/>, <see cref="QueueLayoutMode.Stack"/>,
    /// <see cref="QueueLayoutMode.Seats"/> or <see cref="QueueLayoutMode.Line"/>, sized from the element's footprint and the
    /// item size (no fixed sizes). Views only.
    /// </summary>
    public static class ItemLayout
    {
        /// <summary>Seats layout: how far in front of its seat (along the element's +Z) an item stands (m).</summary>
        public const float SeatStandOff = 0.45f;
        /// <summary>Seats layout: where a sitting person's feet stand, in front of its seat's centre (m; the SitIdle clip
        /// moves the pelvis back over the seat).</summary>
        public const float SeatSitOffset = 0.12f;

        /// <summary>Places <paramref name="items"/> (in order: the first is the head / the front row) under
        /// <paramref name="owner"/>.</summary>
        public static void Arrange(SElement owner, IEnumerable<Item> items, QueueLayoutMode mode, float separation,
                                   Vector2 gridArea, Transform itemPosition, float lineLength)
        {
            if (Application.isPlaying && UnityElementLabel.HidesItemsOf(owner)) return;   // hidden by a label: nothing to place
            Vector3 itemSize = Vector3.zero;
            float pivotHeight = 0f;
            bool any = false;
            foreach (Item it in items)
            {
                any = true;
                if (it.vItem is GameObject g && g != null) { itemSize = ItemVisuals.SizeOf(g); pivotHeight = ItemVisuals.PivotHeight(g); break; }
            }
            if (!any) return;
            Transform t = owner.transform;
            var grid = new Grid(owner, mode, separation, gridArea, itemPosition, itemSize, pivotHeight);
            var seats = mode == QueueLayoutMode.Seats ? Seats(t) : null;
            var seatOf = seats != null ? SeatsTaken(owner, items, seats.Count) : null;
            int i = 0, standing = 0;
            foreach (Item it in items)
            {
                if (it.vItem is GameObject gItem && gItem != null)
                {
                    // a person's origin is at its feet (whatever its pose's bounds): no lift, so it never jumps
                    bool person = gItem.GetComponent<PersonAnimator>() != null;
                    float lift = person ? 0f : pivotHeight;
                    if (seatOf != null && seatOf.TryGetValue(it, out int seat))
                    {
                        // its own seat until it leaves (the others do not move up when the first one goes); a person sits
                        // on it, any other item stands on the floor in front
                        Vector3 floor = seats[seat].position - t.up * Vector3.Dot(seats[seat].position - t.position, t.up);
                        gItem.transform.SetPositionAndRotation(floor + t.forward * (person ? SeatSitOffset : SeatStandOff) + t.up * lift, t.rotation);
                        PersonAnimator.SetPose(gItem, person ? PersonAnimator.Pose.Sit : PersonAnimator.Pose.Stand);
                    }
                    else if (mode == QueueLayoutMode.Line)
                    {
                        PersonAnimator.SetPose(gItem, PersonAnimator.Pose.Stand);
                        Vector2 o = LineOffset(i, grid.Step, lineLength, out bool reversed);
                        gItem.transform.position = grid.Origin + t.right * o.x + t.forward * o.y + t.up * lift;
                        gItem.transform.rotation = reversed ? t.rotation * Quaternion.Euler(0f, 180f, 0f) : t.rotation;
                    }
                    else
                    {
                        PersonAnimator.SetPose(gItem, PersonAnimator.Pose.Stand);
                        Vector3 at = grid.Position(seats != null ? standing++ : i);
                        gItem.transform.position = at + t.up * (lift - pivotHeight);
                        gItem.transform.rotation = t.rotation;
                    }
                }
                i++;
            }
        }

        // Seats layout: each item keeps the seat it took until it leaves; a newcomer takes the first free seat (items
        // beyond the seats stand on the grid). Per element, forgotten with it.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SElement, Dictionary<Item, int>> seatsTaken =
            new System.Runtime.CompilerServices.ConditionalWeakTable<SElement, Dictionary<Item, int>>();

        /// <summary>The seat of each held item (Seats layout): kept while the item stays, the first free one for a newcomer.</summary>
        public static Dictionary<Item, int> SeatsTaken(SElement owner, IEnumerable<Item> items, int seatCount)
        {
            var map = seatsTaken.GetOrCreateValue(owner);
            var present = new HashSet<Item>(items);
            var gone = new List<Item>();
            foreach (var kv in map) if (!present.Contains(kv.Key) || kv.Value >= seatCount) gone.Add(kv.Key);
            foreach (var g in gone) map.Remove(g);
            var used = new HashSet<int>(map.Values);
            int next = 0;
            foreach (Item it in items)
            {
                if (map.ContainsKey(it)) continue;
                while (next < seatCount && used.Contains(next)) next++;
                if (next >= seatCount) break;
                map[it] = next;
                used.Add(next);
            }
            return map;
        }

        /// <summary>
        /// Line layout: where item <paramref name="index"/> (0 = the head) stands, in the element's axes (x = right, y = forward
        /// = towards the head), for items <paramref name="step"/> apart. Without a <paramref name="lineLength"/> the line runs
        /// straight back along −Z; with one, a run holds the items that fit in that length and the next run folds back
        /// beside it (1.5 steps to the right), walking the other way (<paramref name="reversed"/>: those items face −Z).
        /// </summary>
        public static Vector2 LineOffset(int index, float step, float lineLength, out bool reversed)
        {
            int perRun = lineLength > 0f && step > 0f ? Mathf.Max(1, Mathf.FloorToInt(lineLength / step + 1e-4f) + 1) : int.MaxValue;
            int run = index / perRun, k = index % perRun;
            reversed = run % 2 == 1;
            float back = reversed ? (perRun - 1 - k) * step : k * step;
            return new Vector2(run * step * 1.5f, -back);
        }

        /// <summary>The <c>Seat</c> nodes under <paramref name="root"/> in hierarchy order (<see cref="QueueLayoutMode.Seats"/>).</summary>
        public static List<Transform> Seats(Transform root)
        {
            var seats = new List<Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>())
                if (t != root && t.name == "Seat") seats.Add(t);
            return seats;
        }

        /// <summary>The cells of one grid layer (columns × rows) for an item of the given size (zero = unknown).</summary>
        public static Vector2Int GridCells(SElement owner, float separation, Vector2 gridArea, Vector3 itemSize)
        {
            var g = new Grid(owner, QueueLayoutMode.Grid, separation, gridArea, null, itemSize, itemSize.y * 0.5f);
            return new Vector2Int(g.cols, g.rows);
        }

        /// <summary>One placement of the items, derived from the element's footprint and the item size (no fixed sizes).</summary>
        private readonly struct Grid
        {
            public readonly int cols, rows;
            private readonly float step, layerStep, baseLift;
            private readonly Vector3 origin, right, forward, up;
            private readonly bool stack;

            /// <summary>Distance between item centres (the separation, or the item size plus a gap).</summary>
            public float Step => step;
            /// <summary>The item position, or the top of the element's footprint.</summary>
            public Vector3 Origin => origin;

            public Grid(SElement owner, QueueLayoutMode mode, float separation, Vector2 gridArea, Transform itemPosition,
                        Vector3 itemSize, float pivotHeight)
            {
                ItemVisuals.Footprint(owner, out Vector3 size, out Vector3 top);
                float itemSpan = Mathf.Max(itemSize.x, itemSize.z);
                // automatic spacing: the item plus a 15 % gap; without an item size, a tenth of the element
                float fallback = Mathf.Max(0.05f, Mathf.Max(size.x, size.z) * 0.1f);
                step = separation > 0f ? separation : itemSpan > 0f ? itemSpan * 1.15f : fallback;
                layerStep = itemSize.y > 0f ? itemSize.y * 1.05f : step;
                baseLift = pivotHeight;   // the bottom on the element, whatever the pivot (S4)
                float width = gridArea.x > 0f ? gridArea.x : size.x;
                float depth = gridArea.y > 0f ? gridArea.y : size.z;
                cols = Mathf.Max(1, Mathf.FloorToInt(width / step));
                rows = Mathf.Max(1, Mathf.FloorToInt(depth / step));
                stack = mode == QueueLayoutMode.Stack;
                bool explicitOrigin = itemPosition != null && itemPosition != owner.transform;
                origin = explicitOrigin ? itemPosition.position : top;
                right = owner.transform.right;
                forward = owner.transform.forward;
                up = owner.transform.up;
            }

            public Vector3 Position(int index)
            {
                if (stack) return origin + up * (baseLift + layerStep * index);
                int perLayer = cols * rows;
                int layer = index / perLayer, inLayer = index % perLayer;
                int row = inLayer / cols, col = inLayer % cols;
                float x = (col - (cols - 1) * 0.5f) * step;
                float z = ((rows - 1) * 0.5f - row) * step;   // front row (+Z) first
                return origin + right * x + forward * z + up * (baseLift + layerStep * layer);
            }
        }
    }
}
