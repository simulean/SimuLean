using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Link that connects multiple origins to multiple destinations.
    /// Supports configurable output routing strategies defined at the element level.
    /// </summary>
    public class GeneralLink : ILink
    {
        List<Element> origins = new List<Element>();
        List<Element> outputs = new List<Element>();
        readonly OutputStrategyContext context = new OutputStrategyContext();   // refilled on every send

        // Fair start: the origins waiting to send, by turn (taken at their first refused send, given up when
        // they send); a freed place is offered to the longest-waiting first. Only with several origins.
        readonly Dictionary<Element, long> waitingTurn = new Dictionary<Element, long>();
        long nextTurn, sendAttempts;

        // Add origin element for NotifyAvailable tracking
        public void AddOrigin(Element origin)
        {
            if (!origins.Contains(origin))
                origins.Add(origin);
        }

        // Add destination element
        public void AddOutput(Element output)
        {
            if (!outputs.Contains(output))
                outputs.Add(output);
        }

        /// <summary>
        /// Sends an item from source element to one of the destinations.
        /// Uses the source element's output strategy to select the destination.
        /// Validates with the destination's input strategy before accepting.
        /// </summary>
        public bool SendItem(Item theItem, Element source)
        {
            sendAttempts++;
            bool sent = TrySend(theItem, source);
            if (origins.Count > 1)
            {
                if (sent) waitingTurn.Remove(source);
                else if (!waitingTurn.ContainsKey(source)) waitingTurn[source] = nextTurn++;
            }
            return sent;
        }

        private bool TrySend(Item theItem, Element source)
        {
            if (outputs.Count == 0)
                return false;

            // A stopped element may not release items (downtime, see Element.Stop).
            if (source.IsOutputBlocked)
                return false;

            // Get output strategy from source element (who decides where to send)
            IOutputStrategy strategy = source.GetOutputStrategy();

            // Strategy selects the output. The context carries the per-model parameters (SimContext), so parallel
            // headless models never share routing state; it is only read here, before any nested send.
            int index = strategy.SelectOutput(outputs, theItem, source, context.Set(source, outputs));

            if (index >= 0 && index < outputs.Count)
            {
                Element target = outputs[index];

                // Re-check the chosen destination (custom strategies may not): a stopped
                // destination refuses items, and the input strategy is evaluated with origin.
                if (target.IsInputBlocked || !target.ValidateInput(theItem, source))
                    return false;

                // Attempt to receive the item. While the destination's Receive runs, the
                // source is "sending": a notification back to it is deferred, not re-entered
                // (see Element.NotifyUnblock).
                bool received;
                source.EnterSend();
                try { received = ItemHandOver.Receive(source, target, theItem); }   // exit / entry labels first
                finally { source.ExitSend(); }

                if (received)
                {
                    (strategy as IRoutingMemory)?.Forget(theItem);   // a per-item choice is spent
                    // Statistics tracking
                    source.StatsCollector.OnExit(theItem);
                    target.StatsCollector.OnEntry(theItem);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Notifies origins that there is available capacity at a destination: the origin that has waited longest first
        /// (it always started at the first origin, which could starve the others), then the next while the
        /// destination refuses their items — its input strategy still decides, so it can give a line priority — then the
        /// origins not waiting, in connection order.
        /// </summary>
        public bool NotifyAvailable(Element source)
        {
            if (origins.Count == 1 || waitingTurn.Count == 0)
            {
                foreach (Element origin in origins)
                    if (Offer(origin)) return true;
                return false;
            }
            // a copy: the offers change the waiting turns (and may notify this link again)
            var order = new List<Element>(origins);
            order.Sort((a, b) =>
            {
                bool wa = waitingTurn.TryGetValue(a, out long ta), wb = waitingTurn.TryGetValue(b, out long tb);
                if (wa != wb) return wa ? -1 : 1;
                return wa ? ta.CompareTo(tb) : origins.IndexOf(a).CompareTo(origins.IndexOf(b));
            });
            foreach (Element origin in order)
                if (Offer(origin)) return true;
            return false;
        }

        // Offers the free place to one origin; an origin that does not even try to send has nothing waiting (one that is
        // sending now gets a deferred notification and keeps its turn).
        private bool Offer(Element origin)
        {
            if (origin.IsOutputBlocked)
                return false;
            long before = sendAttempts;
            if (origin.NotifyUnblock()) return true;
            if (sendAttempts == before && !origin.IsSending) waitingTurn.Remove(origin);
            return false;
        }

        /// <summary>
        /// Creates a link from a single origin to multiple destinations.
        /// </summary>
        public static GeneralLink CreateLink(Element origin, List<Element> destinations)
        {
            GeneralLink theLink = new GeneralLink();
            theLink.AddOrigin(origin);
            origin.SetOutput(theLink);

            foreach (Element dest in destinations)
            {
                // A destination may route this origin to one of its ports (Combiner).
                Element port = dest.ResolveInputFor(origin);
                theLink.AddOutput(port);
                port.SetInput(theLink);
            }

            return theLink;
        }

        /// <summary>
        /// Creates a link from multiple origins to multiple destinations.
        /// </summary>
        public static GeneralLink CreateLink(List<Element> origins, List<Element> destinations)
        {
            GeneralLink theLink = new GeneralLink();

            foreach (Element origin in origins)
            {
                theLink.AddOrigin(origin);
                origin.SetOutput(theLink);
            }

            // One link shared by several origins: a port-resolving destination sees the first
            // origin (all of them then feed the same port).
            Element first = origins.Count > 0 ? origins[0] : null;
            foreach (Element dest in destinations)
            {
                Element port = first != null ? dest.ResolveInputFor(first) : dest;
                theLink.AddOutput(port);
                port.SetInput(theLink);
            }

            return theLink;
        }
    }
}

