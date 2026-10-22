using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SimuLean.Stochastics.Fitting
{
    /// <summary>
    /// A data sample to fit: the values sorted, with their summary (n, mean, standard deviation, minimum,
    /// maximum, coefficient of variation, skewness) and what the fitting families need to know (all integers? all
    /// positive?). Non-finite values are dropped.
    /// </summary>
    public sealed class FitSample
    {
        public double[] Values { get; }
        public int N => Values.Length;
        public double Mean { get; }
        /// <summary>Sample variance (n − 1).</summary>
        public double Variance { get; }
        public double StdDev => Math.Sqrt(Variance);
        public double Min => Values[0];
        public double Max => Values[N - 1];
        public double CoefficientOfVariation => Mean != 0 ? StdDev / Math.Abs(Mean) : double.NaN;
        public double Skewness { get; }
        public bool AllIntegers { get; }
        public bool AllPositive => Min > 0;
        public bool AllNonNegative => Min >= 0;

        public FitSample(IEnumerable<double> values)
        {
            Values = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).OrderBy(v => v).ToArray();
            if (N < 2) throw new ArgumentException("Fitting needs at least two values.");
            Mean = Values.Average();
            double m2 = 0, m3 = 0;
            foreach (double v in Values) { double d = v - Mean; m2 += d * d; m3 += d * d * d; }
            Variance = m2 / (N - 1);
            double sdPop = Math.Sqrt(m2 / N);
            Skewness = sdPop > 0 ? m3 / N / (sdPop * sdPop * sdPop) : 0;
            AllIntegers = Values.All(v => Math.Abs(v - Math.Round(v)) < 1e-9);
        }

        /// <summary>The numbers of a text (one per line, or separated by commas, semicolons, tabs or spaces; a decimal comma
        /// is read when the text has no other comma use) — e.g. a column pasted from a spreadsheet. Words are skipped.</summary>
        public static List<double> ParseNumbers(string text)
        {
            var list = new List<double>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            // a lone "3,5" per line (decimal comma) vs "3,5,7" (separators): decimal comma when no line has two commas
            // and no line also has a point
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool decimalComma = lines.All(l => l.Count(c => c == ',') <= 1) && !text.Contains('.') && text.Contains(',')
                                && lines.All(l => !l.Contains(';') && !l.Contains('\t') && l.Trim().Split(' ').Length == 1);
            foreach (string line in lines)
            {
                string l = decimalComma ? line.Replace(',', '.') : line;
                foreach (string tok in l.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    if (double.TryParse(tok.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) list.Add(v);
            }
            return list;
        }
    }
}
