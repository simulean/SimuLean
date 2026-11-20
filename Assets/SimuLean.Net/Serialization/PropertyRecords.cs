using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimuLean.Serialization
{
    /// <summary>
    /// A <see cref="PropertyBag"/> as typed records for configs (<see cref="PropertySettings"/>):
    /// numbers, texts, references (the referenced entity's name) and arrays. References are
    /// runtime objects: they are written by name and resolved when the model is rebuilt, in a second pass once every element
    /// exists (<see cref="Apply"/> returns them).
    /// </summary>
    public static class PropertyRecords
    {
        /// <summary>Element setting holding its properties (<c>properties</c>; a parameter is <c>properties.&lt;name&gt;</c>).</summary>
        public const string Key = "properties";

        /// <summary>The records of a bag (references by the entity's name).</summary>
        public static List<PropertySettings> Encode(PropertyBag bag)
        {
            var list = new List<PropertySettings>();
            if (bag == null) return list;
            foreach (string name in bag.Names)
            {
                switch (bag.KindOf(name))
                {
                    case PropertyKind.Double: list.Add(PropertySettings.Number(name, bag.GetDouble(name))); break;
                    case PropertyKind.String: bag.TryGetString(name, out string s); list.Add(PropertySettings.OfText(name, s)); break;
                    case PropertyKind.Reference: list.Add(PropertySettings.Reference(name, bag.GetRef(name)?.Name)); break;
                    case PropertyKind.Array:
                        var d = bag.GetArray<double>(name);
                        if (d != null) { list.Add(PropertySettings.OfNumbers(name, (double[])d.Clone())); break; }
                        var t = bag.GetArray<string>(name);
                        if (t != null) list.Add(PropertySettings.OfTexts(name, t));
                        break;
                }
            }
            return list;
        }

        /// <summary>Applies <paramref name="records"/> to <paramref name="bag"/>; references are returned as
        /// (property, target name) for the caller to resolve once every entity exists.</summary>
        public static List<(string name, string target)> Apply(IEnumerable<PropertySettings> records, PropertyBag bag, string owner)
        {
            var refs = new List<(string, string)>();
            if (records == null) return refs;
            foreach (var rec in records)
            {
                if (rec == null) continue;
                string name = (rec.Name ?? "").Trim();
                if (name.Length == 0) throw new FormatException($"'{owner}': a property without a name.");
                if (SimuLean.Expressions.Expression.IsConstantName(name))
                    throw new FormatException($"'{owner}': property '{name}' is a constant (1 / 0) in expressions, not a property name.");
                switch (rec.Kind)
                {
                    case PropertyValueKind.Number:
                        if (double.IsNaN(rec.Value)) throw new FormatException($"'{owner}': property '{name}' is not a number.");
                        bag.SetDouble(name, rec.Value); break;
                    case PropertyValueKind.Text: bag.SetString(name, rec.Text ?? ""); break;
                    case PropertyValueKind.Reference: refs.Add((name, rec.Text ?? "")); break;
                    case PropertyValueKind.Numbers: bag.SetArray(name, (double[])(rec.Numbers ?? new double[0]).Clone()); break;
                    case PropertyValueKind.Texts: bag.SetArray(name, (rec.Texts ?? new List<string>()).ToArray()); break;
                    default: throw new FormatException($"'{owner}': property '{name}' has an unknown kind '{rec.Kind}'.");
                }
            }
            return refs;
        }
    }
}
