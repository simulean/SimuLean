using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Deep copy of the config DTOs by reflection: every public property and field, lists, arrays and dictionaries,
    /// so a field added to a config is copied without anyone having to remember it (the GA used to copy each field by
    /// hand and silently lost new ones). Strings, numbers, enums and other immutable values are shared.
    /// </summary>
    public static class ConfigCloner
    {
        public static T DeepClone<T>(T value) => (T)Clone(value);

        private static object Clone(object value)
        {
            if (value == null) return null;
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string || value is decimal || value is DateTime) return value;
            if (type.IsArray)
            {
                var src = (Array)value;
                var dst = Array.CreateInstance(type.GetElementType(), src.Length);
                for (int i = 0; i < src.Length; i++) dst.SetValue(Clone(src.GetValue(i)), i);
                return dst;
            }
            if (value is IDictionary dict)
            {
                var copy = (IDictionary)Activator.CreateInstance(type);
                foreach (DictionaryEntry kv in dict) copy[kv.Key] = Clone(kv.Value);
                return copy;
            }
            if (value is IList list)
            {
                var copy = (IList)Activator.CreateInstance(type);
                foreach (var item in list) copy.Add(Clone(item));
                return copy;
            }
            if (type.IsValueType) return value;   // structs: copied by value (their reference fields are not DTOs)

            var ctor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (ctor == null) return value;       // not a DTO (no parameterless constructor): shared, as immutable
            object clone = ctor.Invoke(null);
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                object v = Clone(p.GetValue(value));
                if (p.CanWrite) p.SetValue(clone, v);
                else if (v is IList || v is IDictionary)
                {
                    // read-only collection property (e.g. initialised in the constructor): fill it
                    object target = p.GetValue(clone);
                    if (target is IDictionary td && v is IDictionary vd) { td.Clear(); foreach (DictionaryEntry kv in vd) td[kv.Key] = kv.Value; }
                    else if (target is IList tl && v is IList vl) { tl.Clear(); foreach (var item in vl) tl.Add(item); }
                }
            }
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (!f.IsInitOnly) f.SetValue(clone, Clone(f.GetValue(value)));
            return clone;
        }
    }
}
