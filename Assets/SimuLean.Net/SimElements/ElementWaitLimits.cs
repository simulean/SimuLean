using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Max-wait timers: the side of the element. Every wait the element owns that carries a limit
    /// (<see cref="WaitLimit"/>: its resource requests, its queued items, its list pull) records its timeouts here — the
    /// statistics <c>timeouts</c> and <c>timeouts:&lt;kind&gt;</c> (only for the kinds it limits, so the other models' results
    /// keep their shape) and the event <see cref="WaitTimedOut"/> (the signal
    /// <see cref="SimuLean.Signals.SignalKind.WaitTimedOut"/>). An item that leaves on a timeout (Reroute) goes to the
    /// element's <see cref="TimeoutTarget"/> through a link of its own (statistics and downtime blocking as any link).
    /// </summary>
    public abstract partial class Element
    {
        private int[] timeoutCounts;
        private List<WaitKind> limitedWaits;

        /// <summary>A wait of <paramref name="kind"/> of this element carries a limit (set when it is built): its timeouts are
        /// reported.</summary>
        public void DeclareWaitLimit(WaitKind kind)
        {
            if (limitedWaits == null) limitedWaits = new List<WaitKind>();
            if (!limitedWaits.Contains(kind)) limitedWaits.Add(kind);
        }

        /// <summary>The kinds of wait of this element that carry a limit (empty = none).</summary>
        public IReadOnlyList<WaitKind> LimitedWaits => (IReadOnlyList<WaitKind>)limitedWaits ?? Array.Empty<WaitKind>();

        /// <summary>Timeouts of every kind since the start or the warm-up.</summary>
        public int Timeouts
        {
            get { int n = 0; if (timeoutCounts != null) foreach (int c in timeoutCounts) n += c; return n; }
        }

        /// <summary>Timeouts of one kind since the start or the warm-up.</summary>
        public int TimeoutsOf(WaitKind kind) => timeoutCounts != null ? timeoutCounts[(int)kind] : 0;

        /// <summary>Raised on every timeout of a wait of this element, whatever its action (before the action).</summary>
        public event Action<Element, WaitTimeout> WaitTimedOut;

        /// <summary>A wait of this element timed out: counted, then <see cref="WaitTimedOut"/>.</summary>
        public void RecordTimeout(WaitKind kind, Item item, double waited, TimeoutAction action)
        {
            if (timeoutCounts == null) timeoutCounts = new int[3];
            timeoutCounts[(int)kind]++;
            WaitTimedOut?.Invoke(this, new WaitTimeout(kind, item, waited, action));
        }

        private void ResetTimeouts() { if (timeoutCounts != null) Array.Clear(timeoutCounts, 0, timeoutCounts.Length); }

        // ── where an item goes on a timeout (Reroute) ──
        private GeneralLink timeoutLink;

        /// <summary>Where the items of this element go when their wait times out with Reroute (a Queue's items, a Multi-Server's
        /// jobs); null = none.</summary>
        public Element TimeoutTarget { get; private set; }

        /// <summary>Sets the timeout target: a link of its own from this element to <paramref name="target"/> (not one of the
        /// element's outputs: the normal routing never sends items there), with the target's input notified through it.</summary>
        public void SetTimeoutTarget(Element target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (target == this) throw new ArgumentException($"'{Name}': the timeout target is the element itself.");
            TimeoutTarget = target;
            timeoutLink = new GeneralLink { Routing = FirstAvailableRouting };
            timeoutLink.AddOrigin(this);
            Element port = target.ResolveInputFor(this);
            timeoutLink.AddOutput(port);
            port.SetInput(timeoutLink);
        }

        private static readonly IOutputStrategy FirstAvailableRouting = new FirstAvailableStrategy();

        /// <summary>Sends <paramref name="item"/> to the timeout target through its link (false: none, a stop blocks it, or the
        /// target cannot take it now — it notifies this element when it can).</summary>
        protected bool SendToTimeoutTarget(Item item) => timeoutLink != null && timeoutLink.SendItem(item, this);
    }
}
