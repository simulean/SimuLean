using System;
using System.Collections.Generic;

namespace SimuLean.Resources
{
    /// <summary>
    /// Output link of an element whose items are <b>carried by resources</b> (operators, vehicles) to the next
    /// elements — the "transport" use of a resource, next to "process". Abstract
    /// carriers take a transport time per move; <b>spatial</b> carriers (vehicles on the network) first travel
    /// empty to the origin's <see cref="Element.Location"/>, load, travel to the destination's location with their
    /// travel model (unless <see cref="TravelTime"/> is set) and unload.
    /// <list type="bullet">
    /// <item>When the origin offers an item, a destination with room is chosen (the origin's output strategy among
    /// the destinations that can take it, counting the items already on their way to each), a transport request is
    /// made to the dispatcher and the origin keeps the item (refused send → BLOCKED, as with a full destination).</item>
    /// <item>When a resource is assigned it loads the item: the origin hands it over (<see cref="Element.SendHeldItem"/>)
    /// and the item leaves it (<c>OnExit</c>); it travels for the transport time, unloads and delivers it to the
    /// destination (<c>Receive</c>, <c>OnEntry</c>) — statistics stay link-driven. If the destination cannot take
    /// it then (another element filled it), the resource waits, HELD, until it can.</item>
    /// <item>Resources are released at the delivery (task type Transport).</item>
    /// </list>
    /// </summary>
    public sealed class ResourceTransportLink : ILink
    {
        private readonly Element origin;
        private readonly List<Element> destinations = new List<Element>();
        private readonly Dictionary<Element, int> inTransit = new Dictionary<Element, int>();
        private readonly HashSet<Item> requested = new HashSet<Item>();
        private readonly TransportMoves moves;
        private readonly List<Element> candidates = new List<Element>();                 // refilled on every choice
        private readonly OutputStrategyContext context = new OutputStrategyContext();

        public ResourceTransportLink(Element origin, StationResources transport)
        {
            this.origin = origin ?? throw new ArgumentNullException(nameof(origin));
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            moves = new TransportMoves(origin, transport);
        }

        /// <summary>Dispatcher, requirements and priority of the transport requests.</summary>
        public StationResources Transport { get; }

        // A move takes load + travel + unload, defined separately: the travel leg is what the transport network
        // will compute for spatial resources; loading and unloading stay as defined here or by the resource.

        /// <summary>Travel time of a move (item, destination); null = 0 for abstract carriers and the carrier's travel
        /// model for spatial ones (set, it overrides the model's time; the carrier still ends at the destination).</summary>
        public Func<Item, Element, double> TravelTime { get => moves.TravelTime; set => moves.TravelTime = value; }

        /// <summary>Load time per item; null = the resource's own <see cref="TaskExecutor.LoadTime"/>.</summary>
        public Func<Item, double> LoadTime { get => moves.LoadTime; set => moves.LoadTime = value; }

        /// <summary>Unload time per item; null = the resource's own <see cref="TaskExecutor.UnloadTime"/>.</summary>
        public Func<Item, double> UnloadTime { get => moves.UnloadTime; set => moves.UnloadTime = value; }

        public IReadOnlyList<Element> Destinations => destinations;

        /// <summary>The element whose items it carries.</summary>
        public Element Origin => origin;

        /// <summary>Items on their way (requested, loaded or waiting at a full destination).</summary>
        public int InTransit { get { int n = 0; foreach (var v in inTransit.Values) n += v; return n; } }

        /// <summary>True when a carrier was requested for <paramref name="item"/> and has not loaded it yet.</summary>
        public bool IsRequested(Item item) => item != null && requested.Contains(item);

