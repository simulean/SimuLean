using System;
using System.Collections.Generic;
using SimuLean.Collections;
using SimuLean.Resources;

namespace SimuLean.Rules
{
    /// <summary>
    /// Routing by a rule (<c>outputStrategy.strategyType = Rule</c>): the <see cref="IRoutingRule"/> scores each
    /// output; the highest wins, ties go to the first in connection order — as the <see cref="BestScoreRoutingStrategy"/> it
    /// mirrors. This class (<see cref="BestScoreFull.Skip"/>) scores only the outputs that can take the item now (none: the
    /// item waits); <see cref="RuleWaitRoutingStrategy"/> scores every output that admits it and waits for the best. The
    /// rule's draws are kept per item and output until the item leaves (<see cref="IRoutingMemory.Forget"/>).
    /// </summary>
    public class RuleRoutingStrategy : RuleSiteBase, IOutputStrategy, IRoutingMemory
    {
        private readonly IRoutingRule rule;

        public RuleRoutingStrategy(IRoutingRule rule, string owner) : base(rule, owner, RuleSite.Routing) { this.rule = rule; }

        public virtual int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context)
            => Best(outputs, item, source, true);

        /// <summary>The best-scored output among those that can take the item now (<paramref name="needRoom"/>) or that admit
        /// it, the first on a tie (−1: none).</summary>
        protected int Best(List<Element> outputs, Item item, Element source, bool needRoom)
        {
            var clock = source?.GetSimClock();
            int eligible = 0;
            for (int i = 0; i < outputs.Count; i++)
                if (needRoom ? outputs[i].CanAccept(item, source) : outputs[i].ValidateInput(item, source)) eligible++;
            if (eligible == 0) return -1;
            var ctx = Context(clock, source, item, eligible);
            var random = Random;
            int best = -1;
            double bestScore = 0;
            var counters = source?.Counters;   // read-only: the scores of the choice (logic viewer)
            counters?.BeginScores(outputs.Count);
            for (int i = 0; i < outputs.Count; i++)
            {
                var d = outputs[i];
                if (needRoom ? !d.CanAccept(item, source) : !d.ValidateInput(item, source)) continue;
                double v;
                random.Begin(item, i);
                try { v = rule.Score(in ctx, new ElementView(d, i)); }
                catch (Exception e) { throw Fail(e, clock); }
                finally { random.End(); }
                v = Checked(v, clock);
                counters?.Score(i, v);
                if (best < 0 || v > bestScore) { best = i; bestScore = v; }
            }
            counters?.EndScores(best);
            return best;
        }

