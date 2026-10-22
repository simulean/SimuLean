using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Shared SKU-keyed stock buffer used as an input port by material-consuming
    /// stations (<see cref="PickingStation"/>, <see cref="AssemblyStation"/>).
    /// Items arriving through the link layer are stored in per-SKU FIFO queues;
    /// the owning station draws them by SKU and quantity.
    ///
    /// The SKU of an item is its type string (<see cref="Item.GetTypeName"/>);
    /// items without a type string fall back to "type_&lt;id&gt;".
    /// Being an <see cref="Element"/>, all arrivals cross a Link, so statistics
    /// (input counts, content, stay times) are collected automatically.
    /// </summary>
    public class SkuStockPort : Element
    {
        private readonly int capacity;                    // < 0 → unbounded
        private readonly Dictionary<string, Queue<Item>> store
            = new Dictionary<string, Queue<Item>>();
        private int totalCount;
        private readonly Action<Item> onItemArrived;

        /// <param name="capacity">Maximum items held across all SKUs; negative = unbounded.</param>
        /// <param name="onItemArrived">Callback invoked after an item is stored (station re-evaluates readiness).</param>
        public SkuStockPort(string name, SimClock simClock, int capacity,
                            Action<Item> onItemArrived,
                            InputStrategy strategy = null, IVElement vElement = null)
            : base(name, simClock, vElement)
        {
            this.capacity      = capacity;
            this.onItemArrived = onItemArrived;
            if (strategy != null)
                SetInputStrategy(strategy);
        }

        /// <summary>SKU key of an item: its type string, or "type_&lt;id&gt;" fallback.</summary>
        public static string KeyOf(Item item)
        {
            string t = item.GetTypeName();
            return string.IsNullOrEmpty(t) ? "type_" + item.GetId() : t;
        }

        public override void Start()
        {
            base.Start();
            store.Clear();
            totalCount = 0;
        }

        // ── stock queries ────────────────────────────────────────────────────

        public int CountOf(string sku)
            => store.TryGetValue(sku, out var q) ? q.Count : 0;

        public int TotalCount => totalCount;

        /// <summary>True when every SKU→qty need can be satisfied from stock.</summary>
        public bool Has(IReadOnlyDictionary<string, int> needs)
        {
            foreach (var kv in needs)
                if (CountOf(kv.Key) < kv.Value)
                    return false;
            return true;
        }

        // ── stock movement (called by the owning station) ────────────────────

        /// <summary>Removes up to <paramref name="qty"/> items of <paramref name="sku"/> (FIFO).</summary>
        public List<Item> Draw(string sku, int qty)
        {
            var drawn = new List<Item>();
            if (store.TryGetValue(sku, out var q))
            {
                while (drawn.Count < qty && q.Count > 0)
                {
                    drawn.Add(q.Dequeue());
                    totalCount--;
                }
            }
            return drawn;
        }

        /// <summary>Removes and returns every stored item (used for batch returns).</summary>
        public List<Item> DrainAll()
        {
            var all = new List<Item>();
            foreach (var q in store.Values)
                while (q.Count > 0)
                    all.Add(q.Dequeue());
            store.Clear();
            totalCount = 0;
            return all;
        }

        /// <summary>Tells upstream elements that space is available again.</summary>
        public void NotifyUpstream()
            => NotifyInput();

        // ── Element contract ─────────────────────────────────────────────────

        public override int GetQueueLength()  => totalCount;

        public override int GetFreeCapacity()
            => capacity < 0 ? int.MaxValue : capacity - totalCount;

        public override bool CheckAvailability(Item theItem)
            => (capacity < 0 || totalCount < capacity);

        public override bool Receive(Item theItem)
        {
            if (!CheckAvailability(theItem))
                return false;

            string key = KeyOf(theItem);
            if (!store.TryGetValue(key, out var q))
            {
                q = new Queue<Item>();
                store[key] = q;
            }
            q.Enqueue(theItem);
            totalCount++;

            vElement.LoadItem(theItem);
            onItemArrived?.Invoke(theItem);
            return true;
        }

        public override bool Unblock()
        {
            NotifyUpstream();
            return true;
        }
    }
}
