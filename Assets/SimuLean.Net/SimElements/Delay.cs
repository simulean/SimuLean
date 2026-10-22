using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// An activity without capacity: every item that enters waits its own delay, drawn when it
    /// enters (an expression reads the item, e.g. <c>t_test</c>), all at once — a patient at home for a night, three months
    /// on a therapy, a curing time, a transit. Its cost does not depend on how many items wait (a <see cref="MultiServer"/>
    /// with a huge capacity was the workaround). The delays are work of the element (<see cref="Element.ScheduleWork"/>):
    /// an Immediate stop pauses them. An item whose delay is over and that cannot leave waits, in the order they finished,
    /// until the output frees (state BLOCKED when nothing else is running).
    /// </summary>
    public class Delay : Element
    {
        private readonly IRandomSampler delay;

        // Items in their delay, each with its pending work; finished items that could not leave, oldest first.
        private readonly Dictionary<Item, WorkHandle> running = new Dictionary<Item, WorkHandle>();
        private readonly LinkedList<Item> finished = new LinkedList<Item>();
        private readonly Dictionary<Item, LinkedListNode<Item>> finishedNodes = new Dictionary<Item, LinkedListNode<Item>>();

        /// <param name="delay">The delay of each item (expressions read the item and the element).</param>
        /// <param name="vElement">The view (null = headless).</param>
        public Delay(IRandomSampler delay, string name, SimClock clock, InputStrategy inputStrategy = null, IVElement vElement = null)
            : base(name, clock, vElement)
        {
            this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
            this.inputStrategy = inputStrategy;
        }

        public override void Start()
        {
            running.Clear();
            finished.Clear();
            finishedNodes.Clear();
            base.Start();
        }

        /// <summary>Items held: in their delay plus finished and waiting to leave.</summary>
        public override int GetQueueLength() => running.Count + finished.Count;

        /// <summary>Unbounded (negative), as a sink.</summary>
        public override int GetFreeCapacity() => -1;

        public override bool CheckAvailability(Item theItem) => true;   // admission: the input strategy, checked by the link

        /// <summary>Items in their delay (any order), then the finished ones waiting to leave (oldest first).</summary>
        public IEnumerable<Item> Items
        {
            get
            {
                foreach (var it in running.Keys) yield return it;
                foreach (var it in finished) yield return it;
            }
        }

        /// <summary>Items still in their delay.</summary>
        public int RunningCount => running.Count;

        public override bool Receive(Item theItem)
        {
            double d = SimuLean.Expressions.Samplers.Sample(delay, theItem, this);
            running[theItem] = ScheduleWork(new DoneEvent(this, theItem), d);
            vElement.LoadItem(theItem);
            UpdateState();
            return true;
        }

        private void Done(Item item)
        {
            running.Remove(item);
            // in order: an item finishing while others wait to leave queues behind them
            if (!HasItemToOffer() && SendOut(item))
            {
                vElement.ReportState("Exit");
            }
            else
            {
                finishedNodes[item] = finished.AddLast(item);
                if (finished.Count > 1 && GetOutput() is ListLink list) list.Advertise(item, this);
            }
            UpdateState();
        }

        public override bool Unblock()
        {
            if (finished.Count == 0) return false;
            if (OutputRequestsCarriers)
            {
                // Every finished item gets its own carrier, in order; one refused keeps the rest waiting.
                foreach (Item it in new List<Item>(finished))
                {
                    if (!finishedNodes.ContainsKey(it) || AwaitsCarrier(it)) continue;
                    if (SendHeldItem(it)) return true;
                    if (!finishedNodes.ContainsKey(it)) return true;   // loaded inside its own request
                    if (!AwaitsCarrier(it)) break;
                }
                UpdateState();
                return false;
            }
            Item head = finished.First.Value;
            if (!SendOut(head)) { UpdateState(); return false; }
            Take(head);
            vElement.ReportState("Exit");
            UpdateState();
            return true;
        }

        // A finished item still to be offered (it leaves first); items waiting for their carrier were offered already.
        private bool HasItemToOffer()
        {
            if (!OutputRequestsCarriers) return finished.Count > 0;
            foreach (Item it in finished) if (!AwaitsCarrier(it)) return true;
            return false;
        }

        /// <summary>Hands over a specific finished item (pulled from a list), wherever it is in the waiting order.</summary>
        public override bool SendHeldItem(Item item)
        {
            if (item == null || !finishedNodes.ContainsKey(item) || !SendOut(item)) return false;
            Take(item);
            vElement.ReportState("Exit");
            UpdateState();
            return true;
        }

        private void Take(Item item)
        {
            if (finishedNodes.TryGetValue(item, out var node)) { finished.Remove(node); finishedNodes.Remove(item); }
        }

        private void UpdateState()
            => SetState(running.Count > 0 ? ElementState.PROCESSING : finished.Count > 0 ? ElementState.BLOCKED : ElementState.IDLE);

        private sealed class DoneEvent : IEvent
        {
            private readonly Delay owner;
            private readonly Item item;
            public DoneEvent(Delay owner, Item item) { this.owner = owner; this.item = item; }
            void IEvent.Execute() => owner.Done(item);
        }
    }
}
