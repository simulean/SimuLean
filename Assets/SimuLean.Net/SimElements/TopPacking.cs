using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Parts packed on a main item's top, in order: side by side along its width (x), a new row (towards −z) when the row is
    /// full, a new layer when the top is full; each row and layer centred; a part larger than the top gets a row (a layer)
    /// of its own. One function for the model and the view: the Combiner's packed load height
    /// (<see cref="Combiner.LoadHeight"/>) and the Combiner's view (which lays the parts where <see cref="Places"/> says), so
    /// the height a run computes is the height the view shows.
    /// </summary>
    public static class TopPacking
    {
        /// <summary>The space between parts, a share of each part's size.</summary>
        public const double Gap = 0.01;

        /// <summary>How far a row or a layer may exceed the top before the next part starts a new one (a share of it).</summary>
        public const double Slack = 1.02;

        /// <summary>Where parts of <paramref name="sizes"/> (x = across the width, y = height, z = along the length) rest on a
        /// top of <paramref name="width"/> × <paramref name="length"/>: the bottom centre of each, from the centre of the
        /// top (x right, y up, z forward).</summary>
        public static (double x, double y, double z)[] Places(double width, double length, IList<(double x, double y, double z)> sizes)
        {
            var at = new (double x, double y, double z)[sizes.Count];
            Pack(width, length, sizes, at);
            return at;
        }

        /// <summary>The height of the layers of parts of <paramref name="sizes"/> packed on a <paramref name="width"/> ×
        /// <paramref name="length"/> top (each layer as high as its highest part).</summary>
        public static double Height(double width, double length, IList<(double x, double y, double z)> sizes)
            => Pack(width, length, sizes, null);

        // the packing, the places filled when asked; returns the height of the layers
        private static double Pack(double width, double length, IList<(double x, double y, double z)> sizes, (double x, double y, double z)[] at)
        {
            int n = sizes.Count;
            var rowOf = at != null ? new int[n] : null;
            var layerOf = at != null ? new int[n] : null;
            var rowWidth = at != null ? new List<double>() : null;
            var layerDepth = at != null ? new List<double>() : null;
            double x = 0, z = 0, y = 0, rowDepth = 0, layerHeight = 0, rowEnd = 0, layerEnd = 0;
            int row = 0, layer = 0, inRow = 0, inLayer = 0;
            for (int k = 0; k < n; k++)
            {
                var s = sizes[k];
                if (inRow > 0 && x + s.x > width * Slack)
                {
                    rowWidth?.Add(rowEnd); z += rowDepth; x = 0; rowDepth = 0; rowEnd = 0; row++; inRow = 0;
                }
                if (inLayer > 0 && inRow == 0 && z + s.z > length * Slack)
                {
                    layerDepth?.Add(layerEnd); y += layerHeight; z = 0; layerHeight = 0; layerEnd = 0; layer++; inLayer = 0;
                }
                if (at != null)
                {
                    at[k] = (x + s.x * 0.5, y, z + s.z * 0.5);
                    rowOf[k] = row; layerOf[k] = layer;
                    if (x + s.x > rowEnd) rowEnd = x + s.x;
                    if (z + s.z > layerEnd) layerEnd = z + s.z;
                }
                x += s.x * (1 + Gap);
                if (s.z * (1 + Gap) > rowDepth) rowDepth = s.z * (1 + Gap);
                if (s.y > layerHeight) layerHeight = s.y;
                inRow++; inLayer++;
            }
            if (at != null)
            {
                rowWidth.Add(rowEnd); layerDepth.Add(layerEnd);
                for (int k = 0; k < n; k++)
                    at[k] = (at[k].x - rowWidth[rowOf[k]] * 0.5, at[k].y, layerDepth[layerOf[k]] * 0.5 - at[k].z);
            }
            return y + layerHeight;
        }
    }
}
