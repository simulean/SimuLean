using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The per-edit check of an item label read that nothing sets: <c>simulean_set</c> of an
    /// expression field warns when its owner reads a label no source, assignment, reaction or element of the scene sets
    /// (<see cref="SimuLean.Logic.ModelLogic.UnsetLabelReads"/>; most often a text without quotes, <c>type = A</c>);
    /// <c>simulean_apply</c> checks the objects it set once, after its last edit (a label may be set by a later operation).
    /// </summary>
    public static partial class SimuLeanCommands
    {
        /// <summary>The unset-label warnings of the scene's model for <paramref name="owners"/> (all when null).</summary>
        internal static List<string> LabelWarnings(ICollection<string> owners)
        {
            var warnings = new List<string>();
            bool log = Debug.unityLogger.logEnabled;
            try
            {
                Debug.unityLogger.logEnabled = false;   // the extraction's own warnings belong to validate
                var config = SimuLean.Unity.SceneExtractor.Extract().Config;
                var report = SimuLean.Logic.ModelLogic.Of(config);
                if (owners == null) return SimuLean.Logic.ModelLogic.UnsetLabelReads(config, report);
                foreach (string o in owners) warnings.AddRange(SimuLean.Logic.ModelLogic.UnsetLabelReads(config, report, o));
            }
            catch (Exception) { /* a model that does not extract yet: validate reports it */ }
            finally { Debug.unityLogger.logEnabled = log; }
            return warnings;
        }
    }
}
