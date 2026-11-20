using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The edits of one <c>simulean_apply</c>. What a single <c>simulean_set</c> does at once after its edit — rebuild a
    /// rack's lanes, check a rule parameter list, check the labels an expression reads — waits for the end of the list: a
    /// later operation may set another layout field, the rule or the label. Passed to each edit it covers; an edit without
    /// one (a plain <c>simulean_set</c>) does its follow-ups at once.
    /// </summary>
    internal sealed class EditSession
    {
        private readonly HashSet<UnityStorageRack> racks = new HashSet<UnityStorageRack>();
        private readonly HashSet<Component> ruleChecks = new HashSet<Component>();
        private readonly List<string> edited = new List<string>();

        /// <summary>The rack's lanes are rebuilt once, after the last edit (when the list is kept).</summary>
        public void RebuildLater(UnityStorageRack rack) => racks.Add(rack);

        /// <summary>The component's rule parameter lists are checked once, after the last edit.</summary>
        public void CheckRulesLater(Component component) => ruleChecks.Add(component);

        /// <summary>An object a <c>set</c> operation changed: its unset label reads are checked after the last edit.</summary>
        public void Edited(string objectName) => edited.Add(objectName);

        /// <summary>The racks whose layout changed (still in the scene), in the order first edited.</summary>
        public List<UnityStorageRack> RacksToRebuild => racks.Where(r => r != null).ToList();

        /// <summary>The objects the <c>set</c> operations changed, each once, in the order first edited.</summary>
        public List<string> EditedObjects => edited.Distinct().ToList();

        /// <summary>The problems of the rule parameter lists edited (components still in the scene).</summary>
        public List<string> RuleWarnings()
        {
            var w = new List<string>();
            foreach (var c in ruleChecks) if (c != null) w.AddRange(RuleParameterChecks.Problems(c));
            return w;
        }
    }
}
