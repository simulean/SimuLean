using System;

namespace SimuLean.Unity
{
    /// <summary>
    /// A short note for agents on a field whose right use is not obvious from its name and type:
    /// <c>simulean_types</c> / <c>simulean_params</c> list it under <c>help</c>, by the field's path (a list
    /// item's field as <c>stops[i].emptiesOrigins</c>). Keep it to the trap and the pattern that avoids it; the tooltip stays
    /// the inspector's text. Used sparingly: every note is read on every answer for that type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class SimuLeanHelpAttribute : Attribute
    {
        public SimuLeanHelpAttribute(string text) { Text = text; }

        /// <summary>The note.</summary>
        public string Text { get; }
    }
}
