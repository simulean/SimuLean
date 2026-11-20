using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A path through ordered points, its corners optionally rounded with circular arcs, sampled by arc
    /// length: <see cref="Sample"/> gives the point and the direction of travel <c>s</c> metres from the start (before the
    /// start / past the end it continues straight along the first / last segment). Pure geometry, view side only — used by
    /// <see cref="UnityPacedLine"/> (pitches along a serpentine line) and the milk-run train view (legs through via points).
    /// </summary>
    public sealed class PolylinePath
    {
        private readonly List<Vector3> points = new List<Vector3>();
        private readonly List<float> cumulative = new List<float>();

        /// <summary>Length of the path (the rounded corners included).</summary>
        public float Length { get; private set; }

        /// <summary>The sampled points (the arcs as short segments).</summary>
        public IReadOnlyList<Vector3> Points => points;

        /// <summary>
        /// The path through <paramref name="corners"/>; each interior corner is replaced by an arc of
        /// <paramref name="cornerRadius"/> (shrunk where the segments are too short: the arc ends at most halfway along
        /// each segment), drawn with <paramref name="segmentsPer90"/> segments per quarter turn.
        /// </summary>
        public static PolylinePath Build(IList<Vector3> corners, float cornerRadius = 0f, int segmentsPer90 = 8)
        {
            var p = new PolylinePath();
            if (corners == null || corners.Count == 0) return p;
            p.Add(corners[0]);
            for (int i = 1; i < corners.Count - 1; i++)
            {
                Vector3 a = corners[i - 1], c = corners[i], b = corners[i + 1];
                Vector3 d1 = c - a, d2 = b - c;
                float l1 = d1.magnitude, l2 = d2.magnitude;
                if (l1 < 1e-5f || l2 < 1e-5f || cornerRadius <= 0f) { p.Add(c); continue; }
                d1 /= l1; d2 /= l2;
                float theta = Mathf.Acos(Mathf.Clamp(Vector3.Dot(d1, d2), -1f, 1f));   // turn angle
                if (theta < 1e-3f || theta > Mathf.PI - 1e-3f) { p.Add(c); continue; }
                float tanHalf = Mathf.Tan(theta * 0.5f);
                float t = Mathf.Min(cornerRadius * tanHalf, l1 * 0.5f, l2 * 0.5f);
                float r = t / tanHalf;
                Vector3 t1 = c - d1 * t;
                Vector3 n = (d2 - d1 * Vector3.Dot(d1, d2)).normalized;     // towards the inside of the turn
                Vector3 centre = t1 + n * r;
                int steps = Mathf.Max(2, Mathf.CeilToInt(segmentsPer90 * theta / (Mathf.PI * 0.5f)));
                for (int k = 0; k <= steps; k++)
                {
                    float phi = theta * k / steps;
                    p.Add(centre - n * (r * Mathf.Cos(phi)) + d1 * (r * Mathf.Sin(phi)));
                }
            }
            if (corners.Count > 1) p.Add(corners[corners.Count - 1]);
            return p;
        }

        private void Add(Vector3 v)
        {
            if (points.Count > 0 && (v - points[points.Count - 1]).sqrMagnitude < 1e-10f) return;
            Length += points.Count > 0 ? Vector3.Distance(points[points.Count - 1], v) : 0f;
            points.Add(v);
            cumulative.Add(Length);
        }

        /// <summary>The point <paramref name="s"/> metres along the path and the direction of travel there.</summary>
        public Vector3 Sample(float s, out Vector3 direction)
        {
            direction = Vector3.forward;
            int n = points.Count;
            if (n == 0) return Vector3.zero;
            if (n == 1) return points[0];
            if (s <= 0f)
            {
                direction = (points[1] - points[0]).normalized;
                return points[0] + direction * s;
            }
            if (s >= Length)
            {
                direction = (points[n - 1] - points[n - 2]).normalized;
                return points[n - 1] + direction * (s - Length);
            }
            int lo = 0, hi = n - 1;                    // the segment [lo, lo + 1] holding s
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (cumulative[mid] <= s) lo = mid; else hi = mid;
            }
            float seg = cumulative[lo + 1] - cumulative[lo];
            direction = (points[lo + 1] - points[lo]).normalized;
            return seg <= 0f ? points[lo] : Vector3.Lerp(points[lo], points[lo + 1], (s - cumulative[lo]) / seg);
        }

        public Vector3 PointAt(float s) => Sample(s, out _);
    }
}
