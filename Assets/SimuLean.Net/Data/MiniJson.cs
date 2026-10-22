using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SimuLean.Data
{
    /// <summary>
    /// Minimal, dependency-free JSON writer/reader for engine-side data exchange
    /// (external planners, scripts). Objects map to <c>Dictionary&lt;string, object&gt;</c>,
    /// arrays to <c>List&lt;object&gt;</c>, numbers to <c>double</c> (or <c>long</c> when
    /// integral and in range), plus string / bool / null. Invariant culture throughout.
    /// </summary>
    public static class MiniJson
    {
        // ── writing ──────────────────────────────────────────────────────────

        /// <summary>Serializes dictionaries, lists/arrays, strings, numbers, bools and null.</summary>
        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case double d: WriteNumber(sb, d); return;
                case float f: WriteNumber(sb, f); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case IDictionary dict:
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry e in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                        sb.Append(':');
                        Write(sb, e.Value);
                    }
                    sb.Append('}');
                    return;
                case IEnumerable list:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (object item in list)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    return;
                // other numbers unquoted; any other formattable value (a date, an enum, a Guid) as a string: it was
                // written unquoted, invalid JSON that could not be read back
                case decimal _: case short _: case ushort _: case byte _: case sbyte _: case uint _: case ulong _:
                    sb.Append(((IFormattable)v).ToString(null, CultureInfo.InvariantCulture)); return;
                case IFormattable fmt:
                    WriteString(sb, fmt.ToString(null, CultureInfo.InvariantCulture)); return;
                default:
                    WriteString(sb, v.ToString()); return;
            }
        }

        private static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ── reading ──────────────────────────────────────────────────────────

        /// <summary>Parses JSON text. Throws <see cref="FormatException"/> on malformed input.</summary>
        public static object Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            int pos = 0;
            object v = ReadValue(json, ref pos);
            SkipWs(json, ref pos);
            if (pos != json.Length) throw new FormatException($"Unexpected data at {pos}.");
            return v;
        }

        private static object ReadValue(string s, ref int p)
        {
            SkipWs(s, ref p);
            if (p >= s.Length) throw new FormatException("Unexpected end of JSON.");
            char c = s[p];
            if (c == '{') return ReadObject(s, ref p);
            if (c == '[') return ReadArray(s, ref p);
            if (c == '"') return ReadString(s, ref p);
            if (c == 't') { Expect(s, ref p, "true"); return true; }
            if (c == 'f') { Expect(s, ref p, "false"); return false; }
            if (c == 'n') { Expect(s, ref p, "null"); return null; }
            return ReadNumber(s, ref p);
        }

        private static Dictionary<string, object> ReadObject(string s, ref int p)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            p++; SkipWs(s, ref p);
            if (p < s.Length && s[p] == '}') { p++; return d; }
            while (true)
            {
                SkipWs(s, ref p);
                string key = ReadString(s, ref p);
                SkipWs(s, ref p);
                if (p >= s.Length || s[p] != ':') throw new FormatException($"':' expected at {p}.");
                p++;
                d[key] = ReadValue(s, ref p);
                SkipWs(s, ref p);
                if (p >= s.Length) throw new FormatException("Unterminated object.");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == '}') { p++; return d; }
                throw new FormatException($"',' or '}}' expected at {p}.");
            }
        }

        private static List<object> ReadArray(string s, ref int p)
        {
            var list = new List<object>();
            p++; SkipWs(s, ref p);
            if (p < s.Length && s[p] == ']') { p++; return list; }
            while (true)
            {
                list.Add(ReadValue(s, ref p));
                SkipWs(s, ref p);
                if (p >= s.Length) throw new FormatException("Unterminated array.");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == ']') { p++; return list; }
                throw new FormatException($"',' or ']' expected at {p}.");
            }
        }

        private static string ReadString(string s, ref int p)
        {
            if (s[p] != '"') throw new FormatException($"String expected at {p}.");
            p++;
            var sb = new StringBuilder();
            while (p < s.Length)
            {
                char c = s[p++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (p >= s.Length) break;
                char e = s[p++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (p + 4 > s.Length || !int.TryParse(s.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            throw new FormatException($"Bad \\u escape at {p}.");
                        sb.Append((char)code);
                        p += 4; break;
                    default: throw new FormatException($"Bad escape at {p}.");
                }
            }
            throw new FormatException("Unterminated string.");
        }

        private static object ReadNumber(string s, ref int p)
        {
            int start = p;
            while (p < s.Length && "+-0123456789.eE".IndexOf(s[p]) >= 0) p++;
            string text = s.Substring(start, p - start);
            if (text.Length == 0) throw new FormatException($"Unexpected character '{s[start]}' at {start}.");
            if (text.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                return l;
            return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int p, string word)
        {
            if (string.CompareOrdinal(s, p, word, 0, word.Length) != 0)
                throw new FormatException($"'{word}' expected at {p}.");
            p += word.Length;
        }

        private static void SkipWs(string s, ref int p)
        {
            while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
        }

        // ── typed accessors for parsed objects ───────────────────────────────

        public static double GetDouble(this Dictionary<string, object> o, string key, double def = 0)
            => o != null && o.TryGetValue(key, out object v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : def;

        public static long GetLong(this Dictionary<string, object> o, string key, long def = 0)
            => o != null && o.TryGetValue(key, out object v) && v != null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : def;

        public static string GetString(this Dictionary<string, object> o, string key, string def = null)
            => o != null && o.TryGetValue(key, out object v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : def;

        public static bool GetBool(this Dictionary<string, object> o, string key, bool def = false)
            => o != null && o.TryGetValue(key, out object v) && v is bool b ? b : def;

        public static List<object> GetList(this Dictionary<string, object> o, string key)
            => o != null && o.TryGetValue(key, out object v) ? v as List<object> : null;

        public static Dictionary<string, object> GetObject(this Dictionary<string, object> o, string key)
            => o != null && o.TryGetValue(key, out object v) ? v as Dictionary<string, object> : null;
    }
}
