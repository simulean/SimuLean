using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Statistics;

namespace SimuLean.Collections
{
    /// <summary>
    /// One entry of a <see cref="SimList"/>: an item advertised by the element that holds it
    /// (<see cref="Owner"/>), or any value (an order, a token, a message — <see cref="Item"/> is null).
    /// The entry only points at the value: an advertised item stays in its owner until pulled.
    /// </summary>
    public sealed class ListEntry
    {
        /// <summary>The listed value (an <see cref="SimuLean.Item"/> for item entries).</summary>
        public object Value { get; internal set; }

        /// <summary>The item, for item entries (null for value entries).</summary>
        public Item Item => Value as Item;

        /// <summary>Element holding the item (item entries), or the element that pushed the value (may be null).</summary>
        public Element Owner { get; internal set; }

        /// <summary>Simulation time of the push.</summary>
        public double PushTime { get; internal set; }

        /// <summary>Fields given at push time (value entries; items read their labels live).</summary>
        public IReadOnlyDictionary<string, object> Fields => fields;

        internal Dictionary<string, object> fields;
        internal long Sequence;

        public override string ToString()
            => (Item != null ? "item " + Item.GetId() : "value " + Value) + (Owner != null ? " @" + Owner.GetName() : "");
    }
}
