using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Statistics;

namespace SimuLean.Collections
{
    /// <summary>Statistics of a monitored list field: its values when entries are pushed and when pulled.</summary>
    public sealed class ListFieldStats
    {
        public string Field { get; internal set; }
        public StatTally AtPush { get; } = new StatTally();
        public StatTally AtPull { get; } = new StatTally();
        internal void Reset() { AtPush.Reset(); AtPull.Reset(); }
    }
}
