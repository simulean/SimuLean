using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The card of a SimuLean model: an asset <c>ModelCard.asset</c> next to the model's scene, in the model's
    /// folder <c>&lt;modelsFolder&gt;/&lt;Name&gt;/</c> (<c>SimuLeanModels</c>, editor). It says what the model is for people
    /// (name, description, author, dates, the example it was copied from; the thumbnail <c>Thumbnail.png</c> beside it is
    /// captured when the scene is saved) and for agents (what it represents, the KPIs that matter, the parameters worth
    /// varying, the assumptions) — <c>simulean_model</c> answers it as its <c>info</c> block, <c>simulean_card</c> reads and
    /// writes it. Pure metadata: nothing reads it at run time.
    /// </summary>
    public sealed class SimuLeanModelCard : ScriptableObject
    {
        public const string FileName = "ModelCard.asset";
        public const string ThumbnailName = "Thumbnail.png";

        [Tooltip("The model's name as shown (the folder and the scene keep the model's name).")]
        public string displayName = "";
        [TextArea(2, 6), Tooltip("What the model is, in a few lines.")]
        public string description = "";
        public string author = "";
        [Tooltip("The model's scene (kept up to date on rename).")]
        public string scenePath = "";
        [Tooltip("The example scene this model was copied from (empty for a model made from scratch).")]
        public string copiedFrom = "";
        [Tooltip("When the model was created (local time, yyyy-MM-dd HH:mm:ss).")]
        public string created = "";
        [Tooltip("When its scene was last saved (local time, yyyy-MM-dd HH:mm:ss).")]
        public string modified = "";

        [Header("Notes for agents")]
        [TextArea(2, 8), Tooltip("What the model represents: the system, its scope, what is left out.")]
        public string represents = "";
        [TextArea(2, 8), Tooltip("The KPIs that matter (and the metric paths that measure them, e.g. out=Exit.input).")]
        public string kpis = "";
        [TextArea(2, 8), Tooltip("The parameters worth varying (catalog paths and sensible ranges, e.g. Buffer.capacity=1..10).")]
        public string parameters = "";
        [TextArea(2, 8), Tooltip("The assumptions (data sources, simplifications, calibration).")]
        public string assumptions = "";

        public const string DateFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>The card's text fields by name, in their order (what <c>simulean_card</c> reads and writes).</summary>
        public static IReadOnlyList<string> Fields => fields.Value;

        private static readonly Lazy<List<string>> fields = new Lazy<List<string>>(() =>
        {
            var list = new List<string>();
            foreach (var f in typeof(SimuLeanModelCard).GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (f.FieldType == typeof(string)) list.Add(f.Name);
            return list;
        });

        /// <summary>A field's value by name (<see cref="Fields"/>); null when there is no such field.</summary>
        public string Get(string field) => Field(field)?.GetValue(this) as string;

        /// <summary>Sets a field by name; false when there is no such field.</summary>
        public bool Set(string field, string value)
        {
            var f = Field(field);
            if (f == null) return false;
            f.SetValue(this, value ?? "");
            return true;
        }

        /// <summary>Every field with its value, in order.</summary>
        public Dictionary<string, string> ToDictionary()
        {
            var d = new Dictionary<string, string>();
            foreach (string f in Fields) d[f] = Get(f);
            return d;
        }

        /// <summary>The last-saved time (<see cref="modified"/>, else <see cref="created"/>); <see cref="DateTime.MinValue"/> when unset.</summary>
        public DateTime ModifiedTime
            => DateTime.TryParseExact(string.IsNullOrEmpty(modified) ? created : modified, DateFormat,
                                      System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
                ? t : DateTime.MinValue;

        private static FieldInfo Field(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var f = typeof(SimuLeanModelCard).GetField(name.Trim(), BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return f != null && f.FieldType == typeof(string) ? f : null;
        }
    }
}
