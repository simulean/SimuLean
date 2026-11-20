using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimuLean
{
    /// <summary>
    /// How an item looks in the 3D view a colour and a scale of its visual, set by a reaction's
    /// <c>SetItemLook</c> action. View-only data: kept outside the item's labels (<see cref="SimEntity.Properties"/>), so no
    /// rule, statistic or admission reads it and setting it changes no result; headless runs carry it and nothing reads it.
    /// </summary>
    public sealed class ItemLook
    {
        /// <summary>The colour that tints the visual (null = its own).</summary>
        public LookColor? Color { get; }
        /// <summary>The visual's scale, a factor of its own (NaN = its own).</summary>
        public double Scale { get; }

        public ItemLook(LookColor? color, double scale = double.NaN)
        {
            if (!double.IsNaN(scale) && !(scale > 0) || double.IsInfinity(scale))
                throw new ArgumentOutOfRangeException(nameof(scale), $"the scale {scale} must be > 0.");
            Color = color;
            Scale = scale;
        }

        /// <summary>True when it sets nothing.</summary>
        public bool IsEmpty => Color == null && double.IsNaN(Scale);

        /// <summary>This look with what <paramref name="change"/> sets (what it leaves unset stays).</summary>
        public ItemLook With(ItemLook change)
        {
            if (change == null || change.IsEmpty) return this;
            return new ItemLook(change.Color ?? Color, double.IsNaN(change.Scale) ? Scale : change.Scale);
        }

        public override string ToString()
            => (Color != null ? Color.Value.ToString() : "") + (double.IsNaN(Scale) ? "" : (Color != null ? " " : "") + "×" + Scale.ToString("0.###", CultureInfo.InvariantCulture));
    }

    /// <summary>A colour of an <see cref="ItemLook"/>: red, green, blue and alpha in 0..1. Written <c>#RRGGBB</c>,
    /// <c>#RRGGBBAA</c>, <c>#RGB</c> or a name (<see cref="Names"/>).</summary>
    public readonly struct LookColor : IEquatable<LookColor>
    {
        public readonly double R, G, B, A;

        public LookColor(double r, double g, double b, double a = 1)
        {
            R = Clamp(r); G = Clamp(g); B = Clamp(b); A = Clamp(a);
        }

        private static double Clamp(double v) => double.IsNaN(v) ? 0 : v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>The colour names understood (lower case).</summary>
        public static readonly IReadOnlyDictionary<string, LookColor> Names = new Dictionary<string, LookColor>(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = Hex(0xE53935), ["green"] = Hex(0x43A047), ["blue"] = Hex(0x1E88E5), ["yellow"] = Hex(0xFDD835),
            ["orange"] = Hex(0xFB8C00), ["purple"] = Hex(0x8E24AA), ["cyan"] = Hex(0x00ACC1), ["magenta"] = Hex(0xD81B60),
            ["white"] = Hex(0xFFFFFF), ["black"] = Hex(0x000000), ["grey"] = Hex(0x9E9E9E), ["gray"] = Hex(0x9E9E9E),
            ["brown"] = Hex(0x6D4C41), ["pink"] = Hex(0xF48FB1),
        };

        private static LookColor Hex(int rgb) => new LookColor(((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0);

        /// <summary>Reads a colour; false (and why) when the text is none.</summary>
        public static bool TryParse(string text, out LookColor color, out string error)
        {
            color = default; error = null;
            string t = text?.Trim();
            if (string.IsNullOrEmpty(t)) { error = "no colour given"; return false; }
            if (Names.TryGetValue(t, out color)) return true;
            if (t[0] == '#')
            {
                string h = t.Substring(1);
                if (h.Length == 3) h = new string(new[] { h[0], h[0], h[1], h[1], h[2], h[2] });
                if ((h.Length == 6 || h.Length == 8)
                    && uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
                {
                    if (h.Length == 6) v = (v << 8) | 0xFF;
                    color = new LookColor(((v >> 24) & 0xFF) / 255.0, ((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0);
                    return true;
                }
            }
            error = $"'{t}' is not a colour: #RRGGBB, #RRGGBBAA, #RGB or a name ({string.Join(", ", Names.Keys)})";
            return false;
        }

        /// <summary><c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when not opaque.</summary>
        public override string ToString()
        {
            int B8(double v) => (int)Math.Round(v * 255);
            string s = "#" + B8(R).ToString("X2") + B8(G).ToString("X2") + B8(B).ToString("X2");
            return A < 1 ? s + B8(A).ToString("X2") : s;
        }

        public bool Equals(LookColor o) => R == o.R && G == o.G && B == o.B && A == o.A;
        public override bool Equals(object obj) => obj is LookColor o && Equals(o);
        public override int GetHashCode() => (R, G, B, A).GetHashCode();
    }
}
