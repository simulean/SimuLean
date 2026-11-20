using System;
using System.Globalization;
using SimuLean.Expressions;

namespace SimuLean
{
    /// <summary>The waits a max-wait timer covers: the <c>kind</c> of a <see cref="WaitTimeout"/>, the signal
    /// <see cref="SimuLean.Signals.SignalKind.WaitTimedOut"/> and the statistics <c>timeouts:&lt;kind&gt;</c>.</summary>
    public enum WaitKind
    {
        /// <summary>A job waiting for the resources of a use (<c>processBy</c>, <c>setup.setupBy</c>, <c>repairBy</c>, an assembly
        /// task): from its request until every requirement is granted (with <c>processBy.acquireBeforeEntry</c>: until its
        /// item enters).</summary>
        Resource,
        /// <summary>An item waiting in a Queue to leave it (reneging): from its entry until it leaves.</summary>
        Queue,
        /// <summary>A puller waiting for a matching entry of the list it pulls from: from its pull until an entry matches.</summary>
        ListPull,
    }

    /// <summary>What a wait does when its max wait is over. Whatever the action, the timeout is counted
    /// (<c>timeouts</c>) and raises <see cref="SimuLean.Signals.SignalKind.WaitTimedOut"/>.</summary>
    public enum TimeoutAction
    {
        /// <summary>Only record it: the wait goes on (the default).</summary>
        KeepWaiting,
        /// <summary>Cancel the wait and release everything held for it. A station's job then asks again at once (a new
        /// request, behind the others, with a new timer); a job secured before its part is dropped and asked again at
        /// the next offer; a Queue's item leaves by its timeout target (it has nothing to release: as Reroute).</summary>
        GiveUp,
        /// <summary>Items only: the item leaves by the element's timeout target (a Queue; a Multi-Server's job — the item
        /// leaves unprocessed); while the target cannot take it, it waits for it.</summary>
        Reroute,
    }

    /// <summary>One timeout: where (the element), the item if any, the kind of wait, how long it waited and the action.</summary>
    public readonly struct WaitTimeout
    {
        public readonly WaitKind Kind;
        public readonly Item Item;
        public readonly double Waited;
        public readonly TimeoutAction Action;
        public WaitTimeout(WaitKind kind, Item item, double waited, TimeoutAction action)
        {
            Kind = kind; Item = item; Waited = waited; Action = action;
        }
    }

    /// <summary>
    /// A wait's limit: the max wait — a time, or an expression over the item and the element evaluated when the
    /// wait starts (random functions draw from the element's stream of the key) — and what to do when it is over. A value
    /// ≤ 0 (or none) = no limit for that wait.
    /// </summary>
    public sealed class WaitLimit
    {
        private readonly Expression expression;
        private readonly double constant;
        private readonly string purpose;
        private readonly EntityScope scope;

        /// <summary>A constant max wait.</summary>
        public WaitLimit(double maxWait, TimeoutAction onTimeout)
        {
            constant = maxWait;
            OnTimeout = onTimeout;
        }

        private WaitLimit(Expression expression, string purpose, TimeoutAction onTimeout)
        {
            this.expression = expression;
            this.purpose = purpose;
            constant = double.NaN;
            scope = new EntityScope();
            OnTimeout = onTimeout;
        }

        public TimeoutAction OnTimeout { get; }

        /// <summary>The max wait's text (a number or the expression).</summary>
        public string Text => expression?.Text ?? constant.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>The limit of a wait that starts now (NaN or ≤ 0 = none).</summary>
        public double DelayFor(Item item, Element element)
        {
            if (expression == null) return constant;
            scope.Set(item, element);
            scope.RandomStream = element?.RandomStreamFor(purpose);
            try { return expression.Number(scope); }
            finally { scope.Item = null; scope.Element = null; }
        }

        /// <summary>
        /// The limit given by <paramref name="text"/> (empty, or a number ≤ 0 = none: null). A number is a constant; any other
        /// text an expression over the item and the element (a bare name = the item's label), whose random functions draw
        /// from the element's stream <paramref name="purpose"/> (the key). A bad expression fails naming <paramref name="owner"/>.
        /// </summary>
        public static WaitLimit Parse(string text, TimeoutAction onTimeout, string owner, string purpose)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return v > 0 && !double.IsInfinity(v) ? new WaitLimit(v, onTimeout) : null;
            try { return new WaitLimit(Expression.Parse(text, ExpressionContext.MaxWait, owner, purpose), purpose, onTimeout); }
            catch (FormatException e) { throw new FormatException($"{owner}: bad max wait '{text}' ({purpose}): {e.Message}", e); }
        }

        /// <summary>A timeout action by name (empty = KeepWaiting); an unknown one fails naming <paramref name="owner"/>.</summary>
        public static TimeoutAction ActionOf(string text, string owner)
        {
            if (!TryActionOf(text, out TimeoutAction a))
                throw new FormatException($"{owner}: unknown timeout action '{text}' (KeepWaiting, GiveUp or Reroute).");
            return a;
        }

        /// <summary>The timeout action <paramref name="text"/> names (empty = KeepWaiting; a name in any case or a number);
        /// false for any other text. The builder and the logic view read a timeout action through it.</summary>
        public static bool TryActionOf(string text, out TimeoutAction action)
        {
            action = TimeoutAction.KeepWaiting;
            if (string.IsNullOrWhiteSpace(text)) return true;
            return Enum.TryParse(text.Trim(), true, out action) && Enum.IsDefined(typeof(TimeoutAction), action);
        }

        /// <summary>The statistic name of a kind: <c>resource</c>, <c>queue</c>, <c>list_pull</c>.</summary>
        public static string KindName(WaitKind kind)
            => kind == WaitKind.Resource ? "resource" : kind == WaitKind.Queue ? "queue" : "list_pull";
    }

    /// <summary>The timer of one wait (an ordinary event, not an element's work: a stop does not pause it). Cancelled when the
    /// wait ends; <see cref="Fired"/> runs the owner's timeout.</summary>
    internal sealed class WaitTimer : IEvent
    {
        private readonly Action<WaitTimer> fired;
        public readonly double Started;
        public readonly object Token;
        public EventHandle Handle;

        private WaitTimer(Action<WaitTimer> fired, double start, object token) { this.fired = fired; Started = start; Token = token; }

        /// <summary>Starts a timer for <paramref name="limit"/> (null when the limit gives no time for this wait).</summary>
        public static WaitTimer Start(SimClock clock, WaitLimit limit, Item item, Element element, object token, Action<WaitTimer> fired)
        {
            if (limit == null || clock == null) return null;
            double delay = limit.DelayFor(item, element);
            if (double.IsNaN(delay) || !(delay > 0) || double.IsInfinity(delay)) return null;
            var t = new WaitTimer(fired, clock.GetSimulationTime(), token);
            t.Handle = clock.ScheduleEvent(t, delay);
            return t;
        }

        public bool Fired { get; private set; }

        /// <summary>The wait ended (or was reset): the timer will not fire.</summary>
        public void Cancel() => Handle?.Cancel();

        public void Execute()
        {
            Fired = true;
            fired(this);
        }
    }
}
