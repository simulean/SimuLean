namespace SimuLean.Results.Charts
{
    /// <summary>
    /// The colours of charts and state views (0xRRGGBB), one source for the engine's charts and the Unity inspector
    /// (<c>InspectorPalette</c> reads them): a series palette, the known states' colours and display order.
    /// </summary>
    public static class ChartPalette
    {
        /// <summary>Series colours, in order.</summary>
        public static readonly int[] Series =
        {
            0x4E79A7, 0xF28E2B, 0x59A14F, 0xE15759, 0x76B7B2, 0xEDC948, 0xB07AA1, 0xFF9DA7, 0x9C755F, 0xBAB0AC,
        };

        public static int SeriesColor(int index) => Series[((index % Series.Length) + Series.Length) % Series.Length];

        /// <summary>Preferred display order of the core states (others follow).</summary>
        public static readonly string[] StateOrder =
        {
            "PROCESSING", "RECEIVING", "TRAVELLING", "TRAVELLING_LOADED", "LOADING", "UNLOADING", "TRAVELLING_EMPTY",
            "SETUP", "WAITING_FOR_RESOURCE", "BLOCKED", "STOPPED", "BREAKDOWN", "SCHEDULED_DOWN", "OFF_SHIFT", "IDLE",
        };

        /// <summary>A state's colour (known states fixed, others from a stable hash of the name).</summary>
        public static int StateColor(string name)
        {
            switch (name)
            {
                case "PROCESSING": case "TRAVELLING_LOADED": return Rgb(0.30f, 0.75f, 0.35f);
                case "RECEIVING": return Rgb(0.50f, 0.82f, 0.45f);
                case "SETUP": return Rgb(0.95f, 0.80f, 0.25f);
                case "WAITING_FOR_RESOURCE": return Rgb(0.80f, 0.60f, 0.85f);
                case "BLOCKED": return Rgb(0.97f, 0.56f, 0.18f);
                case "STOPPED": case "BREAKDOWN": return Rgb(0.88f, 0.28f, 0.24f);
                case "SCHEDULED_DOWN": case "OFF_SHIFT": return Rgb(0.38f, 0.50f, 0.90f);
                case "TRAVELLING": case "LOADING": return Rgb(0.30f, 0.72f, 0.82f);
                case "UNLOADING": return Rgb(0.16f, 0.50f, 0.62f);
                case "TRAVELLING_EMPTY": return Rgb(0.62f, 0.80f, 0.98f);
                case "IDLE": return Rgb(0.55f, 0.57f, 0.61f);
                case null: return 0x808080;
            }
            unchecked
            {
                int h = 17;
                foreach (char c in name) h = h * 31 + c;
                return Hsv((h & 0xFFFF) / 65535f, 0.45f, 0.85f);
            }
        }

        /// <summary>The index of a state in <see cref="StateOrder"/> (unknown states after the known ones).</summary>
        public static int StateRank(string name)
        {
            int i = System.Array.IndexOf(StateOrder, name);
            return i < 0 ? 999 : i;
        }

        public static int Rgb(float r, float g, float b) => (Byte(r) << 16) | (Byte(g) << 8) | Byte(b);
        public static (float r, float g, float b) ToFloats(int rgb) => (((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

        private static int Byte(float v) => v <= 0 ? 0 : v >= 1 ? 255 : (int)(v * 255 + 0.5f);

        private static int Hsv(float h, float s, float v)
        {
            float c = v * s, x = c * (1 - System.Math.Abs(h * 6 % 2 - 1)), m = v - c;
            float r, g, b;
            switch ((int)(h * 6) % 6)
            {
                case 0: r = c; g = x; b = 0; break;
                case 1: r = x; g = c; b = 0; break;
                case 2: r = 0; g = c; b = x; break;
                case 3: r = 0; g = x; b = c; break;
                case 4: r = x; g = 0; b = c; break;
                default: r = c; g = 0; b = x; break;
            }
            return Rgb(r + m, g + m, b + m);
        }
    }
}
