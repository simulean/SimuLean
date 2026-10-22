using System;
using System.Collections.Generic;
using SimuLean.Resources;

namespace SimuLean
{
    /// <summary>
    /// An item on foot between two elements (<see cref="WalkLink"/>): its <see cref="Leg"/> — the same animation contract as
    /// a resource's move (<see cref="TravelLeg"/>: route, path, start and end time) — and whether it waits at the
    /// destination's door. A view reads it to draw the walk; nothing feeds back.
    /// </summary>
    public sealed class ItemWalk
    {
        public Item Item { get; internal set; }
        public Element From { get; internal set; }
        public Element To { get; internal set; }
        public TravelLeg Leg { get; internal set; }
        /// <summary>True when it arrived but the destination had no room: it waits at its door.</summary>
        public bool Waiting { get; internal set; }
    }

    /// <summary>
    /// Output link of an element whose items <b>walk by themselves</b> to the next element (the item is a
    /// mobile entity — a patient walks, as Simio's entities on paths or AnyLogic's <c>MoveTo</c>; the staff stay resources).
    /// The walk uses the resources' travel models (<see cref="ITravelModel"/>: the transport network, a walk grid, a
    /// travel matrix) and their animation contract (<see cref="TravelLeg"/>), so a patient and an operator move alike.
    /// <list type="bullet">
    /// <item>When the origin offers an item, a destination is chosen as the transport link does: the origin's output
    /// strategy among the destinations that can take it, counting the items already on their way to each.</item>
    /// <item>The item leaves the origin (<c>OnExit</c>, its exit labels) and walks from the origin's
    /// <see cref="Element.Location"/> to the destination's in the planned time — model time — then enters it
    /// (<c>Receive</c>, <c>OnEntry</c>, its entry labels): statistics stay link-driven; the walk is in neither element (a
    /// sink's <c>age</c> includes it).</item>
    /// <item>If the destination cannot take it on arrival (another input filled it, a stop), it waits at its door until it
    /// can.</item>
    /// <item>Two elements without a location, or at the same one, hand the item over at once (a general link).</item>
    /// </list>
    /// No resource and no capacity on the way; the elements' stops do not pause a walk.
    /// </summary>
    public sealed class WalkLink : ILink
    {
        private readonly Element origin;
        private readonly List<Element> destinations = new List<Element>();
        private readonly Dictionary<Element, int> inTransit = new Dictionary<Element, int>();
        private readonly List<ItemWalk> walks = new List<ItemWalk>();
        // the walks waiting at each door, in arrival order (a destination with room takes the first; was a scan of every
        // walk on each notification, review2 #24)
        private readonly Dictionary<Element, Queue<ItemWalk>> waitingAt = new Dictionary<Element, Queue<ItemWalk>>();
        private readonly List<Element> candidates = new List<Element>();
        private readonly OutputStrategyContext context = new OutputStrategyContext();

        public WalkLink(Element origin, ITravelModel travel)
        {
            this.origin = origin ?? throw new ArgumentNullException(nameof(origin));
            Travel = travel ?? throw new ArgumentNullException(nameof(travel));
        }

        /// <summary>How the items walk (route and time between two locations; its speed is the walking speed).</summary>
        public ITravelModel Travel { get; }
        public Element Origin => origin;
        public IReadOnlyList<Element> Destinations => destinations;

        /// <summary>The items on foot now (walking, or waiting at a door).</summary>
        public IReadOnlyList<ItemWalk> Walks => walks;

        /// <summary>A walk ended: the item entered its destination (it is no longer in <see cref="Walks"/>). For views only —
        /// a walk shorter than a frame, or one drawn behind the model, still has its way to show; nothing may feed back.</summary>
        public event Action<ItemWalk> Arrived;

        /// <summary>Creates the link from <paramref name="origin"/> to <paramref name="destinations"/> (sets the origin's
        /// output and the destinations' input, resolving combiner ports).</summary>
        public static WalkLink Create(Element origin, IEnumerable<Element> destinations, ITravelModel travel)
        {
            var link = new WalkLink(origin, travel);
            origin.SetOutput(link);
            foreach (var d in destinations)
            {
                Element port = d.ResolveInputFor(origin);
                if (!link.destinations.Contains(port)) link.destinations.Add(port);
                port.SetInput(link);
            }
            return link;
        }

        private readonly Dictionary<Element, double> walkTimes = new Dictionary<Element, double>();

        /// <summary>The time of the walk to <paramref name="to"/> (model time; 0 at the same place or without locations) —
        /// what an agenda releases its patients ahead by.</summary>
        public double WalkTime(Element to)
        {
            if (walkTimes.TryGetValue(to, out double t)) return t;
            string a = origin.Location, b = to.Location;
            t = string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a == b ? 0.0 : Travel.Plan(null, a, b).Duration;
            if (double.IsNaN(t) || t < 0) t = 0.0;
            walkTimes[to] = t;
            return t;
        }

        /// <summary>The longest walk to the destinations (model time).</summary>
        public double LongestWalk()
        {
            double m = 0;
            foreach (var d in destinations) m = Math.Max(m, WalkTime(d));
            return m;
        }

        /// <summary>Forgets the items on their way (new run).</summary>
        public void Reset() { inTransit.Clear(); walks.Clear(); waitingAt.Clear(); }

        // ── origin side ──────────────────────────────────────────────────────

