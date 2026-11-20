using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// A random choice among <b>text values</b> with given probabilities (an empirical discrete distribution over names:
    /// part references, product variants, destinations). Written as a table <c>value:weight|value:weight|…</c>
    /// (e.g. <c>"R1:0.28|R2:0.24|R3:0.48"</c>); weights need not add up to 1 (they are normalised), a value without a
    /// weight weighs 1, a weight may use a decimal comma. Draws with the inverse transform on the cumulative weights,
    /// one <see cref="IRandomEngine.NextDouble"/> per draw, from its own stream (<see cref="IRandomStreamUser"/>:
    /// <c>SimContext.AssignRandomStream(choice, owner, purpose)</c>).
    /// </summary>
    public sealed class DiscreteChoice : IRandomStreamUser
    {
        private readonly string[] values;
        private readonly double[] cumulative;   // normalised, last = 1
        private IRandomEngine engine;

        public DiscreteChoice(IReadOnlyList<string> values, IReadOnlyList<double> weights, IRandomEngine engine = null)
        {
            if (values == null || weights == null || values.Count == 0 || values.Count != weights.Count)
                throw new ArgumentException("A discrete choice needs one weight per value and at least one value.");
            this.values = new string[values.Count];
            cumulative = new double[values.Count];
            double total = 0;
            for (int i = 0; i < values.Count; i++)
            {
                double w = weights[i];
                if (double.IsNaN(w) || w < 0 || double.IsInfinity(w))
                    throw new ArgumentException($"Discrete choice: the weight of '{values[i]}' must be a non-negative number.");
                this.values[i] = values[i] ?? "";
                total += w;
                cumulative[i] = total;
            }
            if (!(total > 0)) throw new ArgumentException("Discrete choice: the weights add up to 0.");
            for (int i = 0; i < cumulative.Length; i++) cumulative[i] /= total;
            cumulative[cumulative.Length - 1] = 1.0;
            this.engine = engine ?? new UnassignedStreamEngine(nameof(DiscreteChoice));
        }

        /// <summary>The values, in table order.</summary>
        public IReadOnlyList<string> Values => values;

        /// <summary>Probability of value <paramref name="index"/> (normalised weight).</summary>
        public double Probability(int index) => cumulative[index] - (index > 0 ? cumulative[index - 1] : 0);

        public void SetRandomEngine(IRandomEngine engine) => this.engine = engine ?? this.engine;

        /// <summary>Draws the index of a value.</summary>
        public int SampleIndex()
        {
            double u = engine.NextDouble();
            for (int i = 0; i < cumulative.Length; i++)
                if (u < cumulative[i]) return i;
            return cumulative.Length - 1;
        }

        /// <summary>Draws a value.</summary>
        public string Sample() => values[SampleIndex()];

        /// <summary>
        /// Parses a table <c>value:weight|value:weight|…</c> (separator <c>|</c>, or <c>;</c> / new lines; the weight
        /// after the last <c>:</c>). Throws <see cref="FormatException"/> on an empty table or a bad weight.
        /// </summary>
        public static DiscreteChoice Parse(string table, IRandomEngine engine = null)
        {
            var vals = new List<string>();
            var weights = new List<double>();
            foreach (var (name, w) in ParseEntries(table)) { vals.Add(name); weights.Add(w); }
            try { return new DiscreteChoice(vals, weights, engine); }
            catch (ArgumentException e) { throw new FormatException(e.Message, e); }
        }

        /// <summary>The entries of a table text <c>A:0.3|B:0.7</c> (also separated by <c>;</c> or new lines; a weight left
        /// out = 1), as written (not normalised). Throws for an empty table or a bad weight.</summary>
        public static List<(string value, double weight)> ParseEntries(string table)
        {
            if (string.IsNullOrWhiteSpace(table)) throw new FormatException("Discrete choice: empty table.");
            var entries = new List<(string, double)>();
            foreach (string raw in table.Split(new[] { '|', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string entry = raw.Trim();
                if (entry.Length == 0) continue;
                int sep = entry.LastIndexOf(':');
                string name = sep > 0 ? entry.Substring(0, sep).Trim() : entry;
                double w = 1;
                if (sep > 0)
                {
                    string ws = entry.Substring(sep + 1).Trim().Replace(',', '.');
                    if (!double.TryParse(ws, NumberStyles.Float, CultureInfo.InvariantCulture, out w))
                        throw new FormatException($"Discrete choice: bad weight '{entry.Substring(sep + 1)}' of '{name}'.");
                }
                entries.Add((name, w));
            }
            if (entries.Count == 0) throw new FormatException("Discrete choice: empty table.");
            return entries;
        }

        /// <summary>The table text (<c>value:probability|…</c>, invariant culture).</summary>
        public string ToSpec()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(values[i]).Append(':').Append(Probability(i).ToString("R", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