        public void Forget(Item item) => Random?.Forget(item);
    }

    /// <summary>Routing by a rule that waits for the best output (<see cref="BestScoreFull.Wait"/>): the output it names over
    /// every destination that admits the item, full or not (a routing by rule, <see cref="IRuleRouting"/>).</summary>
    public sealed class RuleWaitRoutingStrategy : RuleRoutingStrategy, IRuleRouting
    {
        public RuleWaitRoutingStrategy(IRoutingRule rule, string owner) : base(rule, owner) { }

        public override int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context)
        {
            int r = Route(outputs, item, source);
            if (r >= 0 && outputs[r].CanAccept(item, source)) return r;
            if (r >= 0) source?.Counters.HeldBack();   // read-only counter: held for its full best output
            return -1;
        }

        public int Route(List<Element> outputs, Item item, Element source) => Best(outputs, item, source, false);
    }

    /// <summary>
    /// A Queue's release order by a rule (<c>offerRule</c>), in place of the order keys of
    /// <see cref="ItemOrder"/> it mirrors: the <see cref="IReleaseRule"/> scores each held item, offered best first (highest
    /// score), ties in arrival order. Scored live at every offer; the rule's draws are kept per item while it is held.
    /// </summary>
    public sealed class RuleItemOrder : RuleSiteBase, IItemOrder
    {
        private readonly IReleaseRule rule;
        private readonly List<(Item item, int arrival, double score)> sorting = new List<(Item, int, double)>();
        private readonly HashSet<object> held = new HashSet<object>();

        public RuleItemOrder(IReleaseRule rule, string owner) : base(rule, owner, RuleSite.Release) { this.rule = rule; }

        /// <summary>How the logic viewer and the queue name it.</summary>
        public string Text => ChoiceRules.OrderPrefix + RuleName;

        private double Score(in ChoiceContext ctx, Item item, SimClock clock)
        {
            double v;
            var random = Random;
            random.Begin(item, 0);
            try { v = rule.Score(in ctx, new ItemView(item, ctx.Now)); }
            catch (Exception e) { throw Fail(e, clock); }
            finally { random.End(); }
            return Checked(v, clock);
        }

        private int Count(IEnumerable<Item> items, SimClock clock)
        {
            int n = 0;
            foreach (var _ in items) n++;
            RandomFor(clock);
            if (n > 0 && Random.HasRecords)
            {
                held.Clear();
                foreach (var i in items) held.Add(i);
                Random.KeepOnly(held);
                held.Clear();
            }
            return n;
        }

        public Item Best(IEnumerable<Item> items, Element holder)
        {
            var clock = holder?.GetSimClock();
            int n = Count(items, clock);
            if (n == 0) return null;
            var ctx = Context(clock, holder, null, n);
            Item best = null;
            double bestScore = 0;
            foreach (var item in items)
            {
                double v = Score(in ctx, item, clock);
                if (best == null || v > bestScore) { best = item; bestScore = v; }
            }
            return best;
        }

        public void Sort(IEnumerable<Item> items, Element holder, List<Item> into)
        {
            into.Clear();
            sorting.Clear();
            var clock = holder?.GetSimClock();
            int n = Count(items, clock);
            if (n == 0) return;
            var ctx = Context(clock, holder, null, n);
            foreach (var item in items) sorting.Add((item, sorting.Count, Score(in ctx, item, clock)));
            sorting.Sort((a, b) => { int c = b.score.CompareTo(a.score); return c != 0 ? c : a.arrival.CompareTo(b.arrival); });
            foreach (var s in sorting) into.Add(s.item);
            sorting.Clear();
        }
    }

    /// <summary>
    /// A resource team's request order by a rule (a request order <c>rule:Type</c>), in place of the list
    /// order it mirrors: the <see cref="IDispatchRule"/> scores each pending request, tried best first (highest score), ties in
    /// request order. The rule's draws are kept per request while it waits.
    /// </summary>
    public sealed class RuleRequestOrder : RuleSiteBase
    {
        private readonly IDispatchRule rule;
        private readonly List<(ListEntry entry, int index, double score)> sorting = new List<(ListEntry, int, double)>();
        private readonly HashSet<object> waiting = new HashSet<object>();

        public RuleRequestOrder(IDispatchRule rule, string owner) : base(rule, owner, RuleSite.Dispatch) { this.rule = rule; }

        /// <summary>The pending requests of <paramref name="requests"/> best first (ties in request order); empty when none.</summary>
        public List<ListEntry> Ordered(SimList requests, SimClock clock)
        {
            var all = requests.Ordered(null);
            if (all.Count == 0) return all;
            RandomFor(clock);
            if (Random.HasRecords)
            {
                waiting.Clear();
                foreach (var e in all) waiting.Add(e.Value);
                Random.KeepOnly(waiting);
                waiting.Clear();
            }
            var ctx = Context(clock, null, null, all.Count);
            var random = Random;
            sorting.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                var request = all[i].Value as ResourceRequest;
                double v;
                random.Begin(all[i].Value, 0);
                try { v = rule.Score(in ctx, new RequestView(request, ctx.Now)); }
                catch (Exception e) { throw Fail(e, clock); }
                finally { random.End(); }
                sorting.Add((all[i], i, Checked(v, clock)));
            }
            sorting.Sort((a, b) => { int c = b.score.CompareTo(a.score); return c != 0 ? c : a.index.CompareTo(b.index); });
            all.Clear();
            foreach (var s in sorting) all.Add(s.entry);
            sorting.Clear();
            return all;
        }
    }
}
