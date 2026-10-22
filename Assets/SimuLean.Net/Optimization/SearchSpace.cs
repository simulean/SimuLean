using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SimuLean.Optimization
{
    /// <summary>Kind of a decision variable.</summary>
    public enum VariableKind
    {
        /// <summary>Whole numbers in [Min, Max] — one, or a vector of <see cref="DecisionVariable.Length"/>.</summary>
        Integer,
        /// <summary>Real numbers in [Min, Max] — one, or a vector of <see cref="DecisionVariable.Length"/>.</summary>
        Real,
        /// <summary>An order of <see cref="DecisionVariable.Length"/> items (a permutation of 0..Length−1).</summary>
        Permutation,
        /// <summary><see cref="DecisionVariable.Length"/> yes / no decisions (0 or 1 each).</summary>
        Binary,
        /// <summary>One of the <see cref="DecisionVariable.Options"/> (the value is its index).</summary>
        Choice,
    }

    /// <summary>One decision of the problem (what an algorithm chooses), independent of what it means in the model.</summary>
    public sealed class DecisionVariable
    {
        public string Name { get; }
        public VariableKind Kind { get; }
        /// <summary>Bounds of each value (Integer / Real; 0 … options − 1 for a Choice).</summary>
        public double Min { get; }
        public double Max { get; }
        /// <summary>Values of the variable: 1 for a single number or choice; the items of a vector / permutation.</summary>
        public int Length { get; }
        /// <summary>The options of a Choice (empty otherwise).</summary>
        public IReadOnlyList<string> Options { get; }

        private DecisionVariable(string name, VariableKind kind, double min, double max, int length, IReadOnlyList<string> options = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A decision variable needs a name.");
            if (length < 1) throw new ArgumentException($"Variable '{name}': at least one value.");
            Name = name; Kind = kind; Min = min; Max = max; Length = length;
            Options = options ?? Array.Empty<string>();
        }

        /// <summary>A whole number in [min, max], or a vector of <paramref name="length"/> of them.</summary>
        public static DecisionVariable Integer(string name, int min, int max, int length = 1)
        {
            if (max < min) throw new ArgumentException($"Variable '{name}': max {max} < min {min}.");
            return new DecisionVariable(name, VariableKind.Integer, min, max, length);
        }

        /// <summary>A real number in [min, max], or a vector of <paramref name="length"/> of them.</summary>
        public static DecisionVariable Real(string name, double min, double max, int length = 1)
        {
            if (!(max >= min)) throw new ArgumentException($"Variable '{name}': max {max} < min {min}.");
            return new DecisionVariable(name, VariableKind.Real, min, max, length);
        }

        public static DecisionVariable Permutation(string name, int length)
        {
            if (length < 1) throw new ArgumentException($"Variable '{name}': a permutation needs at least one item.");
            return new DecisionVariable(name, VariableKind.Permutation, 0, length - 1, length);
        }

        public static DecisionVariable Binary(string name, int length)
        {
            if (length < 1) throw new ArgumentException($"Variable '{name}': a binary vector needs at least one item.");
            return new DecisionVariable(name, VariableKind.Binary, 0, 1, length);
        }

        /// <summary>One of <paramref name="options"/> (at least one).</summary>
        public static DecisionVariable Choice(string name, IEnumerable<string> options)
        {
            var list = new List<string>(options ?? throw new ArgumentNullException(nameof(options)));
            if (list.Count == 0) throw new ArgumentException($"Variable '{name}': a choice needs at least one option.");
            return new DecisionVariable(name, VariableKind.Choice, 0, list.Count - 1, 1, list);
        }

        /// <summary>A single number (Integer / Real of length 1): the variables the fitness can name.</summary>
        public bool IsNumber => (Kind == VariableKind.Integer || Kind == VariableKind.Real) && Length == 1;

        /// <summary>Clamps (and rounds, for Integer / Binary / Choice) one value into the variable's range.</summary>
        public double Clamp(double v)
        {
            if (double.IsNaN(v)) v = Min;
            if (Kind != VariableKind.Real) v = Math.Round(v, MidpointRounding.AwayFromZero);
            return v < Min ? Min : v > Max ? Max : v;
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case VariableKind.Choice: return $"{Name} Choice {{{string.Join(", ", Options)}}}";
                case VariableKind.Integer:
                case VariableKind.Real:
                    return $"{Name} {Kind}{(Length > 1 ? " ×" + Length : "")} [{Min.ToString(CultureInfo.InvariantCulture)}, {Max.ToString(CultureInfo.InvariantCulture)}]";
                default: return $"{Name} {Kind} ×{Length}";
            }
        }
    }

    /// <summary>The decisions of a problem, in order. Algorithms work on it without knowing the model.</summary>
    public sealed class SearchSpace
    {
        private readonly List<DecisionVariable> variables = new List<DecisionVariable>();
        private readonly Dictionary<string, int> index = new Dictionary<string, int>();

        public IReadOnlyList<DecisionVariable> Variables => variables;

        public SearchSpace Add(DecisionVariable v)
        {
            if (v == null) throw new ArgumentNullException(nameof(v));
            if (index.ContainsKey(v.Name)) throw new ArgumentException($"Decision variable '{v.Name}' is defined twice.");
            index[v.Name] = variables.Count;
            variables.Add(v);
            return this;
        }

        public int IndexOf(string name) => index.TryGetValue(name, out int i) ? i : -1;

        public int Count => variables.Count;
    }

    /// <summary>
    /// A point of a <see cref="SearchSpace"/>: <see cref="DecisionVariable.Length"/> values per variable — the numbers,
    /// the order (Permutation), 0 / 1 each (Binary) or the option's index (Choice).
    /// </summary>
    public sealed class Candidate
    {
        private readonly double[][] values;
        private string key;

        public SearchSpace Space { get; }

        public Candidate(SearchSpace space)
        {
            Space = space ?? throw new ArgumentNullException(nameof(space));
            values = new double[space.Count][];
            for (int i = 0; i < space.Count; i++)
            {
                var v = space.Variables[i];
                values[i] = new double[v.Length];
                for (int k = 0; k < v.Length; k++)
                    values[i][k] = v.Kind == VariableKind.Permutation ? k : v.Kind == VariableKind.Binary ? 0 : v.Min;
            }
        }

        /// <summary>The single value of variable <paramref name="i"/> (a number, 0 / 1, or a Choice's option index).</summary>
        public double Number(int i) => values[i][0];
        public double Number(string name) => values[Index(name)][0];

        /// <summary>Sets the single value of variable <paramref name="i"/> (clamped / rounded into its range).</summary>
        public Candidate SetNumber(int i, double value)
        {
            values[i][0] = Space.Variables[i].Clamp(value);
            key = null;
            return this;
        }

        /// <summary>The values of variable <paramref name="i"/> — do not modify; use <see cref="SetValues"/>.</summary>
        public double[] Values(int i) => values[i];

        /// <summary>Sets every value of variable <paramref name="i"/>: numbers are clamped into the range; a permutation
        /// must be one, binary values 0 / 1.</summary>
        public Candidate SetValues(int i, double[] value)
        {
            var v = Space.Variables[i];
            if (value == null || value.Length != v.Length)
                throw new ArgumentException($"Variable '{v.Name}' needs {v.Length} values.");
            var copy = new double[value.Length];
            for (int k = 0; k < copy.Length; k++)
            {
                double x = value[k];
                if (v.Kind == VariableKind.Binary && x != 0 && x != 1) throw new ArgumentException($"Variable '{v.Name}': binary values are 0 or 1.");
                copy[k] = v.Kind == VariableKind.Permutation ? x : v.Clamp(x);
            }
            if (v.Kind == VariableKind.Permutation && !IsPermutation(copy))
                throw new ArgumentException($"Variable '{v.Name}': not a permutation of 0..{v.Length - 1}.");
            values[i] = copy;
            key = null;
            return this;
        }

        /// <summary>The values of variable <paramref name="i"/> as whole numbers (Permutation, Binary, Integer vectors).</summary>
        public int[] Vector(int i)
        {
            var r = new int[values[i].Length];
            for (int k = 0; k < r.Length; k++) r[k] = (int)values[i][k];
            return r;
        }
        public int[] Vector(string name) => Vector(Index(name));

        public Candidate SetVector(int i, int[] value)
        {
            if (value == null) throw new ArgumentException($"Variable '{Space.Variables[i].Name}' needs {Space.Variables[i].Length} values.");
            var d = new double[value.Length];
            for (int k = 0; k < d.Length; k++) d[k] = value[k];
            return SetValues(i, d);
        }

        /// <summary>The chosen option of a Choice.</summary>
        public string Option(int i) => Space.Variables[i].Options[(int)values[i][0]];

        /// <summary>The value of variable <paramref name="i"/> as text: a number, the option, or the values separated by
        /// commas (invariant culture).</summary>
        public string Text(int i) => Text(i, "R");

        /// <summary>Variable <paramref name="i"/> rounded for display (6 significant digits). <see cref="Text(int)"/> is
        /// exact: stored and exported values are re-run as they were (G6 stored 12.3457 for 12.3456789).</summary>
        public string DisplayText(int i) => Text(i, "G6");

        private string Text(int i, string format)
        {
            var v = Space.Variables[i];
            if (v.Kind == VariableKind.Choice) return Option(i);
            var parts = new string[values[i].Length];
            for (int k = 0; k < parts.Length; k++) parts[k] = values[i][k].ToString(format, CultureInfo.InvariantCulture);
            return string.Join(", ", parts);
        }

        /// <summary>Sets variable <paramref name="i"/> from text (the form of <see cref="Text"/>; a Choice also by its index;
        /// true / false for a single binary value).</summary>
        public Candidate SetText(int i, string text)
        {
            var v = Space.Variables[i];
            text = (text ?? "").Trim();
            if (v.Kind == VariableKind.Choice)
            {
                int o = -1;
                for (int k = 0; k < v.Options.Count; k++) if (string.Equals(v.Options[k], text, StringComparison.OrdinalIgnoreCase)) o = k;
                if (o < 0 && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out o))
                    throw new FormatException($"Variable '{v.Name}': '{text}' is not one of {string.Join(", ", v.Options)}.");
                return SetNumber(i, o);
            }
            if (v.Kind == VariableKind.Binary && v.Length == 1 && bool.TryParse(text, out bool b)) return SetNumber(i, b ? 1 : 0);
            string[] parts = text.Trim('[', ']').Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != v.Length)
                throw new FormatException($"Variable '{v.Name}': {v.Length} value(s) expected, '{text}' has {parts.Length}.");
            var d = new double[parts.Length];
            for (int k = 0; k < d.Length; k++)
                if (!double.TryParse(parts[k].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d[k]))
                    throw new FormatException($"Variable '{v.Name}': '{parts[k].Trim()}' is not a number.");
            return SetValues(i, d);
        }

        public Candidate Clone()
        {
            var c = new Candidate(Space);
            for (int i = 0; i < values.Length; i++) c.values[i] = (double[])values[i].Clone();
            return c;
        }

        /// <summary>Identity of the point (equal candidates have equal keys): caching repeated evaluations.</summary>
        public string Key
        {
            get
            {
                if (key != null) return key;
                var sb = new StringBuilder();
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0) sb.Append(';');
                    for (int k = 0; k < values[i].Length; k++)
                    {
                        if (k > 0) sb.Append(',');
                        sb.Append(values[i][k].ToString("R", CultureInfo.InvariantCulture));
                    }
                }
                return key = sb.ToString();
            }
        }

        public override string ToString()
        {
            var parts = new List<string>();
            for (int i = 0; i < values.Length; i++)
            {
                var v = Space.Variables[i];
                parts.Add(v.Name + " = " + (v.Length > 1 ? "[" + DisplayText(i) + "]" : DisplayText(i)));
            }
            return string.Join(", ", parts);
        }

        private int Index(string name)
        {
            int i = Space.IndexOf(name);
            if (i < 0) throw new ArgumentException($"No decision variable '{name}'.");
            return i;
        }

        private static bool IsPermutation(double[] p)
        {
            var seen = new bool[p.Length];
            foreach (double d in p)
            {
                int x = (int)d;
                if (x != d || x < 0 || x >= p.Length || seen[x]) return false;
                seen[x] = true;
            }
            return true;
        }
    }
}