        /// <summary>Creates the link from <paramref name="origin"/> to <paramref name="destinations"/> (sets the
        /// origin's output and the destinations' input, resolving combiner ports).</summary>
        public static ResourceTransportLink Create(Element origin, IEnumerable<Element> destinations, StationResources transport)
        {
            var link = new ResourceTransportLink(origin, transport);
            origin.SetOutput(link);
            foreach (var d in destinations)
            {
                Element port = d.ResolveInputFor(origin);
                if (!link.destinations.Contains(port)) link.destinations.Add(port);
                port.SetInput(link);
            }
            return link;
        }

        /// <summary>Forgets requests, items on their way and waiting deliveries (new run).</summary>
        public void Reset()
        {
            inTransit.Clear(); requested.Clear(); moves.Reset();
        }

        // ── origin side ──────────────────────────────────────────────────────

        public bool SendItem(Item theItem, Element source)
        {
            if (moves.TakeLoading(theItem))
            {
                // The assigned resource loads it now: it leaves the origin. Only now is it no longer "requested": the
                // hand-over may come after the load (deferred), and an offer meanwhile must not request a second
                // carrier.
                requested.Remove(theItem);
                (source.GetOutputStrategy() as IRoutingMemory)?.Forget(theItem);   // a per-item choice is spent (review2 #2)
                ItemHandOver.Exit(source, theItem);
                source.StatsCollector.OnExit(theItem);
                return true;
            }
            if (requested.Contains(theItem) || source.IsOutputBlocked) return false;

            Element dest = ChooseDestination(theItem, source);
            if (dest == null) return false;                  // no room anywhere: the origin stays blocked

            inTransit[dest] = (inTransit.TryGetValue(dest, out int n) ? n : 0) + 1;
            requested.Add(theItem);
            moves.Request(theItem, dest, Deliver);
            return false;                                    // stays in the origin until loaded
        }

        private bool HasRoom(Element d, Item item, Element source)
        {
            if (d.IsInputBlocked || !d.CheckAvailability(item) || !d.ValidateInput(item, source)) return false;
            int free = d.GetFreeCapacity();
            int coming = inTransit.TryGetValue(d, out int n) ? n : 0;
            return free < 0 || free - coming > 0;   // room already promised to items on their way
        }

        private Element ChooseDestination(Item item, Element source)
        {
            // A routing by rule (an expression on the item's labels) names the destination over all of them: the item goes
            // there or waits — its index is over the destinations, not over those with room (a full named
            // destination sent the item to the first one with room).
            if (source.GetOutputStrategy() is IRuleRouting rule)
            {
                int r = rule.Route(destinations, item, source);
                return r >= 0 && HasRoom(destinations[r], item, source) ? destinations[r] : null;
            }
            candidates.Clear();
            foreach (var d in destinations)
                if (HasRoom(d, item, source)) candidates.Add(d);
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];
            int i = source.GetOutputStrategy().SelectOutput(candidates, item, source, context.Set(source, candidates));
            return i >= 0 && i < candidates.Count ? candidates[i] : null;   // none chosen: it waits (review2 #1)
        }

        // ── delivery (the carrier unloaded) ──────────────────────────────────

        private bool Deliver(Item item, Element d)
        {
            if (d.IsInputBlocked || !d.CheckAvailability(item) || !d.ValidateInput(item, origin) || !ItemHandOver.Receive(null, d, item))
                return false;
            d.StatsCollector.OnEntry(item);
            inTransit[d]--;
            // The room promised to this item is no longer counted: items still waiting in the origin may go now
            // (the destination itself may never notify, e.g. a queue passing items straight through).
            origin.GetSimClock().Schedule(() => { if (!origin.IsOutputBlocked) origin.NotifyUnblock(); }, 0);
            return true;
        }

        // ── destination side ─────────────────────────────────────────────────

        /// <summary>A destination has room: first a resource waiting there delivers, else the origin may send again.</summary>
        public bool NotifyAvailable(Element source)
        {
            bool? waited = moves.DeliverWaiting(source);
            if (waited.HasValue) return waited.Value;
            if (origin.IsOutputBlocked) return false;
            return origin.NotifyUnblock();
        }
    }
}
