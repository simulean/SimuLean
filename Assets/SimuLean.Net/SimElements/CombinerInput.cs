using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// One component input of a <see cref="Combiner"/> (a port links connect to): it admits an item when some assembly is
    /// gathering, has room on this input and its matching (the pull mode, per assembly) admits it, and gives it to the first
    /// such assembly (several assemblies in parallel). The components wait in their assembly.
    /// </summary>
    public class CombinerInput : Element
    {
        private readonly Combiner owner;
        private readonly int inputId;

        /// <param name="owner">The combiner.</param>
        /// <param name="inputId">Index of the input in the combiner (0-based).</param>
        public CombinerInput(Combiner owner, int inputId, string name, SimClock simClock, IVElement vElement = null)
            : base(name, simClock, vElement)
        {
            this.owner = owner;
            this.inputId = inputId;
            inputStrategy = new PortStrategy(owner, inputId);   // the assemblies' matching, checked by the links
        }

        /// <summary>The combiner's location: carriers deliver components there (follow-up #11: a spatial crane serving
        /// a bay through a list targets this port, which has no node of its own).</summary>
        public override string Location
        {
            get => base.Location ?? owner.Location;
            set => base.Location = value;
        }

        /// <summary>Components waiting on this input (every assembly).</summary>
        public override int GetQueueLength() => owner.Waiting(inputId);

        /// <summary>Room on this input: the gathering assemblies' (one assembly's requirement when none gathers).</summary>
        public override int GetFreeCapacity() => owner.Room(inputId);

        /// <summary>The assemblies check their sets (one may be complete now), then upstream is asked for more.</summary>
        public override bool Unblock()
        {
            owner.CheckAssemblies();
            NotifyInput();
            return true;
        }

        public override bool Receive(Item theItem)
        {
            var a = owner.TargetFor(inputId, theItem, LastOrigin, matching: true);   // the first assembly that takes it
            LastOrigin = null;
            if (a == null) return false;
            // Upstream is asked again when the set is complete, and also while some assembly still has room on this input:
            // a FIFO queue queues new arrivals behind the waiting ones and only offers them when asked, so with a
            // requirement ≥ 2 the set never completed (benchmark finding C1). The sender is still inside its
            // own send here, so its Unblock is deferred to a zero-delay event (Element.NotifyUnblock).
            if (owner.ComponentReceived(a, inputId, theItem) || owner.TargetFor(inputId, null, null, matching: false) != null)
                NotifyInput();
            return true;
        }

        /// <summary>Room on some gathering assembly (the matching is the input strategy, checked by the link).</summary>
        public override bool CheckAvailability(Item theItem) => owner.TargetFor(inputId, theItem, null, matching: false) != null;

        /// <summary>The components waiting on this input, assembly by assembly.</summary>
        public IEnumerable<Item> GetItems()
        {
            if (owner.Assemblies == null) yield break;
            foreach (var a in owner.Assemblies) foreach (var item in a.PartsOf(inputId)) yield return item;
        }

        // The sender of the item being validated (a matching rule may read it), kept between ValidateInput and Receive.
        private Element LastOrigin;

        public override bool ValidateInput(Item item, Element origin)
        {
            LastOrigin = origin;
            return base.ValidateInput(item, origin);
        }

        /// <summary>Admits an item some gathering assembly with room on the input matches.</summary>
        private sealed class PortStrategy : InputStrategy
        {
            private readonly Combiner owner;
            private readonly int input;
            public PortStrategy(Combiner owner, int input) { this.owner = owner; this.input = input; }
            public override bool Admits(Element target, Item item, Element origin) => owner.TargetFor(input, item, origin, matching: true) != null;
        }
    }
}