        public bool SendItem(Item theItem, Element source)
        {
            if (source.IsOutputBlocked) return false;
            Element dest = ChooseDestination(theItem, source);
            if (dest == null) return false;                  // no room anywhere: the origin stays blocked

            string a = source.Location, b = dest.Location;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a == b)
            {
                // the same place: handed over at once, as a general link does — with its checks (a stopped or full
                // destination takes nothing; waiting at the door only applies to a walk; review2 #5)
                if (dest.IsInputBlocked || !dest.CheckAvailability(theItem)) return false;
                source.EnterSend();
                bool ok;
                try { ok = ItemHandOver.Receive(source, dest, theItem); }
                finally { source.ExitSend(); }
                if (!ok) return false;
                (source.GetOutputStrategy() as IRoutingMemory)?.Forget(theItem);   // a per-item choice is spent (review2 #2)
                source.StatsCollector.OnExit(theItem);
                dest.StatsCollector.OnEntry(theItem);
                return true;
            }

            TravelPlan plan = Travel.Plan(null, a, b);
            if (double.IsNaN(plan.Duration) || plan.Duration < 0)
                throw new InvalidOperationException($"'{source.GetName()}': a walk needs a defined time (network, walk grid or travel matrix).");
            // it leaves now and walks
            (source.GetOutputStrategy() as IRoutingMemory)?.Forget(theItem);
            ItemHandOver.Exit(source, theItem);
            source.StatsCollector.OnExit(theItem);
            inTransit[dest] = (inTransit.TryGetValue(dest, out int n) ? n : 0) + 1;
            double now = source.GetSimClock().GetSimulationTime();
            var walk = new ItemWalk
            {
                Item = theItem, From = source, To = dest,
                Leg = new TravelLeg
                {
                    From = a, To = b, Route = plan.Route, Path = plan.Path, Distance = plan.Distance,
                    StartTime = now, EndTime = now + plan.Duration, Carried = new[] { theItem },
                },
            };
            walks.Add(walk);
            source.GetSimClock().Schedule(() => Arrive(walk), plan.Duration);
            return true;
        }

        /// <summary>The items set off even when the destination has no room now, and wait at its door (patients going to
        /// their appointment: the room frees as they arrive). False (default) = an item walks only to a destination with room
        /// for it, counting the items on their way.</summary>
        public bool WaitAtDoor { get; set; }

        // Room at a destination now, counting the items already on their way to it.
        private bool HasRoom(Element d, Item item, Element source)
        {
            if (d.IsInputBlocked || !d.CheckAvailability(item) || !d.ValidateInput(item, source)) return false;
            int free = d.GetFreeCapacity();
            int coming = inTransit.TryGetValue(d, out int n) ? n : 0;
            return free < 0 || free - coming > 0;
        }

        private Element ChooseDestination(Item item, Element source)
        {
            var strategy = source.GetOutputStrategy();
            // A routing by rule (an expression on the item's labels) names the destination over all of them: the item goes
            // there or waits — never to another one because that one is full (its index is over the destinations).
            if (strategy is IRuleRouting rule)
            {
                int r = rule.Route(destinations, item, source);
                if (r < 0) return null;
                Element d = destinations[r];
                if (WaitAtDoor) return d.ValidateInput(item, source) ? d : null;   // it sets off and waits at the door
                return HasRoom(d, item, source) ? d : null;
            }
            candidates.Clear();
            foreach (var d in destinations)
                if (WaitAtDoor ? d.ValidateInput(item, source) : HasRoom(d, item, source)) candidates.Add(d);
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];
            int i = strategy.SelectOutput(candidates, item, source, context.Set(source, candidates));
            if (i >= 0 && i < candidates.Count) return candidates[i];
            return WaitAtDoor ? candidates[0] : null;   // nobody can take it now: it waits (or goes to the first door)
        }

        // ── arrival ──────────────────────────────────────────────────────────

        private void Arrive(ItemWalk walk)
        {
            if (Deliver(walk)) return;
            walk.Waiting = true;                             // at the door until the destination has room
            if (!waitingAt.TryGetValue(walk.To, out var q)) waitingAt[walk.To] = q = new Queue<ItemWalk>();
            q.Enqueue(walk);
        }

        private bool Deliver(ItemWalk walk)
        {
            Element d = walk.To;
            if (d.IsInputBlocked || !d.CheckAvailability(walk.Item) || !d.ValidateInput(walk.Item, origin)
                || !ItemHandOver.Receive(null, d, walk.Item))
                return false;
            walks.Remove(walk);
            d.StatsCollector.OnEntry(walk.Item);
            inTransit[d]--;
            Arrived?.Invoke(walk);
            // the room promised to it is free again: items still waiting in the origin may go now
            origin.GetSimClock().Schedule(() => { if (!origin.IsOutputBlocked) origin.NotifyUnblock(); }, 0);
            return true;
        }

        // ── destination side ─────────────────────────────────────────────────

        /// <summary>A destination has room: an item waiting at its door enters first, else the origin may send again.</summary>
        public bool NotifyAvailable(Element source)
        {
            // the first to arrive at that door enters first
            if (waitingAt.TryGetValue(source, out var q) && q.Count > 0)
            {
                if (!Deliver(q.Peek())) return false;
                q.Dequeue();
                return true;
            }
            if (origin.IsOutputBlocked) return false;
            return origin.NotifyUnblock();
        }
    }
}
