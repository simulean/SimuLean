using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SimuLean.Expressions;
using SimuLean.Serialization;

namespace SimuLean.Logic
{
    /// <summary>
    /// Item labels read that nothing sets: a label value is an expression, so an unquoted
    /// text (<c>type = A</c>) reads the label <c>A</c> — every item got an empty label and the run only warned at its first
    /// read. A read of <c>item.X</c> is flagged when no writer of the model can set <c>X</c>:
    /// <list type="bullet">
    /// <item>a label assignment (create / entry / exit labels, a task's labels…) or a reaction's Set Item Label — the
    /// report's <see cref="DecisionSite.Writes"/>;</item>
    /// <item>a label an element sets by its own logic (a pick face's <c>sku</c>, a call-off's <c>family</c>, a stacker's
    /// <c>stackCount</c>, an agenda's <c>appt_time</c>…; <see cref="EngineLabels"/>);</item>
    /// <item>a name the model's settings give that is not an expression (a schedule table's column, a call-off's reference
    /// label, a type label…): any such text counts, so a label set by a configured name is never flagged.</item>
    /// </list>
    /// A model that reads a file at run (a Source's table file) may set labels from columns not known before the run: then
    /// nothing is flagged. The item's built-ins (<see cref="EntityScope.ItemBuiltIns"/>) are not labels.
    /// </summary>
    public static partial class ModelLogic
    {
        /// <summary>The labels engine elements set by their own logic (copies of an item's labels add no name).</summary>
        public static readonly IReadOnlyList<string> EngineLabels = new[]
        {
            CallOffFamily.LabelCallOffTime, CallOffFamily.LabelFamily, CallOffFamily.LabelPosition, "reference", "vehicle",
            PickFace.SkuLabel,
            SequencingStation.LabelSlot, SequencingStation.LabelFirstVehicle, SequencingStation.LabelLastVehicle,
            SequencingStation.LabelPieces, SequencingStation.LabelGaps, SequencingStation.LabelCloseTime,
            Stacker.LabelPosition, Stacker.LabelCount, Stacker.LabelHeight, Stacker.LabelKey, Stacker.LabelReason,
            SimuLean.Storage.StorageSystem.DefaultHeightLabel, "height",
            "booked_at", "appt_time", "lead_time", "rescheduled", "no_show",        // Agenda
            "ship", "blocks",                                                       // ErectionDock
        };

        /// <summary>One warning per item label read that nothing in <paramref name="model"/> sets, naming the reader and
        /// suggesting the quoted text: <c>Src.createLabels: reads item label 'A', which nothing sets — did you mean the text
        /// 'A'? …</c>. <paramref name="owner"/> limits it to one owner (an element, team or reaction name).</summary>
        public static List<string> UnsetLabelReads(SimulationConfig model, LogicReport report = null, string owner = null)
        {
            var warnings = new List<string>();
            if (model == null) return warnings;
            report = report ?? Of(model);
            var known = new HashSet<string>(EngineLabels, StringComparer.OrdinalIgnoreCase);
            foreach (string b in EntityScope.ItemBuiltIns) known.Add(b);
            foreach (var o in report.Owners)
                foreach (var s in o.Sites)
                    foreach (string w in s.Writes)
                        if (w.StartsWith("item.", StringComparison.Ordinal)) known.Add(LabelOf(w));
            bool fileAtRun = false;
            foreach (var e in model.Elements)
                if (e?.Settings != null) Names(e.Settings, e.Settings.GetType(), known, ref fileAtRun, 0);
            foreach (var t in model.ResourceTeams)
                if (t != null) Names(t, typeof(ResourceTeamConfig), known, ref fileAtRun, 0);
            if (fileAtRun) return warnings;

            foreach (var o in report.Owners)
            {
                if (owner != null && o.Name != owner) continue;
                foreach (var s in o.Sites)
                    foreach (string r in s.Reads)
                    {
                        if (!r.StartsWith("item.", StringComparison.Ordinal)) continue;
                        string label = LabelOf(r);
                        if (label.Length == 0 || known.Contains(label)) continue;
                        string text = $"{o.Name}.{s.Key}: reads the item label '{label}', which nothing in the model sets — did you mean " +
                                      $"the text '{label}'? Label values are expressions: write text in quotes ('{label}').";
                        if (!warnings.Contains(text)) warnings.Add(text);
                    }
            }
            return warnings;
        }

        // "item.family" / "item.family (label)" → "family"
        private static string LabelOf(string read)
        {
            string s = read.Substring("item.".Length).Trim();
            int cut = s.IndexOfAny(new[] { ' ', '(', '[' });
            return cut >= 0 ? s.Substring(0, cut) : s;
        }

        // The texts of the settings that are not expressions (names a writer may set: a table's columns, a reference label…);
        // fileAtRun when a file is read at run (its columns are not known here).
        private static void Names(object group, Type type, HashSet<string> into, ref bool fileAtRun, int depth)
        {
            if (group == null || depth > 6) return;
            foreach (var m in SettingsPaths.Members(type))
            {
                object v = m.Get(group);
                if (v == null) continue;
                var a = Attr(m);
                if (a != null && a.Role == SettingRole.File)
                {
                    if (v is string f && f.Trim().Length > 0) fileAtRun = true;
                    continue;
                }
                if (a != null && (a.Context != null || a.Role == SettingRole.Expression || a.Form == LogicForm.Assignments)) continue;
                if (m.IsGroup) { Names(v, m.Type, into, ref fileAtRun, depth + 1); continue; }
                if (m.RecordType != null && v is IList records)
                {
                    foreach (object r in records) Names(r, m.RecordType, into, ref fileAtRun, depth + 1);
                    continue;
                }
                AddNames(v, into);
            }
        }

        private static void AddNames(object v, HashSet<string> into)
        {
            switch (v)
            {
                case string s:
                    if (s.Trim().Length > 0) into.Add(s.Trim());
                    break;
                case IDictionary d:
                    foreach (DictionaryEntry kv in d)
                        if (kv.Key is string k && k.Trim().Length > 0) into.Add(k.Trim());   // a table's columns, not its cells
                    break;
            }
        }
    }
}
