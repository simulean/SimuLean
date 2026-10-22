using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Min-heap event queue keyed by (time, seq). The monotonically increasing
    /// sequence number breaks ties between events scheduled for the same time,
    /// giving deterministic FIFO ordering (required for reproducible runs).
    /// Supports cancellation via <see cref="EventHandle"/> using lazy deletion:
    /// cancelled entries are skipped when they reach the top, and the heap is
    /// rebuilt when more than half of its entries are dead.
    /// </summary>
    class DoubleMinBinaryHeap
    {
        private struct Entry
        {
            public double Time;
            public long Seq;
            public EventHandle Handle;
        }

        private readonly List<Entry> heap;
        private long seqCounter;
        private int deadCount;
        private int backgroundLive;   // live entries whose event is an IBackgroundEvent

        public DoubleMinBinaryHeap(int n)
        {
            heap = new List<Entry>(n);
        }

        private static bool Less(Entry a, Entry b)
            => a.Time < b.Time || (a.Time == b.Time && a.Seq < b.Seq);

        /// <summary>Number of live (non-cancelled) entries.</summary>
        public int Count()
        {
            return heap.Count - deadCount;
        }

        /// <summary>Number of live entries that can move items (not <see cref="IBackgroundEvent"/>s).</summary>
        public int ForegroundCount() => heap.Count - deadCount - backgroundLive;

        /// <summary>Time of the earliest live entry (0 if empty).</summary>
        public double GetMinValue()
        {
            PurgeTop();
            return heap.Count == 0 ? 0 : heap[0].Time;
        }

        /// <summary>
        /// Schedules <paramref name="content"/> at absolute time <paramref name="key"/>.
        /// Returns a handle that can cancel the entry before it fires.
        /// </summary>
        public EventHandle Add(double key, IEvent content)
        {
            var handle = new EventHandle(this, content, key);
            if (content is IBackgroundEvent) backgroundLive++;
            heap.Add(new Entry { Time = key, Seq = seqCounter++, Handle = handle });
            SiftUp(heap.Count - 1);
            return handle;
        }

        /// <summary>Earliest live event without removing it (null if empty).</summary>
        public object First()
        {
            PurgeTop();
            return heap.Count == 0 ? null : heap[0].Handle.Event;
        }

        /// <summary>Removes and returns the earliest live event (null if empty).</summary>
        public object RetrieveFirst()
        {
            PurgeTop();
            if (heap.Count == 0)
                return null;

            EventHandle first = heap[0].Handle;
            RemoveRoot();
            if (first.Event is IBackgroundEvent) backgroundLive--;
            return first.Event;
        }

        // ── cancellation bookkeeping ─────────────────────────────────────────

        /// <summary>Called by <see cref="EventHandle.Cancel"/> (lazy deletion).</summary>
        internal void NotifyCanceled(EventHandle handle)
        {
            deadCount++;
            if (handle.Event is IBackgroundEvent) backgroundLive--;

            // Rebuild when the majority of entries are dead to bound memory in
            // cancel-heavy models (breakdowns, preemption, reneging).
            if (deadCount > heap.Count / 2)
                Rebuild();
        }

        private void Rebuild()
        {
            var live = new List<Entry>(heap.Count - deadCount);
            foreach (var e in heap)
            {
                if (e.Handle.Canceled)
                    e.Handle.InHeap = false;
                else
                    live.Add(e);
            }

            heap.Clear();
            heap.AddRange(live);
            deadCount = 0;

            for (int i = heap.Count / 2 - 1; i >= 0; i--)
                SiftDown(i);
        }

        /// <summary>Pops cancelled entries off the top until the root is live.</summary>
        private void PurgeTop()
        {
            while (heap.Count > 0 && heap[0].Handle.Canceled)
            {
                RemoveRoot();
                deadCount--;
            }
        }

        // ── heap mechanics ───────────────────────────────────────────────────

        private void RemoveRoot()
        {
            heap[0].Handle.InHeap = false;

            int n = heap.Count - 1;
            heap[0] = heap[n];
            heap.RemoveAt(n);

            if (heap.Count > 0)
                SiftDown(0);
        }

        private void SiftUp(int child)
        {
            while (child > 0)
            {
                int parent = (child - 1) / 2;
                if (!Less(heap[child], heap[parent]))
                    return;

                Swap(child, parent);
                child = parent;
            }
        }

        private void SiftDown(int i)
        {
            int n = heap.Count;
            while (true)
            {
                int left = 2 * i + 1, right = 2 * i + 2, smallest = i;

                if (left  < n && Less(heap[left],  heap[smallest])) smallest = left;
                if (right < n && Less(heap[right], heap[smallest])) smallest = right;

                if (smallest == i)
                    return;

                Swap(i, smallest);
                i = smallest;
            }
        }

        private void Swap(int i, int j)
        {
            Entry interm = heap[i];
            heap[i] = heap[j];
            heap[j] = interm;
        }
    }
}
