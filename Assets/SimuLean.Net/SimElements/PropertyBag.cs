using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace SimuLean
{
    /// <summary>Kind of a property value.</summary>
    public enum PropertyKind { None, Double, String, Reference, Array }

    /// <summary>
    /// The user properties ("labels") of a <see cref="SimEntity"/>: typed, one dictionary per kind,
    /// each allocated on its first write — most items only ever use numbers, so they keep exactly one dictionary.
    /// A name has one kind at a time (setting it as another kind replaces it). Kinds: <b>double</b> (the hot path:
    /// <see cref="TryGetDouble"/> / <see cref="SetDouble"/> neither box nor allocate), <b>string</b>, <b>entity
    /// reference</b> (runtime only: serialized as the referent's name and resolved when the model is rebuilt) and
    /// <b>array</b>. <see cref="View"/> is a read-only, boxing view of everything for editors and generic code.
    /// </summary>
    public sealed class PropertyBag
    {
        private Dictionary<string, double> doubles;
        private Dictionary<string, string> strings;
        private Dictionary<string, SimEntity> refs;
        private Dictionary<string, Array> arrays;
        private PropertyView view;

        // ── numbers ──────────────────────────────────────────────────────────

        public void SetDouble(string name, double value)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (doubles == null) doubles = new Dictionary<string, double>();
            doubles[name] = value;
            RemoveOther(name, PropertyKind.Double);
        }

        public bool TryGetDouble(string name, out double value)
        {
            if (doubles != null && name != null) return doubles.TryGetValue(name, out value);
            value = 0; return false;
        }

        public double GetDouble(string name, double defaultValue = 0) => TryGetDouble(name, out double v) ? v : defaultValue;

        // ── text ─────────────────────────────────────────────────────────────

        public void SetString(string name, string value)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (strings == null) strings = new Dictionary<string, string>();
            strings[name] = value ?? string.Empty;
            RemoveOther(name, PropertyKind.String);
        }

        public bool TryGetString(string name, out string value)
        {
            if (strings != null && name != null) return strings.TryGetValue(name, out value);
            value = null; return false;
        }

        // ── references (runtime only) ────────────────────────────────────────

        public void SetRef(string name, SimEntity value)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (refs == null) refs = new Dictionary<string, SimEntity>();
            refs[name] = value;
            RemoveOther(name, PropertyKind.Reference);
        }

        public SimEntity GetRef(string name) => refs != null && name != null && refs.TryGetValue(name, out var e) ? e : null;

        // ── arrays ───────────────────────────────────────────────────────────

        public void SetArray(string name, Array value)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (arrays == null) arrays = new Dictionary<string, Array>();
            arrays[name] = value;
            RemoveOther(name, PropertyKind.Array);
        }

        public T[] GetArray<T>(string name) => arrays != null && name != null && arrays.TryGetValue(name, out var a) ? a as T[] : null;

        // ── any kind ─────────────────────────────────────────────────────────

        /// <summary>Sets from text: a number (dot or comma decimal separator) is stored as a double, anything else as a
        /// string. Null / empty values are ignored.</summary>
        public void SetParsed(string name, string text)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(text)) return;
            if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) SetDouble(name, d);
            else SetString(name, text);
        }

        public PropertyKind KindOf(string name)
        {
            if (name == null) return PropertyKind.None;
            if (doubles != null && doubles.ContainsKey(name)) return PropertyKind.Double;
            if (strings != null && strings.ContainsKey(name)) return PropertyKind.String;
            if (refs != null && refs.ContainsKey(name)) return PropertyKind.Reference;
            if (arrays != null && arrays.ContainsKey(name)) return PropertyKind.Array;
            return PropertyKind.None;
        }

        public bool Has(string name) => KindOf(name) != PropertyKind.None;

        public bool Remove(string name)
        {
            bool any = false;
            if (doubles != null && doubles.Remove(name)) any = true;
            if (strings != null && strings.Remove(name)) any = true;
            if (refs != null && refs.Remove(name)) any = true;
            if (arrays != null && arrays.Remove(name)) any = true;
            return any;
        }

        public int Count => (doubles?.Count ?? 0) + (strings?.Count ?? 0) + (refs?.Count ?? 0) + (arrays?.Count ?? 0);

        /// <summary>Every name (numbers, texts, references, arrays).</summary>
        public IEnumerable<string> Names
        {
            get
            {
                if (doubles != null) foreach (var k in doubles.Keys) yield return k;
                if (strings != null) foreach (var k in strings.Keys) yield return k;
                if (refs != null) foreach (var k in refs.Keys) yield return k;
                if (arrays != null) foreach (var k in arrays.Keys) yield return k;
            }
        }

        /// <summary>The value boxed (double, string, <see cref="SimEntity"/> or array), or null.</summary>
        public object Get(string name)
        {
            if (name == null) return null;
            if (doubles != null && doubles.TryGetValue(name, out double d)) return d;
            if (strings != null && strings.TryGetValue(name, out string s)) return s;
            if (refs != null && refs.TryGetValue(name, out SimEntity e)) return e;
            if (arrays != null && arrays.TryGetValue(name, out Array a)) return a;
            return null;
        }

        /// <summary>The value as text: a string as is, a number in the invariant culture, a reference by its entity's
        /// name, an array as comma-separated values; null when absent.</summary>
        public string GetText(string name)
        {
            switch (KindOf(name))
            {
                case PropertyKind.Double: return doubles[name].ToString("R", CultureInfo.InvariantCulture);
                case PropertyKind.String: return strings[name];
                case PropertyKind.Reference: return refs[name]?.Name;
                case PropertyKind.Array:
                    var parts = new List<string>();
                    foreach (var o in arrays[name]) parts.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
                    return string.Join(",", parts);
            }
            return null;
        }

        /// <summary>Copies every property of <paramref name="other"/> (arrays shared, not cloned).</summary>
        public void CopyFrom(PropertyBag other)
        {
            if (other == null) return;
            if (other.doubles != null) foreach (var kv in other.doubles) SetDouble(kv.Key, kv.Value);
            if (other.strings != null) foreach (var kv in other.strings) SetString(kv.Key, kv.Value);
            if (other.refs != null) foreach (var kv in other.refs) SetRef(kv.Key, kv.Value);
            if (other.arrays != null) foreach (var kv in other.arrays) SetArray(kv.Key, kv.Value);
        }

        public void Clear() { doubles?.Clear(); strings?.Clear(); refs?.Clear(); arrays?.Clear(); }

        /// <summary>Read-only boxing view of every property (editors, generic code); no copy is made.</summary>
        public IReadOnlyDictionary<string, object> View => view ?? (view = new PropertyView(this));

        private void RemoveOther(string name, PropertyKind keep)
        {
            if (keep != PropertyKind.Double) doubles?.Remove(name);
            if (keep != PropertyKind.String) strings?.Remove(name);
            if (keep != PropertyKind.Reference) refs?.Remove(name);
            if (keep != PropertyKind.Array) arrays?.Remove(name);
        }

        private sealed class PropertyView : IReadOnlyDictionary<string, object>
        {
            private readonly PropertyBag bag;
            public PropertyView(PropertyBag bag) { this.bag = bag; }

            public object this[string key] => bag.Has(key) ? bag.Get(key) : throw new KeyNotFoundException(key);
            public IEnumerable<string> Keys => bag.Names;
            public IEnumerable<object> Values { get { foreach (var k in bag.Names) yield return bag.Get(k); } }
            public int Count => bag.Count;
            public bool ContainsKey(string key) => bag.Has(key);
            public bool TryGetValue(string key, out object value) { value = bag.Get(key); return bag.Has(key); }
            public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
            {
                foreach (var k in bag.Names) yield return new KeyValuePair<string, object>(k, bag.Get(k));
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    /// <summary>
    /// What every simulation object has: a name and user <see cref="Properties"/>. <see cref="Item"/>
    /// and <see cref="Element"/> (and so resources) derive from it. Universal attributes are ordinary typed members of
    /// the classes, never property names. The bag is created on first use, so an entity without properties costs
    /// nothing extra.
    /// </summary>
    public abstract class SimEntity
    {
        private PropertyBag properties;

        /// <summary>User properties ("labels").</summary>
        public PropertyBag Properties => properties ?? (properties = new PropertyBag());

        /// <summary>True when it has at least one property (without creating the bag).</summary>
        public bool HasProperties => properties != null && properties.Count > 0;

        /// <summary>Name of the entity (element name, item type).</summary>
        public abstract string Name { get; }
    }
}
