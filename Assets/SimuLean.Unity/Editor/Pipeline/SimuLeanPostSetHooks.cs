using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// What an edit of a component's field sets off once <c>simulean_set</c> has applied it, by component type: a rack's
    /// lanes rebuilt, a rule parameter list checked, an element's flow warnings, the labels an expression reads. Inside a
    /// <c>simulean_apply</c> (an <see cref="EditSession"/>) the rebuild and the cross-object checks wait for its last edit.
    /// A new follow-up of an edit is a hook here, not a type test in <c>SetOf</c>.
    /// </summary>
    public static partial class SimuLeanCommands
    {
        /// <summary>One applied field edit: what the hooks read, and what they add to the answer.</summary>
        private sealed class FieldEdit
        {
            public GameObject Owner;
            public Component Component;
            public string Key, Path;
            public SimuLeanExpressionAttribute Expression;   // null when the field is not an expression
            public EditSession Session;                      // null for a single simulean_set
            public string Rebuilt;
            public readonly List<string> Warnings = new List<string>();
        }

        /// <summary>A follow-up of the edits of one component type: a rebuild runs before the answer's own checks (they read
        /// the rebuilt object), a check adds its warnings after them.</summary>
        private sealed class PostSetHook
        {
            public readonly bool Rebuild;
            private readonly Type component;
            private readonly Func<Component, FieldEdit, bool> when;
            private readonly Action<Component, FieldEdit> run;

            private PostSetHook(bool rebuild, Type component, Func<Component, FieldEdit, bool> when, Action<Component, FieldEdit> run)
            {
                Rebuild = rebuild;
                this.component = component;
                this.when = when;
                this.run = run;
            }

            public static PostSetHook Rebuilds<T>(Func<T, FieldEdit, bool> when, Action<T, FieldEdit> run) where T : Component
                => new PostSetHook(true, typeof(T), (c, e) => when((T)c, e), (c, e) => run((T)c, e));

            public static PostSetHook Checks<T>(Func<T, FieldEdit, bool> when, Action<T, FieldEdit> run) where T : Component
                => new PostSetHook(false, typeof(T), (c, e) => when((T)c, e), (c, e) => run((T)c, e));

            public void RunIfApplies(FieldEdit e)
            {
                if (component.IsInstanceOfType(e.Component) && when(e.Component, e)) run(e.Component, e);
            }
        }

        // in this order: the warnings of the answer keep it
        private static readonly PostSetHook[] PostSetHooks =
        {
            // a rack's layout changed: its lanes follow at once (in a session once, after its last edit)
            PostSetHook.Rebuilds<UnityStorageRack>((rack, e) => IsRackLayoutField(e.Path), (rack, e) =>
            {
                if (e.Session != null) e.Session.RebuildLater(rack);
                else e.Rebuilt = RebuildRack(rack);
            }),
            // a lane's lengths or spacing changed: its rollers / floor marking follow (the inspector's OnValidate runs only on
            // an inspector edit)
            PostSetHook.Rebuilds<UnityLengthLimitedQueue>((q, e) => e.Path.StartsWith("laneLengths", StringComparison.Ordinal) || e.Path == "laneSpacing",
                                                         (q, e) => q.FitLaneViews()),
            // a combiner's input order given before its connections: they must not replace it
            PostSetHook.Checks<UnityCombiner>((comb, e) => e.Key == "inputOrder", (comb, e) => comb.NoteInputOrderSet()),
            // a rule parameter list that changes nothing: without its rule, or a name its rule lacks (in a session checked
            // once after its last edit: the rule may be set after its parameters)
            PostSetHook.Checks<Component>((c, e) => IsRuleSitePath(e.Path), (c, e) =>
            {
                if (e.Session != null) e.Session.CheckRulesLater(c);
                else e.Warnings.AddRange(RuleParameterChecks.Problems(c));
            }),
            // what the edit of an element's flow leaves without effect
            PostSetHook.Checks<SElement>((se, e) => true, (se, e) => e.Warnings.AddRange(FlowWarnings(se, e.Key))),
            // a label read that nothing sets (a session checks every object it set once, after its last edit)
            PostSetHook.Checks<Component>((c, e) => e.Expression != null && e.Session == null,
                                          (c, e) => e.Warnings.AddRange(LabelWarnings(new[] { e.Owner.name }))),
        };

        private static void RunPostSetHooks(FieldEdit edit, bool rebuild)
        {
            foreach (var hook in PostSetHooks)
                if (hook.Rebuild == rebuild) hook.RunIfApplies(edit);
        }
    }
}
