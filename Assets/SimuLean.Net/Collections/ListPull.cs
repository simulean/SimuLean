using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Statistics;

namespace SimuLean.Collections
{
    /// <summary>A pull registered on a <see cref="SimList"/>; <see cref="Cancel"/> withdraws it.</summary>
    public sealed class ListPull
    {
        internal ModelList<ListEntry>.BackOrder BackOrder;
        internal bool Scheduled;
        internal SimList List;
        internal int Run;   // the list's run when the pull was registered (Reset starts a new one)

        /// <summary>True until delivered or cancelled.</summary>
        public bool IsPending { get; internal set; } = true;

        /// <summary>
        /// Withdraws the pull. If an entry had already been matched for it (delivery pending in the
        /// same time step) the entry returns to the list. Returns false if already delivered / cancelled.
        /// </summary>
        public bool Cancel()
        {
            if (!IsPending) return false;
            IsPending = false;
            if (!Scheduled)
            {
                BackOrder?.Cancel();
                List?.PullCancelled(this);
            }
            return true;
        }
    }
}
