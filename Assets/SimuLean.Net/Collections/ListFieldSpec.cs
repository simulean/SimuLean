using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Statistics;

namespace SimuLean.Collections
{
    /// <summary>How a list field gets its value.</summary>
    public enum ListFieldKind
    {
        /// <summary>An item label (<see cref="ListFieldSpec.source"/> = label name; empty = the field name).</summary>
        Label,
        /// <summary>A formula in the common expression syntax (<see cref="SimuLean.Expressions.Expression"/>): bare names
        /// are the entry's fields, <c>item.x</c> its item's properties — e.g. <c>prio * 10 + age</c>.</summary>
        Expression,
        /// <summary>A built-in: <c>age</c>, <c>pushTime</c>, <c>itemPriority</c>, <c>owner</c>, <c>type</c>.</summary>
        BuiltIn,
    }

    /// <summary>
    /// A field defined on a list (serializable: Unity inspector and headless config): its name, how it is
    /// computed, and whether its values are recorded (<see cref="monitor"/>: statistics of the values of the
    /// entries when pushed and when pulled). Queries and orders can use it by name.
    /// </summary>
    [Serializable]
    public class ListFieldSpec
    {
        public string name = "";
        public ListFieldKind kind = ListFieldKind.Label;
        /// <summary>Label name, formula or built-in name, depending on <see cref="kind"/>.</summary>
        public string source = "";
        public bool monitor = true;

        public ListFieldSpec() { }
        public ListFieldSpec(string name, ListFieldKind kind, string source = "", bool monitor = true)
        { this.name = name; this.kind = kind; this.source = source; this.monitor = monitor; }

        public ListFieldSpec Clone() => (ListFieldSpec)MemberwiseClone();

        /// <summary>Compact text form <c>name|kind|source|monitor</c>.</summary>
        public string Encode() => $"{name}|{kind}|{source}|{(monitor ? 1 : 0)}";
    }
}
