using System.Collections.Generic;
using SimuLean.Data;
using SimuLean.Results;
using SimuLean.Results.Charts;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>Where a chart's results come from.</summary>
    public enum ChartDataSource
    {
        /// <summary>The results being looked at: the Results tab's set (by default the last one produced); on a dashboard in Play
        /// (<see cref="UnityDashboard"/>), the run going on.</summary>
        ShownResults,
        /// <summary>Sets of the results store (by id), compared side by side when several.</summary>
        StoredSets,
    }

    /// <summary>
    /// A chart saved with the project: an asset holding a <see cref="ChartQuery"/> — kind, the
    /// value(s), categories, filters — and where its results come from. Drawn by the engine
    /// (<see cref="ChartBuilder"/> → <see cref="ChartRenderer"/>), so the Results tab, a dashboard in Play (G2b) and the
    /// exported PNG show the same image. Create: SimuLean window ▸ Results ▸ Charts ▸ New, or Assets ▸ Create ▸ SimuLean ▸ Chart.
    /// </summary>
    [CreateAssetMenu(menuName = "SimuLean/Chart", fileName = "Chart")]
    public sealed class ChartDefinition : ScriptableObject
    {
        public ChartQuery spec = new ChartQuery();

        [Tooltip("The results charted: the ones shown in the Results tab, or sets of the results store.")]
        public ChartDataSource source = ChartDataSource.ShownResults;

        [Tooltip("Stored sets (Results tab ▸ Stored, the #id), compared when several.")]
        public List<int> storedSets = new List<int>();

        [Tooltip("Size of the exported chart, in points (PNG at 2 pixels per point; SVG in points).")]
        public Vector2Int exportSize = new Vector2Int(800, 500);

        /// <summary>The chart's data for <paramref name="shown"/> (the shown tables) or, from the store, its own sets.</summary>
        public ChartData Build(IReadOnlyDictionary<string, ResultTable> shown, string projectRoot)
        {
            if (source == ChartDataSource.StoredSets)
            {
                if (storedSets.Count == 0) return new ChartData { Title = Title, Message = "No stored set chosen (the #id of Results ▸ Stored)." };
                try
                {
                    var store = ResultsOutput.Store(projectRoot);
                    var sets = new List<ResultSet>();
                    foreach (int id in storedSets) sets.Add(store.Load(id));
                    return ChartBuilder.Build(spec, sets.Count == 1 ? sets[0] : ResultSet.Compare(sets));
                }
                catch (System.Exception e) { return new ChartData { Title = Title, Message = "The stored sets could not be read: " + e.Message }; }
            }
            if (shown == null) return new ChartData { Title = Title, Message = "No results yet: run the model, an experiment or a study." };
            return ChartBuilder.Build(spec, shown);
        }

        public ChartData Build(ResultSet shown, string projectRoot) => Build(shown == null ? null : Tables(shown), projectRoot);

        private string Title => string.IsNullOrWhiteSpace(spec.Title) ? name : spec.Title;

        public static Dictionary<string, ResultTable> Tables(ResultSet set)
        {
            var map = new Dictionary<string, ResultTable>();
            foreach (var t in set.Tables()) map[t.Name] = t;
            return map;
        }
    }

    /// <summary>A <see cref="ChartImage"/> as a Unity texture (rows flipped: Unity's origin is bottom-left), and the charts' fonts.</summary>
    public static class ChartTextures
    {
        /// <summary>
        /// The engine reads the chart fonts (Inter) through <c>Resources</c> (<c>SimuLean/Fonts/*.bytes</c>), so a player build
        /// has them; in the editor the engine also finds them on disk. Call before rendering at run time (idempotent).
        /// </summary>
        public static void UseResourceFonts()
        {
            if (ChartFonts.Provider != null) return;
            ChartFonts.Provider = name =>
            {
                var asset = UnityEngine.Resources.Load<TextAsset>("SimuLean/Fonts/" + name);
                return asset != null ? asset.bytes : null;
            };
        }

        public static Texture2D ToTexture(ChartImage image, Texture2D reuse = null)
        {
            var tex = reuse != null && reuse.width == image.Width && reuse.height == image.Height ? reuse
                : new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            if (reuse != null && tex != reuse) Object.DestroyImmediate(reuse);
            int row = image.Width * 4;
            var flipped = new byte[image.Rgba.Length];
            for (int y = 0; y < image.Height; y++) System.Buffer.BlockCopy(image.Rgba, y * row, flipped, (image.Height - 1 - y) * row, row);
            tex.LoadRawTextureData(flipped);
            tex.Apply(false);
            return tex;
        }
    }
}
