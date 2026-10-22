using System;
using System.Collections.Generic;

namespace SimuLean.Collections
{
    /// <summary>
    /// A FlexSim-style model list: a decoupling rendezvous between producers
    /// (Push) and consumers (Pull) of any model object — items, move requests,
    /// vehicles, orders.
    ///
    /// The essential feature is <b>back-orders</b>: a Pull with no matching
    /// entry registers a back-order; a later Push that matches it fulfills the
    /// pull directly (the value never enters the list). This makes
    /// "idle transporter waits for work" and "work waits for a transporter"
    /// symmetric without bespoke wiring.
    ///
    /// Determinism: entries and back-orders carry a monotone sequence number.
    /// With no sort, pulls are FIFO by insertion; back-orders are always
    /// fulfilled oldest-first among those whose filter matches. No wall-clock,
    /// no unordered iteration — safe for reproducible simulation runs.
    ///
    /// Not thread-safe by design: it lives inside a single model's event loop
    /// (one SimClock), like every other engine structure.
    /// </summary>
    public class ModelList<T>
    {
        private sealed class Entry
        {
            public T Value;
            public long Seq;
        }

        /// <summary>A registered back-order (pending pull); <see cref="Cancel"/> withdraws it.</summary>
        public sealed class BackOrder
        {
            internal Func<T, bool> Filter;
            internal Action<T> OnFulfilled;
            internal long Seq;
            internal ModelList<T> Owner;

            /// <summary>True until fulfilled or cancelled.</summary>
            public bool IsPending { get; internal set; } = true;

            /// <summary>Withdraws the back-order. Returns false if it was already fulfilled / cancelled.</summary>
            public bool Cancel()
            {
                if (!IsPending) return false;
                IsPending = false;
                Owner.backOrders.Remove(this);
                return true;
            }
        }

        private readonly List<Entry> entries = new List<Entry>();
        private readonly List<BackOrder> backOrders = new List<BackOrder>();
        private long seqSource = 0;

        /// <summary>Number of stored (unpulled) entries.</summary>
        public int Count => entries.Count;

        /// <summary>Number of unfulfilled back-orders (pending pulls).</summary>
        public int BackOrderCount => backOrders.Count;

        /// <summary>
        /// Pushes a value. If a registered back-order's filter matches, the
        /// oldest such back-order is fulfilled immediately and the value never
        /// enters the list; otherwise the value is stored.
        /// Returns true if the push fulfilled a back-order.
        /// </summary>
        public bool Push(T value)
        {
            for (int i = 0; i < backOrders.Count; i++)   // oldest first
            {
                BackOrder bo = backOrders[i];
                if (bo.Filter == null || bo.Filter(value))
                {
                    backOrders.RemoveAt(i);
                    bo.IsPending = false;
                    bo.OnFulfilled(value);
                    return true;
                }
            }

            entries.Add(new Entry { Value = value, Seq = seqSource++ });
            return false;
        }

        /// <summary>
        /// Attempts an immediate pull. Selects among entries matching
        /// <paramref name="filter"/> (null = all); with a <paramref name="sort"/>,
        /// the best-ranked entry wins (ties broken by insertion order),
        /// otherwise FIFO. Returns false if nothing matches.
        /// </summary>
        public bool TryPull(Func<T, bool> filter, Comparison<T> sort, out T value)
        {
            int best = FindBest(filter, sort);
            if (best < 0)
            {
                value = default(T);
                return false;
            }

            value = entries[best].Value;
            entries.RemoveAt(best);
            return true;
        }

        private int FindBest(Func<T, bool> filter, Comparison<T> sort)
        {
            int best = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                if (filter != null && !filter(entries[i].Value))
                    continue;

                if (best < 0)
                {
                    best = i;
                    if (sort == null)
                        break;              // FIFO: first match wins
                }
                else
                {
                    int cmp = sort(entries[i].Value, entries[best].Value);
                    // strict improvement only — equal keeps the earlier entry (deterministic)
                    if (cmp < 0)
                        best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// Like <see cref="TryPull"/> but leaves the entry in the list.
        /// Use to inspect the next candidate before committing to remove it.
        /// </summary>
        public bool TryPeek(Func<T, bool> filter, Comparison<T> sort, out T value)
        {
            int best = FindBest(filter, sort);
            if (best < 0)
            {
                value = default(T);
                return false;
            }
            value = entries[best].Value;
            return true;
        }

        /// <summary>
        /// Pulls a matching value now, or registers a back-order fulfilled by a
        /// future <see cref="Push"/>. <paramref name="onFulfilled"/> is invoked
        /// exactly once in both cases (unless the back-order is cancelled).
        /// Returns the back-order, or null when the pull was satisfied at once.
        /// </summary>
        public BackOrder Pull(Func<T, bool> filter, Comparison<T> sort, Action<T> onFulfilled)
        {
            if (onFulfilled == null)
                throw new ArgumentNullException(nameof(onFulfilled));

            T value;
            if (TryPull(filter, sort, out value))
            {
                onFulfilled(value);
                return null;
            }

            var bo = new BackOrder
            {
                Filter = filter,
                OnFulfilled = onFulfilled,
                Seq = seqSource++,
                Owner = this,
            };
            backOrders.Add(bo);
            return bo;
        }

        /// <summary>Removes a specific stored value (reference/equality match). Returns true if found.</summary>
        public bool Remove(T value)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (EqualityComparer<T>.Default.Equals(entries[i].Value, value))
                {
                    entries.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Snapshot of stored entries in insertion order (for inspection/UI; do not mutate model state through it).</summary>
        public List<T> Snapshot()
        {
            var result = new List<T>(entries.Count);
            foreach (Entry e in entries)
                result.Add(e.Value);
            return result;
        }

        /// <summary>Clears entries and back-orders (model reset).</summary>
        public void Clear()
        {
            entries.Clear();
            foreach (var bo in backOrders) bo.IsPending = false;
            backOrders.Clear();
            seqSource = 0;
        }
    }
}
